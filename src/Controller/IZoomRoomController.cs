using System;
using System.Collections.Generic;
using PepperDash.ZoomRoom.Sdk;
using PepperDash.ZoomRoom.Sdk.EventArgs;

namespace PepperDash.Essentials.Plugins
{
    /// <summary>
    /// Abstraction over the ZRC SDK surface used by <see cref="ZoomRoom"/>.
    /// Implemented by <see cref="ZrcSdkController"/> for production use; can be
    /// replaced with a test double in unit tests.
    /// </summary>
    public interface IZoomRoomController : IDisposable
    {
        // ── Lifecycle ─────────────────────────────────────────────────────────

        /// <summary>Initialize the SDK with the given config directory path.</summary>
        bool Initialize(string configPath);

        /// <summary>Returns the current connection state (0=Established, 1=Connected, 2=Disconnected).</summary>
        int GetConnectionState();

        /// <summary>
        /// Raised with the watchdog's assessment of whether the room is actually reachable (true=online).
        /// Distinct from <see cref="ConnectionStateChanged"/>: it fires on silent/half-open drops the SDK
        /// never reports, so devcomm can be corrected and auto-repair triggered.
        /// </summary>
        event EventHandler<bool> HealthStateChanged;

        /// <summary>
        /// Actively probes the link (real SDK round-trip). If the room is unreachable it marks the
        /// controller offline and starts auto-repair; if a prior offline was a false alarm it clears it.
        /// Safe to call periodically (comms-monitor poll) or on demand (after command failures).
        /// </summary>
        void RunHealthCheck(string reason);

        /// <summary>
        /// Synchronously queries the current meeting status. Unlike <see cref="MeetingStatusChanged"/>,
        /// this does not require a status change to have occurred - call it once connected to pick up a
        /// meeting that was already in progress before the SDK callbacks were registered.
        /// </summary>
        /// <returns>The current meeting status, or null if the query failed.</returns>
        MeetingStatus? GetMeetingStatus();

        /// <summary>
        /// Synchronously queries the meeting this room is in (number, name, ...). Needed for a meeting
        /// the room joined rather than started: the start-meeting callbacks are the only other source
        /// of the meeting number.
        /// </summary>
        /// <returns>False when not in a meeting or the query failed.</returns>
        bool TryGetMeetingInfo(out CurrentMeetingInfo info);

        /// <summary>Whether the breakout room feature is enabled in the current meeting.</summary>
        /// <returns>False when not in a meeting or the query failed.</returns>
        bool TryGetBreakoutEnabled(out bool enabled);

        // ── Pairing ───────────────────────────────────────────────────────────

        bool PairWithActivationCode(string activationCode);
        bool CanRetryPair();
        bool RetryPair();
        bool Unpair();

        /// <summary>
        /// Re-pairs using the activation code from configuration, keeping the stored pairing so a
        /// rejected code leaves the room as it was. An activation code works only once.
        /// </summary>
        bool RepairWithConfiguredCode();

        // ── Device ────────────────────────────────────────────────────────────

        bool WakeUp();
        bool Logout();
        bool RestartOs();

        // ── Meeting ───────────────────────────────────────────────────────────

        bool StartMeeting(string meetingNumber);
        bool StartInstantMeeting();
        bool StartMeetingWithHostKey(string hostKey);
        bool JoinMeeting(string meetingNumber);
        bool JoinMeetingWithPassword(string meetingNumber, string password);
        bool JoinMeetingWithUrl(string url);
        bool EndMeeting();
        bool LeaveMeeting();

        /// <summary>Accepts (true) or declines (false) the incoming meeting invite from the last MeetingInvite event.</summary>
        bool AnswerMeetingInvite(bool accept);
        bool SendMeetingPassword(string password);
        bool CancelEnteringMeetingPassword();
        bool CancelWaitingForHost();
        bool LockMeeting(bool locked);

        // ── Audio ─────────────────────────────────────────────────────────────

        bool SetAudioMute(bool mute);
        bool MuteUserAudio(int userId, bool mute);
        bool MuteAllAudio(bool mute);
        bool SetMuteOnEntry(bool mute);
        bool AnswerUnmuteRequest(bool accepted);
        bool AllowAttendeesUnmute(bool allow);
        bool AllowAttendeesStartVideo(bool allow);

