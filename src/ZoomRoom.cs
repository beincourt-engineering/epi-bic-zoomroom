using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.CrestronThread;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.AppServer.Messengers;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;
using PepperDash.Essentials.Core.Presets;
using PepperDash.Essentials.Core.Queues;
using PepperDash.Essentials.Core.Routing;
using PepperDash.Essentials.Devices.Common.Cameras;
using PepperDash.Essentials.Devices.Common.Codec;
using PepperDash.Essentials.Devices.Common.VideoCodec;
using PepperDash.Essentials.Devices.Common.VideoCodec.Interfaces;
using PepperDash.ZoomRoom.Sdk;
using PepperDash.ZoomRoom.Sdk.EventArgs;
// Crestron.SimplSharpPro.DeviceSupport has a Feedback type too.
using Feedback = PepperDash.Essentials.Core.Feedback;

namespace PepperDash.Essentials.Plugins
{
	public partial class ZoomRoom : VideoCodecBase, IHasCodecSelfView, IHasDirectoryHistoryStack, ICommunicationMonitor,
		IHasScheduleAwareness, IHasCodecCameras, IHasParticipants, IHasCameraOff, IHasCameraMuteWithUnmuteReqeust, IHasCameraAutoMode,
		IHasFarEndContentStatus, IHasSelfviewPosition, IHasPhoneDialing, IHasZoomRoomLayouts, IHasParticipantPinUnpin,
		IHasParticipantAudioMute, IHasSelfviewSize, IPasswordPrompt, IHasStartMeeting, IHasMeetingInfo, IHasPresentationOnlyMeeting,
		IHasMeetingLock, IHasMeetingRecordingWithPrompt, IZoomWirelessShareInstructions, IHasCodecRoomPresets, IRoutingSinkWithFeedback,
		IHasFeedback
	{
#pragma warning disable CS0067 // Required by IHasCameraMuteWithUnmuteReqeust; never raised because Zoom Room SDK handles video state directly
		public event EventHandler VideoUnmuteRequested;
#pragma warning restore CS0067

		private const long MeetingRefreshTimer = 60000;
		public uint DefaultMeetingDurationMin { get; private set; }

		// ── SDK-backed state (replaces the old zStatus/zConfiguration model) ──
		private readonly IZoomRoomController _controller;
		private bool _sdkAudioMuted;
		private bool _sdkCameraOff;
		private bool _cameraAutoModeOn; // tracks SmartCameraMask: SpeakerFocus(auto) vs Manual
		private bool _sdkIsRecording;
		private bool _sdkCanRecord; // room "can start recording" — from MeetingRecordingInfo.canIRecord
		private bool _sdkMeetingLocked;
		private bool _sdkIsHost;
		private bool _sdkIsCoHost;
		private int _sdkSharingState; // 0 = not sharing

		// True only while this plugin has explicitly asked to share the room's HDMI/BlackMagic
		// content (via StartSharing). The Zoom Room appliance can auto-start that same share on its
		// own the moment it senses a signal on the content HDMI input, with no call into this class
		// at all - this room's presentation content is meant to reach the call only through the
		// room's own routing, never the codec's native auto-share. Set true in StartSharing, false
		// in StopSharing/on disconnect, and checked in OnControllerAirPlayStatusChanged to reverse
		// any share this plugin did not ask for.
		private bool _weRequestedHdmiSharing;

		// True once the participant roster has confirmed our own entry (IsMyself) for the current
		// join attempt. The ZRC SDK reports MeetingStatus.InMeeting the instant the room joins the
		// Zoom session infrastructure - which happens BEFORE a host admits it from a waiting room -
		// and can then cycle Exit/ConnectingToMeeting/Exit for several more seconds before real
		// admission (confirmed via live testing: zero participant data logged for 10+ seconds after
		// the first InMeeting, then an Exit/reconnect cycle, then the real InMeeting with roster data
		// following ~3s later). The roster is the one reliable "actually admitted" signal, so
		// ApplyMeetingStatus only marks a call Connected once this is true - see
		// BeginPendingRosterAdmission/ConfirmRosterAdmission/RefreshRosterAdmissionFromParticipants.
		private bool _hasConfirmedRosterAdmission;
		// True from the moment a join/start is issued until roster admission is confirmed or the
		// timeout below fires. While true, ExitMeeting/NotInMeeting notifications are treated as
		// transient noise from the pre-admission dance rather than a real disconnect, so the UI
		// doesn't bounce back to idle mid-join.
		private bool _isPendingRosterAdmission;
		private CTimer _pendingRosterAdmissionTimeoutTimer;
		private CTimer _connectTimeSeedAdmissionTimer;
		private const int PendingRosterAdmissionTimeoutMs = 60000;
		private const int ConnectTimeSeedAdmissionGraceMs = 10000;
									  // Typed reference to avoid downcasting CommunicationMonitor at every call site (#26)
		private SdkConnectionMonitor _sdkMonitor;
		// (best-effort) to drive ToggleParticipantPinState. Keyed by userId -> screenIndex.
		private readonly Dictionary<int, int> _pinnedUserScreens = new Dictionary<int, int>();
		// Ringing incoming meeting-invite, surfaced as an ActiveCall so the standard Accept/Reject
		// path works. Answered via AnswerMeetingInvite (the native side cached the full invite).
		private CodecActiveCallItem _pendingInviteCall;
		// Fallback safety net: the SDK only notifies of a resolved invite via MeetingInviteTreated
		// (answered/declined/expired elsewhere) -- a silently-ignored invite gets no such notification,
		// so this timer drops it from ActiveCalls after MeetingInviteTimeoutMs regardless.
		private CTimer _pendingInviteTimeoutTimer;
		private const int DefaultMeetingInviteTimeoutMs = 45000;
		private string _currentMeetingId = string.Empty;
		private string _currentMeetingNumber = string.Empty;
		private string _currentMeetingName = string.Empty;
		private string _activeSipCallId = string.Empty;
		private bool _meetingPasswordRequired;
		// Layout page state, driven by the SDK's VideoPageStatus notification.
		private bool _layoutIsOnFirstPage;
		private bool _layoutIsOnLastPage;
		private int _currentPageVideoType; // PageVideoType (0 = GalleryView)
		private bool _contentSwappedWithThumbnail;
		private string _currentVideoOrder = "Default";
		private string _currentThumbnailsPosition = "Bottom";
		// Screen layout status, driven by the SDK's ScreenLayoutStatus notification.
		private ScreenLayoutStatusEventArgs _screenLayoutStatus;
		// Self-view PiP support/state, driven by the SDK's VideoThumbInfo notification.
		private bool _sdkSelfviewThumbSupported = true;
		// Hide-self-video (IHasCodecSelfView on/off) state. The ZRC SDK exposes no query, so this is
		// plugin-cached and reflects the last SetMyVideoHidden this plugin issued.
		private bool _selfVideoHidden;
		// Room speaker (audio output) volume state. Level is the Essentials 0-65535 range.
		private ushort _sdkSpeakerVolumeLevel;
		private bool _sdkSpeakerMuted;

		// True once the SDK reports the room Connected/Established. Gates outbound command methods so
		// join/start/invite actions issued before the room is paired/connected are dropped with a clear
		// log entry instead of silently failing inside the SDK (the old ZoomRoomSyncState used to gate this).
		private volatile bool _isConnected;

		private readonly object _participantLock = new object();
		// Raw SDK participant info keyed by userID, kept in sync with Participants.CurrentParticipants.
		// Carries the far-end camera-control flags the Essentials Participant type does not, so
		// UpdateFarEndCameras() can discover which participants expose controllable cameras.
		private readonly Dictionary<int, ParticipantInfo> _participantInfoByUserId = new Dictionary<int, ParticipantInfo>();
		// Directory/phonebook contacts accumulated from the SDK contact subscription (R-D), keyed by
		// contact ID. The subscription delivers contacts in pages; merging here lets DirectoryRoot be
		// rebuilt from the union of all batches received.
		private readonly object _directoryLock = new object();
		private readonly Dictionary<string, ContactInfo> _directoryContactsById = new Dictionary<string, ContactInfo>();
		// Pagination state for phonebook downloads. The ZRC SDK contact subscribe is range-based;
		// each SubscribeContacts(start, count) only notifies for that window. We keep subscribing
		// to successive windows while a page comes back full (== PhonebookPageSize).
		private const int PhonebookPageSize = 50;
		private int _phonebookNextStart;
		// The native SDK gives no reliable "this is definitely the last batch" signal for a directory
		// fetch: both genuine paged responses and unrelated ambient IM/presence deltas can arrive as
		// several progressively-larger batches for what is conceptually one fetch (observed in the
		// field as e.g. 2, then 23, then 40 contacts trickling in). Rather than guessing completion from
		// a single batch's size, debounce: only finalize/publish once no further ContactListChanged has
		// arrived for PhonebookSettleMs (#contacts-pagination).
		private CTimer _phonebookSettleTimer;
		private readonly object _phonebookSettleLock = new object();
		private const int PhonebookSettleMs = 1500;
		private IHasCameraControls _selectedCamera;
		private CodecDirectory _currentDirectoryResult;

		private readonly ZoomRoomPropertiesConfig _props;

		// Contact names from hiddenContactNames: kept out of the directory, never invited, and
		// declined automatically when they invite this room.
		private readonly HashSet<string> _hiddenContactNames;

		public ZoomRoom(DeviceConfig config, IZoomRoomController controller, ZoomRoomPropertiesConfig props)
			: base(config)
		{
			DefaultMeetingDurationMin = 30;

			_props = props;

			_hiddenContactNames = new HashSet<string>(
				(props.HiddenContactNames ?? new List<string>())
					.Where(n => !string.IsNullOrWhiteSpace(n))
					.Select(n => n.Trim()),
				StringComparer.OrdinalIgnoreCase);

			_controller = controller;

			Status = new ZoomRoomStatus();
			Configuration = new ZoomRoomConfiguration();

			_sdkMonitor = new SdkConnectionMonitor(this, () => _controller.RunHealthCheck("scheduled poll"));
			CommunicationMonitor = _sdkMonitor;
			DeviceManager.AddDevice(CommunicationMonitor);

			CodecInfo = new ZoomRoomInfo(Status, Configuration);

			PhonebookSyncState = new CodecPhonebookSyncState(Key + "--PhonebookSync");

			ContentInput1 = new RoutingInputPort(RoutingPortNames.HdmiIn1,
				eRoutingSignalType.Audio | eRoutingSignalType.Video,
				eRoutingPortConnectionType.Hdmi, null, this);

			CamInput1 = new RoutingInputPort("usbIn1",
				eRoutingSignalType.Video, eRoutingPortConnectionType.UsbC, null, this);

			CamInput2 = new RoutingInputPort("usbIn2",
				eRoutingSignalType.Video, eRoutingPortConnectionType.UsbC, null, this);

			CamInput3 = new RoutingInputPort("usbIn3",
				eRoutingSignalType.Video, eRoutingPortConnectionType.UsbC, null, this);

			CamInput4 = new RoutingInputPort("usbIn4",
				eRoutingSignalType.Video, eRoutingPortConnectionType.UsbC, null, this);

			Output1 = new RoutingOutputPort(RoutingPortNames.HdmiOut1,
				eRoutingSignalType.Audio | eRoutingSignalType.Video,
				eRoutingPortConnectionType.Hdmi, null, this);

			Output2 = new RoutingOutputPort(RoutingPortNames.HdmiOut2,
				eRoutingSignalType.Video,
				eRoutingPortConnectionType.DisplayPort, null, this);

			Output3 = new RoutingOutputPort(RoutingPortNames.HdmiOut3,
				eRoutingSignalType.Audio | eRoutingSignalType.Video,
				eRoutingPortConnectionType.Hdmi, null, this);

			SelfviewIsOnFeedback = new BoolFeedback(SelfViewIsOnFeedbackFunc);

			CameraIsOffFeedback = new BoolFeedback(CameraIsOffFeedbackFunc);

			CameraIsMutedFeedback = CameraIsOffFeedback;

			CameraAutoModeIsOnFeedback = new BoolFeedback(CameraAutoModeIsOnFeedbackFunc);

			CodecSchedule = new CodecScheduleAwareness(MeetingRefreshTimer);

			if (_props.MinutesBeforeMeetingStart > 0)
			{
				CodecSchedule.MeetingWarningMinutes = _props.MinutesBeforeMeetingStart;
			}

			ReceivingContent = new BoolFeedback(FarEndIsSharingContentFeedbackFunc);

			SelfviewPipPositionFeedback = new StringFeedback(SelfviewPipPositionFeedbackFunc);

			// TODO: #714 [ ] SelfviewPipSizeFeedback
			SelfviewPipSizeFeedback = new StringFeedback(SelfviewPipSizeFeedbackFunc);

			Cameras = new List<IHasCameraControls>();

			// Initialized here (not in SetUpCameras, which only runs once connected) so the
			// SelectedCamera setter never dereferences a null feedback.
			SelectedCameraFeedback = new StringFeedback(() => _selectedCamera != null ? _selectedCamera.Key : string.Empty);
			ControllingFarEndCameraFeedback = new BoolFeedback(() => SelectedCamera is IAmFarEndCamera);

			SetUpDirectory();

			Participants = new CodecParticipants();

			SupportsCameraOff = true; // Always allow turning off the camera for zoom calls?
			SupportsCameraAutoMode = _props.SupportsCameraAutoMode;

			PhoneOffHookFeedback = new BoolFeedback("phoneOffHook", PhoneOffHookFeedbackFunc);
			CallerIdNameFeedback = new StringFeedback(CallerIdNameFeedbackFunc);
			CallerIdNumberFeedback = new StringFeedback(CallerIdNumberFeedbackFunc);

			LocalLayoutFeedback = new StringFeedback(LocalLayoutFeedbackFunc);

			LayoutViewIsOnFirstPageFeedback = new BoolFeedback(LayoutViewIsOnFirstPageFeedbackFunc);
			LayoutViewIsOnLastPageFeedback = new BoolFeedback(LayoutViewIsOnLastPageFeedbackFunc);
			CanSwapContentWithThumbnailFeedback = new BoolFeedback(CanSwapContentWithThumbnailFeedbackFunc);
			ContentSwappedWithThumbnailFeedback = new BoolFeedback(ContentSwappedWithThumbnailFeedbackFunc);

			NumberOfScreensFeedback = new IntFeedback(NumberOfScreensFeedbackFunc);

			MeetingIsLockedFeedback = new BoolFeedback("meetingIsLocked", () => _sdkMeetingLocked);

			MeetingIsRecordingFeedback = new BoolFeedback("meetingIsRecording", () => _sdkIsRecording);

			RecordConsentPromptIsVisible = new BoolFeedback(() => _recordConsentPromptIsVisible);

			InCallFeedback = new BoolFeedback("inCall", () => IsInCall);

			ParticipantCountFeedback = new IntFeedback("participantCount", () =>
			{
				lock (_participantLock)
					return Participants.CurrentParticipants?.Count ?? 0;
			});

			SetUpRouting();

			// Built last, once every feedback it lists exists. See IHasFeedback.Feedbacks.
			_monitoredFeedbacks = new FeedbackCollection<Feedback>
			{
				InCallFeedback,
				ParticipantCountFeedback,
				PrivacyModeIsOnFeedback,
				SharingContentIsOnFeedback,
				MeetingIsRecordingFeedback,
				MeetingIsLockedFeedback,
				PhoneOffHookFeedback,
			};
		}

		public ZoomRoomStatus Status { get; private set; }

		public ZoomRoomConfiguration Configuration { get; private set; }

		// Periodic booking refresh — polls ListMeeting() on a fixed interval while connected.
		// The ZRC SDK has no "calendar changed" push event; polling is the only mechanism.
		private CTimer _bookingRefreshTimer;
		private const int BookingRefreshIntervalMs = 5 * 60 * 1000; // 5 minutes

		/// <summary>
		/// Gets and returns the scaled volume of the codec
		/// </summary>
		protected override Func<int> VolumeLevelFeedbackFunc
		{
			get { return () => _sdkSpeakerVolumeLevel; } // room audio-output (speaker) level, 0-65535
		}

		protected override Func<bool> PrivacyModeIsOnFeedbackFunc
		{
			get { return () => _sdkAudioMuted; }
		}

		protected override Func<bool> StandbyIsOnFeedbackFunc
		{
			get { return () => false; }
		}

		protected override Func<string> SharingSourceFeedbackFunc
		{
			get { return () => _sdkSharingState != 0 ? "Sharing" : "None"; }
		}

		protected override Func<bool> SharingContentIsOnFeedbackFunc
		{
			get { return () => _sdkSharingState != 0; }
		}

		// KNOWN LIMITATION: the ZRC SDK's sharing status (SharingStatusEventArgs.SharingState, surfaced via
		// OnControllerSharingStatusChanged) reports only THIS room's sharing — there is no far-end/remote
		// sharing signal. ReceivingContent is therefore hardwired false and must NOT be relied upon to detect
		// a remote participant sharing. Revisit if/when the SDK exposes a far-end sharing flag.
		protected Func<bool> FarEndIsSharingContentFeedbackFunc
		{
			get { return () => false; }
		}

		protected override Func<bool> MuteFeedbackFunc
		{
			get { return () => _sdkSpeakerMuted; } // room audio-output mute (volume zeroed)
		}

		protected Func<bool> SelfViewIsOnFeedbackFunc
		{
			// Self-view is "on" when the room's own self video is not hidden (IHasCodecSelfView maps to
			// the SDK's hide-self-video, which removes the ZR's own tiles from its layout locally).
			get { return () => !_selfVideoHidden; }
		}

		protected Func<bool> CameraIsOffFeedbackFunc
		{
			get { return () => _sdkCameraOff; }
		}

		protected Func<bool> CameraAutoModeIsOnFeedbackFunc
		{
			get { return () => _cameraAutoModeOn; }
		}

		protected Func<string> SelfviewPipPositionFeedbackFunc
		{
			get
			{
				return
					() =>
						_currentSelfviewPipPosition != null
							? _currentSelfviewPipPosition.Command ?? "Unknown"
							: "Unknown";
			}
		}

		// TODO: #714 [ ] SelfviewPipSizeFeedbackFunc
		protected Func<string> SelfviewPipSizeFeedbackFunc
		{
			get
			{
				return
					() =>
						_currentSelfviewPipSize != null
							? _currentSelfviewPipSize.Command ?? "Unknown"
							: "Unknown";
			}
		}

		protected Func<bool> LocalLayoutIsProminentFeedbackFunc
		{
			get { return () => false; }
		}


		public RoutingOutputPort Output1 { get; private set; }
		public RoutingOutputPort Output2 { get; private set; }
		public RoutingOutputPort Output3 { get; private set; }

		public RoutingInputPort ContentInput1 { get; private set; }


		// There doesn't have to be 4 physical USB ports to support up to 4 USB cameras.  A single USB connection can carry multiple cameras.
		public RoutingInputPort CamInput1 { get; private set; }

		public RoutingInputPort CamInput2 { get; private set; }

		public RoutingInputPort CamInput3 { get; private set; }

		public RoutingInputPort CamInput4 { get; private set; }


		#region ICommunicationMonitor Members

		public StatusMonitorBase CommunicationMonitor { get; private set; }

		#endregion

		#region IHasFeedback Members

		/// <summary>
		/// True while a meeting is connecting or connected. A ringing invite doesn't count (see IsInCall).
		/// </summary>
		public BoolFeedback InCallFeedback { get; private set; }

		/// <summary>
		/// Number of people on the meeting roster, this room included. 0 outside a meeting.
		/// </summary>
		public IntFeedback ParticipantCountFeedback { get; private set; }

		private readonly FeedbackCollection<Feedback> _monitoredFeedbacks;

		// Implemented explicitly so VideoCodecBase's own public Feedbacks list, a separate member that
		// isn't part of any interface, is left as it is. Monitoring consumers read IHasFeedback: they
		// subscribe to each listed feedback once, after activation, and re-send the device's state on any
		// change. So the list is built once, in the constructor, and holds only keyed values that change
		// a few times per meeting at most.
		FeedbackCollection<Feedback> IHasFeedback.Feedbacks => _monitoredFeedbacks;

		#endregion

		#region IHasCodecCameras Members

		// BREAKING CHANGE (v3 migration): this event's signature changed from the non-generic
		// EventHandler<CameraSelectedEventArgs> to EventHandler<CameraSelectedEventArgs<IHasCameraControls>>.
		// Subscribers compiled against the old signature must update their handler to the generic args type.
		public event EventHandler<CameraSelectedEventArgs<IHasCameraControls>> CameraSelected;

		public List<IHasCameraControls> Cameras { get; private set; }

		public IHasCameraControls SelectedCamera
		{
			get { return _selectedCamera; }
			private set
			{
				_selectedCamera = value;
				SelectedCameraFeedback.FireUpdate();
				ControllingFarEndCameraFeedback.FireUpdate();

				var handler = CameraSelected;
				if (handler != null)
				{
					handler(this, new CameraSelectedEventArgs<IHasCameraControls>(_selectedCamera));
				}
			}
		}


		public StringFeedback SelectedCameraFeedback { get; private set; }

		public void SelectCamera(string key)
		{
			// Read the camera list under _participantLock — SetUpCameras and UpdateFarEndCameras
			// mutate Cameras from the SDK connection/participant threads.
			IHasCameraControls camera;
			lock (_participantLock)
				camera = Cameras.FirstOrDefault(c => c.Key.Equals(key));

			if (camera == null)
			{
				this.LogWarning("SelectCamera: no camera with key {CameraKey}", key);
				return;
			}

			// Far-end cameras are selected locally only (no SDK device switch); near-end cameras
			// switch the room's active camera via the setting service, keyed by device ID.
			if (camera is IAmFarEndCamera)
			{
				SelectedCamera = camera;
				return;
			}

			if (_controller.SetCurrentCamera(key))
			{
				SelectedCamera = camera;
			}
			else
			{
				// SDK rejected the switch (wrong meeting state, device unavailable). Leave SelectedCamera
				// unchanged and re-assert the true current selection so the UI doesn't desync from intent.
				this.LogWarning("SelectCamera: SDK rejected switch to {CameraKey}; selection unchanged.", key);
				SelectedCameraFeedback.FireUpdate();
			}
		}

		public CameraBase FarEndCamera { get; private set; }

		public BoolFeedback ControllingFarEndCameraFeedback { get; private set; }

		#endregion

		#region IHasCodecSelfView Members

		public BoolFeedback SelfviewIsOnFeedback { get; private set; }

		/// <summary>
		/// Re-emits the self-view feedback from plugin-cached state. NOTE: the ZRC SDK exposes no
		/// self-view query, so this does NOT refresh from the device — a self-view change made on the
		/// native Zoom Room UI is not reflected until this plugin next sets it. (IHasCodecSelfView
		/// requires this method name; it cannot be renamed to signal the cached-only behavior.)
		/// </summary>
		public void GetSelfViewMode() { SelfviewIsOnFeedback.FireUpdate(); }

		public void SelfViewModeOn()
		{
			// "On" = self-view visible = not hidden.
			_controller.SetMyVideoHidden(false);
			_selfVideoHidden = false;
			SelfviewIsOnFeedback.FireUpdate();
		}

		public void SelfViewModeOff()
		{
			_controller.SetMyVideoHidden(true);
			_selfVideoHidden = true;
			SelfviewIsOnFeedback.FireUpdate();
		}

		public void SelfViewModeToggle()
		{
			if (SelfviewIsOnFeedback.BoolValue)
			{
				SelfViewModeOff();
			}
			else
			{
				SelfViewModeOn();
			}
		}

		#endregion

		#region IHasDirectoryHistoryStack Members

		public event EventHandler<DirectoryEventArgs> DirectoryResultReturned;
		public CodecDirectory DirectoryRoot { get; private set; }

		public CodecDirectory CurrentDirectoryResult
		{
			get { return _currentDirectoryResult; }
			private set
			{
				_currentDirectoryResult = value;

				this.LogDebug("CurrentDirectoryResult Updated.  ResultsFolderId: {ResultsFolderId}  Contact Count: {ContactCount}",
					_currentDirectoryResult.ResultsFolderId, _currentDirectoryResult.CurrentDirectoryResults.Count);

				OnDirectoryResultReturned(_currentDirectoryResult);
			}
		}

		public CodecPhonebookSyncState PhonebookSyncState { get; private set; }

		public void SearchDirectory(string searchString)
		{
			var directoryResults = new CodecDirectory();

			directoryResults.AddContactsToDirectory(
				DirectoryRoot.CurrentDirectoryResults.FindAll(
					c => c.Name.IndexOf(searchString, 0, StringComparison.OrdinalIgnoreCase) > -1));

			DirectoryBrowseHistoryStack.Clear();
			CurrentDirectoryResult = directoryResults;
		}

		public void GetDirectoryFolderContents(string folderId)
		{
			var directoryResults = new CodecDirectory { ResultsFolderId = folderId };

			directoryResults.AddContactsToDirectory(
				DirectoryRoot.CurrentDirectoryResults.FindAll(c => c.ParentFolderId.Equals(folderId)));

			DirectoryBrowseHistoryStack.Push(_currentDirectoryResult);

			CurrentDirectoryResult = directoryResults;
		}

		public void SetCurrentDirectoryToRoot()
		{
			DirectoryBrowseHistoryStack.Clear();

			CurrentDirectoryResult = DirectoryRoot;
		}

		public void GetDirectoryParentFolderContents()
		{
			if (DirectoryBrowseHistoryStack.Count == 0)
			{
				return;
			}

			var currentDirectory = DirectoryBrowseHistoryStack.Pop();

			CurrentDirectoryResult = currentDirectory;
		}

		public BoolFeedback CurrentDirectoryResultIsNotDirectoryRoot { get; private set; }

		public List<CodecDirectory> DirectoryBrowseHistory { get; private set; }

		public Stack<CodecDirectory> DirectoryBrowseHistoryStack { get; private set; }

		#endregion

		#region IHasScheduleAwareness Members

		public CodecScheduleAwareness CodecSchedule { get; private set; }

		public void GetSchedule()
		{
			_controller.ListMeeting();
		}

		#endregion

		#region IRouting Members

		public void ExecuteSwitch(object inputSelector, object outputSelector, eRoutingSignalType signalType)
		{
			ExecuteSwitch(inputSelector);
		}

		#endregion

		#region IRoutingSinkWithFeedback Members

		public RoutingInputPort CurrentInputPort { get; private set; }

		public event InputChangedEventHandler InputChanged;

		#endregion

		#region ICurrentSources Members

		public Dictionary<eRoutingSignalType, IRoutingSource> CurrentSources { get; private set; } = new Dictionary<eRoutingSignalType, IRoutingSource>();

		public Dictionary<eRoutingSignalType, string> CurrentSourceKeys { get; private set; } = new Dictionary<eRoutingSignalType, string>();

		public event EventHandler<CurrentSourcesChangedEventArgs> CurrentSourcesChanged;

		public void SetCurrentSource(eRoutingSignalType signalType, IRoutingSource sourceDevice)
		{
			CurrentSources.TryGetValue(signalType, out var previousSource);
			CurrentSources[signalType] = sourceDevice;
			CurrentSourceKeys[signalType] = sourceDevice?.Key;
			CurrentSourcesChanged?.Invoke(this, new CurrentSourcesChangedEventArgs(signalType, previousSource, sourceDevice));
		}

		#endregion



		private void SetUpDirectory()
		{
			DirectoryRoot = new CodecDirectory() { ResultsFolderId = "root" };

			CurrentDirectoryResultIsNotDirectoryRoot = new BoolFeedback(() => CurrentDirectoryResult.ResultsFolderId != "root");

			CurrentDirectoryResult = DirectoryRoot;

			DirectoryBrowseHistory = new List<CodecDirectory>();
			DirectoryBrowseHistoryStack = new Stack<CodecDirectory>();
		}

		private void SetUpRouting()
		{
			// Set up input ports
			InputPorts.Add(ContentInput1);
			InputPorts.Add(CamInput1);
			InputPorts.Add(CamInput2);
			InputPorts.Add(CamInput3);
			InputPorts.Add(CamInput4);

			// Set up output ports
			OutputPorts.Add(Output1);
			OutputPorts.Add(Output2);
			OutputPorts.Add(Output3);
		}

		/// <summary>
		/// Starts the HTTP feedback server and syncronizes state of codec
		/// </summary>
		/// <returns></returns>
		protected override bool CustomActivate()
		{
			// A console command that fails to register must not stop activation: base.CustomActivate()
			// creates the Mobile Control messengers, and without them the panel gets no state for this room.
			try
			{
				RegisterPairingConsoleCommands(this);
			}
			catch (Exception ex)
			{
				this.LogWarning("Could not register the Zoom Room console commands: {message}", ex.Message);
			}

			// Starts the liveness-poll watchdog that keeps devcomm honest and auto-repairs silent drops.
			CommunicationMonitor.Start();

			return base.CustomActivate();
		}

		// The pairing console commands are registered once per program and shared by every Zoom Room
		// device. With one device they take the same arguments as before; with several, the device key
		// comes first (e.g. "pairZoomRoom zoomRoom-2 1234-5678-9012-3456").
		private static readonly object PairingCommandLock = new object();
		private static readonly List<ZoomRoom> PairingCommandRooms = new List<ZoomRoom>();

		private static void RegisterPairingConsoleCommands(ZoomRoom room)
		{
			lock (PairingCommandLock)
			{
				PairingCommandRooms.Add(room);
				if (PairingCommandRooms.Count > 1) return;
			}

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => { if (arg.Length > 0) r._controller.PairWithActivationCode(arg); }),
				"pairZoomRoom", "Pair Zoom Room with activation code: [deviceKey] <code>", ConsoleAccessLevelEnum.AccessOperator);

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => r._controller.RetryPair()),
				"repairZoomRoom", "Reconnect to last paired Zoom Room: [deviceKey]", ConsoleAccessLevelEnum.AccessOperator);

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => r._controller.Unpair()),
				"unpairZoomRoom", "Unpair from Zoom Room: [deviceKey]", ConsoleAccessLevelEnum.AccessOperator);

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => r._controller.RepairWithConfiguredCode()),
				"forceRepairZoom", "Re-pair with configured code, keeps stored pairing: [deviceKey]", ConsoleAccessLevelEnum.AccessOperator);

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => r.ActiveSpeakerConsoleCommand(arg)),
				"zoomActiveSpeaker", "Multi-camera active speaker: [deviceKey] list | <tile>", ConsoleAccessLevelEnum.AccessOperator);

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => r.CameraNameConsoleCommand(arg)),
				"zoomCameraName", "Zoom Room camera names: [deviceKey] list | <n> <name>", ConsoleAccessLevelEnum.AccessOperator);

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => r.RenameTileConsoleCommand(arg)),
				"zoomRenameTile", "Rename a camera tile in this meeting: [deviceKey] list | <tile> <name>", ConsoleAccessLevelEnum.AccessOperator);

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => r.MultiCameraConsoleCommand(arg)),
				"zoomMultiCamera", "Cameras in multi-camera: [deviceKey] list | <n> on|off", ConsoleAccessLevelEnum.AccessOperator);

			CrestronConsole.AddNewConsoleCommand(
				s => ForPairingTarget(s, (r, arg) => r.InteropConsoleCommand(arg)),
				"zoomInterop", "Teams/Webex/Google: [deviceKey] info|join <svc> <id> [pass]|leave", ConsoleAccessLevelEnum.AccessOperator);
		}

		// Picks the Zoom Room a pairing command is for and runs it with the rest of the arguments. A
		// leading device key selects that device; without one the command applies to the only device.
		private static void ForPairingTarget(string args, Action<ZoomRoom, string> command)
		{
			var text = (args ?? string.Empty).Trim();
			var firstSpace = text.IndexOf(' ');
			var firstWord = firstSpace < 0 ? text : text.Substring(0, firstSpace);

			ZoomRoom target;
			List<string> keys;
			lock (PairingCommandLock)
			{
				keys = PairingCommandRooms.Select(r => r.Key).ToList();
				target = PairingCommandRooms.FirstOrDefault(r => r.Key.Equals(firstWord, StringComparison.OrdinalIgnoreCase));
				if (target != null)
					text = firstSpace < 0 ? string.Empty : text.Substring(firstSpace + 1).Trim();
				else if (PairingCommandRooms.Count == 1)
					target = PairingCommandRooms[0];
			}

			if (target == null)
			{
				CrestronConsole.ConsoleCommandResponse("Several Zoom Rooms are configured - put the device key first: {0}\r\n", string.Join(", ", keys));
				return;
			}

			command(target, text);
		}

		/// <summary>
		/// Registers the Zoom Room-specific Mobile Control messenger (zoom layouts, participants,
		/// recording, meeting lock, wireless sharing, camera auto mode, selfview, phone dialing, etc.)
		/// alongside the default core messengers. Called automatically during activation by
		/// <see cref="EssentialsDevice.CustomActivate"/>.
		/// </summary>
		protected override void CreateMobileControlMessengers()
		{
			// Keep the default core messengers for the standard interfaces this codec implements
			// (IHasStartMeeting, IHasMeetingInfo, IPasswordPrompt, IHasCodecCameras, IHasCodecSelfView,
			// IHasFarEndContentStatus, ICommunicationMonitor, ...). The ZoomRoomMessenger below only
			// adds the Zoom-specific actions/status those core messengers don't cover.
			base.CreateMobileControlMessengers();

			var controller = DeviceManager.AllDevices.OfType<IMobileControl>().FirstOrDefault();
			if (controller == null)
			{
				this.LogWarning("No IMobileControl controller found; ZoomRoomMessenger will not be registered for {key}", Key);
				return;
			}

			var path = $"/device/{Key}";

			// Device-level glue (dial/invite, near-end camera mute, end-meeting, directory).
			controller.AddDeviceMessenger(new ZoomRoomMessenger($"{Key}-zoomRoom-{controller.Key}", path, this));

			// One messenger per capability interface (named after the interface, per the core convention).
			controller.AddDeviceMessenger(new IHasParticipantsMessenger($"{Key}-participants-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IHasParticipantPinUnpinMessenger($"{Key}-participantPin-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IHasMeetingLockMessenger($"{Key}-meetingLock-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IHasMeetingRecordingWithPromptMessenger($"{Key}-meetingRecording-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new ZoomRoomPromptsMessenger($"{Key}-prompts-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new ZoomRoomHostControlsMessenger($"{Key}-hostControls-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new ZoomRoomBreakoutMessenger($"{Key}-breakout-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new ZoomRoomWebinarMessenger($"{Key}-webinar-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IHasPresentationOnlyMeetingMessenger($"{Key}-presentationOnly-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IHasCameraAutoModeMessenger($"{Key}-cameraAutoMode-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IHasSelfviewPositionMessenger($"{Key}-selfviewPosition-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IHasSelfviewSizeMessenger($"{Key}-selfviewSize-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IHasPhoneDialingMessenger($"{Key}-phoneDialing-{controller.Key}", path, this));
			// Reuse the core IHasScheduleAwarenessMessenger (it ships in mobile-control-messengers but isn't
			// in the auto-registry); note its ctor arg order is (key, source, messagePath).
			controller.AddDeviceMessenger(new IHasScheduleAwarenessMessenger($"{Key}-schedule-{controller.Key}", this, path));
			controller.AddDeviceMessenger(new IHasZoomRoomLayoutsMessenger($"{Key}-zoomLayouts-{controller.Key}", path, this));
			controller.AddDeviceMessenger(new IZoomWirelessShareInstructionsMessenger($"{Key}-wirelessShare-{controller.Key}", path, this));

			// Camera-preset recall/save actions (the preset LIST status is published by the auto-registered
			// core IHasCodecRoomPresetsMessenger; this adds the inbound actions it lacks).
			controller.AddDeviceMessenger(new IHasCodecRoomPresetsActionsMessenger($"{Key}-presetActions-{controller.Key}", path, this));
		}

		#region Overrides of Device

		protected override void Initialize()
		{
			_controller.ConnectionStateChanged += OnControllerConnectionStateChanged;
			_controller.HealthStateChanged += OnControllerHealthStateChanged;
			_controller.PairRoomResult += (s, e) => this.LogInformation("PairRoomResult [{Code}]: {Desc}", e.ErrorCode, ZrcSdkCodes.GetPairRoomResultDescription(e.ErrorCode));
			_controller.MeetingStatusChanged += OnControllerMeetingStatusChanged;
			_controller.InstantMeetingStarted += OnControllerInstantMeetingStarted;
			_controller.StartPmiResult += OnControllerStartPmiResult;
			_controller.ExitMeeting += OnControllerExitMeeting;
			_controller.MeetingNeedsPassword += OnControllerMeetingNeedsPassword;
			_controller.MeetingInvite += OnControllerMeetingInvite;
			_controller.MeetingInviteTreated += OnControllerMeetingInviteTreated;
			_controller.MeetingLockStatusChanged += OnControllerMeetingLockStatusChanged;
			_controller.AudioMuteStatusChanged += OnControllerAudioMuteStatusChanged;
			_controller.RecordingStatusChanged += OnControllerRecordingStatusChanged;
			_controller.MeetingRecordingInfoChanged += OnControllerMeetingRecordingInfoChanged;
			_controller.CameraPresetInfoChanged += OnControllerCameraPresetInfoChanged;
			_controller.RecordingRequestReceived += OnControllerRecordingRequestReceived;
			_controller.ParticipantsInitialized += OnControllerParticipantEvent;
			_controller.UserJoined += OnControllerParticipantEvent;
			// UserLeft and UserUpdated: the ZRC SDK (.22) routes all roster changes through
			// UserJoined with different NeedCleanUp/payload combinations; UserLeft and UserUpdated
			// are never raised. Wire them defensively so they behave correctly if the SDK ever
			// starts using them.
			_controller.UserLeft += (s, e) => ApplyParticipantEvent(e, isLeaveEvent: true);
			_controller.UserUpdated += OnControllerParticipantEvent;
			// ParticipantCountChanged is NOT subscribed here: the SDK fires both
			// the participant-list callback and the count callback for every join/leave/update.
			// UserJoined/Left/Updated already call Participants.OnParticipantsChanged(), so
			// a separate ParticipantCountChanged handler would double-publish the roster event.
			_controller.HostChanged += OnControllerHostChanged;
			SubscribePromptEvents();
			SubscribeHostControlEvents();
			SubscribeBreakoutEvents();
			SubscribeWebinarEvents();
			SubscribeIntegrationEvents();
			_controller.SharingStatusChanged += OnControllerSharingStatusChanged;
			_controller.AirPlayStatusChanged += OnControllerAirPlayStatusChanged;
			_controller.VideoPageStatusChanged += OnControllerVideoPageStatusChanged;
			_controller.ScreenLayoutStatusChanged += OnControllerScreenLayoutStatusChanged;
			_controller.DynamicLayoutOptionChanged += OnControllerDynamicLayoutOptionChanged;
			_controller.LayoutDiagnostic += OnControllerLayoutDiagnostic;
			_controller.VideoThumbInfoChanged += OnControllerVideoThumbInfoChanged;
			_controller.SipCallStatusChanged += OnControllerSipCallStatusChanged;
			_controller.ContactListChanged += OnControllerContactListChanged;
			_controller.MeetingListChanged += OnControllerMeetingListChanged;

			if (!_controller.Initialize(_props.SdkConfigPath))
			{
				// Surface init failures (bad/missing SdkConfigPath, native wrapper load failure, etc.)
				// instead of silently proceeding as if the SDK were ready. _isConnected stays false,
				// so command methods are gated off (ZrcSdkController.Guard) and the comms monitor stays offline.
				this.LogError("ZRC SDK controller failed to initialize (sdkConfigPath=\"{Path}\"). Device will not be functional.", _props.SdkConfigPath);
				// No connection event will ever come, so without this the monitor stays Unknown for the
				// whole run and monitoring can't tell a failed start from one still in progress.
				_sdkMonitor.SetOnline(false);
			}
		}

		#endregion

		// ── SDK event handlers ───────────────────────────────────────────────

		private void OnControllerConnectionStateChanged(object sender, SdkEventArgs e)
		{
			var state = (ConnectionState)e.ErrorCode;
			var online = state == ConnectionState.Established || state == ConnectionState.Connected;
			this.LogInformation("SDK connection state changed: {State} ({Code})", state, e.ErrorCode);

			_isConnected = online;
			_sdkMonitor.SetOnline(online);

			// Fetch initial data only once fully Connected. At Established the SDK service helpers
			// (contacts, meeting list, settings) aren't ready yet, so these calls return failure;
			// the Connected event that follows pairing is when they succeed.
			if (state == ConnectionState.Connected)
			{
				SeedSpeakerVolume();
				ScheduleFixedAudioLevels("controller connected");
				// The multi-camera selection is a persistent Zoom Room setting; put back what the room wants.
				ScheduleMultiCameraApply(3000);

				// Populate near-end cameras from the SDK device list (real device IDs).
				SetUpCameras();

				// Auto-download the directory/phonebook unless disabled by config. Results arrive
				// asynchronously via ContactListChanged and populate DirectoryRoot.
				if (!_props.DisablePhonebookAutoDownload)
				{
					this.LogInformation("Requesting directory contacts (auto-download)");
					StartPhonebookFetch();
				}

				// Request the current schedule/bookings. Results arrive asynchronously via
				// MeetingListChanged and populate CodecSchedule.
				this.LogInformation("Requesting meeting schedule (bookings)");
				_controller.ListMeeting();
				StartBookingRefreshTimer();

				// Signal readiness: wires EISC camera joins (VideoCodecBase.LinkVideoCodecToApi)
				// and unblocks MC /fullStatus (ZoomRoomMessenger.SendFullStatus gates on IsReady).
				SetIsReady();

				// Seed the current meeting status in case the Zoom Room was already in a meeting
				// before this program (re)started and registered SDK callbacks. MeetingStatusChanged
				// only fires on a subsequent *change* - without this, a meeting already in progress
				// at connect time would never be reported.
				var currentMeetingStatus = _controller.GetMeetingStatus();
				if (currentMeetingStatus.HasValue)
				{
					this.LogInformation("Seeding current meeting status on connect: {Status}", currentMeetingStatus.Value);

					// If we're discovering an already-active meeting (rather than this program having
					// just issued a Dial/StartMeeting), neither _isPendingRosterAdmission nor
					// _hasConfirmedRosterAdmission is set, so ApplyMeetingStatus below would leave the
					// call sitting on Connecting - and the UI showing "not in a call" - forever, with no
					// timeout to fall back on (that timeout only arms from BeginPendingRosterAdmission,
					// which nothing calls on this path). See BeginConnectTimeSeedAdmissionCheck.
					if (currentMeetingStatus.Value == MeetingStatus.InMeeting && !_hasConfirmedRosterAdmission && !_isPendingRosterAdmission)
					{
						BeginConnectTimeSeedAdmissionCheck();
					}

					ApplyMeetingStatus(currentMeetingStatus.Value);
				}
				else
				{
					this.LogWarning("Unable to query current meeting status on connect");
				}
			}
			else if (!online)
			{
				StopBookingRefreshTimer();
				ResetMeetingState();
				lock (_phonebookSettleLock) _phonebookSettleTimer?.Stop();
				lock (_directoryLock) _directoryContactsById.Clear();
				PhonebookSyncState.CodecDisconnected();
			}
		}

		// Watchdog-driven truth for silent/half-open drops the SDK never reported. Keeps devcomm honest
		// and runs the same teardown as a real disconnect when going offline; full on-connect seeding is
		// left to the SDK's Connected event produced by auto-repair.
		private void OnControllerHealthStateChanged(object sender, bool online)
		{
			if (_isConnected == online) return;

			this.LogInformation("Health watchdog reports {State}", online ? "online" : "offline");
			_isConnected = online;
			_sdkMonitor.SetOnline(online);

			if (!online)
			{
				StopBookingRefreshTimer();
				ResetMeetingState();
				lock (_phonebookSettleLock) _phonebookSettleTimer?.Stop();
				lock (_directoryLock) _directoryContactsById.Clear();
				PhonebookSyncState.CodecDisconnected();
			}
		}

		// Reads the current room speaker volume once on connect so the volume feedback reflects
		// the real level. There is no SDK push for output-volume changes, so external (Zoom UI)
		// changes won't update the feedback until the next set from this plugin.
		private void SeedSpeakerVolume()
		{
			var sdkVolume = _controller.GetSpeakerVolume();
			if (sdkVolume < 0f) return; // get failed (e.g. setting service not ready yet)
			_sdkSpeakerVolumeLevel = (ushort)Math.Round(Math.Max(0f, Math.Min(SdkSpeakerVolumeMax, sdkVolume)) / SdkSpeakerVolumeMax * 65535f);
			_sdkSpeakerMuted = _sdkSpeakerVolumeLevel == 0;
			VolumeLevelFeedback.FireUpdate();
			MuteFeedback.FireUpdate();
		}

		private const int FixedAudioLevelsDelayMs = 3000;
		private readonly object _fixedAudioLevelsLock = new object();
		private CTimer _fixedAudioLevelsTimer;

		// Sets the fixed levels a moment after the connect / meeting start, from a timer thread. Set
		// straight from the MeetingStatusChanged handler, the level sets and the watchdog's health probe
		// that followed them all failed (JASC hardware), which marked the room offline and reconnected
		// it - over and over, as each reconnect started a meeting again. Repeated triggers within the
		// delay set the levels once.
		private void ScheduleFixedAudioLevels(string reason)
		{
			if (!_props.FixedSpeakerVolume.HasValue && !_props.FixedMicVolume.HasValue) return;

			lock (_fixedAudioLevelsLock)
			{
				_fixedAudioLevelsTimer?.Stop();
				_fixedAudioLevelsTimer = new CTimer(_ => ApplyFixedAudioLevels(reason), FixedAudioLevelsDelayMs);
			}
		}

		// Sets the configured fixed speaker/mic levels (fixedSpeakerVolume / fixedMicVolume, percent).
		// Both use the same 0-255 SDK scale (mic confirmed on JASC: 90% reads back as 229); the mic is
		// logged on either side of the set.
		private void ApplyFixedAudioLevels(string reason)
		{
			if (_props.FixedSpeakerVolume.HasValue)
			{
				var level = PercentToLevel(_props.FixedSpeakerVolume.Value);
				_sdkSpeakerVolumeLevel = level;
				_sdkSpeakerMuted = false;
				var ok = _controller.SetSpeakerVolume(LevelToSdkVolume(level));
				this.LogInformation("Fixed speaker volume {Percent}% ({Reason}): set {Result}",
					_props.FixedSpeakerVolume.Value, reason, ok ? "ok" : "failed");
				VolumeLevelFeedback.FireUpdate();
				MuteFeedback.FireUpdate();
			}

			if (_props.FixedMicVolume.HasValue)
			{
				var before = _controller.GetMicrophoneVolume();
				var ok = _controller.SetMicrophoneVolume(LevelToSdkVolume(PercentToLevel(_props.FixedMicVolume.Value)));
				this.LogInformation("Fixed mic volume {Percent}% ({Reason}): before {Before}, set {Result}, after {After}",
					_props.FixedMicVolume.Value, reason, before, ok ? "ok" : "failed", _controller.GetMicrophoneVolume());
			}
		}

		private static ushort PercentToLevel(int percent) =>
			(ushort)Math.Round(Math.Max(0, Math.Min(100, percent)) / 100f * 65535f);

		// True when fixedSpeakerVolume holds the speaker; the volume/mute command is then ignored.
		private bool SpeakerVolumeIsFixed(string command)
		{
			if (!_props.FixedSpeakerVolume.HasValue) return false;
			this.LogDebug("{Command} ignored: speaker volume is fixed at {Percent}%", command, _props.FixedSpeakerVolume.Value);
			return true;
		}

		private void OnControllerMeetingStatusChanged(object sender, SdkEventArgs e)
		{
			var status = (MeetingStatus)e.ErrorCode;
			this.LogInformation("MeetingStatusChanged: {Status} ({Code})", status, e.ErrorCode);

			ApplyMeetingStatus(status);
		}

		/// <summary>
		/// Applies a meeting status to this device's call/meeting state. Shared by
		/// <see cref="OnControllerMeetingStatusChanged"/> (an actual SDK status-changed event) and the
		/// connect-time seed in <see cref="OnControllerConnectionStateChanged"/> (a synchronous query of
		/// the status that may have already been in effect before the SDK callbacks were registered).
		/// </summary>
		private void ApplyMeetingStatus(MeetingStatus status)
		{
			switch (status)
			{
				case MeetingStatus.InMeeting:
					{
						ScheduleFixedAudioLevels("meeting started");
						ScheduleBreakoutEnabledCheck();

						// See _hasConfirmedRosterAdmission: InMeeting alone doesn't mean the room has
						// actually been admitted from a waiting room, so don't show Connected until the
						// roster confirms it - until then this is functionally identical to
						// ConnectingToMeeting.
						var targetStatus = _hasConfirmedRosterAdmission ? eCodecCallStatus.Connected : eCodecCallStatus.Connecting;

						RefreshMeetingIdentity();

						if (ActiveCalls.Count == 0)
						{
							var call = new CodecActiveCallItem
							{
								Name = _currentMeetingName,
								Number = _currentMeetingNumber,
								Id = _currentMeetingId,
								Status = targetStatus,
								Type = eCodecCallType.Video,
							};
							ActiveCalls.Add(call);
							OnCallStatusChange(call);
						}
						else
						{
							var existing = ActiveCalls.FirstOrDefault();
							if (existing != null)
							{
								existing.Status = targetStatus;
								OnCallStatusChange(existing);
							}
						}
						UpdateMeetingInfo();
						break;
					}
				case MeetingStatus.ConnectingToMeeting:
					{
						if (ActiveCalls.Count == 0)
						{
							var call = new CodecActiveCallItem
							{
								Name = _currentMeetingName,
								Number = _currentMeetingNumber,
								Id = _currentMeetingId,
								Status = eCodecCallStatus.Connecting,
								Type = eCodecCallType.Video,
							};
							ActiveCalls.Add(call);
							OnCallStatusChange(call);
						}
						break;
					}
				case MeetingStatus.NotInMeeting:
				case MeetingStatus.LoggedOut:
					{
						if (_isPendingRosterAdmission)
						{
							this.LogInformation("MeetingStatusChanged: {0} while still pending roster admission - ignoring as transient noise from the pre-admission dance", status);
							break;
						}
						ResetMeetingState();
						break;
					}
			}
		}

		// A meeting this room joined (by number, invite or schedule) rather than started gets no number
		// from the start-meeting callbacks, so ask the SDK. The number is what MeetingInfo.Id reports,
		// which is how anything outside this device can tell which meeting the room is in.
		private void RefreshMeetingIdentity()
		{
			if (!string.IsNullOrEmpty(_currentMeetingNumber)) return;

			try
			{
				if (!_controller.TryGetMeetingInfo(out var info) || info == null || string.IsNullOrEmpty(info.MeetingNumber))
					return;

				_currentMeetingNumber = info.MeetingNumber;
				_currentMeetingId = info.MeetingNumber;
				if (string.IsNullOrEmpty(_currentMeetingName))
					_currentMeetingName = info.MeetingName;

				this.LogDebug("Meeting number {MeetingNumber} read from the SDK for a joined meeting", info.MeetingNumber);
			}
			catch (Exception ex)
			{
				this.LogException(ex, "RefreshMeetingIdentity: meeting info query threw");
			}
		}

		/// <summary>
		/// Marks the start of a join/start attempt: clears any prior roster-admission confirmation and
		/// arms the pending-admission window (see _isPendingRosterAdmission) so ExitMeeting/NotInMeeting
		/// noise during the pre-admission dance doesn't bounce the UI back to idle, with a timeout
		/// safety net in case admission never actually arrives (rejected, meeting ended, etc.).
		/// </summary>
		private void BeginPendingRosterAdmission()
		{
			_hasConfirmedRosterAdmission = false;
			_isPendingRosterAdmission = true;

			_pendingRosterAdmissionTimeoutTimer?.Stop();
			_pendingRosterAdmissionTimeoutTimer = new CTimer(_ =>
			{
				this.LogWarning("Pending roster admission timed out after {0}ms with no roster confirmation - treating the join as failed", PendingRosterAdmissionTimeoutMs);
				_isPendingRosterAdmission = false;
				ResetMeetingState();
			}, PendingRosterAdmissionTimeoutMs);
		}

		/// <summary>
		/// Handles discovering an already-active meeting on connect (see the connect-time seed in
		/// OnControllerConnectionStateChanged) - distinct from BeginPendingRosterAdmission, which guards
		/// a *live* join attempt against the waiting-room dance. That ambiguity doesn't apply here: this
		/// program is just now registering SDK callbacks against a meeting that was already running
		/// (e.g. the Essentials program restarted mid-call), not initiating a join, so a waiting-room
		/// stint isn't realistically still in progress. Immediately checks the roster in case it's
		/// already available, then gives it a short grace period for the normal participant-changed push
		/// to confirm it - but if nothing arrives in that window, trusts the SDK's own InMeeting status
		/// directly and confirms admission anyway, rather than leaving the call stuck on Connecting (and
		/// the UI showing the room as not in a call) indefinitely.
		/// </summary>
		private void BeginConnectTimeSeedAdmissionCheck()
		{
			RefreshRosterAdmissionFromParticipants();
			if (_hasConfirmedRosterAdmission) return;

			_connectTimeSeedAdmissionTimer?.Stop();
			_connectTimeSeedAdmissionTimer = new CTimer(_ =>
			{
				if (_hasConfirmedRosterAdmission) return;
				this.LogWarning("No roster confirmation arrived within {0}ms of discovering an already-active meeting on connect - trusting the SDK's InMeeting status directly", ConnectTimeSeedAdmissionGraceMs);
				ConfirmRosterAdmission();
			}, ConnectTimeSeedAdmissionGraceMs);
		}

		/// <summary>
		/// Confirms genuine meeting admission and promotes any call already sitting in ActiveCalls as
		/// Connecting (from InMeeting or ConnectingToMeeting arriving before the roster did) to
		/// Connected. Called once the roster contains our own entry - see
		/// RefreshRosterAdmissionFromParticipants.
		/// </summary>
		private void ConfirmRosterAdmission()
		{
			if (_hasConfirmedRosterAdmission) return;

			this.LogInformation("Roster admission confirmed - marking the call Connected");
			_hasConfirmedRosterAdmission = true;
			_isPendingRosterAdmission = false;
			_pendingRosterAdmissionTimeoutTimer?.Stop();
			_pendingRosterAdmissionTimeoutTimer = null;
			_connectTimeSeedAdmissionTimer?.Stop();
			_connectTimeSeedAdmissionTimer = null;

			// Webinar or not decides whether the participants page offers Panelists | Attendees.
			RefreshWebinar(requestAttendees: false);

			var existing = ActiveCalls.FirstOrDefault();
			if (existing != null && existing.Status != eCodecCallStatus.Connected)
			{
				existing.Status = eCodecCallStatus.Connected;
				OnCallStatusChange(existing);
			}
		}

		/// <summary>
		/// Checks whether the roster now contains our own entry (IsMyself) and, if so, confirms real
		/// meeting admission - see _hasConfirmedRosterAdmission for why this, not MeetingStatus alone,
		/// is what actually gates showing the call as Connected. Called alongside
		/// RefreshHostFromParticipants/RefreshCoHostFromParticipants from the participant-changed
		/// handler.
		/// </summary>
		private void RefreshRosterAdmissionFromParticipants()
		{
			if (_hasConfirmedRosterAdmission) return;

			bool isMyselfPresent;
			lock (_participantLock)
				isMyselfPresent = Participants.CurrentParticipants.Any(p => p.IsMyself);

			if (isMyselfPresent)
			{
				ConfirmRosterAdmission();
			}
		}

		private void OnControllerInstantMeetingStarted(object sender, SdkEventArgs e)
		{
			this.LogInformation("InstantMeetingStarted code={Code} meetingNumber={Number}", e.ErrorCode, e.Message);
			if (!string.IsNullOrEmpty(e.Message))
			{
				_currentMeetingNumber = e.Message;
				_currentMeetingId = e.Message;
			}
			UpdateMeetingInfo();
		}

		private void OnControllerStartPmiResult(object sender, SdkEventArgs e)
		{
			this.LogInformation("StartPmiResult code={Code} meetingNumber={Number}", e.ErrorCode, e.Message);
			if (!string.IsNullOrEmpty(e.Message))
			{
				_currentMeetingNumber = e.Message;
				_currentMeetingId = e.Message;
			}
			UpdateMeetingInfo();
		}

		private void OnControllerExitMeeting(object sender, SdkEventArgs e)
		{
			this.LogInformation("ExitMeeting reason={Reason} ({Code})", (ExitMeetingReason)e.ErrorCode, e.ErrorCode);

			if (_isPendingRosterAdmission)
			{
				// The SDK cycles Exit/ConnectingToMeeting/Exit while a join sits in a waiting room
				// pending host approval (confirmed via live testing) - none of that is a real
				// disconnect. Ignore it and let a later ConnectingToMeeting/InMeeting/roster
				// confirmation (or the pending-admission timeout) carry the join forward instead of
				// bouncing the UI back to idle.
				this.LogInformation("ExitMeeting while still pending roster admission - ignoring as transient noise from the pre-admission dance");
				return;
			}

			ResetMeetingState();
		}

		/// <summary>
		/// Resets all meeting-scoped state fields. Called from ExitMeeting, NotInMeeting/LoggedOut,
		/// and disconnect paths so every meeting-end scenario leaves the device in a consistent clean state.
		/// </summary>
		private void ResetMeetingState()
		{
			_hasConfirmedRosterAdmission = false;
			_isPendingRosterAdmission = false;
			_pendingRosterAdmissionTimeoutTimer?.Stop();
			_pendingRosterAdmissionTimeoutTimer = null;
			_connectTimeSeedAdmissionTimer?.Stop();
			_connectTimeSeedAdmissionTimer = null;

			_currentMeetingId = string.Empty;
			_currentMeetingNumber = string.Empty;
			_currentMeetingName = string.Empty;
			_sdkIsHost = false;
			// Co-host reaches Mobile Control only through CoHostChanged (host rides MeetingInfo, sent
			// below), so clearing it silently left the panel showing host controls in the next meeting.
			var wasCoHost = _sdkIsCoHost;
			_sdkIsCoHost = false;

			_sdkMeetingLocked = false;
			_sdkIsRecording = false;
			_sdkCanRecord = false;
			_sdkSharingState = 0;
			_weRequestedHdmiSharing = false;
			_sdkPhoneOffHook = false;
			_sdkSipCallerName = string.Empty;
			_sdkSipCallerNumber = string.Empty;
			_recordConsentPromptIsVisible = false;
			_recordingRequestSenderName = string.Empty;
			_recordingRequestType = "unknown";
			RecordConsentPromptIsVisible.FireUpdate();
			// The fields above were cleared without the SDK notifications that normally fire these.
			MeetingIsLockedFeedback.FireUpdate();
			MeetingIsRecordingFeedback.FireUpdate();
			SharingContentIsOnFeedback.FireUpdate();
			SharingSourceFeedback.FireUpdate();
			PhoneOffHookFeedback.FireUpdate();
			CallerIdNameFeedback.FireUpdate();
			CallerIdNumberFeedback.FireUpdate();
			ClearPrompts("meeting reset");
			ResetHostControls();
			ResetBreakout();
			ResetWebinar();
			ResetCameraTileNames();
			ResetShareLayout();
			lock (_participantLock)
			{
				_pinnedUserScreens.Clear();
				_spotlightedUserIds.Clear();
				_participantInfoByUserId.Clear();
				_cameraTileNumberByUserId.Clear();
				_cameraTileZoomNameByUserId.Clear();
				_cameraTileInputByUserId.Clear();
				_expectedCameraTileInputs.Clear();
				Participants.CurrentParticipants = new System.Collections.Generic.List<Participant>();
			}
			ParticipantCountFeedback.FireUpdate();
			RaisePinSpotlightChanged();
			_pendingInviteCall = null;
			ActiveCalls.Clear();
			OnCallStatusChange(new CodecActiveCallItem { Status = eCodecCallStatus.Disconnected });
			UpdateFarEndCameras();
			UpdateMeetingInfo();
			if (wasCoHost)
				CoHostChanged?.Invoke(this, false);
		}

		private void OnControllerMeetingNeedsPassword(object sender, SdkEventArgs e)
		{
			var wrongAndRetry = e.ErrorCode == 1;
			OnPasswordRequired(wrongAndRetry, false, false, "Password required to join this meeting.");
		}

		private void OnControllerMeetingInvite(object sender, MeetingInviteEventArgs e)
		{
			// Fires on OnReceiveMeetingInviteNotification (a contact/room inviting this room into a
			// meeting). The native side caches the full invite so it can be answered with
			// AnswerMeetingInvite. Surface it as a Ringing/Incoming ActiveCall (with caller + meeting
			// details) so the standard codec Accept/Reject (and touchpanel incoming-call UI) work.
			var caller = string.IsNullOrEmpty(e.CallerName) ? "Incoming meeting invite" : e.CallerName;
			this.LogInformation("MeetingInvite received from \"{Caller}\" meetingNumber={MeetingNumber} meetingId={MeetingId} contactId={ContactId}",
				caller, e.MeetingNumber, e.MeetingId, e.CallerContactId);

			if (IsHiddenContactName(e.CallerName) || IsHiddenContactId(e.CallerContactId))
			{
				this.LogWarning("Declining meeting invite from hidden contact \"{Caller}\"", caller);
				_controller.AnswerMeetingInvite(false);
				return;
			}

			if (_pendingInviteCall != null) return; // already ringing

			_pendingInviteCall = new CodecActiveCallItem
			{
				Name = caller,
				Number = e.MeetingNumber,
				Id = string.IsNullOrEmpty(e.MeetingNumber) ? "meeting-invite" : e.MeetingNumber,
				Status = eCodecCallStatus.Ringing,
				Direction = eCodecCallDirection.Incoming,
				Type = eCodecCallType.Video,
			};
			ActiveCalls.Add(_pendingInviteCall);
			OnCallStatusChange(_pendingInviteCall);

			var timeoutMs = _props.MeetingInviteTimeoutMs > 0 ? _props.MeetingInviteTimeoutMs : DefaultMeetingInviteTimeoutMs;
			_pendingInviteTimeoutTimer?.Stop();
			_pendingInviteTimeoutTimer = new CTimer(_ => OnPendingInviteTimedOut(), timeoutMs);
		}

		// The SDK's only "invite resolved" signal (MeetingInviteTreated) doesn't fire for an invite
		// that's simply left ringing with no action anywhere -- this is the fallback for that case.
		private void OnPendingInviteTimedOut()
		{
			var item = _pendingInviteCall;
			if (item == null) return;

			this.LogInformation("Meeting invite from \"{Caller}\" timed out with no response after {TimeoutMs}ms — removing from active calls",
				item.Name, _props.MeetingInviteTimeoutMs > 0 ? _props.MeetingInviteTimeoutMs : DefaultMeetingInviteTimeoutMs);

			item.Status = eCodecCallStatus.Disconnected;
			ActiveCalls.Remove(item);
			OnCallStatusChange(item);
			_pendingInviteCall = null;
		}

		private void OnControllerMeetingInviteTreated(object sender, MeetingInviteTreatedEventArgs e)
		{
			// Fires when the invite is resolved by any means other than our own AcceptCall/RejectCall
			// (e.g. answered/declined on another paired device, or expired/cancelled by the caller).
			_pendingInviteTimeoutTimer?.Stop();

			var item = _pendingInviteCall;
			if (item == null || !string.Equals(item.Number, e.MeetingNumber, StringComparison.Ordinal))
				return; // already resolved locally, or this is a different invite

			this.LogInformation("MeetingInvite from \"{Caller}\" treated elsewhere: accepted={Accepted}", item.Name, e.Accepted);

			// AcceptCall/RejectCall already promote/clear locally-answered invites; this only needs to
			// clean up when nothing local has touched it yet (accepted=false is the common case here,
			// but even accepted=true elsewhere means it's no longer "ringing" for this device).
			item.Status = eCodecCallStatus.Disconnected;
			ActiveCalls.Remove(item);
			OnCallStatusChange(item);
			_pendingInviteCall = null;
		}

		private void OnControllerMeetingLockStatusChanged(object sender, SdkEventArgs e)
		{
			_sdkMeetingLocked = e.ErrorCode == 1;
			MeetingIsLockedFeedback.FireUpdate();
			UpdateMeetingInfo();
		}

		private void OnControllerAudioMuteStatusChanged(object sender, SdkEventArgs e)
		{
			_sdkAudioMuted = e.ErrorCode == 1;
			PrivacyModeIsOnFeedback.FireUpdate();
		}

		private void OnControllerRecordingStatusChanged(object sender, SdkEventArgs e)
		{
			_sdkIsRecording = e.ErrorCode == 1;
			MeetingIsRecordingFeedback.FireUpdate();
			UpdateMeetingInfo();
		}

		private void OnControllerMeetingRecordingInfoChanged(object sender, MeetingRecordingInfoEventArgs e)
		{
			_sdkCanRecord = e.CanIRecord;

			// This notification is the authoritative recording state - the separate status event can
			// only ever say "being recorded", while this one is pushed on every change including a stop.
			if (_sdkIsRecording != e.IsMeetingBeingRecorded)
			{
				_sdkIsRecording = e.IsMeetingBeingRecorded;
				this.LogInformation("Cloud recording: isRecording={IsRecording}", _sdkIsRecording);
				MeetingIsRecordingFeedback.FireUpdate();
			}

			UpdateMeetingInfo(); // refreshes MeetingInfo.CanRecord on the bridge join
			if (_sdkRecordingPaused != e.IsCloudRecordingPaused || _sdkRecordingConnecting != e.IsConnectingToCloud)
			{
				_sdkRecordingPaused = e.IsCloudRecordingPaused;
				_sdkRecordingConnecting = e.IsConnectingToCloud;
				this.LogDebug("Cloud recording: paused={Paused} connecting={Connecting}", _sdkRecordingPaused, _sdkRecordingConnecting);
				RecordingExtrasChanged?.Invoke(this, EventArgs.Empty);
			}
		}

		// A participant asked this room (the host) for permission to record. The SDK carries the
		// requester's display name in Message (empty for a cloud recording request) and the native
		// RecordingType in ErrorCode (0 local, 1 cloud). Answered via RecordingPromptAcknowledgement.
		private void OnControllerRecordingRequestReceived(object sender, SdkEventArgs e)
		{
			_recordingRequestSenderName = e?.Message ?? string.Empty;
			_recordingRequestType = e?.ErrorCode == 1 ? "cloud" : e?.ErrorCode == 0 ? "local" : "unknown";
			this.LogInformation("RecordingRequest from \"{Sender}\" type={Type}", _recordingRequestSenderName, _recordingRequestType);
			_recordConsentPromptIsVisible = true;
			RecordConsentPromptIsVisible.FireUpdate();
		}

		/// <summary>
		/// Test hook: raises the recording-request prompt exactly as an SDK request would, without a
		/// participant having to ask. From the console:
		/// <c>devjson:1 {"deviceKey":"zoomRoom","methodName":"SimulateRecordingRequest","params":["Test User", 0]}</c>
		/// (recordingType 0 = local, 1 = cloud). Allow/Deny from the UI then runs the real answer path;
		/// outside a meeting the SDK just returns an error code, which is logged.
		/// </summary>
		public void SimulateRecordingRequest(string senderName, int recordingType)
		{
			this.LogWarning("SIMULATED recording request (console test hook): sender=\"{Sender}\" type={Type}", senderName, recordingType);
			OnControllerRecordingRequestReceived(this, new SdkEventArgs { Message = senderName ?? string.Empty, ErrorCode = recordingType });
		}

		// ── Unified participant event handler ─────────────────────────────────────
		// All four SDK participant events (Initialized, UserJoined, UserLeft, UserUpdated)
		// are routed here. In SDK .22 only ParticipantsInitialized and UserJoined are raised
		// (all roster changes arrive via UserJoined); UserLeft and UserUpdated are dead but wired
		// defensively. UserLeft is routed with isLeaveEvent=true via a lambda at the call site (#22).

		private void OnControllerParticipantEvent(object sender, ParticipantListEventArgs e)
		{
			if (e?.Participants == null) return;
			LogIncomingParticipantRoles(sender?.GetType().Name ?? "unknown", e);
			ApplyParticipantEvent(e, isLeaveEvent: false);
		}

		private void ApplyParticipantEvent(ParticipantListEventArgs e, bool isLeaveEvent)
		{
			lock (_participantLock)
			{
				if (e.NeedCleanUp)
				{
					// Full roster replace (used by ParticipantsInitialized and clean-up UserJoined).
					Participants.CurrentParticipants = MapParticipants(e.Participants);
					TrackParticipantInfo(e.Participants, fullReplace: true, isLeave: false);
				}
				else if (isLeaveEvent)
				{
					// UserLeft: remove named participants.
					foreach (var info in e.Participants)
					{
						var existing = Participants.CurrentParticipants.FirstOrDefault(p => p.UserId == info.UserID);
						if (existing != null) Participants.CurrentParticipants.Remove(existing);
					}
					TrackParticipantInfo(e.Participants, fullReplace: false, isLeave: true);
				}
				else
				{
					// UserJoined / UserUpdated: add or update in place.
					// The SDK re-sends a participant via UserJoined when their role changes (e.g.
					// co-host promotion), so an already-present participant must be updated, not ignored.
					foreach (var info in e.Participants)
					{
						var existing = Participants.CurrentParticipants.FirstOrDefault(p => p.UserId == info.UserID);
						if (existing == null)
							Participants.CurrentParticipants.Add(MapParticipant(info));
						else
							UpdateParticipantFrom(existing, info);
					}
					TrackParticipantInfo(e.Participants, fullReplace: false, isLeave: false);
				}
			}

			Participants.OnParticipantsChanged();
			ParticipantCountFeedback.FireUpdate();
			RefreshHostFromParticipants();
			RefreshCoHostFromParticipants();
			RefreshRosterAdmissionFromParticipants();
			UpdateFarEndCameras();
			RefreshBreakoutRoster();
			ScheduleCameraTileNames();
		}

		private void OnControllerHostChanged(object sender, SdkEventArgs e)
		{
			_sdkIsHost = e.ErrorCode == 1;
			this.LogDebug("HostChanged: isHost={IsHost}", _sdkIsHost);
			UpdateMeetingInfo();
			NoteRoleChange();
			ScheduleBreakoutEnabledCheck();
		}

		/// <summary>
		/// Derives this room's host status from the roster (the <c>IsMyself</c> participant's
		/// <c>IsHost</c> flag). The SDK's HostChanged notification only fires on a host *change*, so
		/// when the room is host from the start of a meeting it never arrives — this captures it.
		/// </summary>
		private void RefreshHostFromParticipants()
		{
			bool isHost;
			lock (_participantLock)
				isHost = Participants.CurrentParticipants.Any(p => p.IsMyself && p.IsHost);

			if (isHost == _sdkIsHost) return;
			_sdkIsHost = isHost;
			this.LogDebug("Host state from roster: isHost={IsHost}", isHost);
			UpdateMeetingInfo();
			NoteRoleChange();
			ScheduleBreakoutEnabledCheck();
		}

		/// <summary>
		/// Fires when this room's co-host status changes. Unlike host status, IHasMeetingInfo's
		/// MeetingInfo class has no co-host field (fixed shape from PepperDashEssentials), so this is
		/// surfaced as its own event/property rather than through MeetingInfoChanged - see
		/// ZoomRoomMessenger for how it reaches Mobile Control.
		/// </summary>
		public event EventHandler<bool> CoHostChanged;

		public bool IsCoHost => _sdkIsCoHost;

		/// <summary>
		/// Derives this room's co-host status from the roster (the <c>IsMyself</c> participant's
		/// <c>IsCohost</c> flag), the same way <see cref="RefreshHostFromParticipants"/> derives host
		/// status - the SDK re-sends a participant via UserJoined on a role change (see
		/// LogIncomingParticipantRoles), which is what this relies on to observe a co-host promotion.
		/// </summary>
		private void RefreshCoHostFromParticipants()
		{
			bool isCoHost;
			lock (_participantLock)
				isCoHost = Participants.CurrentParticipants.Any(p => p.IsMyself && p.IsCohost);

			if (isCoHost == _sdkIsCoHost) return;
			_sdkIsCoHost = isCoHost;
			this.LogDebug("Co-host state from roster: isCoHost={IsCoHost}", isCoHost);
			CoHostChanged?.Invoke(this, isCoHost);
			NoteRoleChange();
			ScheduleBreakoutEnabledCheck();
		}

		// Diagnostic (Debug): logs the raw role flags the SDK delivers for each participant in a
		// participant event. Used to confirm whether co-host promotions actually arrive over the
		// participant feed (vs. only via a no-data "participants changed" signal).
		private void LogIncomingParticipantRoles(string source, ParticipantListEventArgs e)
		{
			if (e?.Participants == null) return;
			foreach (var info in e.Participants)
				this.LogDebug("{Source}: userId={UserId} name=\"{Name}\" isHost={IsHost} isCohost={IsCohost} isAltHost={IsAltHost} userType={UserType} cleanup={Cleanup}",
					source, info.UserID, info.UserName, info.IsHost, info.IsCohost, info.IsOriginalOrAlternativeHost, info.UserType, e.NeedCleanUp);
		}

		private void OnControllerSharingStatusChanged(object sender, SharingStatusEventArgs e)
		{
			_sdkSharingState = e.SharingState;
			UpdateMeetingInfo();
			SharingContentIsOnFeedback.FireUpdate();
			SharingSourceFeedback.FireUpdate();
			ReceivingContent.FireUpdate();
			CanSwapContentWithThumbnailFeedback.FireUpdate();
			OnShareStateForShareLayout();
		}

		private void OnControllerAirPlayStatusChanged(object sender, AirPlayStatusEventArgs e)
		{
			// The Zoom Room appliance can auto-start sharing the content HDMI input the moment it
			// senses a signal, entirely on its own - this fires with no call into StartSharing at
			// all. This room's content is meant to reach the call only through the room's own
			// explicit routing, so immediately reverse any HDMI share this plugin did not ask for.
			if (e.IsSharingBlackMagic && !_weRequestedHdmiSharing)
			{
				this.LogWarning("Zoom Room auto-started sharing the content HDMI input without a StartSharing request (signal detected on connect) — stopping it");
				_controller.ShareBlackMagic(false, false);
			}

			Status.Sharing.isAirHostClientConnected = e.IsAirHostClientConnected;
			Status.Sharing.isBlackMagicConnected = e.IsBlackMagicConnected;
			Status.Sharing.isBlackMagicDataAvailable = e.IsBlackMagicDataAvailable;
			Status.Sharing.isSharingBlackMagic = e.IsSharingBlackMagic;
			Status.Sharing.isDirectPresentationConnected = e.IsDirectPresentationConnected;
			Status.Sharing.password = e.Password;
			Status.Sharing.serverName = e.ServerName;
			Status.Sharing.wifiName = e.WifiName;
			Status.Sharing.directPresentationPairingCode = e.DirectPresentationPairingCode;
			Status.Sharing.directPresentationSharingKey = e.DirectPresentationSharingKey;
			Status.Sharing.dispState = (zStatus.eDisplayState)e.InstructionDisplayState;
			OnShareInfoChanged(Status.Sharing);
		}

		private void OnControllerVideoPageStatusChanged(object sender, VideoPageStatusEventArgs e)
		{
			_layoutIsOnFirstPage = e.IsInFirstPage;
			_layoutIsOnLastPage = e.IsInLastPage;
			_currentPageVideoType = e.PageVideoType; // keep the SDK's current page type for TurnVideoPage
			LayoutViewIsOnFirstPageFeedback.FireUpdate();
			LayoutViewIsOnLastPageFeedback.FireUpdate();
		}

		private void OnControllerVideoThumbInfoChanged(object sender, VideoThumbInfoEventArgs e)
		{
			this.LogDebug("VideoThumbInfo: isSupported={IsSupported} position={Position} size={Size} layout={Layout}",
				e.IsSupported, e.Position, e.Size, LastSelectedLayout);

			_sdkSelfviewThumbSupported = e.IsSupported;
			SelfviewPipSizeFeedback.FireUpdate();
		}

		private void OnControllerScreenLayoutStatusChanged(object sender, ScreenLayoutStatusEventArgs e)
		{
			_screenLayoutStatus = e;

			// Update the current layout from the first screen's active layout source type.
			if (e.LayoutInfos != null && e.LayoutInfos.Length > 0)
			{
				var primaryScreen = e.LayoutInfos[0];

				this.LogDebug("ScreenLayoutStatus: screen={Screen} rawLayout={RawLayout} mappedLayout={MappedLayout}",
					primaryScreen.Screen, primaryScreen.Layout, MapScreenLayoutSourceTypeToLayoutStyle(primaryScreen.Layout));
				if (primaryScreen.LayoutCtrlInfos != null)
				{
					foreach (var ctrl in primaryScreen.LayoutCtrlInfos)
					{
						this.LogDebug("  ctrl: rawLayout={RawLayout} mappedLayout={MappedLayout} enable={Enable} visible={Visible}",
							ctrl.Layout, MapScreenLayoutSourceTypeToLayoutStyle(ctrl.Layout), ctrl.Enable, ctrl.Visible);
					}
				}

				// Confirmed live via ScreenLayoutStatus ctrl infos: the controller exposes Multi-Speaker as
				// a distinct layout whose ScreenLayoutSourceType is -1 (the SDK has no dedicated enum value
				// for it, so it arrives as None). Dynamic Gallery is the separate DynamicView (10).
				var mappedCurrentLayout = MapScreenLayoutSourceTypeToLayoutStyle(primaryScreen.Layout);
				if (mappedCurrentLayout != zConfiguration.eLayoutStyle.None)
				{
					LastSelectedLayout = mappedCurrentLayout;
					LocalLayoutFeedback.FireUpdate();
				}

				// Compute available layouts from the ctrlInfos (enabled entries).
				ComputeAvailableLayoutsFromScreenStatus(primaryScreen, e.IsInContentOnly);
			}

			// Update content swap state from the SDK booleans.
			_contentSwappedWithThumbnail = e.IsInFloatingShareContent;
			ContentSwappedWithThumbnailFeedback.FireUpdate();
			CanSwapContentWithThumbnailFeedback.FireUpdate();

			ScreenLayoutStatusChanged?.Invoke(this, e);
			OnLayoutInfoChanged();
			OnShareStateForShareLayout();
		}

		// DynamicLayoutType last reported by the SDK (SpeakersOnBottom=0/Middle=1/Top=2, Unknown=-1).
		// On single-screen rooms this sub-option distinguishes Dynamic Gallery from Multi-Speaker.
		private int _dynamicLayoutOption = -1;

		private void OnControllerDynamicLayoutOptionChanged(object sender, SdkEventArgs e)
		{
			_dynamicLayoutOption = e.ErrorCode;
			this.LogInformation("DynamicLayoutOption changed: {DynamicLayoutOption} (SpeakersOnBottom=0/Middle=1/Top=2)", _dynamicLayoutOption);
		}

		// Layout tracer: logs every layout-helper notification the SDK delivers, for investigating layout behavior.
		private void OnControllerLayoutDiagnostic(object sender, SdkEventArgs e)
		{
			this.LogDebug("LayoutTrace: {Message} (hint={Hint})", e.Message, e.ErrorCode);
		}

		/// <summary>
		/// Maps SDK ScreenLayoutSourceType int to the Essentials eLayoutStyle enum.
		/// </summary>
		private static zConfiguration.eLayoutStyle MapScreenLayoutSourceTypeToLayoutStyle(int screenLayoutSourceType)
		{
			// ScreenLayoutSourceType: None=-1, ActiveVideo=0, SelfVideo=1, PinnedVideo=2,
			// Spotlight=3, Gallery=4, SharedContent=5, Background=6, LocalView=7,
			// ImmersiveView=8, ZoomAppsView=9, DynamicView=10, ThumbnailView=11, ThumbnailShareView=12
			//
			// Confirmed live via ScreenLayoutStatus ctrl infos: Dynamic Gallery = DynamicView (10) and
			// Multi-Speaker = -1 (the SDK exposes no dedicated ScreenLayoutSourceType for Multi-Speaker,
			// so it reports as None/-1). These are distinct, separately-selectable layouts.
			return screenLayoutSourceType switch
			{
				-1 => zConfiguration.eLayoutStyle.MultiSpeaker,     // No dedicated SDK type -- controller's "Multi-Speaker"
				0 => zConfiguration.eLayoutStyle.Speaker,           // ActiveVideo = single active-speaker view
				4 => zConfiguration.eLayoutStyle.Gallery,           // Gallery
				5 => zConfiguration.eLayoutStyle.ContentOnly,       // SharedContent = "Shared Content"
				10 => zConfiguration.eLayoutStyle.Dynamic,          // DynamicView = "Dynamic Gallery"
				11 => zConfiguration.eLayoutStyle.Thumbnail,        // ThumbnailView
				12 => zConfiguration.eLayoutStyle.ThumbnailAndShare, // ThumbnailShareView = "Thumbnail & Share"
				_ => zConfiguration.eLayoutStyle.None,
			};
		}

		/// <summary>
		/// Computes AvailableLayouts from the SDK's ScreenLayoutCtrlInfo entries for the primary screen.
		/// </summary>
		private void ComputeAvailableLayoutsFromScreenStatus(ScreenLayoutInfoEventArgs screenInfo, bool isInContentOnly)
		{
			if (screenInfo.LayoutCtrlInfos == null || screenInfo.LayoutCtrlInfos.Length == 0)
				return; // keep previous available layouts if no ctrl info provided

			var available = zConfiguration.eLayoutStyle.None;
			foreach (var ctrl in screenInfo.LayoutCtrlInfos)
			{
				if (!ctrl.Enable) continue;
				var mapped = MapScreenLayoutSourceTypeToLayoutStyle(ctrl.Layout);
				if (mapped == zConfiguration.eLayoutStyle.None)
					continue;
				// Multi-Speaker can't be commanded via the SDK (maps to None/-1); hide the button unless opted in.
				if (mapped == zConfiguration.eLayoutStyle.MultiSpeaker && !_props.ShowMultiSpeakerLayout)
					continue;
				available |= mapped;
			}

			// CancelContentOnly is only meaningful while the SDK reports we're actually IN content-only
			// mode -- ContentOnly being an available *destination* doesn't mean there's anything to cancel.
			if (isInContentOnly)
				available |= zConfiguration.eLayoutStyle.CancelContentOnly;

			if (available != zConfiguration.eLayoutStyle.None)
				AvailableLayouts = available;
		}

		private void OnControllerSipCallStatusChanged(object sender, SIPCall e)
		{
			if (e == null) return;
			_activeSipCallId = e.CallID;
			_sdkPhoneOffHook = !System.Array.Exists(_sipTerminalStatuses, s => s == e.Status);
			_sdkSipCallerName = e.PeerDisplayName ?? string.Empty;
			_sdkSipCallerNumber = e.PeerNumber ?? string.Empty;
			this.LogDebug("SipCallStatusChanged: CallID={CallId} Status={Status} OffHook={OffHook} Caller={Caller}",
				_activeSipCallId, e.Status, _sdkPhoneOffHook, _sdkSipCallerName);
			PhoneOffHookFeedback.FireUpdate();
			CallerIdNameFeedback.FireUpdate();
			CallerIdNumberFeedback.FireUpdate();
		}

		private static System.Collections.Generic.List<Participant> MapParticipants(ParticipantInfo[] participants)
		{
			var list = new System.Collections.Generic.List<Participant>();
			foreach (var info in participants)
				list.Add(MapParticipant(info));
			return list;
		}

		private static Participant MapParticipant(ParticipantInfo info)
		{
			return new Participant
			{
				UserId = info.UserID,
				Name = info.UserName,
				IsHost = info.IsHost,
				IsCohost = info.IsCohost,
				IsMyself = info.IsMySelf,
				AudioMuteFb = info.AudioMuted,
				VideoMuteFb = !info.VideoSending,
				HandIsRaisedFb = info.HandRaised,
				// IsPinnedFb: SDK does not expose per-participant pin state; defaults to false.
			};
		}

		/// <summary>
		/// Updates an existing roster <see cref="Participant"/> in place from a fresh
		/// <see cref="ParticipantInfo"/>. Shared by the UserJoined (role re-send) and UserUpdated
		/// handlers so both apply the same mutable fields (role, mute, hand). UserId/IsMyself are
		/// identity and never change for a given roster entry, so they are left untouched.
		/// </summary>
		private static void UpdateParticipantFrom(Participant existing, ParticipantInfo info)
		{
			existing.Name = info.UserName;
			existing.IsHost = info.IsHost;
			existing.IsCohost = info.IsCohost;
			existing.AudioMuteFb = info.AudioMuted;
			existing.VideoMuteFb = !info.VideoSending;
			existing.HandIsRaisedFb = info.HandRaised;
		}


		protected override void OnCallStatusChange(CodecActiveCallItem item)
		{
			base.OnCallStatusChange(item);
			InCallFeedback.FireUpdate();
		}

		/// <summary>
		/// Starts sharing the HDMI source (Zoom "black magic" cable share), also shown locally.
		/// Requires an HDMI source physically connected and providing an active signal -- if not,
		/// the SDK call fails immediately (see isBlackMagicConnected/isBlackMagicDataAvailable).
		/// </summary>
		public override void StartSharing()
		{
			if (!Status.Sharing.isBlackMagicConnected || !Status.Sharing.isBlackMagicDataAvailable)
			{
				this.LogWarning("StartSharing: no HDMI source detected (connected={Connected} dataAvailable={DataAvailable}) — ShareBlackMagic will likely fail",
					Status.Sharing.isBlackMagicConnected, Status.Sharing.isBlackMagicDataAvailable);
			}
			_weRequestedHdmiSharing = true;
			_controller.ShareBlackMagic(true, false);
		}

		/// <summary>
		/// Stops sharing the current presentation
		/// </summary>
		public override void StopSharing()
		{
			_weRequestedHdmiSharing = false;
			_controller.StopShare();
		}



		public override void PrivacyModeOn()
		{
			_controller.SetAudioMute(true);
		}

		public override void PrivacyModeOff()
		{
			_controller.SetAudioMute(false);
		}

		public override void PrivacyModeToggle()
		{
			if (PrivacyModeIsOnFeedback.BoolValue)
			{
				PrivacyModeOff();
			}
			else
			{
				PrivacyModeOn();
			}
		}

		// The ZRC SDK speaker volume float is on a 0-255 scale (confirmed on hardware .116, and matches
		// the SDK's far-end audio volume range [0,255]); the Essentials slider is 0-65535. Both
		// conversions live here. The SDK has no discrete output-mute, so MuteOn/Off set/restore the volume.
		private const float SdkSpeakerVolumeMax = 255f;
		private const ushort VolumeStep = 3277; // ~5% of 65535 per VolumeUp/Down press

		private static float LevelToSdkVolume(ushort level) => level / 65535f * SdkSpeakerVolumeMax;

		public override void MuteOff()
		{
			if (SpeakerVolumeIsFixed(nameof(MuteOff))) return;
			if (!_sdkSpeakerMuted) return;
			_sdkSpeakerMuted = false;
			_controller.SetSpeakerVolume(LevelToSdkVolume(_sdkSpeakerVolumeLevel));
			MuteFeedback.FireUpdate();
		}

		public override void MuteOn()
		{
			if (SpeakerVolumeIsFixed(nameof(MuteOn))) return;
			if (_sdkSpeakerMuted) return;
			_sdkSpeakerMuted = true;
			_controller.SetSpeakerVolume(0f); // no discrete SDK output-mute; zero the volume, restore on unmute
			MuteFeedback.FireUpdate();
		}

		public override void MuteToggle()
		{
			if (MuteFeedback.BoolValue)
			{
				MuteOff();
			}
			else
			{
				MuteOn();
			}
		}


		/// <summary>
		/// Increments the voluem
		/// </summary>
		/// <param name="pressRelease"></param>
		public override void VolumeUp(bool pressRelease)
		{
			if (!pressRelease) return; // act on press; continuous press-hold ramp could be added with a timer
			SetVolume((ushort)Math.Min(ushort.MaxValue, _sdkSpeakerVolumeLevel + VolumeStep));
		}

		/// <summary>
		/// Decrements the volume
		/// </summary>
		/// <param name="pressRelease"></param>
		public override void VolumeDown(bool pressRelease)
		{
			if (!pressRelease) return;
			SetVolume((ushort)Math.Max(0, _sdkSpeakerVolumeLevel - VolumeStep));
		}

		/// <summary>
		/// Scales the level and sets the codec to the specified level within its range
		/// </summary>
		/// <param name="level">level from slider (0-65535 range)</param>
		public override void SetVolume(ushort level)
		{
			if (SpeakerVolumeIsFixed(nameof(SetVolume))) return;
			_sdkSpeakerVolumeLevel = level;
			_sdkSpeakerMuted = false; // actively setting volume clears mute
			_controller.SetSpeakerVolume(LevelToSdkVolume(level));
			VolumeLevelFeedback.FireUpdate();
			MuteFeedback.FireUpdate();
		}

		/// <summary>
		/// Recalls the default volume on the codec
		/// </summary>
		public void VolumeSetToDefault()
		{
		}

		/// <summary>
		/// 
		/// </summary>
		public override void StandbyActivate()
		{
			// No corresponding function on device
		}

		/// <summary>
		/// 
		/// </summary>
		public override void StandbyDeactivate()
		{
			// No corresponding function on device
		}

		public override void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
		{
			var joinMap = new ZoomRoomJoinMap(joinStart);

			var customJoins = JoinMapHelper.TryGetJoinMapAdvancedForDevice(joinMapKey);

			if (customJoins != null)
			{
				joinMap.SetCustomJoinData(customJoins);
			}

			if (bridge != null)
			{
				bridge.AddJoinMap(Key, joinMap);
			}

			LinkVideoCodecToApi(this, trilist, joinMap);

			LinkZoomRoomToApi(trilist, joinMap);
		}

		/// <summary>
		/// Links all the specific Zoom functionality to the API bridge
		/// </summary>
		/// <param name="trilist"></param>
		/// <param name="joinMap"></param>
		public void LinkZoomRoomToApi(BasicTriList trilist, ZoomRoomJoinMap joinMap)
		{
			// Manual phonebook fetch on the input side of join 100 (core wires the search-busy FB
			// on the output side of the same join — input/output are independent). This is the only
			// trigger to load contacts when DisablePhonebookAutoDownload is set.
			trilist.SetSigFalseAction(joinMap.PhonebookGet.JoinNumber,
				() =>
				{
					lock (_directoryLock) _directoryContactsById.Clear();
					StartPhonebookFetch();
				});

			var meetingInfoCodec = this as IHasMeetingInfo;
			if (meetingInfoCodec != null)
			{
				if (meetingInfoCodec.MeetingInfo != null)
				{
					trilist.SetBool(joinMap.MeetingCanRecord.JoinNumber, meetingInfoCodec.MeetingInfo.CanRecord);
				}

				meetingInfoCodec.MeetingInfoChanged += (o, a) =>
					{
						trilist.SetBool(joinMap.MeetingCanRecord.JoinNumber, a.Info.CanRecord);
					};
			}

			var recordingCodec = this as IHasMeetingRecordingWithPrompt;
			if (recordingCodec != null)
			{
				trilist.SetSigFalseAction(joinMap.StartRecording.JoinNumber, () => recordingCodec.StartRecording());
				trilist.SetSigFalseAction(joinMap.StopRecording.JoinNumber, () => recordingCodec.StopRecording());

				recordingCodec.MeetingIsRecordingFeedback.LinkInputSig(trilist.BooleanInput[joinMap.StartRecording.JoinNumber]);
				recordingCodec.MeetingIsRecordingFeedback.LinkComplementInputSig(trilist.BooleanInput[joinMap.StopRecording.JoinNumber]);

				trilist.SetSigFalseAction(joinMap.RecordingPromptAgree.JoinNumber, () => recordingCodec.RecordingPromptAcknowledgement(true));
				trilist.SetSigFalseAction(joinMap.RecordingPromptDisagree.JoinNumber, () => recordingCodec.RecordingPromptAcknowledgement(false));

				recordingCodec.RecordConsentPromptIsVisible.LinkInputSig(trilist.BooleanInput[joinMap.RecordConsentPromptIsVisible.JoinNumber]);
			}

			var layoutsCodec = this as IHasZoomRoomLayouts;
			if (layoutsCodec != null)
			{
				layoutsCodec.LayoutInfoChanged += (o, a) =>
				{
					trilist.SetBool(joinMap.LayoutGalleryIsAvailable.JoinNumber,
						zConfiguration.eLayoutStyle.Gallery == (a.AvailableLayouts & zConfiguration.eLayoutStyle.Gallery));

					trilist.SetBool(joinMap.LayoutSpeakerIsAvailable.JoinNumber,
						zConfiguration.eLayoutStyle.Speaker == (a.AvailableLayouts & zConfiguration.eLayoutStyle.Speaker));

					trilist.SetBool(joinMap.LayoutStripIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.Thumbnail
																			   ==
																			   (a.AvailableLayouts & zConfiguration.eLayoutStyle.Thumbnail));
					trilist.SetBool(joinMap.LayoutShareAllIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.ContentOnly
																				  ==
																				  (a.AvailableLayouts &
																				   zConfiguration.eLayoutStyle.ContentOnly));
					trilist.SetBool(joinMap.LayoutCancelContentOnlyIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.CancelContentOnly
																				  ==
																				  (a.AvailableLayouts &
																				   zConfiguration.eLayoutStyle.CancelContentOnly));
					trilist.SetBool(joinMap.LayoutDynamicIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.Dynamic
																				  ==
																				  (a.AvailableLayouts &
																				   zConfiguration.eLayoutStyle.Dynamic));
					trilist.SetBool(joinMap.LayoutMultiSpeakerIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.MultiSpeaker
																				  ==
																				  (a.AvailableLayouts &
																				   zConfiguration.eLayoutStyle.MultiSpeaker));
					trilist.SetBool(joinMap.LayoutThumbnailAndShareIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.ThumbnailAndShare
																				  ==
																				  (a.AvailableLayouts &
																				   zConfiguration.eLayoutStyle.ThumbnailAndShare));

					// pass the names used to set the layout through the bridge
					trilist.SetString(joinMap.LayoutGalleryIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.Gallery.ToString());
					trilist.SetString(joinMap.LayoutSpeakerIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.Speaker.ToString());
					trilist.SetString(joinMap.LayoutStripIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.Thumbnail.ToString());
					trilist.SetString(joinMap.LayoutShareAllIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.ContentOnly.ToString());
					trilist.SetString(joinMap.LayoutCancelContentOnlyIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.CancelContentOnly.ToString());
					trilist.SetString(joinMap.LayoutDynamicIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.Dynamic.ToString());
					trilist.SetString(joinMap.LayoutMultiSpeakerIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.MultiSpeaker.ToString());
					trilist.SetString(joinMap.LayoutThumbnailAndShareIsAvailable.JoinNumber, zConfiguration.eLayoutStyle.ThumbnailAndShare.ToString());
				};

				trilist.SetSigFalseAction(joinMap.SwapContentWithThumbnail.JoinNumber, () => layoutsCodec.SwapContentWithThumbnail());

				layoutsCodec.CanSwapContentWithThumbnailFeedback.LinkInputSig(
					trilist.BooleanInput[joinMap.CanSwapContentWithThumbnail.JoinNumber]);
				layoutsCodec.ContentSwappedWithThumbnailFeedback.LinkInputSig(
					trilist.BooleanInput[joinMap.SwapContentWithThumbnail.JoinNumber]);

				layoutsCodec.LayoutViewIsOnFirstPageFeedback.LinkInputSig(
					trilist.BooleanInput[joinMap.LayoutIsOnFirstPage.JoinNumber]);
				layoutsCodec.LayoutViewIsOnLastPageFeedback.LinkInputSig(trilist.BooleanInput[joinMap.LayoutIsOnLastPage.JoinNumber]);
				trilist.SetSigFalseAction(joinMap.LayoutTurnToNextPage.JoinNumber, () => layoutsCodec.LayoutTurnNextPage());
				trilist.SetSigFalseAction(joinMap.LayoutTurnToPreviousPage.JoinNumber, () => layoutsCodec.LayoutTurnPreviousPage());
				trilist.SetSigFalseAction(joinMap.GetAvailableLayouts.JoinNumber, () => layoutsCodec.GetAvailableLayouts());

				trilist.SetStringSigAction(joinMap.GetSetCurrentLayout.JoinNumber, (s) =>
				{
					try
					{
						var style = (zConfiguration.eLayoutStyle)Enum.Parse(typeof(zConfiguration.eLayoutStyle), s, true);
						SetLayout(style);
					}
					catch (Exception e)
					{
						this.LogInformation("Unable to parse '{LayoutStyleString}' to zConfiguration.eLayoutStyle: {Exception}", s, e);
					}
				});

				layoutsCodec.LocalLayoutFeedback.LinkInputSig(trilist.StringInput[joinMap.GetSetCurrentLayout.JoinNumber]);
			}

			var pinCodec = this as IHasParticipantPinUnpin;
			if (pinCodec != null)
			{
				pinCodec.NumberOfScreensFeedback.LinkInputSig(trilist.UShortInput[joinMap.NumberOfScreens.JoinNumber]);

				// Set the value of the local property to be used when pinning a participant
				trilist.SetUShortSigAction(joinMap.ScreenIndexToPinUserTo.JoinNumber, (u) => ScreenIndexToPinUserTo = u);
			}

			var layoutSizeCodec = this as IHasSelfviewSize;
			if (layoutSizeCodec != null)
			{
				trilist.SetSigFalseAction(joinMap.GetSetSelfviewPipSize.JoinNumber, layoutSizeCodec.SelfviewPipSizeToggle);
				trilist.SetStringSigAction(joinMap.GetSetSelfviewPipSize.JoinNumber, (s) =>
				{
					try
					{
						var size = (zConfiguration.eLayoutSize)Enum.Parse(typeof(zConfiguration.eLayoutSize), s, true);
						var cmd = SelfviewPipSizes.FirstOrDefault(c => c.Command.Equals(size.ToString()));
						SelfviewPipSizeSet(cmd);
					}
					catch (Exception e)
					{
						this.LogInformation("Unable to parse '{LayoutSizeString}' to zConfiguration.eLayoutSize: {Exception}", s, e);
					}
				});

				layoutSizeCodec.SelfviewPipSizeFeedback.LinkInputSig(trilist.StringInput[joinMap.GetSetSelfviewPipSize.JoinNumber]);
			}

			MeetingInfoChanged += (device, args) =>
			{
				trilist.SetString(joinMap.MeetingInfoId.JoinNumber, args.Info.Id);
				trilist.SetString(joinMap.MeetingInfoHost.JoinNumber, args.Info.Host);
				trilist.SetString(joinMap.MeetingInfoPassword.JoinNumber, args.Info.Password);
				trilist.SetBool(joinMap.IsHost.JoinNumber, args.Info.IsHost);
				trilist.SetBool(joinMap.ShareOnlyMeeting.JoinNumber, args.Info.IsSharingMeeting);
				trilist.SetBool(joinMap.WaitingForHost.JoinNumber, args.Info.WaitingForHost);
				//trilist.SetString(joinMap.CurrentSource.JoinNumber, args.Info.ShareStatus);
			};

			trilist.SetSigFalseAction(joinMap.StartMeetingNow.JoinNumber, () => StartMeeting(0));
			trilist.SetSigFalseAction(joinMap.ShareOnlyMeeting.JoinNumber, StartSharingOnlyMeeting);
			trilist.SetSigFalseAction(joinMap.StartNormalMeetingFromSharingOnlyMeeting.JoinNumber, StartNormalMeetingFromSharingOnlyMeeting);

			trilist.SetStringSigAction(joinMap.SubmitPassword.JoinNumber, SubmitPassword);

			// Subscribe to call status to clear ShowPasswordPrompt when in meeting
			this.CallStatusChange += (o, a) =>
				{
					if (a.CallItem.Status == eCodecCallStatus.Connected || a.CallItem.Status == eCodecCallStatus.Disconnected)
					{
						trilist.SetBool(joinMap.MeetingPasswordRequired.JoinNumber, false);
					}

				};

			trilist.SetSigFalseAction(joinMap.CancelJoinAttempt.JoinNumber, () =>
			{
				trilist.SetBool(joinMap.MeetingPasswordRequired.JoinNumber, false);
				EndAllCalls();
			});

			PasswordRequired += (devices, args) =>
			{
				this.LogDebug("***********************************PaswordRequired. Message: {Message} Cancelled: {Cancelled} Last Incorrect: {LastIncorrect} Failed: {Failed}", args.Message, args.LoginAttemptCancelled, args.LastAttemptWasIncorrect, args.LoginAttemptFailed);

				if (args.LoginAttemptCancelled)
				{
					trilist.SetBool(joinMap.MeetingPasswordRequired.JoinNumber, false);
					return;
				}

				if (!string.IsNullOrEmpty(args.Message))
				{
					trilist.SetString(joinMap.PasswordPromptMessage.JoinNumber, args.Message);
				}

				if (args.LoginAttemptFailed)
				{
					// login attempt failed
					return;
				}

				trilist.SetBool(joinMap.PasswordIncorrect.JoinNumber, args.LastAttemptWasIncorrect);
				trilist.SetBool(joinMap.MeetingPasswordRequired.JoinNumber, true);
			};

			trilist.OnlineStatusChange += (device, args) =>
			{
				if (!args.DeviceOnLine) return;

				ComputeAvailableLayouts();
				layoutsCodec.LocalLayoutFeedback.FireUpdate();
				layoutsCodec.CanSwapContentWithThumbnailFeedback.FireUpdate();
				layoutsCodec.ContentSwappedWithThumbnailFeedback.FireUpdate();
				layoutsCodec.LayoutViewIsOnFirstPageFeedback.FireUpdate();
				layoutsCodec.LayoutViewIsOnLastPageFeedback.FireUpdate();
				pinCodec.NumberOfScreensFeedback.FireUpdate();
				layoutSizeCodec.SelfviewPipSizeFeedback.FireUpdate();
			};

			var wirelessInfoCodec = this as IZoomWirelessShareInstructions;
			if (wirelessInfoCodec != null)
			{
				if (Status != null && Status.Sharing != null)
				{
					SetSharingStateJoins(Status.Sharing, trilist, joinMap);
				}

				wirelessInfoCodec.ShareInfoChanged += (o, a) =>
					{
						SetSharingStateJoins(a.SharingStatus, trilist, joinMap);
					};
			}
		}

		void SetSharingStateJoins(zStatus.Sharing state, BasicTriList trilist, ZoomRoomJoinMap joinMap)
		{
			trilist.SetBool(joinMap.IsSharingAirplay.JoinNumber, state.isAirHostClientConnected);
			trilist.SetBool(joinMap.IsSharingHdmi.JoinNumber, state.isBlackMagicConnected || state.isDirectPresentationConnected);

			trilist.SetString(joinMap.DisplayState.JoinNumber, state.dispState.ToString());
			trilist.SetString(joinMap.AirplayShareCode.JoinNumber, state.password);
			trilist.SetString(joinMap.LaptopShareKey.JoinNumber, state.directPresentationSharingKey);
			trilist.SetString(joinMap.WifiName.JoinNumber, state.wifiName);
			trilist.SetString(joinMap.ServerName.JoinNumber, state.serverName);
		}

		public override void ExecuteSwitch(object selector)
		{
			var action = selector as Action;
			if (action == null)
			{
				return;
			}

			action();
		}

		public void AcceptCall()
		{
			var incomingCall =
				ActiveCalls.FirstOrDefault(
					c => c.Status.Equals(eCodecCallStatus.Ringing) && c.Direction.Equals(eCodecCallDirection.Incoming));

			if (incomingCall != null)
				AcceptCall(incomingCall);
		}

		// A ringing (unanswered) invite isn't actually "in a call" -- only report true once a call
		// is answered/connected, so mobile control doesn't show an in-call state for a pending invite.
		public override bool IsInCall =>
			ActiveCalls != null && ActiveCalls.Any(c => c.IsActiveCall && c.Status != eCodecCallStatus.Ringing);

		public override void AcceptCall(CodecActiveCallItem call)
		{
			if (call == null) return;

			// An incoming ringing call is a meeting invite — accept it via AnswerMeetingInvite (there
			// is no meeting number in the invite event to JoinMeeting with). The resulting InMeeting
			// status promotes this ActiveCall to Connected.
			if (call.Direction == eCodecCallDirection.Incoming && call.Status == eCodecCallStatus.Ringing)
			{
				_pendingInviteTimeoutTimer?.Stop();
				_controller.AnswerMeetingInvite(true);
				_pendingInviteCall = null;
				return;
			}

			_controller.JoinMeeting(call.Id);
		}

		public void RejectCall()
		{
			var incomingCall =
				ActiveCalls.FirstOrDefault(
					c => c.Status.Equals(eCodecCallStatus.Ringing) && c.Direction.Equals(eCodecCallDirection.Incoming));

			if (incomingCall != null)
				RejectCall(incomingCall);
		}

		public override void RejectCall(CodecActiveCallItem call)
		{
			// Decline the incoming meeting invite via the SDK (answers the cached invite with
			// accept=false), then clear the ringing ActiveCall.
			_pendingInviteTimeoutTimer?.Stop();
			_controller.AnswerMeetingInvite(false);

			var item = call ?? _pendingInviteCall;
			if (item != null)
			{
				item.Status = eCodecCallStatus.Disconnected;
				ActiveCalls.Remove(item);
				OnCallStatusChange(item);
			}
			_pendingInviteCall = null;
		}

		public override void Dial(Meeting meeting)
		{
			this.LogInformation("Dialing meeting.Id: {MeetingId} Title: {MeetingTitle}", meeting.Id, meeting.Title);
			// Capture the scheduled meeting's title now so MeetingInfo.Name (see ApplyMeetingStatus)
			// reflects it once the meeting connects - _currentMeetingName was otherwise never set by
			// anything, so MeetingInfo.Name was always empty regardless of how the meeting was joined.
			_currentMeetingName = meeting.Title ?? string.Empty;
			BeginPendingRosterAdmission();
			_controller.JoinMeeting(meeting.Id);
		}

		public override void Dial(string number)
		{
			this.LogDebug("Dialing number: {Number}", number);
			BeginPendingRosterAdmission();
			_controller.JoinMeeting(number);
		}

		/// <summary>
		/// Dials a meeting with a password
		/// </summary>
		public override void Dial(string number, string password)
		{
			this.LogDebug("Dialing meeting number: {Number} with password: {Password}", number, password);
			BeginPendingRosterAdmission();
			_controller.JoinMeetingWithPassword(number, password);
		}

		/// <summary>
		/// Invites a contact to either a new meeting (if not already in a meeting) or the current meeting.
		/// Currently only invites a single user
		/// </summary>
		/// <param name="contact"></param>
		public override void Dial(IInvitableContact contact)
		{
			var ic = contact as InvitableDirectoryContact;

			if (ic == null || string.IsNullOrEmpty(ic.ContactId))
			{
				this.LogWarning("Dial(IInvitableContact): contact has no ContactId");
				return;
			}

			if (IsHiddenContactId(ic.ContactId))
			{
				this.LogWarning("Dial(IInvitableContact): {ContactId} is a hidden contact — not invited", ic.ContactId);
				return;
			}

			var contactIds = new[] { ic.ContactId };
			if (IsInCall)
			{
				this.LogInformation("Inviting contact {ContactId} to current meeting", ic.ContactId);
				_controller.InviteAttendees(contactIds);
			}
			else
			{
				this.LogInformation("Starting new meeting with contact {ContactId}", ic.ContactId);
				_controller.MeetWithIMUsers(contactIds);
			}
		}

		/// <summary>
		/// Invites contacts to a new meeting for a specified duration
		/// </summary>
		/// <param name="contacts"></param>
		/// <param name="duration"></param>
		public void InviteContactsToNewMeeting(List<InvitableDirectoryContact> contacts, uint duration)
		{
			var contactIds = GetContactIds(contacts);
			if (contactIds.Length == 0)
			{
				this.LogWarning("InviteContactsToNewMeeting: no valid contact IDs");
				return;
			}

			this.LogInformation("Starting new meeting with {Count} contact(s)", contactIds.Length);
			_controller.MeetWithIMUsers(contactIds);
		}

		/// <summary>
		/// Invites contacts to an existing meeting
		/// </summary>
		/// <param name="contacts"></param>
		public void InviteContactsToExistingMeeting(List<InvitableDirectoryContact> contacts)
		{
			var contactIds = GetContactIds(contacts);
			if (contactIds.Length == 0)
			{
				this.LogWarning("InviteContactsToExistingMeeting: no valid contact IDs");
				return;
			}

			this.LogInformation("Inviting {Count} contact(s) to current meeting", contactIds.Length);
			_controller.InviteAttendees(contactIds);
		}

		// Extracts the non-empty contact IDs from a list of invitable contacts, leaving out hidden ones.
		private string[] GetContactIds(List<InvitableDirectoryContact> contacts)
		{
			if (contacts == null) return Array.Empty<string>();
			return contacts
				.Where(c => c != null && !string.IsNullOrEmpty(c.ContactId))
				.Select(c => c.ContactId)
				.Where(id =>
				{
					if (!IsHiddenContactId(id)) return true;
					this.LogWarning("{ContactId} is a hidden contact — not invited", id);
					return false;
				})
				.ToArray();
		}

		// The name a contact is listed under: its screen name, else first + last name.
		private static string ContactDisplayName(ContactInfo c)
		{
			return !string.IsNullOrEmpty(c.ScreenName)
				? c.ScreenName
				: string.Join(" ", new[] { c.FirstName, c.LastName }.Where(s => !string.IsNullOrEmpty(s)));
		}

		private bool IsHiddenContactName(string name)
		{
			return _hiddenContactNames.Count > 0
				&& !string.IsNullOrWhiteSpace(name)
				&& _hiddenContactNames.Contains(name.Trim());
		}

		// Resolves a contact ID against the downloaded directory; an ID that is not in it is not hidden.
		private bool IsHiddenContactId(string contactId)
		{
			if (_hiddenContactNames.Count == 0 || string.IsNullOrEmpty(contactId)) return false;

			ContactInfo contact;
			lock (_directoryLock)
			{
				if (!_directoryContactsById.TryGetValue(contactId, out contact)) return false;
			}
			return IsHiddenContactName(ContactDisplayName(contact));
		}

		/// <summary>
		/// Console/test helper: dumps the downloaded directory contacts with their contact IDs to the
		/// log, so a `contactId` can be copied for <see cref="InviteContactById"/>. Mirrors
		/// <see cref="LogParticipants"/>. The directory auto-downloads on connect.
		/// </summary>
		public void LogDirectory()
		{
			List<ContactInfo> snapshot;
			lock (_directoryLock) snapshot = _directoryContactsById.Values.ToList();

			if (snapshot.Count == 0)
			{
				this.LogInformation("Directory: (empty — not yet downloaded, or phonebook auto-download disabled)");
				return;
			}

			this.LogInformation("Directory ({Count}):", snapshot.Count);
			foreach (var c in snapshot)
			{
				this.LogInformation(
					"  contactId=\"{ContactId}\" name=\"{Name}\" email=\"{Email}\" sip=\"{Sip}\"",
					c.ContactID,
					string.IsNullOrEmpty(c.ScreenName) ? string.Format("{0} {1}", c.FirstName, c.LastName).Trim() : c.ScreenName,
					c.Email, c.SipPhoneNumber);
			}
		}

		/// <summary>
		/// Console/test helper: invites a directory contact by its <paramref name="contactId"/>. If this
		/// room is in a meeting the contact is invited to it (<c>InviteAttendees</c>); otherwise a new
		/// meeting is started with them (<c>MeetWithIMUsers</c>) — same routing as
		/// <see cref="Dial(IInvitableContact)"/>, but callable from the console with a plain string.
		/// Get IDs from <see cref="LogDirectory"/>.
		/// </summary>
		public void InviteContactById(string contactId)
		{
			if (string.IsNullOrEmpty(contactId))
			{
				this.LogWarning("InviteContactById: no contactId supplied");
				return;
			}

			bool known;
			lock (_directoryLock) known = _directoryContactsById.ContainsKey(contactId);
			if (!known)
				this.LogWarning("InviteContactById: {ContactId} not in the downloaded directory — sending anyway", contactId);

			if (IsHiddenContactId(contactId))
			{
				this.LogWarning("InviteContactById: {ContactId} is a hidden contact — not invited", contactId);
				return;
			}

			var ids = new[] { contactId };
			if (IsInCall)
			{
				this.LogInformation("Inviting contact {ContactId} to the current meeting", contactId);
				_controller.InviteAttendees(ids);
			}
			else
			{
				this.LogInformation("Starting a new meeting with contact {ContactId}", contactId);
				_controller.MeetWithIMUsers(ids);
			}
		}


		/// <summary>
		/// Starts a PMI Meeting for the specified duration (or default meeting duration if 0 is specified)
		/// </summary>
		/// <param name="duration">duration of meeting</param>
		public void StartMeeting(uint duration)
		{
			// A room that starts its own instant meeting is unconditionally its host from the first
			// instant - unlike joining a meeting, there is no scenario where StartInstantMeeting
			// succeeds and this room is not the host. Assert that directly rather than depending on
			// RefreshHostFromParticipants' roster-based fallback, which only runs from a
			// participant-list-changed callback: a meeting nobody else ever joins may never produce
			// one, leaving _sdkIsHost stuck false and EndCall/EndAllCalls calling LeaveMeeting()
			// instead of EndMeetingForAll() - the room starts a meeting it then can't end
			// (#confirmed via live testing).
			_sdkIsHost = true;
			BeginPendingRosterAdmission();
			_controller.StartInstantMeeting();
		}

		public void LeaveMeeting()
		{
			this.LogInformation("LeaveMeeting: calling ZrcSdk LeaveMeeting");
			_meetingPasswordRequired = false;
			try
			{
				_controller.LeaveMeeting();
			}
			catch (Exception ex)
			{
				this.LogException(ex, "LeaveMeeting: ZrcSdk LeaveMeeting threw");
			}
		}

		/// <summary>
		/// Ends the current meeting for all participants (host action), as opposed to <see cref="LeaveMeeting"/>
		/// which only removes this Zoom Room from the meeting.
		/// </summary>
		public void EndMeetingForAll()
		{
			this.LogInformation("EndMeetingForAll: calling ZrcSdk EndMeeting (isHost={IsHost}, isCoHost={IsCoHost})", _sdkIsHost, _sdkIsCoHost);
			_meetingPasswordRequired = false;
			try
			{
				_controller.EndMeeting();
			}
			catch (Exception ex)
			{
				this.LogException(ex, "EndMeetingForAll: ZrcSdk EndMeeting threw");
			}
		}

		/// <summary>
		/// Mobile Control's generic "end this call" action (from IHasCodecCallControls) for the host
		/// must actually end the meeting for everyone, not just remove this Zoom Room from it - this
		/// previously always called LeaveMeeting() regardless of role, so a host pressing what the UI
		/// labeled "End Call" only ever left the meeting (#confirmed via live testing). Ending for all
		/// is a host-only control in Zoom, so a co-host (like any other participant) leaves instead.
		/// </summary>
		public override void EndCall(CodecActiveCallItem call)
		{
			this.LogInformation("EndCall: isHost={IsHost}, isCoHost={IsCoHost}", _sdkIsHost, _sdkIsCoHost);
			if (_sdkIsHost)
			{
				EndMeetingForAll();
			}
			else
			{
				LeaveMeeting();
			}
		}

		/// <summary>Same host-only rule as <see cref="EndCall"/> - see its remarks.</summary>
		public override void EndAllCalls()
		{
			this.LogInformation("EndAllCalls: isHost={IsHost}, isCoHost={IsCoHost}", _sdkIsHost, _sdkIsCoHost);
			if (_sdkIsHost)
			{
				EndMeetingForAll();
			}
			else
			{
				LeaveMeeting();
			}
		}

		public override void SendDtmf(string s)
		{
			SendDtmfToPhone(s);
		}

		// Kicks off the phonebook directory download. Results accumulate into _directoryContactsById
		// via OnControllerContactListChanged and are rebuilt/published once the sync settles — see
		// FinalizePhonebookSync (#30, #contacts-pagination).
		private void StartPhonebookFetch()
		{
			_phonebookNextStart = 0;
			_controller.SubscribeContacts(0, PhonebookPageSize, false);
		}

		private void StartBookingRefreshTimer()
		{
			_bookingRefreshTimer?.Dispose();
			_bookingRefreshTimer = new CTimer(_ =>
			{
				if (_isConnected)
				{
					this.LogDebug("Booking refresh: polling ListMeeting()");
					_controller.ListMeeting();
				}
			}, null, BookingRefreshIntervalMs, BookingRefreshIntervalMs);
		}

		private void StopBookingRefreshTimer()
		{
			_bookingRefreshTimer?.Dispose();
			_bookingRefreshTimer = null;
		}

		private void OnControllerContactListChanged(object sender, ContactListEventArgs e)
		{
			if (e == null || e.Contacts == null) return;

			lock (_directoryLock)
			{
				foreach (var c in e.Contacts)
				{
					if (string.IsNullOrEmpty(c.ContactID)) continue;
					_directoryContactsById[c.ContactID] = c;
				}

				// Only a genuine paged directory response (never an ambient IM/presence delta) can mean
				// "there might be another page" — and only when it actually came back full. Requesting
				// the next page here is still safe/idempotent even if this batch turns out to be the
				// only one; finalization is entirely decided by the settle timer below.
				if (e.Source == ContactListSource.DynamicListPage && e.Contacts.Length == PhonebookPageSize)
				{
					_phonebookNextStart += PhonebookPageSize;
					this.LogDebug("Phonebook page full ({BatchCount} contacts) — fetching next page at index {Start}",
						e.Contacts.Length, _phonebookNextStart);
					_controller.SubscribeContacts(_phonebookNextStart, PhonebookPageSize, false);
				}
			}

			// Debounce: (re)start the settle timer on every batch (paged or ambient). Only once no
			// further ContactListChanged arrives for PhonebookSettleMs do we treat the accumulated
			// cache as final and rebuild/publish — this is unaffected by how many intermediate
			// batches arrived or what triggered them.
			lock (_phonebookSettleLock)
			{
				_phonebookSettleTimer?.Stop();
				_phonebookSettleTimer = new CTimer(_ => FinalizePhonebookSync(), PhonebookSettleMs);
			}
		}

		// Rebuilds DirectoryRoot from the accumulated contact cache and publishes it, once the
		// phonebook sync has settled (no ContactListChanged for PhonebookSettleMs).
		private void FinalizePhonebookSync()
		{
			lock (_directoryLock)
			{
				var directory = new CodecDirectory { ResultsFolderId = "root" };
				directory.AddContactsToDirectory(
					_directoryContactsById.Values
						.Where(c => !IsHiddenContactName(ContactDisplayName(c)))
						.Select(c => (DirectoryItem)MapDirectoryContact(c)).ToList());

				this.LogDebug("Phonebook sync settled: {Total} total contact(s), {Hidden} hidden",
					directory.Contacts.Count, _directoryContactsById.Count - directory.Contacts.Count);

				DirectoryRoot = directory;

				PhonebookSyncState.SetPhonebookHasFolders(false);
				PhonebookSyncState.InitialPhonebookFoldersReceived();
				PhonebookSyncState.PhonebookRootEntriesReceived();
				PhonebookSyncState.SetNumberOfContacts(directory.Contacts.Count);

				if (CurrentDirectoryResult == null || CurrentDirectoryResult.ResultsFolderId == "root")
				{
					CurrentDirectoryResult = DirectoryRoot;
				}
			}
		}

		// Maps a single SDK contact to an Essentials invitable directory contact (flat, parented to root).
		private static InvitableDirectoryContact MapDirectoryContact(ContactInfo c)
		{
			var name = ContactDisplayName(c);

			var contact = new InvitableDirectoryContact
			{
				Name = string.IsNullOrEmpty(name) ? c.ContactID : name,
				ContactId = c.ContactID,
				ParentFolderId = "root",
			};

			contact.ContactMethods.Add(new ContactMethod
			{
				Number = c.ContactID,
				Device = eContactMethodDevice.Video,
				CallType = eContactMethodCallType.Video,
				ContactMethodId = c.ContactID,
			});

			return contact;
		}

		// Maps the SDK schedule (bookings) into the Essentials CodecScheduleAwareness model and
		// publishes it. The SDK delivers the full list on each update, so the meeting list is
		// replaced wholesale rather than merged.
		private void OnControllerMeetingListChanged(object sender, MeetingListEventArgs e)
		{
			if (e == null || e.Meetings == null) return;

			var meetings = e.Meetings
				.Select(MapMeeting)
				.Where(m => m != null)
				.OrderBy(m => m.StartTime)   // chronological order
				.ToList();

			this.LogDebug("Schedule updated: {MeetingCount} meeting(s) (result {Result})",
				meetings.Count, e.Result);

			CodecSchedule.Meetings = meetings;
		}

		// Maps a single SDK meeting item to an Essentials Meeting. Returns null if start/end times
		// cannot be parsed, since the schedule model relies on them for joinable/warning logic.
		private Meeting MapMeeting(MeetingItemInfo item)
		{
			if (item == null) return null;

			if (!DateTime.TryParse(item.StartTime, CultureInfo.InvariantCulture,
					DateTimeStyles.RoundtripKind, out var startTime) ||
				!DateTime.TryParse(item.EndTime, CultureInfo.InvariantCulture,
					DateTimeStyles.RoundtripKind, out var endTime))
			{
				this.LogDebug("Skipping meeting {MeetingNumber}: unparseable start/end time ('{Start}' / '{End}')",
					item.MeetingNumber, item.StartTime, item.EndTime);
				return null;
			}

			return new Meeting
			{
				Id = item.MeetingNumber,
				Title = item.MeetingName,
				Organizer = item.HostName,
				StartTime = startTime,
				EndTime = endTime,
				Privacy = item.IsPrivate ? eMeetingPrivacy.Private : eMeetingPrivacy.Public,
				Dialable = !string.IsNullOrEmpty(item.MeetingNumber),
				MinutesBeforeMeeting = CodecSchedule.MeetingWarningMinutes,
			};
		}

		/// <summary>
		/// Call when directory results are updated
		/// </summary>
		/// <param name="result"></param>
		private void OnDirectoryResultReturned(CodecDirectory result)
		{
			try
			{
				this.LogDebug("OnDirectoryResultReturned.  Result has {ContactCount} contacts", result.Contacts.Count);

				CurrentDirectoryResultIsNotDirectoryRoot.FireUpdate();

				var directoryResult = result;
				var directoryIsRoot = CurrentDirectoryResultIsNotDirectoryRoot.BoolValue == false;

				// If result is Root, create a copy and filter out contacts whose parent folder is not root
				//if (!CurrentDirectoryResultIsNotDirectoryRoot.BoolValue)
				//{
				//    Debug.Console(2, this, "Filtering DirectoryRoot to remove contacts for display");

				//    directoryResult.ResultsFolderId = result.ResultsFolderId;
				//    directoryResult.AddFoldersToDirectory(result.Folders);
				//    directoryResult.AddContactsToDirectory(
				//        result.Contacts.Where((c) => c.ParentFolderId == result.ResultsFolderId).ToList());
				//}
				//else
				//{
				//    directoryResult = result;
				//}

				this.LogDebug("Updating directoryResult. IsOnRoot: {DirectoryIsRoot} Contact Count: {ContactCount}",
					directoryIsRoot, directoryResult.Contacts.Count);

				// This will return the latest results to all UIs.  Multiple indendent UI Directory browsing will require a different methodology
				var handler = DirectoryResultReturned;
				if (handler != null)
				{
					handler(this, new DirectoryEventArgs
					{
						Directory = directoryResult,
						DirectoryIsOnRoot = directoryIsRoot
					});
				}


			}
			catch (Exception e)
			{
				this.LogDebug("Error: {Exception}", e);
			}

			//PrintDirectory(result);
		}

		/// <summary>
		/// Builds the cameras List by using the Zoom Room zStatus.Cameras data.  Could later be modified to build from config data
		/// </summary>
		private void SetUpCameras()
		{
			// Near-end cameras come from the SDK's setting-service device list (real device IDs).
			// There is no SDK push for camera-list/selection changes, so this is refreshed on connect
			// and after a SelectCamera; external (Zoom UI) camera swaps won't reflect until then.
			var devices = _controller.GetCameras();
			if (devices == null || devices.Length == 0)
			{
				this.LogInformation("No local cameras reported by the SDK");
			}
			else
			{
				// Mutate the shared Cameras list under _participantLock; UpdateFarEndCameras (on the
				// participant thread) and SelectCamera (on the UI thread) also serialize on this lock.
				IHasCameraControls cameraToSelect = null;
				lock (_participantLock)
				{
					foreach (var dev in devices)
					{
						// Crestron UC engine systems report the USB bridge device in the camera list; skip it.
						if (!string.IsNullOrEmpty(dev.Name) && dev.Name.IndexOf("HD-CONV-USB") > -1)
							continue;

						var existingCam = Cameras.FirstOrDefault((c) => c.Key.Equals(dev.Id));
						if (existingCam == null)
						{
							var displayName = string.IsNullOrEmpty(dev.DisplayName) ? dev.Name : dev.DisplayName;
							Cameras.Add(new ZoomRoomCamera(dev.Id, displayName, this));
							this.LogDebug("Added near-end camera id=\"{Id}\" name=\"{Name}\"", dev.Id, displayName);
						}

						if (dev.IsSelected)
						{
							var cam = Cameras.FirstOrDefault((c) => c.Key.Equals(dev.Id));
							if (cam != null) cameraToSelect = cam;
						}
					}
				}

				// Assign SelectedCamera outside the lock so its feedback/CameraSelected dispatch
				// doesn't run while _participantLock is held.
				if (cameraToSelect != null) SelectedCamera = cameraToSelect;
			}

			if (IsInCall)
			{
				UpdateFarEndCameras();
			}
		}

		/// <summary>
		/// Keeps <see cref="_participantInfoByUserId"/> in sync with the SDK participant events.
		/// Mirrors the maintenance applied to <c>Participants.CurrentParticipants</c> so far-end
		/// camera discovery can read the raw camera-control flags. Caller must hold <c>_participantLock</c>.
		/// </summary>
		private void TrackParticipantInfo(ParticipantInfo[] participants, bool fullReplace, bool isLeave)
		{
			if (isLeave)
			{
				foreach (var info in participants)
				{
					_participantInfoByUserId.Remove(info.UserID);
					_cameraTileInputByUserId.Remove(info.UserID);
				}
				ForgetPinSpotlight(participants.Select(info => info.UserID));
				return;
			}

			var joined = participants.Where(info => !_participantInfoByUserId.ContainsKey(info.UserID)).Select(info => info.UserID).ToList();
			if (fullReplace)
				_participantInfoByUserId.Clear();
			foreach (var info in participants)
				_participantInfoByUserId[info.UserID] = info;
			LearnCameraTileInputs(joined);
		}

		/// <summary>
		/// Dynamically creates and removes <see cref="ZoomRoomFarEndCamera"/> instances for the
		/// remote participants whose camera the room is allowed to control. Control of an existing
		/// far-end camera is wired via <see cref="ControlFarEndCamera"/>; this reconciles the
		/// <c>Cameras</c> list against the current participant roster (driven by the SDK participant
		/// events). A participant is considered controllable when it is not the room itself and the
		/// SDK reports it can be requested for control (or the room is already controlling it).
		/// </summary>
		private void UpdateFarEndCameras()
		{
			lock (_participantLock)
			{
				var controllable = _participantInfoByUserId.Values
					.Where(info => !info.IsMySelf && (info.CameraCanRequestControl || info.CameraAmIControlling))
					.ToList();
				var controllableIds = new HashSet<int>(controllable.Select(info => info.UserID));

				// Early-exit if the set of controllable participant IDs is unchanged — the common
				// case during roster churn that doesn't involve camera-capable participants (#32).
				var existingIds = new HashSet<int>(
					Cameras.OfType<ZoomRoomFarEndCamera>()
							.Where(c => c.Id.HasValue)
							.Select(c => c.Id.Value));
				if (controllableIds.SetEquals(existingIds))
					return;

				// Add a camera for each newly-controllable participant.
				foreach (var info in controllable)
				{
					if (Cameras.OfType<ZoomRoomFarEndCamera>().Any(c => c.Id == info.UserID))
						continue;

					var key = string.Format("{0}-farEndCamera-{1}", Key, info.UserID);
					var name = string.IsNullOrEmpty(info.UserName)
						? string.Format("Far End Camera {0}", info.UserID)
						: info.UserName;
					Cameras.Add(new ZoomRoomFarEndCamera(key, name, this, info.UserID));
					this.LogDebug("Added far-end camera userId={UserId} name=\"{Name}\"", info.UserID, name);
				}

				// Remove cameras for participants who left or lost camera-control capability.
				var stale = Cameras.OfType<ZoomRoomFarEndCamera>()
					.Where(c => !(c.Id.HasValue && controllableIds.Contains(c.Id.Value)))
					.ToList();
				foreach (var cam in stale)
				{
					if (ReferenceEquals(SelectedCamera, cam))
						SelectedCamera = null;
					Cameras.Remove(cam);
					this.LogDebug("Removed far-end camera userId={UserId}", cam.Id);
				}
			}
		}

		/// <summary>
		/// Sends a far-end (participant) camera PTZ command to the SDK. The camera's <c>Id</c> is the
		/// target participant userID. Maps the plugin's camera enums to the ZRC SDK's
		/// CameraControlAction / CameraControlType ints.
		/// </summary>
		internal void ControlFarEndCamera(int userId, eZoomRoomCameraState state, eZoomRoomCameraAction action)
		{
			if (!TryMapCameraCommand(state, action, out var controlAction, out var controlType)) return;
			_controller.ControlUserCamera(userId, controlAction, controlType);
		}

		/// <summary>
		/// Sends a near-end PTZ command to a specific local camera by device ID. An empty device ID
		/// targets the room's main camera (the SDK's ControlLocalCamera convention).
		/// </summary>
		internal void ControlNearEndCamera(string deviceId, eZoomRoomCameraState state, eZoomRoomCameraAction action)
		{
			if (!TryMapCameraCommand(state, action, out var controlAction, out var controlType)) return;
			_controller.ControlCamera(deviceId ?? string.Empty, controlAction, controlType);
		}

		// Maps the plugin's camera enums to the ZRC SDK CameraControlAction / CameraControlType ints.
		private bool TryMapCameraCommand(eZoomRoomCameraState state, eZoomRoomCameraAction action, out int controlAction, out int controlType)
		{
			controlAction = -1; controlType = -1;
			switch (state)
			{
				case eZoomRoomCameraState.Start: controlType = 0; break; // CameraControlTypeStart
				case eZoomRoomCameraState.Continue: controlType = 1; break; // CameraControlTypeContinue
				case eZoomRoomCameraState.Stop: controlType = 2; break; // CameraControlTypeStop
				default:
					this.LogWarning("Camera command: unsupported state {State}", state);
					return false;
			}
			switch (action)
			{
				case eZoomRoomCameraAction.Up: controlAction = 0; break; // CameraControlActionMoveUp
				case eZoomRoomCameraAction.Down: controlAction = 1; break; // CameraControlActionMoveDown
				case eZoomRoomCameraAction.Left: controlAction = 2; break; // CameraControlActionMoveLeft
				case eZoomRoomCameraAction.Right: controlAction = 3; break; // CameraControlActionMoveRight
				case eZoomRoomCameraAction.In: controlAction = 4; break; // CameraControlActionZoomIn
				case eZoomRoomCameraAction.Out: controlAction = 5; break; // CameraControlActionZoomOut
				default:
					this.LogWarning("Camera command: unsupported action {Action}", action);
					return false;
			}
			return true;
		}

		#region Implementation of IHasParticipants

		public CodecParticipants Participants { get; private set; }

		public void RemoveParticipant(int userId)
		{
			if (TrySimulatedAttendee(userId, "remove", a => _simulatedAttendees.Remove(a))) return;
			_controller.ExpelUser(userId);
			// Removing a webinar attendee changes the attendee list the page shows.
			ScheduleAttendeeRefresh();
		}

		public void SetParticipantAsHost(int userId)
		{
			_controller.AssignHost(userId);
		}

		public void AdmitParticipantFromWaitingRoom(int userId)
		{
			_controller.AdmitUserFromWaitingRoom(userId);
		}

		/// <summary>Admits everyone currently in the waiting room into the meeting.</summary>
		public void AdmitAllParticipantsFromWaitingRoom()
		{
			_controller.AdmitAllFromWaitingRoom();
		}

		/// <summary>Moves a participant (back) into the waiting room.</summary>
		public void PutParticipantInWaitingRoom(int userId)
		{
			_controller.PutUserInWaitingRoom(userId);
		}

		/// <summary>
		/// The SDK participant infos currently in the waiting room (flagged "silent mode"). The ZRC SDK
		/// has no dedicated waiting-room roster, so this is derived from the participant feed.
		/// </summary>
		private List<ParticipantInfo> GetWaitingRoomInfos()
		{
			lock (_participantLock)
				return _participantInfoByUserId.Values.Where(p => p.IsInSilentMode).ToList();
		}

		/// <summary>
		/// Returns a snapshot of the current participant list taken under <c>_participantLock</c>
		/// so serialization threads don't race with SDK mutation callbacks.
		/// </summary>
		public List<Participant> GetParticipantsSnapshot()
		{
			lock (_participantLock)
				return new System.Collections.Generic.List<Participant>(Participants.CurrentParticipants);
		}

		/// <summary>
		/// Participants currently in the waiting room (derived from the "silent mode" flag). Mapped to the
		/// standard <see cref="Participant"/> shape so the mobile UI can render them like the main roster.
		/// </summary>
		public List<Participant> WaitingRoomParticipants => GetWaitingRoomInfos().Select(MapParticipant).ToList();

		/// <summary>Removes (expels) everyone currently in the waiting room.</summary>
		public void RemoveAllFromWaitingRoom()
		{
			foreach (var info in GetWaitingRoomInfos())
				_controller.ExpelUser(info.UserID);
		}

		#region IHasCodecRoomPresets (camera presets — ZRC SDK, max 3 per camera, idx 0–2)

		/// <inheritdoc />
		public event EventHandler<EventArgs> CodecRoomPresetsListHasChanged;

		/// <inheritdoc />
		public List<CodecRoomPreset> NearEndPresets { get; private set; } = new List<CodecRoomPreset>();

		/// <inheritdoc />
		public List<CodecRoomPreset> FarEndRoomPresets { get; private set; } = new List<CodecRoomPreset>();

		// Presets target the currently-selected camera; empty id falls back to the main near-end camera.
		private string CurrentCameraDeviceId => _controller.GetCurrentCamera()?.Id ?? string.Empty;

		/// <inheritdoc />
		public void CodecRoomPresetSelect(int preset)
		{
			// Bridge (SIMPL/Essentials) uses 1-based preset IDs; ZRC SDK slots are 0-based.
			if (preset < 1) return;
			_controller.GoToCameraPreset((uint)(preset - 1), CurrentCameraDeviceId);
		}

		/// <inheritdoc />
		public void CodecRoomPresetStore(int preset, string description)
		{
			// Bridge (SIMPL/Essentials) uses 1-based preset IDs; ZRC SDK slots are 0-based.
			if (preset < 1) return;
			var slot = (uint)(preset - 1);
			_controller.SetCameraPreset(slot, CurrentCameraDeviceId);
			if (!string.IsNullOrEmpty(description))
				_controller.NameCameraPreset(slot, description, CurrentCameraDeviceId);
		}

		/// <inheritdoc />
		public void SelectFarEndPreset(int preset) =>
			this.LogDebug("SelectFarEndPreset({Preset}) not supported by Zoom Room camera presets", preset);

		private void OnControllerCameraPresetInfoChanged(object sender, CameraPresetInfoEventArgs e)
		{
			NearEndPresets = e.Presets
				.Select(p => new CodecRoomPreset(
					p.Index,
					string.IsNullOrEmpty(p.Name) ? string.Format("Preset {0}", p.Index + 1) : p.Name,
					false, true))
				.ToList();
			CodecRoomPresetsListHasChanged?.Invoke(this, EventArgs.Empty);
		}

		#endregion

		/// <summary>
		/// Console/test helper: lists participants currently in the waiting room. The ZRC SDK has no
		/// dedicated waiting-room roster — waiting users arrive in the normal participant feed flagged
		/// "silent mode" (<see cref="ParticipantInfo.IsInSilentMode"/>). Admit them with
		/// <see cref="AdmitParticipantFromWaitingRoom"/> / <see cref="AdmitAllParticipantsFromWaitingRoom"/>.
		/// </summary>
		public void LogWaitingRoom()
		{
			var waiting = GetWaitingRoomInfos();

			if (waiting.Count == 0)
			{
				this.LogInformation("Waiting room: (empty — no participants in silent mode)");
				return;
			}

			this.LogInformation("Waiting room ({Count}):", waiting.Count);
			foreach (var p in waiting)
				this.LogInformation("  userId={UserId} name=\"{Name}\"", p.UserID, p.UserName);
		}

		/// <summary>
		/// Console/test helper: logs the current participants and their userIds so per-participant
		/// commands (MuteVideoForParticipant, MuteAudioForParticipant, PinUser, etc.) can be exercised
		/// via devjson. Call with: devjson {"deviceKey":"...","methodName":"LogParticipants","params":[]}
		/// </summary>
		public void LogParticipants()
		{
			var list = Participants.CurrentParticipants;
			if (list == null || list.Count == 0)
			{
				this.LogInformation("Participants: (none — not in a meeting or list not yet populated)");
				return;
			}

			this.LogInformation("Participants ({Count}):", list.Count);
			foreach (var p in list)
			{
				this.LogInformation(
					"  userId={UserId} name=\"{Name}\" host={IsHost} cohost={IsCohost} self={IsMyself} audioMuted={AudioMuted} videoMuted={VideoMuted} handRaised={HandRaised}",
					p.UserId, p.Name, p.IsHost, p.IsCohost, p.IsMyself, p.AudioMuteFb, p.VideoMuteFb, p.HandIsRaisedFb);
			}
		}

		/// <summary>
		/// Console/test helper: logs current meeting info, including <c>CanRecord</c> (the
		/// <c>MeetingCanRecord</c> bridge feedback) which is observe-only and has no command.
		/// Join a recording-permitted vs. not-permitted meeting and re-run to see it change.
		/// </summary>
		public void LogMeetingInfo()
		{
			this.LogInformation(
				"Meeting info: inCall={InCall} canRecord={CanRecord} isRecording={IsRecording} locked={Locked} isHost={IsHost}",
				IsInCall, _sdkCanRecord, _sdkIsRecording, _sdkMeetingLocked, _sdkIsHost);
		}

		/// <summary>
		/// Console test shim: logs the room's current speaker volume (the value behind
		/// <c>VolumeLevelFeedback</c>) so the seed-on-reconnect behavior can be verified from the
		/// CLI without a bridge/touchpanel. After setting a volume and rebooting, this should report
		/// the room's current level rather than 0.
		/// </summary>
		public void LogVolume()
		{
			this.LogInformation(
				"Volume: level={Level} (0-65535) muted={Muted}",
				_sdkSpeakerVolumeLevel, _sdkSpeakerMuted);
		}

		#endregion

		// Host controls (mute / video / pin) target OTHER participants. Resolves the live participant
		// for userId, or returns false with a clear warning if it isn't a current participant or is
		// the room itself — both of which the SDK otherwise rejects with an opaque "returned failure".
		private bool TryGetControllableParticipant(int userId, string op, out Participant user)
		{
			lock (_participantLock)
				user = Participants.CurrentParticipants.FirstOrDefault(p => p.UserId == userId);

			if (user == null)
			{
				this.LogWarning("{Op}: userId {UserId} is not a current meeting participant — run LogParticipants for valid IDs.", op, userId);
				return false;
			}
			if (user.IsMyself)
			{
				this.LogWarning("{Op}: userId {UserId} is this room (self); host controls target OTHER participants, not the room.", op, userId);
				user = null;
				return false;
			}
			return true;
		}

		// The touchpanel's Zoom Participants panel sends the same generic per-userId mute actions for
		// every row, including the room's own ("myself") row - it has no way to know that row is
		// special. TryGetControllableParticipant rejects IsMyself outright (host controls target OTHER
		// participants), which used to make the room's own mic/camera buttons in that panel silent
		// no-ops. Route a self-targeted call to the room-level SDK controls instead, so those buttons
		// work: PrivacyModeOn/Off is this room's own mic (SetAudioMute under the hood - see its
		// definition), CameraMuteOn/Off is this room's own camera.
		private bool IsSelf(int userId)
		{
			lock (_participantLock)
				return Participants.CurrentParticipants.Any(p => p.UserId == userId && p.IsMyself);
		}

		#region IHasParticipantAudioMute Members

		public void MuteAudioForAllParticipants()
		{
			_controller.MuteAllAudio(true);
		}

		public void MuteAudioForParticipant(int userId)
		{
			if (IsSelf(userId)) { PrivacyModeOn(); return; }
			if (!TryGetControllableParticipant(userId, nameof(MuteAudioForParticipant), out _)) return;
			_controller.MuteUserAudio(userId, true);
		}

		public void UnmuteAudioForParticipant(int userId)
		{
			if (IsSelf(userId)) { PrivacyModeOff(); return; }
			if (!TryGetControllableParticipant(userId, nameof(UnmuteAudioForParticipant), out _)) return;
			_controller.MuteUserAudio(userId, false);
		}

		public void ToggleAudioForParticipant(int userId)
		{
			if (IsSelf(userId)) { PrivacyModeToggle(); return; }

			if (!TryGetControllableParticipant(userId, nameof(ToggleAudioForParticipant), out var user)) return;

			// NOTE: the host can mute directly, but "unmute" only sends a REQUEST (the participant
			// gets a popup and must accept). So when the tracked state is muted, this requests an
			// unmute rather than forcing it — the participant stays muted until they accept.
			this.LogDebug("ToggleAudioForParticipant: userId={UserId} audioMuted={Muted} -> {Action}",
				userId, user.AudioMuteFb, user.AudioMuteFb ? "Unmute(request)" : "Mute");
			if (user.AudioMuteFb)
			{
				UnmuteAudioForParticipant(userId);
			}
			else
			{
				MuteAudioForParticipant(userId);
			}
		}

		#endregion

		#region IHasParticipantVideoMute Members

		public void MuteVideoForParticipant(int userId)
		{
			if (IsSelf(userId)) { CameraMuteOn(); return; }
			if (!TryGetControllableParticipant(userId, nameof(MuteVideoForParticipant), out _)) return;
			_controller.MuteUserVideo(userId, true);
		}

		public void UnmuteVideoForParticipant(int userId)
		{
			if (IsSelf(userId)) { CameraMuteOff(); return; }
			if (!TryGetControllableParticipant(userId, nameof(UnmuteVideoForParticipant), out _)) return;
			_controller.MuteUserVideo(userId, false);
		}

		public void ToggleVideoForParticipant(int userId)
		{
			if (IsSelf(userId)) { CameraMuteToggle(); return; }

			if (!TryGetControllableParticipant(userId, nameof(ToggleVideoForParticipant), out var user)) return;

			// Same caveat as audio: the host can stop a participant's video directly, but starting it
			// only sends a REQUEST (popup on the participant). So the unmute branch won't force video on.
			this.LogDebug("ToggleVideoForParticipant: userId={UserId} videoMuted={Muted} -> {Action}",
				userId, user.VideoMuteFb, user.VideoMuteFb ? "Unmute(request)" : "Mute");
			if (user.VideoMuteFb)
			{
				UnmuteVideoForParticipant(userId);
			}
			else
			{
				MuteVideoForParticipant(userId);
			}
		}

		#endregion

		#region IHasParticipantPinUnpin Members

		private Func<int> NumberOfScreensFeedbackFunc
		{
			// Status.NumberOfScreens is never populated (JSON pipeline removed).
			// Crestron 4-series Zoom Rooms manage a single display output — return 1.
			get { return () => 1; }
		}

		public IntFeedback NumberOfScreensFeedback { get; private set; }

		public int ScreenIndexToPinUserTo { get; private set; }

		public void PinParticipant(int userId, int screenIndex)
		{
			if (!TryGetControllableParticipant(userId, nameof(PinParticipant), out _)) return;
			// Track on success so ToggleParticipantPinState can later unpin (the SDK exposes no
			// per-participant pin state of its own).
			if (_controller.PinUserOnScreen(userId, screenIndex))
			{
				lock (_participantLock) _pinnedUserScreens[userId] = screenIndex;
				RaisePinSpotlightChanged();
			}
		}

		public void UnPinParticipant(int userId)
		{
			int screen;
			lock (_participantLock) screen = _pinnedUserScreens.TryGetValue(userId, out var s) ? s : 0;
			if (_controller.UnpinUserFromScreen(userId, screen))
			{
				lock (_participantLock) _pinnedUserScreens.Remove(userId);
				RaisePinSpotlightChanged();
			}
		}

		public void ToggleParticipantPinState(int userId, int screenIndex)
		{
			if (!TryGetControllableParticipant(userId, nameof(ToggleParticipantPinState), out _)) return;
			// SDK gives no pin-state feedback, so toggle off our own tracked set rather than the
			// always-false IsPinnedFb (which made the toggle always re-pin and fail on an
			// already-pinned user).
			bool pinned;
			lock (_participantLock) pinned = _pinnedUserScreens.ContainsKey(userId);
			this.LogDebug("ToggleParticipantPinState: userId={UserId} pinned(tracked)={Pinned} -> {Action}",
				userId, pinned, pinned ? "Unpin" : "Pin");
			if (pinned)
				UnPinParticipant(userId);
			else
				PinParticipant(userId, screenIndex);
		}

		#endregion

		#region Implementation of IHasCameraOff

		public BoolFeedback CameraIsOffFeedback { get; private set; }

		public void CameraOff()
		{
			CameraMuteOn();
		}

		#endregion

		public BoolFeedback CameraIsMutedFeedback { get; private set; }

		public void CameraMuteOn()
		{
			_sdkCameraOff = true;
			CameraIsOffFeedback.FireUpdate();
			_controller.SetVideoState(false);
		}

		public void CameraMuteOff()
		{
			_sdkCameraOff = false;
			CameraIsOffFeedback.FireUpdate();
			_controller.SetVideoState(true);
		}

		public void CameraMuteToggle()
		{
			if (CameraIsMutedFeedback.BoolValue)
				CameraMuteOff();
			else
				CameraMuteOn();
		}

		#region Implementation of IHasCameraAutoMode

		// SmartCameraMask: SpeakerFocus(2) = auto-framing follows the speaker; Manual(1) = no auto framing.
		private const int SmartCameraMaskManual = 1;
		private const int SmartCameraMaskSpeakerFocus = 2;

		// Smart-mode targets the selected near-end camera by device ID; empty falls back to the main camera.
		private string SelectedNearEndCameraDeviceId =>
			(_selectedCamera != null && !(_selectedCamera is IAmFarEndCamera)) ? _selectedCamera.Key : string.Empty;

		public void CameraAutoModeOn()
		{
			if (!_controller.ChangeSmartCameraMode(SmartCameraMaskSpeakerFocus, SelectedNearEndCameraDeviceId)) return;
			_cameraAutoModeOn = true;
			CameraAutoModeIsOnFeedback.FireUpdate();
		}

		public void CameraAutoModeOff()
		{
			if (!_controller.ChangeSmartCameraMode(SmartCameraMaskManual, SelectedNearEndCameraDeviceId)) return;
			_cameraAutoModeOn = false;
			CameraAutoModeIsOnFeedback.FireUpdate();
		}

		public void CameraAutoModeToggle()
		{
			if (_cameraAutoModeOn) CameraAutoModeOff();
			else CameraAutoModeOn();
		}

		public BoolFeedback CameraAutoModeIsOnFeedback { get; private set; }

		#endregion

		#region Implementation of IHasFarEndContentStatus

		public BoolFeedback ReceivingContent { get; private set; }

		#endregion

		#region Implementation of IHasSelfviewPosition

		private CodecCommandWithLabel _currentSelfviewPipPosition;

		public StringFeedback SelfviewPipPositionFeedback { get; private set; }

		public void SelfviewPipPositionSet(CodecCommandWithLabel position)
		{
			if (position == null) return;
			_currentSelfviewPipPosition = position;
			// ControlVideoPosition sets position AND size together, so supply the current size.
			_controller.ControlVideoPosition(SelfviewPositionToSdk(position.Command), CurrentSelfviewSizeSdk());
			SelfviewPipPositionFeedback.FireUpdate();
		}

		// Maps the plugin's self-view PiP position command -> ZRC SDK VideoThumbPosition int.
		private static int SelfviewPositionToSdk(string command)
		{
			switch ((command ?? string.Empty).ToLower())
			{
				case "upleft": return 7; // VideoThumbPositionUpLeft
				case "upright": return 3; // VideoThumbPositionUpRight
				case "downright": return 5; // VideoThumbPositionDownRight
				case "downleft": return 8; // VideoThumbPositionDownLeft
				default: return 3; // default UpRight
			}
		}

		// Maps the plugin's self-view PiP size command -> ZRC SDK VideoThumbSize int.
		private static int SelfviewSizeToSdk(string command)
		{
			switch ((command ?? string.Empty).ToLower())
			{
				case "off": return 0; // VideoThumbSizeOff (hides the PiP)
				case "size1": return 1; // VideoThumbSize1x
				case "size2": return 2; // VideoThumbSize2x
				case "size3": return 3; // VideoThumbSize3x
				case "strip": return 4; // VideoThumbSizeVideoStripe
				default: return 1; // default 1x
			}
		}

		private int CurrentSelfviewSizeSdk()
		{
			return _currentSelfviewPipSize != null ? SelfviewSizeToSdk(_currentSelfviewPipSize.Command) : 1;
		}

		private int CurrentSelfviewPositionSdk()
		{
			return _currentSelfviewPipPosition != null ? SelfviewPositionToSdk(_currentSelfviewPipPosition.Command) : 3;
		}

		public void SelfviewPipPositionToggle()
		{
			var available = AvailableSelfviewPipPositions;
			if (_currentSelfviewPipPosition == null || available.Count == 0) return;

			var nextPipPositionIndex = available.IndexOf(_currentSelfviewPipPosition) + 1;

			if (nextPipPositionIndex >= available.Count || nextPipPositionIndex < 0)
				// Not found (current position isn't valid for this layout) or wrapped past the end.
				nextPipPositionIndex = 0;

			SelfviewPipPositionSet(available[nextPipPositionIndex]);
		}

		public List<CodecCommandWithLabel> SelfviewPipPositions = new List<CodecCommandWithLabel>()
		{
			new CodecCommandWithLabel("UpLeft", "Upper Left"),
			new CodecCommandWithLabel("UpRight", "Upper Right"),
			new CodecCommandWithLabel("DownRight", "Lower Right"),
			new CodecCommandWithLabel("DownLeft", "Lower Left")
		};

		// Which self-view PiP positions make sense per layout. No layout-specific restrictions are known
		// yet -- layouts not listed fall back to all positions -- verify/tune against the physical
		// controller UI using the VideoThumbInfoChanged debug log ("VideoThumbInfo: ...").
		private static readonly Dictionary<zConfiguration.eLayoutStyle, string[]> SelfviewPositionsByLayout = new()
		{
			{ zConfiguration.eLayoutStyle.ContentOnly, Array.Empty<string>() }, // full-screen share -- no floating PiP to position
			{ zConfiguration.eLayoutStyle.CancelContentOnly, Array.Empty<string>() },
		};

		/// <summary>
		/// Self-view PiP positions valid for the currently selected layout, further gated by the SDK's
		/// VideoThumbInfo.isSupported flag (empty when the SDK reports the self-view thumb isn't
		/// supported at all in the current context, since there's nothing to position).
		/// </summary>
		public List<CodecCommandWithLabel> AvailableSelfviewPipPositions
		{
			get
			{
				if (!_sdkSelfviewThumbSupported)
					return new List<CodecCommandWithLabel>();

				return SelfviewPositionsByLayout.TryGetValue(LastSelectedLayout, out var allowed)
					? SelfviewPipPositions.Where(p => allowed.Contains(p.Command)).ToList()
					: SelfviewPipPositions;
			}
		}

		private void ComputeSelfviewPipPositionStatus()
		{
			_currentSelfviewPipPosition =
				SelfviewPipPositions.FirstOrDefault(
					p => p.Command.ToLower().Equals(Configuration.Call.Layout.Position.ToString().ToLower()));
		}

		#endregion

		// TODO: #714 [ ] Implementation of IHasSelfviewPipSize

		#region Implementation of IHasSelfviewPipSize

		private CodecCommandWithLabel _currentSelfviewPipSize;

		// Last non-Off size, so SelfViewModeOn can restore the size the user last chose.
		private CodecCommandWithLabel _lastVisibleSelfviewPipSize;

		public StringFeedback SelfviewPipSizeFeedback { get; private set; }

		public void SelfviewPipSizeSet(CodecCommandWithLabel size)
		{
			if (size == null) return;
			_currentSelfviewPipSize = size;
			if (!"Off".Equals(size.Command, StringComparison.OrdinalIgnoreCase))
				_lastVisibleSelfviewPipSize = size;
			// ControlVideoPosition sets size AND position together, so supply the current position.
			_controller.ControlVideoPosition(CurrentSelfviewPositionSdk(), SelfviewSizeToSdk(size.Command));
			SelfviewPipSizeFeedback.FireUpdate();
			SelfviewIsOnFeedback.FireUpdate(); // Off vs non-Off changes the self-view-on state
		}

		public void SelfviewPipSizeToggle()
		{
			var available = AvailableSelfviewPipSizes;
			if (_currentSelfviewPipSize == null || available.Count == 0) return;

			var nextPipSizeIndex = available.IndexOf(_currentSelfviewPipSize) + 1;

			if (nextPipSizeIndex >= available.Count || nextPipSizeIndex < 0)
				// Not found (current size isn't valid for this layout) or wrapped past the end.
				nextPipSizeIndex = 0;

			SelfviewPipSizeSet(available[nextPipSizeIndex]);
		}

		public List<CodecCommandWithLabel> SelfviewPipSizes = new List<CodecCommandWithLabel>()
		{
			new CodecCommandWithLabel("Off", "Off"),
			new CodecCommandWithLabel("Size1", "Size 1"),
			new CodecCommandWithLabel("Size2", "Size 2"),
			new CodecCommandWithLabel("Size3", "Size 3"),
			new CodecCommandWithLabel("Strip", "Strip")
		};

		// Which self-view PiP sizes make sense per layout. Best-effort starting point based on Zoom UX
		// conventions (floating self-view PiP is redundant/unavailable once your own video is already
		// full-screen shared content, or already shown in a thumbnail strip) -- verify/tune against the
		// physical controller UI using the VideoThumbInfoChanged debug log ("VideoThumbInfo: ...").
		// Layouts not listed here fall back to all sizes being offered.
		private static readonly Dictionary<zConfiguration.eLayoutStyle, string[]> SelfviewSizesByLayout = new()
		{
			{ zConfiguration.eLayoutStyle.Thumbnail, new[] { "Off", "Size1", "Size2", "Size3" } }, // Strip is redundant -- already a thumbnail strip
			{ zConfiguration.eLayoutStyle.ThumbnailAndShare, new[] { "Off", "Size1", "Size2", "Size3" } },
			{ zConfiguration.eLayoutStyle.ContentOnly, new[] { "Off" } }, // full-screen share -- no room for a floating PiP
			{ zConfiguration.eLayoutStyle.CancelContentOnly, new[] { "Off" } },
		};

		/// <summary>
		/// Self-view PiP sizes valid for the currently selected layout, further gated by the SDK's
		/// VideoThumbInfo.isSupported flag (only "Off" is offered when the SDK reports the self-view
		/// thumb isn't supported at all in the current context).
		/// </summary>
		public List<CodecCommandWithLabel> AvailableSelfviewPipSizes
		{
			get
			{
				if (!_sdkSelfviewThumbSupported)
					return SelfviewPipSizes.Where(s => s.Command.Equals("Off", StringComparison.OrdinalIgnoreCase)).ToList();

				return SelfviewSizesByLayout.TryGetValue(LastSelectedLayout, out var allowed)
					? SelfviewPipSizes.Where(s => allowed.Contains(s.Command)).ToList()
					: SelfviewPipSizes;
			}
		}

		private void ComputeSelfviewPipSizeStatus()
		{
			_currentSelfviewPipSize =
				SelfviewPipSizes.FirstOrDefault(
					p => p.Command.ToLower().Equals(Configuration.Call.Layout.Size.ToString().ToLower()));
		}

		#endregion

		#region Implementation of IHasPhoneDialing

		// SDK-backed SIP call state — fed by OnControllerSipCallStatusChanged.
		// Status codes where the call is active/ringing (not yet terminated):
		// Incoming(2), Ringing(3), Accepted(9), Hold(10), InCall(11),
		// RemoteHold(13), BothHold(14), SessionInProgress(15), StayOnPhone(16).
		private static readonly int[] _sipTerminalStatuses = { 0, 1, 4, 5, 6, 7, 8, 12 };
		private bool _sdkPhoneOffHook;
		private string _sdkSipCallerName = string.Empty;
		private string _sdkSipCallerNumber = string.Empty;

		private Func<bool> PhoneOffHookFeedbackFunc
		{
			get { return () => _sdkPhoneOffHook; }
		}

		private Func<string> CallerIdNameFeedbackFunc
		{
			get { return () => _sdkSipCallerName; }
		}

		private Func<string> CallerIdNumberFeedbackFunc
		{
			get { return () => _sdkSipCallerNumber; }
		}

		public BoolFeedback PhoneOffHookFeedback { get; private set; }
		public StringFeedback CallerIdNameFeedback { get; private set; }
		public StringFeedback CallerIdNumberFeedback { get; private set; }

		public void DialPhoneCall(string number)
		{
			// SIP is the default transport; PSTN call-out adds the number to the CURRENT meeting
			// (the SDK's third-party-meeting helper has no standalone dial-out), so it only does
			// something when the room is already in a meeting.
			if (_props != null && _props.PhoneDialMode == ePhoneDialMode.Pstn)
			{
				_controller.CallOutPstnUser(number, false, false);
				return;
			}
			_controller.CallSip(number);
		}

		public void EndPhoneCall()
		{
			_controller.TerminateSipCall(_activeSipCallId);
		}

		public void SendDtmfToPhone(string digit)
		{
			// Empty callId targets the single active SIP call.
			_controller.SendDtmfToSipCall(digit, _activeSipCallId ?? string.Empty);
		}

		#endregion

		#region IHasZoomRoomLayouts Members

		public event EventHandler<LayoutInfoChangedEventArgs> LayoutInfoChanged;

		private Func<bool> LayoutViewIsOnFirstPageFeedbackFunc
		{
			get { return () => _layoutIsOnFirstPage; } // driven by the SDK VideoPageStatus notification
		}

		private Func<bool> LayoutViewIsOnLastPageFeedbackFunc
		{
			get { return () => _layoutIsOnLastPage; } // driven by the SDK VideoPageStatus notification
		}

		private Func<bool> CanSwapContentWithThumbnailFeedbackFunc
		{
			// Use the SDK's ScreenLayoutStatus.CanSwitchFloatingShareContent when available;
			// fall back to sharing state as a proxy if the notification hasn't arrived yet.
			get { return () => _screenLayoutStatus?.CanSwitchFloatingShareContent ?? _sdkSharingState != 0; }
		}

		private Func<bool> ContentSwappedWithThumbnailFeedbackFunc
		{
			get { return () => _contentSwappedWithThumbnail; }
		}

		public BoolFeedback LayoutViewIsOnFirstPageFeedback { get; private set; }

		public BoolFeedback LayoutViewIsOnLastPageFeedback { get; private set; }

		public BoolFeedback CanSwapContentWithThumbnailFeedback { get; private set; }

		public BoolFeedback ContentSwappedWithThumbnailFeedback { get; private set; }

		/// <summary>Fires when the SDK reports an updated ScreenLayoutStatus.</summary>
		public event EventHandler<ScreenLayoutStatusEventArgs> ScreenLayoutStatusChanged;

		/// <summary>Last ScreenLayoutStatus received from the SDK. Null until the first notification.</summary>
		public ScreenLayoutStatusEventArgs ScreenLayoutStatus => _screenLayoutStatus;


		public zConfiguration.eLayoutStyle LastSelectedLayout { get; private set; }

		public zConfiguration.eLayoutStyle AvailableLayouts { get; private set; }

		public string CurrentVideoOrder { get; private set; } = "Default";

		public string CurrentThumbnailsPosition { get; private set; } = "Bottom";

		/// <summary>
		/// Determines available layouts from SDK ScreenLayoutStatus when available,
		/// otherwise falls back to reporting all layouts as available.
		/// </summary>
		private void ComputeAvailableLayouts()
		{
			if (_screenLayoutStatus?.LayoutInfos != null && _screenLayoutStatus.LayoutInfos.Length > 0)
			{
				ComputeAvailableLayoutsFromScreenStatus(_screenLayoutStatus.LayoutInfos[0], _screenLayoutStatus.IsInContentOnly);
				this.LogInformation("availablelayouts: {AvailableLayouts} (from SDK ScreenLayoutStatus)", AvailableLayouts);
				return;
			}

			// Fallback: no ScreenLayoutStatus received yet. Report all layouts as available.
			AvailableLayouts = zConfiguration.eLayoutStyle.Gallery
							 | zConfiguration.eLayoutStyle.Speaker
							 | zConfiguration.eLayoutStyle.MultiSpeaker
							 | zConfiguration.eLayoutStyle.Thumbnail
							 | zConfiguration.eLayoutStyle.ContentOnly
							 | zConfiguration.eLayoutStyle.CancelContentOnly
							 | zConfiguration.eLayoutStyle.ThumbnailAndShare
							 | zConfiguration.eLayoutStyle.Dynamic;
			this.LogInformation("availablelayouts: {AvailableLayouts} (static fallback — no SDK data yet)", AvailableLayouts);
		}

		private void OnLayoutInfoChanged()
		{
			var handler = LayoutInfoChanged;
			if (handler != null)
			{
				var currentLayout = zConfiguration.eLayoutStyle.None;

				currentLayout = (zConfiguration.eLayoutStyle)Enum.Parse(typeof(zConfiguration.eLayoutStyle), string.IsNullOrEmpty(LocalLayoutFeedback.StringValue) ? "None" : LocalLayoutFeedback.StringValue, true);

				handler(this, new LayoutInfoChangedEventArgs()
				{
					AvailableLayouts = AvailableLayouts,
					CurrentSelectedLayout = currentLayout,
					LayoutViewIsOnFirstPage = LayoutViewIsOnFirstPageFeedback.BoolValue,
					LayoutViewIsOnLastPage = LayoutViewIsOnLastPageFeedback.BoolValue,
					CanSwapContentWithThumbnail = CanSwapContentWithThumbnailFeedback.BoolValue,
					ContentSwappedWithThumbnail = ContentSwappedWithThumbnailFeedback.BoolValue,
				});
			}
		}

		public void GetAvailableLayouts()
		{
			ComputeAvailableLayouts();
		}

		public void SetLayout(zConfiguration.eLayoutStyle layoutStyle)
		{
			LastSelectedLayout = layoutStyle;
			LocalLayoutFeedback.FireUpdate();

			// CancelContentOnly is a VideoLayoutStyle-only action -- there's no ScreenLayoutSourceType
			// equivalent to "leave content-only mode", so it stays on the deprecated call.
			if (layoutStyle == zConfiguration.eLayoutStyle.CancelContentOnly)
			{
				_controller.UpdateVideoLayoutStyle(5); // VideoLayoutStyleCancelContentOnly
				return;
			}

			// Map Essentials layout style -> SDK ScreenLayoutSourceType int (kept in sync with
			// MapScreenLayoutSourceTypeToLayoutStyle above so commanded/reported layouts agree).
			// Uses SetScreenLayout, not the deprecated UpdateVideoLayoutStyle/VideoLayoutStyle, since
			// MultiSpeaker/ThumbnailAndShare have no VideoLayoutStyle equivalent.
			int screenLayoutSourceType;
			switch (layoutStyle)
			{
				case zConfiguration.eLayoutStyle.Speaker: screenLayoutSourceType = 0; break;           // ActiveVideo
				case zConfiguration.eLayoutStyle.Gallery: screenLayoutSourceType = 4; break;            // Gallery
				case zConfiguration.eLayoutStyle.ContentOnly: screenLayoutSourceType = 5; break;        // SharedContent
				case zConfiguration.eLayoutStyle.Dynamic: screenLayoutSourceType = 10; break;           // DynamicView = "Dynamic Gallery"
				case zConfiguration.eLayoutStyle.MultiSpeaker: screenLayoutSourceType = -1; break;      // No dedicated SDK type -- controller's "Multi-Speaker" (confirmed live via ctrl infos)
				case zConfiguration.eLayoutStyle.Thumbnail: screenLayoutSourceType = 11; break;         // ThumbnailView
				case zConfiguration.eLayoutStyle.ThumbnailAndShare: screenLayoutSourceType = 12; break; // ThumbnailShareView
				default:
					this.LogWarning("SetLayout: no SDK ScreenLayoutSourceType mapping for {LayoutStyle}", layoutStyle);
					return;
			}

			_controller.SetScreenLayout(0, screenLayoutSourceType); // screen 0 = primary
		}

		public void SetVideoOrder(string videoOrderCommand)
		{
			var normalized = (videoOrderCommand ?? "").Trim();
			var sdkValue = normalized.ToLowerInvariant() switch
			{
				"default" => 0,
				"alphabetical" => 1,
				"reversealphabetical" or "reverse alphabetical" => 2,
				_ => -1,
			};

			if (sdkValue < 0)
			{
				this.LogWarning("SetVideoOrder: unrecognized value '{VideoOrderCommand}' — ignoring", videoOrderCommand);
				return;
			}

			_controller.SetVideoOrder(sdkValue);
			CurrentVideoOrder = normalized.Equals("reversealphabetical", StringComparison.OrdinalIgnoreCase)
				|| normalized.Equals("reverse alphabetical", StringComparison.OrdinalIgnoreCase)
					? "ReverseAlphabetical"
					: normalized.Length > 0 ? char.ToUpperInvariant(normalized[0]) + normalized.Substring(1).ToLowerInvariant() : "Default";
			if (CurrentVideoOrder.Equals("Alphabetical", StringComparison.Ordinal))
				CurrentVideoOrder = "Alphabetical";
			if (CurrentVideoOrder.Equals("Reversealphabetical", StringComparison.Ordinal))
				CurrentVideoOrder = "ReverseAlphabetical";
			if (CurrentVideoOrder.Equals("Default", StringComparison.Ordinal))
				CurrentVideoOrder = "Default";

			OnLayoutInfoChanged();
		}

		public void SetThumbnailsPosition(string thumbnailsPositionCommand)
		{
			var normalized = (thumbnailsPositionCommand ?? "").Trim();
			// SDK ThumbnailsPositionType: Bottom = 0, Top = 1.
			var sdkValue = normalized.ToLowerInvariant() switch
			{
				"top" => 1,
				"bottom" => 0,
				_ => -1,
			};

			if (sdkValue < 0)
			{
				this.LogWarning("SetThumbnailsPosition: unrecognized value '{ThumbnailsPositionCommand}' — ignoring", thumbnailsPositionCommand);
				return;
			}

			_controller.ChangeThumbnailsPosition(sdkValue);
			CurrentThumbnailsPosition = normalized.Length > 0
				? (normalized.Equals("top", StringComparison.OrdinalIgnoreCase) ? "Top" : "Bottom")
				: "Bottom";

			OnLayoutInfoChanged();
		}

		public void SwapContentWithThumbnail()
		{
			// Toggle the single-screen floating-share state (content <-> video primary).
			_contentSwappedWithThumbnail = !_contentSwappedWithThumbnail;
			_controller.SwitchToFloatingShareForSingleScreen(_contentSwappedWithThumbnail);
			ContentSwappedWithThumbnailFeedback.FireUpdate();
		}

		public void LayoutTurnNextPage()
		{
			// _currentPageVideoType is kept in sync by the SDK VideoPageStatus notification.
			_controller.TurnVideoPage(true, _currentPageVideoType);
		}

		public void LayoutTurnPreviousPage()
		{
			_controller.TurnVideoPage(false, _currentPageVideoType);
		}

		#endregion

		#region IHasCodecLayouts Members

		private Func<string> LocalLayoutFeedbackFunc
		{
			// Configuration.Call.Layout.Style is never populated (JSON pipeline removed).
			// Track the last layout explicitly set via SetLayout(); default None means
			// the next LocalLayoutToggle() starts from the beginning of the cycle.
			get
			{
				return () => LastSelectedLayout == zConfiguration.eLayoutStyle.None
								   ? string.Empty
								   : LastSelectedLayout.ToString();
			}
		}

		public StringFeedback LocalLayoutFeedback { get; private set; }

		public void LocalLayoutToggle()
		{
			var nextLayout = GetNextLayout(LastSelectedLayout);

			if (nextLayout != zConfiguration.eLayoutStyle.None)
			{
				SetLayout(nextLayout);
			}
		}

		// Cycle order for LocalLayoutToggle(). Share-related layouts (ContentOnly/CancelContentOnly/
		// ThumbnailAndShare) are intentionally excluded -- those are driven by sharing state, not this toggle.
		private static readonly zConfiguration.eLayoutStyle[] LayoutCycleOrder =
		{
			zConfiguration.eLayoutStyle.Gallery,
			zConfiguration.eLayoutStyle.Speaker,
			zConfiguration.eLayoutStyle.MultiSpeaker,
			zConfiguration.eLayoutStyle.Thumbnail,
			zConfiguration.eLayoutStyle.Dynamic,
		};

		/// <summary>
		/// Tries to get the next available layout, wrapping around <see cref="LayoutCycleOrder"/>.
		/// </summary>
		private zConfiguration.eLayoutStyle GetNextLayout(zConfiguration.eLayoutStyle currentLayout)
		{
			if (AvailableLayouts == zConfiguration.eLayoutStyle.None)
			{
				return zConfiguration.eLayoutStyle.None;
			}

			var startIndex = Array.IndexOf(LayoutCycleOrder, currentLayout);
			for (var offset = 1; offset <= LayoutCycleOrder.Length; offset++)
			{
				var candidate = LayoutCycleOrder[(startIndex + offset + LayoutCycleOrder.Length) % LayoutCycleOrder.Length];
				if (AvailableLayouts.HasFlag(candidate))
					return candidate;
			}

			return zConfiguration.eLayoutStyle.None;
		}

		public void LocalLayoutToggleSingleProminent()
		{
			// "Single prominent" == one large active-speaker tile == the SDK's Speaker layout.
			// Toggle between Speaker (prominent) and Gallery using the same VideoLayoutStyle path
			// as SetLayout. LastSelectedLayout is updated by SetLayout so feedback stays in sync.
			var next = LastSelectedLayout == zConfiguration.eLayoutStyle.Speaker
				? zConfiguration.eLayoutStyle.Gallery
				: zConfiguration.eLayoutStyle.Speaker;
			SetLayout(next);
		}

		public void MinMaxLayoutToggle()
		{
			// No direct ZRC SDK equivalent: the SDK exposes discrete VideoLayoutStyle values
			// (Gallery/Speaker/Thumbnail/ContentOnly) but no "minimize/maximize" toggle. The
			// closest single-screen behavior (float share vs. video) is SwapContentWithThumbnail.
			this.LogWarning("MinMaxLayoutToggle has no Zoom Room SDK equivalent; use SwapContentWithThumbnail or a discrete layout instead");
		}

		#endregion

		#region IPasswordPrompt Members

		public event EventHandler<PasswordPromptEventArgs> PasswordRequired;

		public void SubmitPassword(string password)
		{
			_meetingPasswordRequired = false;
			this.LogDebug("Password Submitted: {Password}", password);
			_controller.SendMeetingPassword(password);
		}

		void OnPasswordRequired(bool lastAttemptIncorrect, bool loginFailed, bool loginCancelled, string message)
		{
			_meetingPasswordRequired = !loginFailed && !loginCancelled;

			var handler = PasswordRequired;
			if (handler != null)
			{
				this.LogDebug("Meeting Password Required: {MeetingPasswordRequired}", _meetingPasswordRequired);

				handler(this, new PasswordPromptEventArgs(lastAttemptIncorrect, loginFailed, loginCancelled, message));
			}
		}

		#endregion

		#region IHasMeetingInfo Members

		public event EventHandler<MeetingInfoEventArgs> MeetingInfoChanged;

		private MeetingInfo _meetingInfo;

		public MeetingInfo MeetingInfo
		{
			get { return _meetingInfo; }
			private set
			{
				if (value != _meetingInfo)
				{
					_meetingInfo = value;

					var handler = MeetingInfoChanged;
					if (handler != null)
					{
						handler(this, new MeetingInfoEventArgs(_meetingInfo));
					}
				}
			}
		}

		#endregion

		/// <summary>
		/// Builds a <see cref="MeetingInfo"/> from the current SDK state and assigns it,
		/// firing <see cref="MeetingInfoChanged"/> if the value has changed.
		/// Field-compares against the last value before allocating so that callers who invoke
		/// this on every small state transition don't generate spurious bridge/MC pushes (#33).
		/// </summary>
		private void UpdateMeetingInfo()
		{
			// Compare constituent fields before allocating; MeetingInfo is a class (ref type) so
			// value != _meetingInfo is always true for a freshly constructed object.
			var cur = _meetingInfo;
			var isSharing = _sdkSharingState > 0;
			var shareStatus = isSharing ? "Sharing" : "None";
			if (cur != null
				&& cur.Id == _currentMeetingId
				&& cur.Name == _currentMeetingName
				&& cur.ShareStatus == shareStatus
				&& cur.IsHost == _sdkIsHost
				&& cur.IsSharingMeeting == isSharing
				&& cur.IsLocked == _sdkMeetingLocked
				&& cur.IsRecording == _sdkIsRecording
				&& cur.CanRecord == _sdkCanRecord)
			{
				return; // no field changed — skip allocation and event
			}

			MeetingInfo = new MeetingInfo(
				_currentMeetingId,
				_currentMeetingName,
				string.Empty, // host name: SDK gap — ZrcSdk does not surface a host-name event
				string.Empty,
				shareStatus,
				_sdkIsHost,
				isSharing,
				false,
				_sdkMeetingLocked,
				_sdkIsRecording,
				_sdkCanRecord);
		}

		#region Implementation of IHasPresentationOnlyMeeting

		public void StartSharingOnlyMeeting()
		{
			StartSharingOnlyMeeting(eSharingMeetingMode.None, 30, String.Empty);
		}

		public void StartSharingOnlyMeeting(eSharingMeetingMode displayMode)
		{
			StartSharingOnlyMeeting(displayMode, DefaultMeetingDurationMin, String.Empty);
		}

		public void StartSharingOnlyMeeting(eSharingMeetingMode displayMode, uint duration)
		{
			StartSharingOnlyMeeting(displayMode, duration, String.Empty);
		}

		public void StartSharingOnlyMeeting(eSharingMeetingMode displayMode, uint duration, string password)
		{
			// Launches a sharing-only ("local presentation") meeting. NOTE: the SDK treats displayMode
			// as the LaunchSharingMeeting "init display state" only — on 4-series firmware the overlay
			// always opens on Desktop regardless. To switch the live instruction (Desktop/iPhone-iPad),
			// call ShowShareInstruction(mode) once the meeting is up. duration/password have no SDK
			// equivalent and are ignored.
			if (duration != 0 || !string.IsNullOrEmpty(password))
				this.LogDebug("StartSharingOnlyMeeting: duration/password are not supported by the SDK and are ignored");
			_controller.LaunchSharingMeeting(true, SharingModeToSdk(displayMode));
		}

		public void StartNormalMeetingFromSharingOnlyMeeting()
		{
			_controller.SwitchFromLocalPresentationToNormalMeeting();
		}

		/// <summary>
		/// Shows the sharing-instruction overlay for the given mode on the room screen
		/// (Laptop → Desktop tab, Ios → iPhone/iPad tab). Use this to switch the displayed
		/// instruction while in a sharing-only meeting — the SDK's <c>LaunchSharingMeeting</c>
		/// "init display state" does not change the live overlay (it always opens Desktop), so
		/// <c>ShowSharingInstruction</c> is the call that actually selects the tab.
		/// </summary>
		public void ShowShareInstruction(eSharingMeetingMode mode)
		{
			_controller.ShowSharingInstruction(true, SharingModeToSdk(mode));
		}

		/// <summary>Dismisses the sharing-instruction overlay on the room screen.</summary>
		public void DismissShareInstruction()
		{
			_controller.ShowSharingInstruction(false, 0);
		}

		// Maps Essentials eSharingMeetingMode -> ZRC SDK SharingInstructionDisplayState int.
		private static int SharingModeToSdk(eSharingMeetingMode mode)
		{
			switch (mode)
			{
				case eSharingMeetingMode.Laptop: return 1; // SharingInstructionDisplayStateDesktop
				case eSharingMeetingMode.Ios: return 2; // SharingInstructionDisplayStateIOS
				default: return 0; // None
			}
		}

		#endregion

		#region IHasMeetingLock Members

		public BoolFeedback MeetingIsLockedFeedback { get; private set; }

		public void LockMeeting()
		{
			_controller.LockMeeting(true);
		}

		public void UnLockMeeting()
		{
			_controller.LockMeeting(false);
		}

		public void ToggleMeetingLock()
		{
			if (MeetingIsLockedFeedback.BoolValue)
			{
				UnLockMeeting();
			}
			else
			{
				LockMeeting();
			}
		}

		#endregion

		#region IHasMeetingRecordingWithPrompt Members

		public BoolFeedback MeetingIsRecordingFeedback { get; private set; }

		bool _recordConsentPromptIsVisible;
		string _recordingRequestSenderName = string.Empty;
		string _recordingRequestType = "unknown";

		public BoolFeedback RecordConsentPromptIsVisible { get; private set; }

		/// <summary>Display name of the participant whose recording request is pending; empty for a cloud request.</summary>
		public string RecordingRequestSenderName => _recordingRequestSenderName;

		/// <summary>"local", "cloud" or "unknown" for the pending recording request.</summary>
		public string RecordingRequestType => _recordingRequestType;

		public void RecordingPromptAcknowledgement(bool agree)
		{
			this.LogInformation("RecordingRequest from \"{Sender}\" answered: agree={Agree}", _recordingRequestSenderName, agree);
			_recordConsentPromptIsVisible = false;
			_recordingRequestSenderName = string.Empty;
			_recordingRequestType = "unknown";
			RecordConsentPromptIsVisible.FireUpdate();
			_controller.ResponseToRecordingRequest(agree);
		}

		public void StartRecording()
		{
			_controller.StartRecording();
		}

		public void StopRecording()
		{
			_controller.StopRecording();
		}

		public void ToggleRecording()
		{
			if (MeetingIsRecordingFeedback.BoolValue)
			{
				StopRecording();
			}
			else
			{
				StartRecording();
			}
		}

		#endregion

		#region IZoomWirelessShareInstructions Members

		public event EventHandler<ShareInfoEventArgs> ShareInfoChanged;

		public zStatus.Sharing SharingState
		{
			get
			{
				return Status.Sharing;
			}
		}

		void OnShareInfoChanged(zStatus.Sharing status)
		{
			this.LogDebug(
@"ShareInfoChanged:
isSharingHDMI: {IsSharingHDMI}
isSharingAirplay: {IsSharingAirplay}
AirplayPassword: {AirplayPassword}
OSD Display State: {DispState}
",
status.isSharingBlackMagic,
status.isAirHostClientConnected,
status.password,
status.dispState);

			var handler = ShareInfoChanged;
			if (handler != null)
			{
				handler(this, new ShareInfoEventArgs(status));
			}
		}

		#endregion
	}
}
