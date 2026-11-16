using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Crestron.SimplSharp;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.ZoomRoom.Sdk;
using PepperDash.ZoomRoom.Sdk.EventArgs;

namespace PepperDash.Essentials.Plugins
{
    /// <summary>
    /// Production <see cref="IZoomRoomController"/> backed by <see cref="ZrcSdk"/>.
    /// Owns the SDK lifecycle: initializes on construction, disposes on program stop.
    /// </summary>
    public class ZrcSdkController : IZoomRoomController, IKeyed
    {
        private readonly ZrcSdk _sdk;
        private readonly string _sdkConfigPath;
        private readonly string _activationCode;
        // Null = the SDK's default room ID (a single Zoom Room); set when several rooms share the SDK.
        private readonly string _roomId;
        private bool _disposed;
        private bool _initialized;
        private string _pendingPassword;
        private CTimer _reconnectTimer;
        private int _reconnectAttempt;
        private bool _isConnected;
        private static readonly int[] ReconnectDelaysMs = { 5000, 10000, 20000, 30000, 60000 };

        // Stored-pairing fallback: when Zoom's server rejects the stored pairing (as opposed to the room
        // or the cloud being unreachable), RetryToPairRoom can never succeed again, so re-pair once with
        // the configured activation code. Re-armed by the next successful pair. 100006000 is deliberately
        // not listed: it is undocumented, has been seen with no stored pairing at all, and arrived about
        // 10 s after the request while the processor could not reach Zoom's cloud (a wrong default
        // gateway). That looks like a timeout, and treating it as a rejection would discard a good stored
        // pairing during a network outage; retrying the stored pairing recovers when the network returns.
        private static readonly int[] StoredPairingRejectedCodes = { 5002, 100000401 };
        private const long StoredPairingFallbackDelayMs = 2000;
        private bool _lastPairUsedStoredCredentials;
        private bool _storedPairingFallbackUsed;
        private CTimer _storedPairingFallbackTimer;

        // Zoom accepts an activation code only once. The code of the last pair attempt made with a code,
        // and a file (a hash, not the code) remembering which code last paired this room, so a code that
        // has already been used is never spent again automatically.
        private string _lastPairActivationCode;

        // Health watchdog: the SDK's connection flag/events can go stale on a silent/half-open drop,
        // but real command results stay truthful. Count consecutive command failures (while we still
        // believe we're connected) and probe the link before declaring the room offline.
        private const int CommandFailureStrikeThreshold = 2;
        private int _consecutiveCommandFailures;
        private bool _everConnected;
        private bool _healthCheckRunning;
        private readonly object _healthLock = new object();

        public event EventHandler<bool> HealthStateChanged;

        public string Key { get; }

        public ZrcSdkController(string key, string sdkConfigPath, string activationCode, string roomId = null)
        {
            Key = key;
            _sdkConfigPath  = sdkConfigPath  ?? "/user/zrcsdk";
            _activationCode = activationCode ?? string.Empty;
            _roomId         = string.IsNullOrWhiteSpace(roomId) ? null : roomId.Trim();

            // The ZRC SDK loads its native wrapper (libzrcsdkwrapperpdt.so) from /usr/lib by
            // default, but /usr/lib is a read-only filesystem at program runtime. The wrapper is
            // embedded in this assembly; extract it into a writable directory and point the SDK
            // there via ZrcSdk.SetLibraryPath before constructing the SDK.
            StageNativeWrapper();

            // Diagnostic: the wrapper has failed to dlopen on some firmware with "GLIBCXX_x not
            // found". Log the device's actual libstdc++/glibc symbol-version ceilings so we can
            // target a compatible cross-compile toolchain. Runs before SDK creation so it's logged
            // even when the load below throws.
            LogNativeRuntimeCeilings();

            try
            {
                _sdk = new ZrcSdk();
            }
            catch (Exception ex)
            {
                // Constructing the SDK loads/initializes the native wrapper. If that fails the
                // factory's static Serilog logger is invisible (separate plugin load context), so
                // the device just vanishes with a generic "unknown device type". Log via the IKeyed
                // channel (reaches the Essentials log) before letting it propagate.
                this.LogError(ex, "Failed to create ZRC SDK — native wrapper load/init failed; device will not load. {Message}", ex.Message);
                throw;
            }

            // Dispose on program stop — mirrors the ControlSystem example
            CrestronEnvironment.ProgramStatusEventHandler += OnProgramStatusEvent;
        }

        private const string NativeWrapperFileName = "libzrcsdkwrapperpdt.so";

        /// <summary>
        /// Extracts the native ZRC SDK wrapper embedded in this assembly into a writable directory
        /// next to the plugin and points the SDK at it via <see cref="ZrcSdk.SetLibraryPath"/>.
        /// Essentials' plugin loader deletes the .cplz and its unzip temp dir after moving only the
        /// managed .dll into <c>loadedAssemblies</c>, so the wrapper must travel inside the assembly
        /// as an embedded resource. The SDK loads it via <c>memfd_create</c>/<c>dlopen</c>, so a
        /// <c>noexec</c> writable path is fine. No-op if an up-to-date copy is already present.
        /// </summary>
        private void StageNativeWrapper()
        {
            // The embedded wrapper is linux-arm only. Fail fast on other architectures
            // (e.g. x64 dev/CI machines) with a clear message rather than a cryptic dlopen error (#28).
            if (RuntimeInformation.OSArchitecture != Architecture.Arm &&
                RuntimeInformation.OSArchitecture != Architecture.Arm64)
            {
                this.LogWarning(
                    "Skipping native wrapper staging: architecture {Arch} is not ARM. " +
                    "The embedded '{File}' is linux-arm only and will not load on this host.",
                    RuntimeInformation.OSArchitecture, NativeWrapperFileName);
                return;
            }

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var targetDir = Path.GetDirectoryName(assembly.Location) ?? "/user";
                var targetPath = Path.Combine(targetDir, NativeWrapperFileName);

                using var resourceStream = assembly.GetManifestResourceStream(NativeWrapperFileName);

                if (resourceStream == null)
                {
                    this.LogError(
                        "Embedded native wrapper '{File}' not found in plugin assembly. " +
                        "Available resources: {Resources}. ZRC SDK will fail to load.",
                        NativeWrapperFileName, string.Join(", ", assembly.GetManifestResourceNames()));
                    return;
                }

                // Compare content, not just length: two wrapper builds can easily land on the same byte
                // count, and a stale .so left in place would silently run the old native code.
                if (File.Exists(targetPath) &&
                    new FileInfo(targetPath).Length == resourceStream.Length &&
                    SameContent(targetPath, resourceStream))
                {
                    this.LogInformation(
                        "Native wrapper already staged at '{Target}' (content matches) — skipping copy.",
                        targetPath);
                    ZrcSdk.SetLibraryPath(targetDir);
                    return;
                }

                resourceStream.Position = 0;
                using (var fileStream = File.Create(targetPath))
                {
                    resourceStream.CopyTo(fileStream);
                }

                ZrcSdk.SetLibraryPath(targetDir);

                this.LogInformation(
                    "Staged embedded native wrapper '{File}' -> '{Target}' ({Bytes} bytes); SDK library path set to '{Dir}'.",
                    NativeWrapperFileName, targetPath, resourceStream.Length, targetDir);
            }
            catch (Exception ex)
            {
                this.LogError(ex,
                    "Failed to stage native wrapper '{File}': {Message}",
                    NativeWrapperFileName, ex.Message);
            }
        }