        // ── In-call prompt answers (see ZoomRoom.Prompts.cs) ─────────────────
        /// <summary>Answers a meeting reminder (native MeetingReminderType value).</summary>
        bool ConfirmMeetingReminder(bool agree, int reminderType);
        bool ConfirmCustomizedMeetingReminder(bool agree, int customizedType);
        /// <summary>Answers a consent (native ConsentType value; consentId for the Common type).</summary>
        bool ConfirmConsent(bool agree, int consentType, string consentId);
        bool ConfirmCombinedConsent(bool agree, long consentType);
        /// <summary>Native PrivacyAlertAction / PrivacyAlertType values.</summary>
        bool HandlePrivacyAlert(int action, int type);
        bool ContinueMeetingOnInactivity();
        bool AnswerHostRequestUnmuteVideo(bool accepted);
        bool RespondRemoteCameraControl(int userId, bool accept);
        bool ResponseHostInviteToMainSession(bool accept);
        bool JoinBreakoutRoom();
        bool StartBreakoutRooms();
        bool StopBreakoutRooms();
        bool BroadcastMessageToBreakoutRooms(string message);
        bool LeaveBreakoutRoom();
        bool AskForHelpInBreakoutRoom();

        // ── Breakout rooms: creator / admin / data ────────────────────────────
        bool CreateBreakoutRooms(int count, int assignType);
        bool AddBreakoutRoom();
        bool DeleteBreakoutRoom(string sessionBID);
        bool RenameBreakoutRoom(string sessionBID, string newName);
        bool AssignUsersToBreakoutRoom(IEnumerable<string> userGuids, string sessionBID);
        bool SetBOOptions(BOOptionsInfo options);
        bool RequestBOOptions();
        bool MoveUserToBreakoutRoom(string userGuid, string sessionBID);
        bool InviteBOUserReturnToMainSession(string userGuid);
        bool IgnoreBOHelpRequest(string userGuid);
        bool JoinBreakoutRoomForHelp(string userGuid, string sessionBID, string sessionName);
        bool JoinBreakoutRoomByBID(string sessionBID);
        bool RequestBreakoutRoomList();
        bool RequestBreakoutRoomUserList();

        // ── Roles ─────────────────────────────────────────────────────────────
        bool ClaimHost(string hostKey);
        bool AssignCohost(int userId, bool assign);
        bool PromoteAttendeeToPanelist(int userId);
        bool DemotePanelistToAttendee(int userId);
        bool AllowWebinarAttendeeTalk(int userId, bool allow);
        /// <summary>Asks for webinar attendees ("" = the first 100, otherwise a name search); answers on <see cref="WebinarAttendeeListReceived"/>.</summary>
        bool ListWebinarAttendees(string keywords);
        /// <summary>Whether the current meeting is a webinar; null when there is no meeting or the query failed.</summary>
        bool? IsWebinarMeeting();

        /// <summary>Sets the room speaker (audio output) volume, in the SDK's native float scale.</summary>
        bool SetSpeakerVolume(float volume);

        /// <summary>Gets the room speaker (audio output) volume in the SDK's native float scale; returns -1 on failure.</summary>
        float GetSpeakerVolume();

        /// <summary>Sets the room microphone (audio input) volume, in the SDK's native float scale.</summary>
        bool SetMicrophoneVolume(float volume);

        /// <summary>Gets the room microphone (audio input) volume in the SDK's native float scale; returns -1 on failure.</summary>
        float GetMicrophoneVolume();

        /// <summary>Turns the Room PC's statistics (CPU and memory use) on or off; while on they arrive on <see cref="StatisticalInfoChanged"/>.</summary>
        bool EnableStatisticalInfo(bool enable);

        /// <summary>
        /// The paired Zoom Room's name, display name and IP address; null when the SDK isn't paired or doesn't
        /// answer. Call it outside an SDK callback.
        /// </summary>
        PairedRoomInfo GetLastZoomRoomInfo();

        // ── Video ─────────────────────────────────────────────────────────────

        bool SetVideoState(bool start);

        /// <summary>Hides or shows the room's own self video locally (does not stop video to the far end).</summary>
        bool SetMyVideoHidden(bool hidden);

        bool MuteUserVideo(int userId, bool mute);
        bool PinUserOnScreen(int userId, int screenIndex = 0);
        bool UnpinUserFromScreen(int userId, int screenIndex = 0);

        /// <summary>Spotlights a participant for everyone in the meeting. Host / co-host.</summary>
        bool SpotlightUser(int userId);

        /// <summary>Removes a participant's spotlight. Host / co-host.</summary>
        bool CancelSpotlightUser(int userId);

        /// <summary>Controls a far-end (participant) camera. action = CameraControlAction, type = CameraControlType (Start/Continue/Stop).</summary>
        bool ControlUserCamera(int userId, int action, int type);

        /// <summary>Controls a local camera. deviceId = camera device ID ("" = main/near-end camera). action = CameraControlAction, type = CameraControlType.</summary>
        bool ControlCamera(string deviceId, int action, int type);

        /// <summary>Sets the smart/auto camera framing mode. mask = SmartCameraMask, deviceId = "" for the main camera.</summary>
        bool ChangeSmartCameraMode(int mask, string deviceId = "");

        /// <summary>Enumerates the room's local cameras (device list); empty array if none/unavailable.</summary>
        CameraDevice[] GetCameras();

        /// <summary>Gets the currently selected/active camera, or null if unavailable.</summary>
        CameraDevice GetCurrentCamera();

        /// <summary>Renames a camera in the Zoom Room's settings (persistent). Returns the SDK code (141 = name can't be set).</summary>
        int SetCameraDisplayName(string deviceId, string displayName);

        /// <summary>
        /// Selects or deselects a camera for multi-camera (persistent Zoom Room setting), adding or
        /// removing its tile in a meeting. Returns the SDK code (136/137/138 = multi-camera refusals).
        /// </summary>
        int SelectMultipleCamera(string deviceId, bool selected);

        // ── Third-party (integration) meetings ────────────────────────────────

        /// <summary>Joins a Teams / Webex / Google (...) meeting by ID. Returns the SDK code (0 = request accepted).</summary>
        int JoinIntegrationMeeting(string meetingId, string password, ThirdPartyMeetingProvider provider);
        int LeaveIntegrationMeeting();
        int MuteIntegrationAudio(bool mute);
        int StopIntegrationVideo(bool stop);
        /// <summary>Whether and how the room may join the provider's meetings; null when the SDK can't say.</summary>
        InterOperabilityInfo GetInterOperabilityInfo(ThirdPartyMeetingProvider provider);

        /// <summary>Selects the active local camera by device ID.</summary>
        bool SetCurrentCamera(string deviceId);

        /// <summary>Saves the current position to a camera preset slot (index 0–2). Empty deviceId = main camera.</summary>
        bool SetCameraPreset(uint index, string deviceId);
        /// <summary>Recalls a camera preset slot (index 0–2). Empty deviceId = main camera.</summary>
        bool GoToCameraPreset(uint index, string deviceId);
        /// <summary>Names a camera preset slot (index 0–2). Empty deviceId = main camera.</summary>
        bool NameCameraPreset(uint index, string name, string deviceId);

        // ── Layout ────────────────────────────────────────────────────────────

        int SetScreenLayout(int screen, int layoutSourceType);
        int SetVideoOrder(int videoOrderType);

        /// <summary>Sets the dynamic-layout sub-option within Dynamic View (DynamicLayoutType: SpeakersOnBottom=0/Middle=1/Top=2). On single-screen rooms this distinguishes Dynamic Gallery from Multi-Speaker.</summary>
        int SetDynamicLayoutOption(int layout);

        /// <summary>Sets the meeting video layout style (VideoLayoutStyle: Gallery=1, Speaker=2, Thumbnail=3, ContentOnly=4, DynamicLayout=6). Distinct from SetVideoOrder, which only reorders tiles.</summary>
        int UpdateVideoLayoutStyle(int videoLayoutStyle);

        /// <summary>Sets the self-view PiP position and size. position = VideoThumbPosition, size = VideoThumbSize (Off=0 hides the PiP).</summary>
        int ControlVideoPosition(int position, int size);

        /// <summary>Pages the gallery/thumbnail/dynamic video view. pageVideoType = PageVideoType (GalleryView=0, ThumbnailView=1, DynamicLayoutView=2).</summary>
        int TurnVideoPage(bool forward, int pageVideoType);

        /// <summary>Changes the thumbnail strip position (top/bottom). type = ThumbnailsPositionType.</summary>
        int ChangeThumbnailsPosition(int type);

        /// <summary>Swaps shared content with the participant video on a single screen. floatingShare = float the share (video full-screen).</summary>
        int SwitchToFloatingShareForSingleScreen(bool floatingShare);

        // ── Recording ─────────────────────────────────────────────────────────

        bool StartRecording();
        bool StopRecording();
        bool PauseRecording();
        bool ResumeRecording();
        bool ResponseToRecordingRequest(bool accept, bool acceptAlways = false);

        // ── Participants ──────────────────────────────────────────────────────

        int GetParticipantCount();