        /// <summary>
        /// Reads the device's C++ runtime / glibc shared objects directly (no shell) and logs the
        /// highest <c>GLIBCXX_*</c> and <c>GLIBC_*</c> symbol versions they provide. This is the
        /// compatibility ceiling the cross-compiled wrapper must stay at or below. Best-effort.
        /// </summary>
        private void LogNativeRuntimeCeilings()
        {
            try
            {
                foreach (var p in new[] { "/usr/lib/libstdc++.so.6", "/lib/libstdc++.so.6" })
                    if (File.Exists(p)) { LogMaxSymbolVersion(p, "GLIBCXX_"); break; }

                foreach (var p in new[] { "/lib/libc.so.6", "/usr/lib/libc.so.6", "/lib/arm-linux-gnueabihf/libc.so.6" })
                    if (File.Exists(p)) { LogMaxSymbolVersion(p, "GLIBC_"); break; }
            }
            catch (Exception ex)
            {
                this.LogWarning("Could not read device C++/glibc runtime versions: {Message}", ex.Message);
            }
        }

        private void LogMaxSymbolVersion(string path, string prefix)
        {
            // Stream-scan in fixed-size chunks instead of reading the full (MB-scale) binary into
            // memory. libstdc++ / libc are typically 1-4 MB on ARM; loading them at every boot
            // consumed significant heap for a diagnostic-only operation (#29).
            const int BufSize = 4096;
            var pattern = System.Text.Encoding.Latin1.GetBytes(prefix);
            var buf = new byte[BufSize + 32]; // 32-byte overlap to catch symbols that span chunk boundaries
            Version max = null; string maxStr = null;
            using var fs = File.OpenRead(path);
            int overlap = 0;
            while (true)
            {
                var read = fs.Read(buf, overlap, BufSize);
                if (read == 0) break;
                var len = overlap + read;
                var text = System.Text.Encoding.Latin1.GetString(buf, 0, len);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                    text, System.Text.RegularExpressions.Regex.Escape(prefix) + @"([0-9]+(?:\.[0-9]+)+)"))
                {
                    if (Version.TryParse(m.Groups[1].Value, out var v) && (max == null || v > max))
                    {
                        max = v; maxStr = m.Value;
                    }
                }
                // Preserve last 32 bytes as overlap for the next chunk
                overlap = Math.Min(32, len);
                Buffer.BlockCopy(buf, len - overlap, buf, 0, overlap);
            }
            this.LogInformation("Runtime check: {Path} provides up to {Max}", path, maxStr ?? "(none found)");
        }

        private void OnProgramStatusEvent(eProgramStatusEventType eventType)
        {
            if (eventType == eProgramStatusEventType.Stopping)
            {
                this.LogInformation("Program stopping — disposing ZRC SDK controller.");
                Dispose();
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public bool Initialize(string configPath)
        {
            if (_initialized)
            {
                this.LogWarning("Initialize called more than once — ignoring");
                return false;
            }
            _initialized = true;

            // Wire all SDK events before calling Initialize so no events are missed
            _sdk.Initialized             += (s, e) => SafeRaise(() => Initialized?.Invoke(this, e));
            _sdk.ConnectionStateChanged  += (s, e) =>
            {
                var state = (ConnectionState)e.ErrorCode;
                if (state == ConnectionState.Disconnected)
                {
                    _isConnected = false;
                    _consecutiveCommandFailures = 0;
                    // Disconnect mid-join: ExitMeeting won't fire, so clear any cached password here.
                    _pendingPassword = null;
                    SafeRaise(() => HealthStateChanged?.Invoke(this, false));
                    ScheduleReconnect();
                }
                else if (state == ConnectionState.Connected || state == ConnectionState.Established)
                {
                    _isConnected = true;
                    _everConnected = true;
                    _consecutiveCommandFailures = 0;
                    // Successfully reconnected — cancel any pending retry and reset the counter.
                    CancelReconnect();
                    SafeRaise(() => HealthStateChanged?.Invoke(this, true));
                }
                SafeRaise(() => ConnectionStateChanged?.Invoke(this, e));
            };
            _sdk.Error                   += (s, e) => SafeRaise(() => Error?.Invoke(this, e));
            _sdk.PairRoomResult          += (s, e) =>
            {
                this.LogInformation("PairRoomResult: [{ErrorCode}] {Message}",
                    e.ErrorCode, ZrcSdkCodes.GetPairRoomResultDescription(e.ErrorCode));
                if (e.ErrorCode == 0)
                {
                    _storedPairingFallbackUsed = false;
                    if (!_lastPairUsedStoredCredentials && !string.IsNullOrEmpty(_lastPairActivationCode))
                        RememberPairedActivationCode(_lastPairActivationCode);
                }
                else if (_lastPairUsedStoredCredentials && Array.IndexOf(StoredPairingRejectedCodes, e.ErrorCode) >= 0)
                    FallBackToActivationCode(e.ErrorCode);
                SafeRaise(() => PairRoomResult?.Invoke(this, e));
            };
            _sdk.MeetingStatus           += (s, e) => SafeRaise(() => MeetingStatusChanged?.Invoke(this, e));
            _sdk.InstantMeetingStarted   += (s, e) => SafeRaise(() => InstantMeetingStarted?.Invoke(this, e));
            _sdk.StartPmiResult          += (s, e) => SafeRaise(() => StartPmiResult?.Invoke(this, e));
            _sdk.ExitMeeting             += (s, e) =>
            {
                _pendingPassword = null; // C4: don't auto-submit stale password to a later meeting
                SafeRaise(() => ExitMeeting?.Invoke(this, e));
            };
            _sdk.MeetingNeedsPassword    += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_pendingPassword))
                {
                    // Auto-supply the password that was passed with JoinMeetingWithPassword
                    this.LogDebug("MeetingNeedsPassword — auto-submitting cached password");
                    var pw = _pendingPassword;
                    _pendingPassword = null;
                    _sdk.SendMeetingPassword(pw);
                }
                else
                {
                    SafeRaise(() => MeetingNeedsPassword?.Invoke(this, e));
                }
            };
            _sdk.MeetingInvite           += (s, e) => SafeRaise(() => MeetingInvite?.Invoke(this, e));
            _sdk.MeetingInviteTreated    += (s, e) => SafeRaise(() => MeetingInviteTreated?.Invoke(this, e));
            _sdk.MeetingLockStatus       += (s, e) => SafeRaise(() => MeetingLockStatusChanged?.Invoke(this, e));
            _sdk.AudioStatus             += (s, e) => SafeRaise(() => AudioMuteStatusChanged?.Invoke(this, e));
            _sdk.RecordingStatus         += (s, e) => SafeRaise(() => RecordingStatusChanged?.Invoke(this, e));
            _sdk.RecordingRequest        += (s, e) => SafeRaise(() => RecordingRequestReceived?.Invoke(this, e));
            _sdk.PromptReceived          += (s, e) => SafeRaise(() => PromptReceived?.Invoke(this, e));
            _sdk.AskUnmuteByHost         += (s, e) => SafeRaise(() => AudioUnmuteRequested?.Invoke(this, e));
            _sdk.MuteOnEntry             += (s, e) => SafeRaise(() => MuteOnEntryChanged?.Invoke(this, e));
            _sdk.AllowAttendeesUnmuteChanged += (s, e) => SafeRaise(() => AllowAttendeesUnmuteChanged?.Invoke(this, e));
            _sdk.AllowAttendeesVideoChanged  += (s, e) => SafeRaise(() => AllowAttendeesVideoChanged?.Invoke(this, e));
            _sdk.BOStatusChanged             += (s, e) => SafeRaise(() => BreakoutStatusChanged?.Invoke(this, e));
            _sdk.BORoomListUpdated           += (s, e) => SafeRaise(() => BreakoutRoomListUpdated?.Invoke(this, e));
            _sdk.BOOptionsChanged            += (s, e) => SafeRaise(() => BreakoutOptionsChanged?.Invoke(this, e));
            _sdk.BOUserStatusChanged         += (s, e) => SafeRaise(() => BreakoutUserStatusChanged?.Invoke(this, e));
            _sdk.BOTimerTick                 += (s, e) => SafeRaise(() => BreakoutTimerTick?.Invoke(this, e));
            _sdk.BOParticipantsUpdated       += (s, e) => SafeRaise(() => BreakoutParticipantsUpdated?.Invoke(this, e));
            _sdk.WebinarAttendeeListReceived += (s, e) => SafeRaise(() => WebinarAttendeeListReceived?.Invoke(this, e));
            _sdk.WebinarCountsChanged        += (s, e) => SafeRaise(() => WebinarCountsChanged?.Invoke(this, e));
            _sdk.IntegrationEvent            += (s, e) => SafeRaise(() => IntegrationEvent?.Invoke(this, e));
            _sdk.FarEndCameraControlRequest += (s, e) => SafeRaise(() => FarEndCameraControlRequested?.Invoke(this, e));
            _sdk.MeetingRecordingInfoChanged += (s, e) => SafeRaise(() => MeetingRecordingInfoChanged?.Invoke(this, e));
            _sdk.StatisticalInfoChanged  += (s, e) => SafeRaise(() => StatisticalInfoChanged?.Invoke(this, e));
            _sdk.CameraPresetInfoChanged += (s, e) => SafeRaise(() => CameraPresetInfoChanged?.Invoke(this, e));
            _sdk.ParticipantsInitialized += (s, e) => SafeRaise(() => ParticipantsInitialized?.Invoke(this, e));
            _sdk.UserJoined              += (s, e) => SafeRaise(() => UserJoined?.Invoke(this, e));
            _sdk.UserLeft                += (s, e) => SafeRaise(() => UserLeft?.Invoke(this, e));
            _sdk.UserUpdated             += (s, e) => SafeRaise(() => UserUpdated?.Invoke(this, e));
            _sdk.ParticipantCount        += (s, e) => SafeRaise(() => ParticipantCountChanged?.Invoke(this, e));
            _sdk.HostChanged             += (s, e) => SafeRaise(() => HostChanged?.Invoke(this, e));
            _sdk.SharingStatusChanged    += (s, e) => SafeRaise(() => SharingStatusChanged?.Invoke(this, e));
            _sdk.AirPlayStatusChanged    += (s, e) => SafeRaise(() => AirPlayStatusChanged?.Invoke(this, e));
            _sdk.VideoPageStatusChanged  += (s, e) => SafeRaise(() => VideoPageStatusChanged?.Invoke(this, e));
            _sdk.ScreenLayoutStatusChanged += (s, e) => SafeRaise(() => ScreenLayoutStatusChanged?.Invoke(this, e));
            _sdk.DynamicLayoutOptionChanged += (s, e) => SafeRaise(() => DynamicLayoutOptionChanged?.Invoke(this, e));
            _sdk.LayoutDiagnostic += (s, e) => SafeRaise(() => LayoutDiagnostic?.Invoke(this, e));
            _sdk.VideoThumbInfoChanged   += (s, e) => SafeRaise(() => VideoThumbInfoChanged?.Invoke(this, e));
            _sdk.SIPCallStatus           += (s, e) => SafeRaise(() => SipCallStatusChanged?.Invoke(this, e));
            _sdk.ControlSystemEnabled    += (s, e) => SafeRaise(() => ZrcsEnabledChanged?.Invoke(this, e));
            _sdk.ContactListChanged      += (s, e) => SafeRaise(() => ContactListChanged?.Invoke(this, e));
            _sdk.MeetingListChanged      += (s, e) => SafeRaise(() => MeetingListChanged?.Invoke(this, e));

            var effectivePath = string.IsNullOrEmpty(configPath) ? _sdkConfigPath : configPath;
            var result = _sdk.Initialize(effectivePath, _roomId);
            this.LogInformation("ZrcSdk.Initialize({ConfigPath}, roomId={RoomId}) = {Result}", effectivePath, _roomId ?? "(default)", result);
            if (!result)
            {
                // The usual cause with several Zoom Rooms: two devices on the same room ID (or both
                // on the default). Without its own ID this device cannot control a room.
                this.LogError("ZRC SDK did not initialize for room ID {RoomId}. Each Zoom Room device needs its own sdkRoomId; at most one may leave it unset.", _roomId ?? "(default)");
                return false;
            }

            // Auto-reconnect or pair with activation code
            if (_sdk.CanRetryToPairLastRoom())
            {
                this.LogInformation("Stored pairing credentials found — reconnecting...");
                _lastPairUsedStoredCredentials = true;
                _sdk.RetryToPairRoom();
            }
            else if (!string.IsNullOrEmpty(_activationCode))
            {
                this.LogInformation("No stored credentials — pairing with activation code.");
                if (ConfiguredCodeAlreadyPaired())
                    this.LogWarning("The configured activation code already paired this room once; Zoom may reject it. If it does, generate a new code in the Zoom portal and run 'pairZoomRoom'.");
                PairWithActivationCode(_activationCode);
            }
            else
            {
                this.LogInformation("No stored credentials and no activationCode configured. Use console command 'pairZoomRoom <code>'.");
            }

            return result;
        }

        private void SafeRaise(Action raise)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                this.LogError(ex, "Exception in SDK event handler: {Message}", ex.Message);
            }
        }

        public int GetConnectionState() => _disposed ? 2 : _sdk.GetConnectionState();

        public MeetingStatus? GetMeetingStatus() => _disposed ? null : _sdk.GetMeetingStatus();

        public bool TryGetMeetingInfo(out CurrentMeetingInfo info)
        {
            info = null;
            return !_disposed && _sdk.TryGetMeetingInfo(out info);
        }

        public bool TryGetBreakoutEnabled(out bool enabled)
        {
            enabled = false;
            return !_disposed && _sdk.IsBOEnabled(out enabled);
        }

        // ── Pairing ───────────────────────────────────────────────────────────

        public bool PairWithActivationCode(string activationCode)
        {
            _lastPairUsedStoredCredentials = false;
            _lastPairActivationCode = activationCode?.Trim();
            return _sdk.PairRoomWithActivationCode(activationCode);
        }

        public bool CanRetryPair()  => _sdk.CanRetryToPairLastRoom();

        public bool RetryPair()
        {
            _lastPairUsedStoredCredentials = true;
            return _sdk.RetryToPairRoom();
        }

        public bool Unpair()        => _sdk.UnpairRoom();

        public bool RepairWithConfiguredCode()
        {
            if (string.IsNullOrEmpty(_activationCode))
            {
                this.LogWarning("RepairWithConfiguredCode called but no activationCode is configured. Use 'pairZoomRoom <code>' instead.");
                return false;
            }

            // Not unpaired first: an activation code works once, so if Zoom rejects this one the stored
            // pairing must still be there to reconnect with. To discard it deliberately, run
            // 'unpairZoomRoom' before 'pairZoomRoom'.
            if (ConfiguredCodeAlreadyPaired())
                this.LogWarning("The configured activation code already paired this room once; Zoom may reject it. The stored pairing is kept either way.");
            this.LogInformation("Re-pairing with the configured activation code.");
            return PairWithActivationCode(_activationCode);
        }

        private string PairedCodeFilePath =>
            Path.Combine(_sdkConfigPath, "paired-code-" + (_roomId ?? "default") + ".sha256");

        private static string HashActivationCode(string code)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes((code ?? string.Empty).Trim()));
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }

        private void RememberPairedActivationCode(string code)
        {
            try
            {
                Directory.CreateDirectory(_sdkConfigPath);
                File.WriteAllText(PairedCodeFilePath, HashActivationCode(code));
            }
            catch (Exception ex)
            {
                this.LogWarning("Could not record which activation code paired this room: {Message}", ex.Message);
            }
        }

        /// <summary>True when the configured activation code is the one that last paired this room.</summary>
        private bool ConfiguredCodeAlreadyPaired()
        {
            if (string.IsNullOrEmpty(_activationCode))
                return false;

            try
            {
                return File.Exists(PairedCodeFilePath)
                    && string.Equals(File.ReadAllText(PairedCodeFilePath).Trim(), HashActivationCode(_activationCode), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                this.LogWarning("Could not read which activation code paired this room: {Message}", ex.Message);
                return false;
            }
        }

        // Called from the SDK's PairRoomResult event when the server rejected the stored pairing. Runs
        // the re-pair on a timer rather than inside the SDK callback, once until a pair succeeds again.
        private void FallBackToActivationCode(int errorCode)
        {
            if (_disposed) return;

            if (string.IsNullOrEmpty(_activationCode))
            {
                this.LogWarning("Zoom rejected the stored pairing (code {Code}) and no activationCode is configured. Pair with 'pairZoomRoom <code>'.", errorCode);
                return;
            }

            if (ConfiguredCodeAlreadyPaired())
            {
                this.LogWarning("Zoom rejected the stored pairing (code {Code}), and the configured activation code has already paired this room, so Zoom will not accept it again. Generate a new code in the Zoom portal and run 'pairZoomRoom'.", errorCode);
                return;
            }

            if (_storedPairingFallbackUsed)
            {
                this.LogWarning("Zoom rejected the stored pairing (code {Code}) again after re-pairing with the configured activation code. Check the code in the Zoom portal and pair with 'pairZoomRoom <code>'.", errorCode);
                return;
            }

            _storedPairingFallbackUsed = true;
            this.LogWarning("Zoom rejected the stored pairing (code {Code}); re-pairing with the configured activation code.", errorCode);

            _storedPairingFallbackTimer?.Dispose();
            _storedPairingFallbackTimer = new CTimer(_ =>
            {
                if (_disposed) return;
                try
                {
                    // The stored pairing is dead, so the reconnect loop's RetryToPairRoom cannot help.
                    CancelReconnect();
                    RepairWithConfiguredCode();
                }
                catch (Exception ex)
                {
                    this.LogError(ex, "Re-pairing with the configured activation code failed: {Message}", ex.Message);
                }
            }, null, StoredPairingFallbackDelayMs);
        }

        // ── Device ────────────────────────────────────────────────────────────

        public bool WakeUp()    => _sdk.WakeZoomRoomUp();
        public bool Logout()    => _sdk.LogoutZoomRoomDevice();
        public bool RestartOs() => _sdk.RestartZoomRoomOS();

        // ── Meeting ───────────────────────────────────────────────────────────

        public bool StartMeeting(string meetingNumber)      => Guard(nameof(StartMeeting)) && _sdk.StartMeeting(meetingNumber);
        public bool StartInstantMeeting()                   => Guard(nameof(StartInstantMeeting)) && _sdk.StartInstantMeeting();
        public bool StartMeetingWithHostKey(string hostKey) => Guard(nameof(StartMeetingWithHostKey)) && _sdk.StartMeetingWithHostKey(hostKey);
        public bool JoinMeeting(string meetingNumber)       => Guard(nameof(JoinMeeting)) && _sdk.JoinMeeting(meetingNumber);
        public bool JoinMeetingWithPassword(string meetingNumber, string password)
        {
            if (!Guard(nameof(JoinMeetingWithPassword))) return false;
            // Cache the password — it will be sent when MeetingNeedsPassword fires
            _pendingPassword = password;
            var result = _sdk.JoinMeeting(meetingNumber);
            if (!result) _pendingPassword = null; // sync rejection — ExitMeeting won't fire
            return result;
        }
        public bool JoinMeetingWithUrl(string url)          => Guard(nameof(JoinMeetingWithUrl)) && Rc(nameof(JoinMeetingWithUrl), _sdk.JoinMeetingWithURL(url));
        public bool EndMeeting()                            => Guard(nameof(EndMeeting)) && Rc(nameof(EndMeeting), _sdk.EndMeeting());
        public bool LeaveMeeting()                          => Guard(nameof(LeaveMeeting)) && Rc(nameof(LeaveMeeting), _sdk.LeaveMeeting());
        public bool AnswerMeetingInvite(bool accept)        => Guard(nameof(AnswerMeetingInvite)) && Rc(nameof(AnswerMeetingInvite), _sdk.AnswerMeetingInvite(accept));
        public bool SendMeetingPassword(string password)    => Guard(nameof(SendMeetingPassword)) && Rc(nameof(SendMeetingPassword), _sdk.SendMeetingPassword(password));
        public bool CancelEnteringMeetingPassword()         => Guard(nameof(CancelEnteringMeetingPassword)) && Rc(nameof(CancelEnteringMeetingPassword), _sdk.CancelEnteringMeetingPassword());
        public bool CancelWaitingForHost()                  => Guard(nameof(CancelWaitingForHost)) && Rc(nameof(CancelWaitingForHost), _sdk.CancelWaitingForHost());
        public bool LockMeeting(bool locked)                => Guard(nameof(LockMeeting)) && Rc(nameof(LockMeeting), _sdk.LockMeeting(locked));

        // ── SDK call result logging ─────────────────────────────────────────────
        // The SDK returns a ZRCSDKError (0 = success) or a bool; ZoomRoom otherwise discards it.
        // Log non-zero / failed results at Warning so SDK rejections (wrong meeting state, feature
        // not available, etc.) are visible during remote testing — a console "method called" only
        // means the C# call didn't throw, not that the SDK acted. Successes log at Debug.
        private int Rc(string op, int code)
        {
            if (code != 0) { this.LogWarning("SDK call {Op} returned error code {Code}", op, code); NoteCommandResult(false); }
            else { this.LogDebug("SDK call {Op} ok", op); NoteCommandResult(true); }
            return code;
        }
        private bool Rc(string op, bool ok)
        {
            if (!ok) { this.LogWarning("SDK call {Op} returned failure", op); NoteCommandResult(false); }
            else { this.LogDebug("SDK call {Op} ok", op); NoteCommandResult(true); }
            return ok;
        }

        // Connection guard for outbound commands: returns false and logs when the SDK is not yet
        // Connected/Established (#25 — centralised here so every command is gated uniformly
        // instead of ad-hoc EnsureConnected() checks scattered across ZoomRoom).
        private bool Guard(string op)
        {
            if (_isConnected) return true;
            this.LogWarning("{Op} ignored: SDK not connected.", op);
            return false;
        }

        // ── Audio ─────────────────────────────────────────────────────────────

        public bool SetAudioMute(bool mute)                    => Guard(nameof(SetAudioMute)) && Rc(nameof(SetAudioMute), _sdk.SetAudioMute(mute));
        public bool MuteUserAudio(int userId, bool mute)       => Guard(nameof(MuteUserAudio)) && Rc(nameof(MuteUserAudio), _sdk.MuteUserAudio(userId, mute));
        public bool MuteAllAudio(bool mute)                    => Guard(nameof(MuteAllAudio)) && Rc(nameof(MuteAllAudio), _sdk.MuteAllAudio(mute));
        public bool SetMuteOnEntry(bool mute)                  => Guard(nameof(SetMuteOnEntry)) && Rc(nameof(SetMuteOnEntry), _sdk.SetMuteOnEntry(mute));
        public bool AnswerUnmuteRequest(bool accepted)         => Guard(nameof(AnswerUnmuteRequest)) && Rc(nameof(AnswerUnmuteRequest), _sdk.AnswerUnmuteRequest(accepted));
        public bool AllowAttendeesUnmute(bool allow)           => Guard(nameof(AllowAttendeesUnmute)) && Rc(nameof(AllowAttendeesUnmute), _sdk.AllowAttendeesUnmute(allow));
        public bool AllowAttendeesStartVideo(bool allow)       => Guard(nameof(AllowAttendeesStartVideo)) && Rc(nameof(AllowAttendeesStartVideo), _sdk.AllowAttendeesStartVideo(allow));

        // ── In-call prompt answers ────────────────────────────────────────────
        public bool ConfirmMeetingReminder(bool agree, int reminderType)      => Guard(nameof(ConfirmMeetingReminder)) && Rc(nameof(ConfirmMeetingReminder), _sdk.ConfirmMeetingReminder(agree, (MeetingReminderType)reminderType));
        public bool ConfirmCustomizedMeetingReminder(bool agree, int type)    => Guard(nameof(ConfirmCustomizedMeetingReminder)) && Rc(nameof(ConfirmCustomizedMeetingReminder), _sdk.ConfirmCustomizedMeetingReminder(agree, type));
        public bool ConfirmConsent(bool agree, int consentType, string id)    => Guard(nameof(ConfirmConsent)) && Rc(nameof(ConfirmConsent), _sdk.ConfirmConsent(agree, (ConsentType)consentType, id));
        public bool ConfirmCombinedConsent(bool agree, long consentType)      => Guard(nameof(ConfirmCombinedConsent)) && Rc(nameof(ConfirmCombinedConsent), _sdk.ConfirmCombinedConsent(agree, consentType));
        public bool HandlePrivacyAlert(int action, int type)                  => Guard(nameof(HandlePrivacyAlert)) && Rc(nameof(HandlePrivacyAlert), _sdk.HandlePrivacyAlert((PrivacyAlertAction)action, (PrivacyAlertType)type));
        public bool ContinueMeetingOnInactivity()                             => Guard(nameof(ContinueMeetingOnInactivity)) && Rc(nameof(ContinueMeetingOnInactivity), _sdk.ContinueMeetingOnInactivity());
        public bool AnswerHostRequestUnmuteVideo(bool accepted)               => Guard(nameof(AnswerHostRequestUnmuteVideo)) && Rc(nameof(AnswerHostRequestUnmuteVideo), _sdk.AnswerHostRequestUnmuteVideo(accepted));
        public bool RespondRemoteCameraControl(int userId, bool accept)       => Guard(nameof(RespondRemoteCameraControl)) && Rc(nameof(RespondRemoteCameraControl), _sdk.RespondRemoteCameraControl(userId, accept));
        public bool ResponseHostInviteToMainSession(bool accept)              => Guard(nameof(ResponseHostInviteToMainSession)) && Rc(nameof(ResponseHostInviteToMainSession), _sdk.ResponseHostInviteToMainSession(accept));
        public bool JoinBreakoutRoom()                                        => Guard(nameof(JoinBreakoutRoom)) && Rc(nameof(JoinBreakoutRoom), _sdk.JoinBreakoutRoom());
        public bool StartBreakoutRooms()                                      => Guard(nameof(StartBreakoutRooms)) && Rc(nameof(StartBreakoutRooms), _sdk.StartBreakoutRooms());
        public bool StopBreakoutRooms()                                       => Guard(nameof(StopBreakoutRooms)) && Rc(nameof(StopBreakoutRooms), _sdk.StopBreakoutRooms());
        public bool BroadcastMessageToBreakoutRooms(string message)           => Guard(nameof(BroadcastMessageToBreakoutRooms)) && Rc(nameof(BroadcastMessageToBreakoutRooms), _sdk.BroadcastMessageToBreakoutRooms(message));
        public bool LeaveBreakoutRoom()                                       => Guard(nameof(LeaveBreakoutRoom)) && Rc(nameof(LeaveBreakoutRoom), _sdk.LeaveBreakoutRoom());
        public bool AskForHelpInBreakoutRoom()                                => Guard(nameof(AskForHelpInBreakoutRoom)) && Rc(nameof(AskForHelpInBreakoutRoom), _sdk.AskForHelpInBreakoutRoom());

        // ── Breakout rooms: creator / admin / data ────────────────────────────
        public bool CreateBreakoutRooms(int count, int assignType)             => Guard(nameof(CreateBreakoutRooms)) && Rc(nameof(CreateBreakoutRooms), _sdk.CreateBreakoutRooms(count, (BOAssignType)assignType));
        public bool AddBreakoutRoom()                                         => Guard(nameof(AddBreakoutRoom)) && Rc(nameof(AddBreakoutRoom), _sdk.AddBreakoutRoom());
        public bool DeleteBreakoutRoom(string sessionBID)                     => Guard(nameof(DeleteBreakoutRoom)) && Rc(nameof(DeleteBreakoutRoom), _sdk.DeleteBreakoutRoom(sessionBID));
        public bool RenameBreakoutRoom(string sessionBID, string newName)     => Guard(nameof(RenameBreakoutRoom)) && Rc(nameof(RenameBreakoutRoom), _sdk.RenameBreakoutRoom(sessionBID, newName));
        public bool AssignUsersToBreakoutRoom(IEnumerable<string> g, string b) => Guard(nameof(AssignUsersToBreakoutRoom)) && Rc(nameof(AssignUsersToBreakoutRoom), _sdk.AssignUsersToBreakoutRoom(g, b));
        public bool SetBOOptions(BOOptionsInfo options)                       => Guard(nameof(SetBOOptions)) && Rc(nameof(SetBOOptions), _sdk.SetBOOptions(options));
        public bool RequestBOOptions()                                        => Guard(nameof(RequestBOOptions)) && Rc(nameof(RequestBOOptions), _sdk.RequestBOOptions());
        public bool MoveUserToBreakoutRoom(string userGuid, string sessionBID) => Guard(nameof(MoveUserToBreakoutRoom)) && Rc(nameof(MoveUserToBreakoutRoom), _sdk.MoveUserToBreakoutRoom(userGuid, sessionBID));
        public bool InviteBOUserReturnToMainSession(string userGuid)          => Guard(nameof(InviteBOUserReturnToMainSession)) && Rc(nameof(InviteBOUserReturnToMainSession), _sdk.InviteBOUserReturnToMainSession(userGuid));
        public bool IgnoreBOHelpRequest(string userGuid)                      => Guard(nameof(IgnoreBOHelpRequest)) && Rc(nameof(IgnoreBOHelpRequest), _sdk.IgnoreBOHelpRequest(userGuid));
        public bool JoinBreakoutRoomForHelp(string g, string b, string n)     => Guard(nameof(JoinBreakoutRoomForHelp)) && Rc(nameof(JoinBreakoutRoomForHelp), _sdk.JoinBreakoutRoomForHelp(g, b, n));
        public bool JoinBreakoutRoomByBID(string sessionBID)                  => Guard(nameof(JoinBreakoutRoomByBID)) && Rc(nameof(JoinBreakoutRoomByBID), _sdk.JoinBreakoutRoomByBID(sessionBID));
        public bool RequestBreakoutRoomList()                                 => Guard(nameof(RequestBreakoutRoomList)) && Rc(nameof(RequestBreakoutRoomList), _sdk.RequestBreakoutRoomList());
        public bool RequestBreakoutRoomUserList()                             => Guard(nameof(RequestBreakoutRoomUserList)) && Rc(nameof(RequestBreakoutRoomUserList), _sdk.RequestBreakoutRoomUserList());

        // ── Roles ─────────────────────────────────────────────────────────────
        public bool ClaimHost(string hostKey)                                 => Guard(nameof(ClaimHost)) && Rc(nameof(ClaimHost), _sdk.ClaimHost(hostKey));
        public bool AssignCohost(int userId, bool assign)                     => Guard(nameof(AssignCohost)) && Rc(nameof(AssignCohost), _sdk.AssignCohost(userId, assign));
        public bool PromoteAttendeeToPanelist(int userId)                     => Guard(nameof(PromoteAttendeeToPanelist)) && Rc(nameof(PromoteAttendeeToPanelist), _sdk.PromoteAttendeeToPanelist(userId));
        public bool DemotePanelistToAttendee(int userId)                      => Guard(nameof(DemotePanelistToAttendee)) && Rc(nameof(DemotePanelistToAttendee), _sdk.DemotePanelistToAttendee(userId));
        public bool AllowWebinarAttendeeTalk(int userId, bool allow)          => Guard(nameof(AllowWebinarAttendeeTalk)) && Rc(nameof(AllowWebinarAttendeeTalk), _sdk.AllowWebinarAttendeeTalk(userId, allow));
        public bool ListWebinarAttendees(string keywords)                     => Guard(nameof(ListWebinarAttendees)) && Rc(nameof(ListWebinarAttendees), _sdk.ListWebinarAttendees(keywords ?? string.Empty));
        public bool? IsWebinarMeeting()
        {
            if (!_isConnected) return null;
            try { return _sdk.TryGetMeetingInfo(out var info) && info != null ? info.IsWebinar : (bool?)null; }
            catch (Exception ex) { this.LogDebug("IsWebinarMeeting query failed: {Message}", ex.Message); return null; }
        }
        public bool SetSpeakerVolume(float volume)             => Guard(nameof(SetSpeakerVolume)) && Rc(nameof(SetSpeakerVolume), _sdk.SetSpeakerVolume(volume));
        public float GetSpeakerVolume()                        => _sdk.GetSpeakerVolume(out var v) ? v : -1f;
        public bool SetMicrophoneVolume(float volume)          => Guard(nameof(SetMicrophoneVolume)) && Rc(nameof(SetMicrophoneVolume), _sdk.SetMicrophoneVolume(volume));
        public float GetMicrophoneVolume()                     => _sdk.GetMicrophoneVolume(out var v) ? v : -1f;
        // Not through Rc: statistics are optional, so a room that refuses them is no strike against its health.
        public bool EnableStatisticalInfo(bool enable)         => Guard(nameof(EnableStatisticalInfo)) && _sdk.EnableStatisticalInfo(enable);

        // ── Video ─────────────────────────────────────────────────────────────

        public bool SetVideoState(bool start)                  => Guard(nameof(SetVideoState)) && Rc(nameof(SetVideoState), _sdk.SetVideoState(start));
        public bool SetMyVideoHidden(bool hidden)              => Guard(nameof(SetMyVideoHidden)) && Rc(nameof(SetMyVideoHidden), _sdk.SetMyVideoHidden(hidden));
        public bool MuteUserVideo(int userId, bool mute)       => Guard(nameof(MuteUserVideo)) && Rc(nameof(MuteUserVideo), _sdk.MuteUserVideo(userId, mute));
        public bool PinUserOnScreen(int userId, int screenIndex = 0)    => Guard(nameof(PinUserOnScreen)) && Rc(nameof(PinUserOnScreen), _sdk.PinUserOnScreen(userId, screenIndex));
        public bool UnpinUserFromScreen(int userId, int screenIndex = 0) => Guard(nameof(UnpinUserFromScreen)) && Rc(nameof(UnpinUserFromScreen), _sdk.UnpinUserFromScreen(userId, screenIndex));
        public bool SpotlightUser(int userId)       => Guard(nameof(SpotlightUser)) && Rc(nameof(SpotlightUser), _sdk.SpotlightUser(userId));
        public bool CancelSpotlightUser(int userId) => Guard(nameof(CancelSpotlightUser)) && Rc(nameof(CancelSpotlightUser), _sdk.CancelSpotlightUser(userId));
        public bool ControlUserCamera(int userId, int action, int type) => Guard(nameof(ControlUserCamera)) && Rc(nameof(ControlUserCamera), _sdk.ControlUserCamera(userId, action, type));
        public bool ControlCamera(string deviceId, int action, int type)    => Guard(nameof(ControlCamera)) && Rc(nameof(ControlCamera), _sdk.ControlCamera(deviceId, action, type));
        public bool ChangeSmartCameraMode(int mask, string deviceId = "")   => Guard(nameof(ChangeSmartCameraMode)) && Rc(nameof(ChangeSmartCameraMode), _sdk.ChangeSmartCameraMode(mask, deviceId));
        public CameraDevice[] GetCameras()              => _sdk.GetCameras();
        public CameraDevice GetCurrentCamera()          => _sdk.TryGetCurrentCamera(out var cam) ? cam : null;
        public int JoinIntegrationMeeting(string meetingId, string password, ThirdPartyMeetingProvider provider)
            => Guard(nameof(JoinIntegrationMeeting)) ? Rc(nameof(JoinIntegrationMeeting), _sdk.JoinIntegrationMeeting(meetingId, password, provider)) : -1;
        public int LeaveIntegrationMeeting()        => Guard(nameof(LeaveIntegrationMeeting)) ? Rc(nameof(LeaveIntegrationMeeting), _sdk.LeaveIntegrationMeeting()) : -1;
        public int MuteIntegrationAudio(bool mute)  => Guard(nameof(MuteIntegrationAudio)) ? Rc(nameof(MuteIntegrationAudio), _sdk.MuteIntegrationAudio(mute)) : -1;
        public int StopIntegrationVideo(bool stop)  => Guard(nameof(StopIntegrationVideo)) ? Rc(nameof(StopIntegrationVideo), _sdk.StopIntegrationVideo(stop)) : -1;
        public InterOperabilityInfo GetInterOperabilityInfo(ThirdPartyMeetingProvider provider)
            => Guard(nameof(GetInterOperabilityInfo)) ? _sdk.GetInterOperabilityInfo(provider) : null;
        public int SetCameraDisplayName(string deviceId, string displayName)
            => Guard(nameof(SetCameraDisplayName)) ? Rc(nameof(SetCameraDisplayName), _sdk.SetCameraDisplayName(deviceId, displayName)) : -1;
        public int SelectMultipleCamera(string deviceId, bool selected)
            => Guard(nameof(SelectMultipleCamera)) ? Rc(nameof(SelectMultipleCamera), _sdk.SelectMultipleCamera(deviceId, selected)) : -1;
        public bool SetCurrentCamera(string deviceId)   => Guard(nameof(SetCurrentCamera)) && Rc(nameof(SetCurrentCamera), _sdk.SetCurrentCamera(deviceId));
        public bool SetCameraPreset(uint index, string deviceId)  => Guard(nameof(SetCameraPreset)) && Rc(nameof(SetCameraPreset), _sdk.SetCameraPreset(index, deviceId ?? ""));
        public bool GoToCameraPreset(uint index, string deviceId) => Guard(nameof(GoToCameraPreset)) && Rc(nameof(GoToCameraPreset), _sdk.GoToCameraPreset(index, deviceId ?? ""));
        public bool NameCameraPreset(uint index, string name, string deviceId) => Guard(nameof(NameCameraPreset)) && Rc(nameof(NameCameraPreset), _sdk.NameCameraPreset(index, name ?? "", deviceId ?? ""));

        // ── Layout ────────────────────────────────────────────────────────────

        public int SetScreenLayout(int screen, int layoutSourceType) => Guard(nameof(SetScreenLayout)) ? Rc(nameof(SetScreenLayout), _sdk.SetScreenLayout(screen, layoutSourceType)) : -1;
        public int SetVideoOrder(int videoOrderType)                 => Guard(nameof(SetVideoOrder)) ? Rc(nameof(SetVideoOrder), _sdk.SetVideoOrder(videoOrderType)) : -1;
        public int SetDynamicLayoutOption(int layout)                => Guard(nameof(SetDynamicLayoutOption)) ? Rc(nameof(SetDynamicLayoutOption), _sdk.SetDynamicLayoutOption(layout)) : -1;
        public int UpdateVideoLayoutStyle(int videoLayoutStyle)      => Guard(nameof(UpdateVideoLayoutStyle)) ? Rc(nameof(UpdateVideoLayoutStyle), _sdk.UpdateVideoLayoutStyle(videoLayoutStyle)) : -1;
        public int ControlVideoPosition(int position, int size)      => Guard(nameof(ControlVideoPosition)) ? Rc(nameof(ControlVideoPosition), _sdk.ControlVideoPosition(position, size)) : -1;
        public int TurnVideoPage(bool forward, int pageVideoType)    => Guard(nameof(TurnVideoPage)) ? Rc(nameof(TurnVideoPage), _sdk.TurnVideoPage(forward, pageVideoType)) : -1;
        public int ChangeThumbnailsPosition(int type)                => Guard(nameof(ChangeThumbnailsPosition)) ? Rc(nameof(ChangeThumbnailsPosition), _sdk.ChangeThumbnailsPosition(type)) : -1;
        public int SwitchToFloatingShareForSingleScreen(bool floatingShare) => Guard(nameof(SwitchToFloatingShareForSingleScreen)) ? Rc(nameof(SwitchToFloatingShareForSingleScreen), _sdk.SwitchToFloatingShareForSingleScreen(floatingShare)) : -1;

        // ── Recording ─────────────────────────────────────────────────────────

        public bool StartRecording()                               => Guard(nameof(StartRecording)) && Rc(nameof(StartRecording), _sdk.StartRecording());
        public bool StopRecording()                                => Guard(nameof(StopRecording)) && Rc(nameof(StopRecording), _sdk.StopRecording());
        public bool PauseRecording()                               => Guard(nameof(PauseRecording)) && Rc(nameof(PauseRecording), _sdk.PauseRecording());
        public bool ResumeRecording()                              => Guard(nameof(ResumeRecording)) && Rc(nameof(ResumeRecording), _sdk.ResumeRecording());
        public bool ResponseToRecordingRequest(bool accept, bool acceptAlways = false)
            => Guard(nameof(ResponseToRecordingRequest)) && Rc(nameof(ResponseToRecordingRequest), _sdk.ResponseToRecordingRequest(accept, acceptAlways));

        // ── Participants ──────────────────────────────────────────────────────

        public int GetParticipantCount() => _sdk.GetParticipantCount();
        public bool ExpelUser(int userId)  => Guard(nameof(ExpelUser)) && Rc(nameof(ExpelUser), _sdk.ExpelUser(userId));
        public bool AssignHost(int userId) => Guard(nameof(AssignHost)) && Rc(nameof(AssignHost), _sdk.AssignHost(userId));
        public int SetMySelfAsActiveSpeaker()              => Guard(nameof(SetMySelfAsActiveSpeaker)) ? Rc(nameof(SetMySelfAsActiveSpeaker), _sdk.SetMySelfAsActiveSpeaker()) : -1;
        public int SetMyChildAsActiveSpeaker(int userId)   => Guard(nameof(SetMyChildAsActiveSpeaker)) ? Rc(nameof(SetMyChildAsActiveSpeaker), _sdk.SetMyChildAsActiveSpeaker(userId)) : -1;
        public int RenameUser(int userId, string name)     => Guard(nameof(RenameUser)) ? Rc(nameof(RenameUser), _sdk.RenameUser(userId, name)) : -1;

        // ── Share ─────────────────────────────────────────────────────────────

        public bool StopShare() => Guard(nameof(StopShare)) && Rc(nameof(StopShare), _sdk.StopShare());
        public bool LaunchSharingMeeting(bool isInLocalShare, int displayState) => Guard(nameof(LaunchSharingMeeting)) && Rc(nameof(LaunchSharingMeeting), _sdk.LaunchSharingMeeting(isInLocalShare, displayState));
        public bool SwitchFromLocalPresentationToNormalMeeting()                => Guard(nameof(SwitchFromLocalPresentationToNormalMeeting)) && Rc(nameof(SwitchFromLocalPresentationToNormalMeeting), _sdk.SwitchFromLocalPresentationToNormalMeeting());
        public bool ShowSharingInstruction(bool show, int instructionState)     => Guard(nameof(ShowSharingInstruction)) && Rc(nameof(ShowSharingInstruction), _sdk.ShowSharingInstruction(show, instructionState));
        public bool ShareBlackMagic(bool isStart, bool isViewLocally)           => Guard(nameof(ShareBlackMagic)) && Rc(nameof(ShareBlackMagic), _sdk.ShareBlackMagic(isStart, isViewLocally));

        // ── Waiting room ──────────────────────────────────────────────────────

        public bool AdmitUserFromWaitingRoom(int userId)  => Guard(nameof(AdmitUserFromWaitingRoom)) && Rc(nameof(AdmitUserFromWaitingRoom), _sdk.AdmitUserFromWaitingRoom(userId));
        public bool AdmitAllFromWaitingRoom()             => Guard(nameof(AdmitAllFromWaitingRoom)) && Rc(nameof(AdmitAllFromWaitingRoom), _sdk.AdmitAllFromWaitingRoom());
        public bool PutUserInWaitingRoom(int userId)      => Guard(nameof(PutUserInWaitingRoom)) && Rc(nameof(PutUserInWaitingRoom), _sdk.PutUserInWaitingRoom(userId));

        // ── SIP / Phone ───────────────────────────────────────────────────────

        public bool TerminateSipCall(string callId)               => Guard(nameof(TerminateSipCall)) && Rc(nameof(TerminateSipCall), _sdk.TerminateSIPCall(callId));
        public bool CallSip(string uri)                           => Guard(nameof(CallSip)) && Rc(nameof(CallSip), _sdk.CallSIP(uri));
        public bool SendDtmfToSipCall(string dtmf, string callId) => Guard(nameof(SendDtmfToSipCall)) && Rc(nameof(SendDtmfToSipCall), _sdk.SendDTMFToSIPCall(dtmf, callId));
        public bool CallOutPstnUser(string phoneNumber, bool cancelCall, bool hasVoicePrompt) => Guard(nameof(CallOutPstnUser)) && Rc(nameof(CallOutPstnUser), _sdk.CallOutPSTNUser(phoneNumber, cancelCall, hasVoicePrompt));

        // ── Contacts / Directory ──────────────────────────────────────────────

        public bool SubscribeContacts(int startIndex, int count, bool searchSip) => Guard(nameof(SubscribeContacts)) && Rc(nameof(SubscribeContacts), _sdk.SubscribeContacts(startIndex, count, searchSip));
        public bool InviteAttendees(string[] contactIds)                          => Guard(nameof(InviteAttendees)) && Rc(nameof(InviteAttendees), _sdk.InviteAttendees(contactIds));
        public bool MeetWithIMUsers(string[] contactIds)                          => Guard(nameof(MeetWithIMUsers)) && Rc(nameof(MeetWithIMUsers), _sdk.MeetWithIMUsers(contactIds));

        // ── Bookings / Schedule ────────────────────────────────────────────────

        public bool ListMeeting() => Guard(nameof(ListMeeting)) && Rc(nameof(ListMeeting), _sdk.ListMeeting());

        // ── ZRCS ──────────────────────────────────────────────────────────────

        public bool IsZrcsEnabled() => _sdk.IsZRCSEnabled();

        // ── Events ───────────────────────────────────────────────────────────

        public event EventHandler<SdkEventArgs> Initialized;
        public event EventHandler<SdkEventArgs> ConnectionStateChanged;
        public event EventHandler<SdkEventArgs> Error;
        public event EventHandler<SdkEventArgs> PairRoomResult;
        public event EventHandler<SdkEventArgs> MeetingStatusChanged;
        public event EventHandler<SdkEventArgs> InstantMeetingStarted;
        public event EventHandler<SdkEventArgs> StartPmiResult;
        public event EventHandler<SdkEventArgs> ExitMeeting;
        public event EventHandler<SdkEventArgs> MeetingNeedsPassword;
        public event EventHandler<MeetingInviteEventArgs> MeetingInvite;
        public event EventHandler<MeetingInviteTreatedEventArgs> MeetingInviteTreated;
        public event EventHandler<SdkEventArgs> MeetingLockStatusChanged;
        public event EventHandler<SdkEventArgs> AudioMuteStatusChanged;
        public event EventHandler<SdkEventArgs> RecordingStatusChanged;
        public event EventHandler<SdkEventArgs> RecordingRequestReceived;
        public event EventHandler<PromptEventArgs> PromptReceived;
        public event EventHandler<SdkEventArgs> AudioUnmuteRequested;
        public event EventHandler<SdkEventArgs> MuteOnEntryChanged;
        public event EventHandler<SdkEventArgs> AllowAttendeesUnmuteChanged;
        public event EventHandler<SdkEventArgs> AllowAttendeesVideoChanged;
        public event EventHandler<SdkEventArgs> BreakoutStatusChanged;
        public event EventHandler<BORoom[]> BreakoutRoomListUpdated;
        public event EventHandler<BOOptionsInfo> BreakoutOptionsChanged;
        public event EventHandler<SdkEventArgs> BreakoutUserStatusChanged;
        public event EventHandler<SdkEventArgs> BreakoutTimerTick;
        public event EventHandler<BOParticipantListEventArgs> BreakoutParticipantsUpdated;
        public event EventHandler<WebinarAttendeeListEventArgs> WebinarAttendeeListReceived;
        public event EventHandler<WebinarCountsEventArgs> WebinarCountsChanged;
        public event EventHandler<IntegrationEventArgs> IntegrationEvent;
        public event EventHandler<SdkEventArgs> FarEndCameraControlRequested;
        public event EventHandler<MeetingRecordingInfoEventArgs> MeetingRecordingInfoChanged;
        public event EventHandler<StatisticalInfoEventArgs> StatisticalInfoChanged;
        public event EventHandler<CameraPresetInfoEventArgs> CameraPresetInfoChanged;
        public event EventHandler<ParticipantListEventArgs> ParticipantsInitialized;
        public event EventHandler<ParticipantListEventArgs> UserJoined;
        public event EventHandler<ParticipantListEventArgs> UserLeft;
        public event EventHandler<ParticipantListEventArgs> UserUpdated;
        public event EventHandler<SdkEventArgs> ParticipantCountChanged;
        public event EventHandler<SdkEventArgs> HostChanged;
        public event EventHandler<SharingStatusEventArgs> SharingStatusChanged;
        public event EventHandler<AirPlayStatusEventArgs> AirPlayStatusChanged;
        public event EventHandler<VideoPageStatusEventArgs> VideoPageStatusChanged;
        public event EventHandler<ScreenLayoutStatusEventArgs> ScreenLayoutStatusChanged;
        public event EventHandler<SdkEventArgs> DynamicLayoutOptionChanged;
        public event EventHandler<SdkEventArgs> LayoutDiagnostic;
        public event EventHandler<VideoThumbInfoEventArgs> VideoThumbInfoChanged;
        public event EventHandler<SIPCall> SipCallStatusChanged;
        public event EventHandler<SdkEventArgs> ZrcsEnabledChanged;
        public event EventHandler<ContactListEventArgs> ContactListChanged;
        public event EventHandler<MeetingListEventArgs> MeetingListChanged;

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelReconnect();
            _storedPairingFallbackTimer?.Dispose();
            _storedPairingFallbackTimer = null;
            CrestronEnvironment.ProgramStatusEventHandler -= OnProgramStatusEvent;
            try
            {
                _sdk.Dispose();
                this.LogInformation("ZRC SDK disposed.");
            }
            catch (Exception ex)
            {
                this.LogError("Error disposing ZRC SDK: {Message}", ex.Message);
            }
        }

        private void ScheduleReconnect()
        {
            if (_disposed) return;
            // A single self-perpetuating loop; extra triggers (SDK event, poll, command failures) no-op.
            if (_reconnectTimer != null) return;
            ScheduleNextReconnect();
        }

        private void ScheduleNextReconnect()
        {
            if (_disposed) return;
            if (!_sdk.CanRetryToPairLastRoom())
            {
                this.LogWarning("Disconnected and no stored pairing credentials — cannot auto-reconnect.");
                _reconnectTimer?.Dispose();
                _reconnectTimer = null;
                return;
            }

            _reconnectAttempt++;
            // Escalating backoff for the first few tries, then hold at the max delay indefinitely.
            // Never give up: an unattended room must self-heal whenever it becomes reachable again.
            var delayMs = ReconnectDelaysMs[Math.Min(_reconnectAttempt - 1, ReconnectDelaysMs.Length - 1)];
            this.LogInformation("Disconnected — reconnect attempt {Attempt} in {Delay}ms", _reconnectAttempt, delayMs);

            _reconnectTimer?.Dispose();
            _reconnectTimer = new CTimer(_ =>
            {
                if (_disposed) return;
                this.LogInformation("Auto-reconnect attempt {Attempt}: calling RetryToPairRoom()", _reconnectAttempt);
                _lastPairUsedStoredCredentials = true;
                _sdk.RetryToPairRoom();
                // A successful pair fires ConnectionStateChanged(Connected) -> CancelReconnect() stops
                // this loop. If it didn't (silent failure), keep retrying at the capped delay.
                if (!_disposed && !_isConnected) ScheduleNextReconnect();
            }, null, delayMs);
        }

        private void CancelReconnect()
        {
            _reconnectAttempt = 0;
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;
        }

        // ── Health watchdog ─────────────────────────────────────────────────────

        // Central choke point for every SDK command result (from the Rc helpers). While we believe
        // we're connected, a run of failures is the signature of a silent/half-open drop, so probe.
        private void NoteCommandResult(bool success)
        {
            if (_disposed) return;
            if (success) { _consecutiveCommandFailures = 0; return; }

            // Guard() blocks commands when we already know we're offline, so a failure reaching here
            // means the SDK still reports connected — exactly the stale-state case we want to catch.
            if (!_isConnected) return;

            _consecutiveCommandFailures++;
            if (_consecutiveCommandFailures >= CommandFailureStrikeThreshold)
            {
                this.LogWarning("{Count} consecutive SDK command failures while marked connected — running health check.", _consecutiveCommandFailures);
                RunHealthCheck("consecutive command failures");
            }
        }

        public void RunHealthCheck(string reason)
        {
            // Only guards an established pairing; before the first connect the pairing flow handles it.
            if (_disposed || !_everConnected) return;

            lock (_healthLock)
            {
                if (_healthCheckRunning) return;
                _healthCheckRunning = true;
            }

            try
            {
                // Real round-trip probe: returns null when the link is actually dead, even if the SDK's
                // connection flag is stale.
                var alive = _sdk.GetMeetingStatus().HasValue;
                if (alive)
                {
                    _consecutiveCommandFailures = 0;
                    if (!_isConnected)
                    {
                        // Link recovered without an SDK event — restore online and let the reconnect
                        // loop's RetryToPairRoom fire a real Connected event for full re-seed.
                        this.LogInformation("Health probe succeeded while offline ({Reason}) — marking connected.", reason);
                        _isConnected = true;
                        SafeRaise(() => HealthStateChanged?.Invoke(this, true));
                    }
                    return;
                }

                this.LogWarning("Health probe failed ({Reason}) — link is down.", reason);
                DeclareOffline(reason);
            }
            catch (Exception ex)
            {
                this.LogError(ex, "Exception during health check ({Reason}): {Message}", reason, ex.Message);
                DeclareOffline(reason);
            }
            finally
            {
                lock (_healthLock) { _healthCheckRunning = false; }
            }
        }

        private void DeclareOffline(string reason)
        {
            _consecutiveCommandFailures = 0;
            if (_isConnected)
            {
                _isConnected = false;
                this.LogWarning("Marking Zoom Room offline ({Reason}); starting auto-repair.", reason);
                SafeRaise(() => HealthStateChanged?.Invoke(this, false));
            }
            ScheduleReconnect();
        }
            /// <summary>SHA-256 compare of a staged file against an embedded resource stream; leaves the stream at position 0.</summary>
        private static bool SameContent(string path, Stream resource)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] fileHash;
            using (var f = File.OpenRead(path)) fileHash = sha.ComputeHash(f);
            resource.Position = 0;
            var resHash = sha.ComputeHash(resource);
            resource.Position = 0;
            return fileHash.AsSpan().SequenceEqual(resHash);
        }
}
}