        /// <summary>Expels (removes) a participant from the meeting. Host only.</summary>
        bool ExpelUser(int userId);

        /// <summary>Assigns the host role to a participant. Host only.</summary>
        bool AssignHost(int userId);

        /// <summary>Multi-camera: makes the room's main video the active speaker. Returns the SDK code.</summary>
        int SetMySelfAsActiveSpeaker();

        /// <summary>Multi-camera: makes one of the room's own camera tiles (a child participant) the active speaker. Returns the SDK code.</summary>
        int SetMyChildAsActiveSpeaker(int userId);

        /// <summary>Renames a participant for the current meeting, e.g. one of the room's camera tiles. Returns the SDK code.</summary>
        int RenameUser(int userId, string name);

        // ── Share ─────────────────────────────────────────────────────────────

        bool StopShare();

        /// <summary>Launches a sharing-only ("local presentation") meeting. displayState = SharingInstructionDisplayState.</summary>
        bool LaunchSharingMeeting(bool isInLocalShare, int displayState);

        /// <summary>Switches an active local presentation into a normal Zoom meeting.</summary>
        bool SwitchFromLocalPresentationToNormalMeeting();

        /// <summary>Shows/hides the wireless-share instruction overlay. instructionState = SharingInstructionDisplayState.</summary>
        bool ShowSharingInstruction(bool show, int instructionState);

        /// <summary>Starts/stops an HDMI ("black magic") cable share. isViewLocally = also show the source locally.</summary>
        bool ShareBlackMagic(bool isStart, bool isViewLocally);

        // ── Waiting room ──────────────────────────────────────────────────────

        bool AdmitUserFromWaitingRoom(int userId);
        bool AdmitAllFromWaitingRoom();
        bool PutUserInWaitingRoom(int userId);

        // ── SIP / Phone ───────────────────────────────────────────────────────

        bool TerminateSipCall(string callId);

        /// <summary>Places an outbound SIP call to the given URI / number.</summary>
        bool CallSip(string uri);

        /// <summary>Sends DTMF digits to an active SIP call (empty callId targets the single active call).</summary>
        bool SendDtmfToSipCall(string dtmf, string callId);

        /// <summary>
        /// Dials out a PSTN phone number into the current meeting. <paramref name="cancelCall"/> cancels an
        /// in-progress call-out; <paramref name="hasVoicePrompt"/> rings the call on the Zoom Room.
        /// </summary>
        bool CallOutPstnUser(string phoneNumber, bool cancelCall, bool hasVoicePrompt);

        // ── Contacts / Directory ──────────────────────────────────────────────

        /// <summary>
        /// Subscribes to a range of directory contacts. Results arrive via <see cref="ContactListChanged"/>.
        /// <paramref name="searchSip"/> requests SIP contacts instead of IM contacts.
        /// </summary>
        bool SubscribeContacts(int startIndex, int count, bool searchSip);

        /// <summary>Invites the given contacts (by contact ID) into the CURRENT meeting.</summary>
        bool InviteAttendees(string[] contactIds);

        /// <summary>Starts a NEW meeting with the given contacts (by contact ID).</summary>
        bool MeetWithIMUsers(string[] contactIds);

        // ── Bookings / Schedule ────────────────────────────────────────────────

        /// <summary>
        /// Requests the current list of scheduled meetings (calendar bookings).
        /// Results arrive via <see cref="MeetingListChanged"/>.
        /// </summary>
        bool ListMeeting();

        // ── ZRCS ──────────────────────────────────────────────────────────────

        bool IsZrcsEnabled();

        // ── Events ───────────────────────────────────────────────────────────

        event EventHandler<SdkEventArgs> Initialized;
        event EventHandler<SdkEventArgs> ConnectionStateChanged;
        event EventHandler<SdkEventArgs> Error;
        event EventHandler<SdkEventArgs> PairRoomResult;
        event EventHandler<SdkEventArgs> MeetingStatusChanged;
        event EventHandler<SdkEventArgs> InstantMeetingStarted;
        event EventHandler<SdkEventArgs> StartPmiResult;
        event EventHandler<SdkEventArgs> ExitMeeting;
        event EventHandler<SdkEventArgs> MeetingNeedsPassword;
        event EventHandler<MeetingInviteEventArgs> MeetingInvite;
        /// <summary>Fires when a pending meeting invite is resolved -- answered here, answered elsewhere, declined, or expired/cancelled by the caller.</summary>
        event EventHandler<MeetingInviteTreatedEventArgs> MeetingInviteTreated;
        event EventHandler<SdkEventArgs> MeetingLockStatusChanged;
        event EventHandler<SdkEventArgs> AudioMuteStatusChanged;
        event EventHandler<SdkEventArgs> RecordingStatusChanged;
        event EventHandler<SdkEventArgs> RecordingRequestReceived;
        /// <summary>Dialog-style notification from the SDK (reminder, consent, request, invite, role change).</summary>
        event EventHandler<PromptEventArgs> PromptReceived;
        /// <summary>The host asked this room to unmute its audio; answer with <see cref="AnswerUnmuteRequest"/>.</summary>
        event EventHandler<SdkEventArgs> AudioUnmuteRequested;
        /// <summary>Mute-on-entry setting changed (ErrorCode 1 = on).</summary>
        event EventHandler<SdkEventArgs> MuteOnEntryChanged;
        /// <summary>Whether attendees may unmute themselves changed (ErrorCode 1 = allowed).</summary>
        event EventHandler<SdkEventArgs> AllowAttendeesUnmuteChanged;
        /// <summary>Whether attendees may start video changed (ErrorCode 1 = allowed).</summary>
        event EventHandler<SdkEventArgs> AllowAttendeesVideoChanged;
        /// <summary>Breakout session status changed (ErrorCode = BO_STATUS: 1 edit, 2 started, 3 stopping, 4 ended).</summary>
        event EventHandler<SdkEventArgs> BreakoutStatusChanged;
        event EventHandler<BORoom[]> BreakoutRoomListUpdated;
        event EventHandler<BOOptionsInfo> BreakoutOptionsChanged;
        /// <summary>This room's own breakout status (ErrorCode = BO_USER_STATUS, Message = joined room BID).</summary>
        event EventHandler<SdkEventArgs> BreakoutUserStatusChanged;
        /// <summary>Breakout timer tick (ErrorCode = remaining seconds).</summary>
        event EventHandler<SdkEventArgs> BreakoutTimerTick;
        event EventHandler<BOParticipantListEventArgs> BreakoutParticipantsUpdated;
        /// <summary>Webinar attendees - they are not part of the meeting roster.</summary>
        event EventHandler<WebinarAttendeeListEventArgs> WebinarAttendeeListReceived;
        /// <summary>Webinar head counts (attendees, raised hands, panelists; -1 = not in this notification).</summary>
        event EventHandler<WebinarCountsEventArgs> WebinarCountsChanged;

        /// <summary>Third-party (Teams, Webex, Google...) meeting events: start result, state, errors, support flags.</summary>
        event EventHandler<IntegrationEventArgs> IntegrationEvent;
        /// <summary>A participant asked to control this room's camera (ErrorCode = userId); answer with <see cref="RespondRemoteCameraControl"/>.</summary>
        event EventHandler<SdkEventArgs> FarEndCameraControlRequested;
        event EventHandler<MeetingRecordingInfoEventArgs> MeetingRecordingInfoChanged;
        /// <summary>The Room PC's statistics, sent while <see cref="EnableStatisticalInfo"/> is on.</summary>
        event EventHandler<StatisticalInfoEventArgs> StatisticalInfoChanged;
        event EventHandler<CameraPresetInfoEventArgs> CameraPresetInfoChanged;
        event EventHandler<ParticipantListEventArgs> ParticipantsInitialized;
        event EventHandler<ParticipantListEventArgs> UserJoined;
        event EventHandler<ParticipantListEventArgs> UserLeft;
        event EventHandler<ParticipantListEventArgs> UserUpdated;
        event EventHandler<SdkEventArgs> ParticipantCountChanged;
        event EventHandler<SdkEventArgs> HostChanged;
        event EventHandler<SharingStatusEventArgs> SharingStatusChanged;
        event EventHandler<AirPlayStatusEventArgs> AirPlayStatusChanged;
        event EventHandler<VideoPageStatusEventArgs> VideoPageStatusChanged;
        event EventHandler<ScreenLayoutStatusEventArgs> ScreenLayoutStatusChanged;
        event EventHandler<SdkEventArgs> DynamicLayoutOptionChanged;
        event EventHandler<SdkEventArgs> LayoutDiagnostic;
        event EventHandler<VideoThumbInfoEventArgs> VideoThumbInfoChanged;
        event EventHandler<SIPCall> SipCallStatusChanged;
        event EventHandler<SdkEventArgs> ZrcsEnabledChanged;
        event EventHandler<ContactListEventArgs> ContactListChanged;
        event EventHandler<MeetingListEventArgs> MeetingListChanged;
    }
}
