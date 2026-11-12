![PepperDash Essentials Pluign Logo](/images/essentials-plugin-blue.png)

# Essentials Plugin Template (c) 2023

## License

Provided under MIT license

## Overview

Fork this repo when creating a new plugin for Essentials. For more information about plugins, refer to the Essentials Wiki [Plugins](https://github.com/PepperDash/Essentials/wiki/Plugins) article.

This repo contains example classes for the three main categories of devices:
* `EssentialsPluginTemplateDevice`: Used for most third party devices which require communication over a streaming mechanism such as a Com port, TCP/SSh/UDP socket, CEC, etc
* `EssentialsPluginTemplateLogicDevice`:  Used for devices that contain logic, but don't require any communication with third parties outside the program
* `EssentialsPluginTemplateCrestronDevice`:  Used for devices that represent a piece of Crestron hardware

There are matching factory classes for each of the three categories of devices.  The `EssentialsPluginTemplateConfigObject` should be used as a template and modified for any of the categories of device.  Same goes for the `EssentialsPluginTemplateBridgeJoinMap`.

This also illustrates how a plugin can contain multiple devices.

## Cloning Instructions

After forking this repository into your own GitHub space, you can create a new repository using this one as the template.  Then you must install the necessary dependencies as indicated below.

## Dependencies

The [Essentials](https://github.com/PepperDash/Essentials) libraries are required. They referenced via nuget. You must have nuget.exe installed and in the `PATH` environment variable to use the following command. Nuget.exe is available at [nuget.org](https://dist.nuget.org/win-x86-commandline/latest/nuget.exe).

### Installing Dependencies

To install dependencies once nuget.exe is installed, run the following command from the root directory of your repository:
`nuget install .\packages.config -OutputDirectory .\packages -excludeVersion`.
Alternatively, you can simply run the `GetPackages.bat` file.
To verify that the packages installed correctly, open the plugin solution in your repo and make sure that all references are found, then try and build it.

### Installing Different versions of PepperDash Core

If you need a different version of PepperDash Core, use the command `nuget install .\packages.config -OutputDirectory .\packages -excludeVersion -Version {versionToGet}`. Omitting the `-Version` option will pull the version indicated in the packages.config file.

### Instructions for Renaming Solution and Files

See the Task List in Visual Studio for a guide on how to start using the template.  There is extensive inline documentation and examples as well.

For renaming instructions in particular, see the XML `remarks` tags on class definitions

## Build Instructions (PepperDash Internal) 

## Generating Nuget Package 

In the solution folder is a file named "PDT.EssentialsPluginTemplate.nuspec" 

1. Rename the file to match your plugin solution name 
2. Edit the file to include your project specifics including
    1. <id>PepperDash.Essentials.Plugin.MakeModel</id> Convention is to use the prefix "PepperDash.Essentials.Plugin" and include the MakeModel of the device. 
    2. <projectUrl>https://github.com/PepperDash/EssentialsPluginTemplate</projectUrl> Change to your url to the project repo

There is no longer a requirement to adjust workflow files for nuget generation for private and public repositories.  This is now handled automatically in the workflow.

__If you do not make these changes to the nuspec file, the project will not generate a nuget package__

## Console Commands

The plugin registers the following console commands (operator access level):

| Command | Description |
|---------|-------------|
| `pairZoomRoom <activation-code>` | Pair the Zoom Room using the supplied activation code. |
| `repairZoomRoom` | Reconnect to the last paired Zoom Room using stored credentials. Reconnect is normally **automatic** (see Connection watchdog below); this is the manual override. |
| `unpairZoomRoom` | Unpair from the Zoom Room. |
| `forceRepairZoom` | Clear stored credentials and re-pair using the activation code from configuration. Use this after rotating the activation code, when stored credentials would otherwise be reused. |

## Behavior notes

### `MeetingInfo.CanRecord`
`CanRecord` reflects the ZRC SDK's `MeetingRecordingInfo.canIRecord` — **whether _this room_ can start recording** (the room's own ability). Because a **host can always record**, `CanRecord` stays `true` while the room is host and **does not track the "Record to computer" switch in Zoom's Host-tools menu**. That switch is a *participant* permission (`RecordingPermissionTypeLocalRecording`) governing whether attendees may record locally — it is not the room's own ability, and the plugin does not currently surface the participant recording-permission states. (Toggling it off while hosting will not change `CanRecord`; this is expected.)

> **Note:** Crestron console command names cannot be a complete prefix of another registered command, so the force re-pair command is named `forceRepairZoom` rather than `repairZoomRoomConfig` (which would collide with `repairZoomRoom`).

### Connection watchdog / auto-repair
The plugin actively monitors the SDK connection and **self-heals silent/half-open drops** — cases where the network dies without a clean close, leaving the SDK reporting connected while it is actually dead (devcomm would otherwise sit stuck at `IsOk`).

- **Detection:** a 30 s liveness poll plus consecutive command-failure strikes trigger a real `GetMeetingStatus()` probe; a failed probe marks the device offline in devcomm (`InError`).
- **Repair:** auto-reconnect with escalating backoff (`5 → 10 → 20 → 30 → 60 s`, capped at 60 s) that **never gives up** until the room is reachable again — no program restart or manual `repairZoomRoom` needed.

See [docs/connection-watchdog-silent-disconnect.md](docs/connection-watchdog-silent-disconnect.md) for the full write-up (issue, replication, fix, and lab validation).
<!-- START Minimum Essentials Framework Versions -->
### Minimum Essentials Framework Versions

- 3.0.0
<!-- END Minimum Essentials Framework Versions -->
<!-- START Config Example -->
### Config Example

```json
{
    "key": "GeneratedKey",
    "uid": 1,
    "name": "GeneratedName",
    "type": "ZoomRoomProperties",
    "group": "Group",
    "properties": {
        "communicationMonitorProperties": "SampleValue",
        "disablePhonebookAutoDownload": true,
        "supportsCameraAutoMode": true,
        "supportsCameraOff": true,
        "showMultiSpeakerLayout": true,
        "autoDefaultLayouts": true,
        "minutesBeforeMeetingStart": 0,
        "meetingInviteTimeoutMs": 0,
        "activationCode": "SampleString",
        "phoneDialMode": "SampleValue",
        "sdkConfigPath": "SampleString",
        "sdkRoomId": "SampleString",
        "FixedSpeakerVolume": 0,
        "FixedMicVolume": 0
    }
}
```
<!-- END Config Example -->
<!-- START Supported Types -->

<!-- END Supported Types -->
<!-- START Join Maps -->

<!-- END Join Maps -->
<!-- START Interfaces Implemented -->
### Interfaces Implemented

- IHasCameraPtzControl
- IHasCameraControls
- IBridgeAdvanced
- IHasCodecSelfView
- IHasDirectoryHistoryStack
- ICommunicationMonitor
- IHasScheduleAwareness
- IHasCodecCameras
- IHasParticipants
- IHasCameraOff
- IHasCameraMuteWithUnmuteReqeust
- IHasCameraAutoMode
- IHasFarEndContentStatus
- IHasSelfviewPosition
- IHasPhoneDialing
- IHasZoomRoomLayouts
- IHasParticipantPinUnpin
- IHasParticipantAudioMute
- IHasSelfviewSize
- IPasswordPrompt
- IHasStartMeeting
- IHasMeetingInfo
- IHasPresentationOnlyMeeting
- IHasMeetingLock
- IHasMeetingRecordingWithPrompt
- IZoomWirelessShareInstructions
- IHasCodecRoomPresets
- IRoutingSinkWithFeedback
- IHasFeedback
- INotifyPropertyChanged
- IAmFarEndCamera
- IZoomRoomController
- IKeyed
<!-- END Interfaces Implemented -->
<!-- START Base Classes -->
### Base Classes

- CameraBase
- EventArgs
- NotifiableObject
- VideoCodecBase
- VideoCodecInfo
- VideoCodecControllerJoinMap
- ZoomRoomCamera
- MessengerBase
- SelfviewOptionMessengerBase
- StatusMonitorBase
<!-- END Base Classes -->
<!-- START Public Methods -->
### Public Methods

- public bool SetCameraInputInMultiCamera(string inputPortKey, bool selected)
- public void RefreshWebinar(bool requestAttendees)
- public void RequestWebinarAttendees(string keywords)
- public void SimulateWebinar(int attendeeCount)
- public void AnswerPrompt(int id, bool accept)
- public void DismissPrompt(int id)
- public void SimulatePrompt(string kind, string text)
- public void SpotlightParticipant(int userId)
- public void UnspotlightParticipant(int userId)
- public void ToggleParticipantSpotlight(int userId)
- public void PositionHome()
- public void PanLeft()
- public void PanRight()
- public void PanStop()
- public void TiltDown()
- public void TiltUp()
- public void TiltStop()
- public void ZoomIn()
- public void ZoomOut()
- public void ZoomStop()
- public void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
- public void MuteAllParticipants()
- public void AskAllToUnmute()
- public void SetMuteOnEntry(bool enabled)
- public void SetAllowAttendeesUnmute(bool allow)
- public void SetAllowAttendeesStartVideo(bool allow)
- public void ClaimHost(string hostKey)
- public void SetParticipantAsCoHost(int userId, bool assign)
- public void PromoteToPanelist(int userId)
- public void DemoteToAttendee(int userId)
- public void AllowAttendeeTalk(int userId, bool allow)
- public void PauseRecording()
- public void ResumeRecording()
- public void SelectCamera(string key)
- public void GetSelfViewMode()
- public void SelfViewModeOn()
- public void SelfViewModeOff()
- public void SelfViewModeToggle()
- public void SearchDirectory(string searchString)
- public void GetDirectoryFolderContents(string folderId)
- public void SetCurrentDirectoryToRoot()
- public void GetDirectoryParentFolderContents()
- public void GetSchedule()
- public void ExecuteSwitch(object inputSelector, object outputSelector, eRoutingSignalType signalType)
- public void SetCurrentSource(eRoutingSignalType signalType, IRoutingSource sourceDevice)
- public void SimulateRecordingRequest(string senderName, int recordingType)
- public void VolumeSetToDefault()
- public void LinkZoomRoomToApi(BasicTriList trilist, ZoomRoomJoinMap joinMap)
- public void AcceptCall()
- public void RejectCall()
- public void InviteContactsToNewMeeting(List<InvitableDirectoryContact> contacts, uint duration)
- public void InviteContactsToExistingMeeting(List<InvitableDirectoryContact> contacts)
- public void LogDirectory()
- public void InviteContactById(string contactId)
- public void StartMeeting(uint duration)
- public void LeaveMeeting()
- public void EndMeetingForAll()
- public void RemoveParticipant(int userId)
- public void SetParticipantAsHost(int userId)
- public void AdmitParticipantFromWaitingRoom(int userId)
- public void AdmitAllParticipantsFromWaitingRoom()
- public void PutParticipantInWaitingRoom(int userId)
- public void RemoveAllFromWaitingRoom()
- public void CodecRoomPresetSelect(int preset)
- public void CodecRoomPresetStore(int preset, string description)
- public void SelectFarEndPreset(int preset)
- public void LogWaitingRoom()
- public void LogParticipants()
- public void LogMeetingInfo()
- public void LogVolume()
- public void MuteAudioForAllParticipants()
- public void MuteAudioForParticipant(int userId)
- public void UnmuteAudioForParticipant(int userId)
- public void ToggleAudioForParticipant(int userId)
- public void MuteVideoForParticipant(int userId)
- public void UnmuteVideoForParticipant(int userId)
- public void ToggleVideoForParticipant(int userId)
- public void PinParticipant(int userId, int screenIndex)
- public void UnPinParticipant(int userId)
- public void ToggleParticipantPinState(int userId, int screenIndex)
- public void CameraOff()
- public void CameraMuteOn()
- public void CameraMuteOff()
- public void CameraMuteToggle()
- public void CameraAutoModeOn()
- public void CameraAutoModeOff()
- public void CameraAutoModeToggle()
- public void SelfviewPipPositionSet(CodecCommandWithLabel position)
- public void SelfviewPipPositionToggle()
- public void SelfviewPipSizeSet(CodecCommandWithLabel size)
- public void SelfviewPipSizeToggle()
- public void DialPhoneCall(string number)
- public void EndPhoneCall()
- public void SendDtmfToPhone(string digit)
- public void GetAvailableLayouts()
- public void SetLayout(zConfiguration.eLayoutStyle layoutStyle)
- public void SetVideoOrder(string videoOrderCommand)
- public void SetThumbnailsPosition(string thumbnailsPositionCommand)
- public void SwapContentWithThumbnail()
- public void LayoutTurnNextPage()
- public void LayoutTurnPreviousPage()
- public void LocalLayoutToggle()
- public void LocalLayoutToggleSingleProminent()
- public void MinMaxLayoutToggle()
- public void SubmitPassword(string password)
- public void StartSharingOnlyMeeting()
- public void StartSharingOnlyMeeting(eSharingMeetingMode displayMode)
- public void StartSharingOnlyMeeting(eSharingMeetingMode displayMode, uint duration)
- public void StartSharingOnlyMeeting(eSharingMeetingMode displayMode, uint duration, string password)
- public void StartNormalMeetingFromSharingOnlyMeeting()
- public void ShowShareInstruction(eSharingMeetingMode mode)
- public void DismissShareInstruction()
- public void LockMeeting()
- public void UnLockMeeting()
- public void ToggleMeetingLock()
- public void RecordingPromptAcknowledgement(bool agree)
- public void StartRecording()
- public void StopRecording()
- public void ToggleRecording()
- public bool SetActiveSpeakerCamera(int tile)
- public void GetBreakoutSnapshot(out List<BreakoutRoomState> rooms, out List<BreakoutParticipantState> unassigned)
- public void RefreshBreakout()
- public void CreateBreakoutRooms(int count, int assignType)
- public void AddBreakoutRoom()
- public void DeleteBreakoutRoom(string roomId)
- public void RenameBreakoutRoom(string roomId, string name)
- public void AssignToBreakoutRoom(IEnumerable<string> userGuids, string roomId)
- public void InviteBackToMainSession(string userGuid)
- public void JoinBreakoutRoomById(string roomId)
- public void SetBreakoutOptions(BreakoutOptionsState o)
- public void StartBreakoutRooms()
- public void StopBreakoutRooms()
- public void BroadcastToBreakoutRooms(string message)
- public void JoinAssignedBreakoutRoom()
- public void LeaveBreakoutRoom()
- public void AskForHelpInBreakoutRoom()
- public void SimulateBreakoutStatus(int status)
- public bool SetLayoutWhileSharing(string layout)
- public void SetCameraTileNames(string[] names)
- public bool SetCameraDisplayName(string deviceId, string displayName)
- public int RenameCameraTile(int tile, string name)
- public bool Initialize(string configPath)
- public int GetConnectionState()
- public bool TryGetMeetingInfo(out CurrentMeetingInfo info)
- public bool TryGetBreakoutEnabled(out bool enabled)
- public bool PairWithActivationCode(string activationCode)
- public bool CanRetryPair()
- public bool RetryPair()
- public bool Unpair()
- public bool RepairWithConfiguredCode()
- public bool WakeUp()
- public bool Logout()
- public bool RestartOs()
- public bool StartMeeting(string meetingNumber)
- public bool StartInstantMeeting()
- public bool StartMeetingWithHostKey(string hostKey)
- public bool JoinMeeting(string meetingNumber)
- public bool JoinMeetingWithPassword(string meetingNumber, string password)
- public bool JoinMeetingWithUrl(string url)
- public bool EndMeeting()
- public bool LeaveMeeting()
- public bool AnswerMeetingInvite(bool accept)
- public bool SendMeetingPassword(string password)
- public bool CancelEnteringMeetingPassword()
- public bool CancelWaitingForHost()
- public bool LockMeeting(bool locked)
- public bool SetAudioMute(bool mute)
- public bool MuteUserAudio(int userId, bool mute)
- public bool MuteAllAudio(bool mute)
- public bool SetMuteOnEntry(bool mute)
- public bool AnswerUnmuteRequest(bool accepted)
- public bool AllowAttendeesUnmute(bool allow)
- public bool AllowAttendeesStartVideo(bool allow)
- public bool ConfirmMeetingReminder(bool agree, int reminderType)
- public bool ConfirmCustomizedMeetingReminder(bool agree, int type)
- public bool ConfirmConsent(bool agree, int consentType, string id)
- public bool ConfirmCombinedConsent(bool agree, long consentType)
- public bool HandlePrivacyAlert(int action, int type)
- public bool ContinueMeetingOnInactivity()
- public bool AnswerHostRequestUnmuteVideo(bool accepted)
- public bool RespondRemoteCameraControl(int userId, bool accept)
- public bool ResponseHostInviteToMainSession(bool accept)
- public bool JoinBreakoutRoom()
- public bool StartBreakoutRooms()
- public bool StopBreakoutRooms()
- public bool BroadcastMessageToBreakoutRooms(string message)
- public bool LeaveBreakoutRoom()
- public bool AskForHelpInBreakoutRoom()
- public bool CreateBreakoutRooms(int count, int assignType)
- public bool AddBreakoutRoom()
- public bool DeleteBreakoutRoom(string sessionBID)
- public bool RenameBreakoutRoom(string sessionBID, string newName)
- public bool AssignUsersToBreakoutRoom(IEnumerable<string> g, string b)
- public bool SetBOOptions(BOOptionsInfo options)
- public bool RequestBOOptions()
- public bool MoveUserToBreakoutRoom(string userGuid, string sessionBID)
- public bool InviteBOUserReturnToMainSession(string userGuid)
- public bool IgnoreBOHelpRequest(string userGuid)
- public bool JoinBreakoutRoomForHelp(string g, string b, string n)
- public bool JoinBreakoutRoomByBID(string sessionBID)
- public bool RequestBreakoutRoomList()
- public bool RequestBreakoutRoomUserList()
- public bool ClaimHost(string hostKey)
- public bool AssignCohost(int userId, bool assign)
- public bool PromoteAttendeeToPanelist(int userId)
- public bool DemotePanelistToAttendee(int userId)
- public bool AllowWebinarAttendeeTalk(int userId, bool allow)
- public bool ListWebinarAttendees(string keywords)
- public bool SetSpeakerVolume(float volume)
- public float GetSpeakerVolume()
- public bool SetMicrophoneVolume(float volume)
- public float GetMicrophoneVolume()
- public bool SetVideoState(bool start)
- public bool SetMyVideoHidden(bool hidden)
- public bool MuteUserVideo(int userId, bool mute)
- public bool PinUserOnScreen(int userId, int screenIndex = 0)
- public bool UnpinUserFromScreen(int userId, int screenIndex = 0)
- public bool SpotlightUser(int userId)
- public bool CancelSpotlightUser(int userId)
- public bool ControlUserCamera(int userId, int action, int type)
- public bool ControlCamera(string deviceId, int action, int type)
- public bool ChangeSmartCameraMode(int mask, string deviceId = "")
- public CameraDevice GetCurrentCamera()
- public int JoinIntegrationMeeting(string meetingId, string password, ThirdPartyMeetingProvider provider)
- public int LeaveIntegrationMeeting()
- public int MuteIntegrationAudio(bool mute)
- public int StopIntegrationVideo(bool stop)
- public InterOperabilityInfo GetInterOperabilityInfo(ThirdPartyMeetingProvider provider)
- public int SetCameraDisplayName(string deviceId, string displayName)
- public int SelectMultipleCamera(string deviceId, bool selected)
- public bool SetCurrentCamera(string deviceId)
- public bool SetCameraPreset(uint index, string deviceId)
- public bool GoToCameraPreset(uint index, string deviceId)
- public bool NameCameraPreset(uint index, string name, string deviceId)
- public int SetScreenLayout(int screen, int layoutSourceType)
- public int SetVideoOrder(int videoOrderType)
- public int SetDynamicLayoutOption(int layout)
- public int UpdateVideoLayoutStyle(int videoLayoutStyle)
- public int ControlVideoPosition(int position, int size)
- public int TurnVideoPage(bool forward, int pageVideoType)
- public int ChangeThumbnailsPosition(int type)
- public int SwitchToFloatingShareForSingleScreen(bool floatingShare)
- public bool StartRecording()
- public bool StopRecording()
- public bool PauseRecording()
- public bool ResumeRecording()
- public bool ResponseToRecordingRequest(bool accept, bool acceptAlways = false)
- public int GetParticipantCount()
- public bool ExpelUser(int userId)
- public bool AssignHost(int userId)
- public int SetMySelfAsActiveSpeaker()
- public int SetMyChildAsActiveSpeaker(int userId)
- public int RenameUser(int userId, string name)
- public bool StopShare()
- public bool LaunchSharingMeeting(bool isInLocalShare, int displayState)
- public bool SwitchFromLocalPresentationToNormalMeeting()
- public bool ShowSharingInstruction(bool show, int instructionState)
- public bool ShareBlackMagic(bool isStart, bool isViewLocally)
- public bool AdmitUserFromWaitingRoom(int userId)
- public bool AdmitAllFromWaitingRoom()
- public bool PutUserInWaitingRoom(int userId)
- public bool TerminateSipCall(string callId)
- public bool CallSip(string uri)
- public bool SendDtmfToSipCall(string dtmf, string callId)
- public bool CallOutPstnUser(string phoneNumber, bool cancelCall, bool hasVoicePrompt)
- public bool SubscribeContacts(int startIndex, int count, bool searchSip)
- public bool InviteAttendees(string[] contactIds)
- public bool MeetWithIMUsers(string[] contactIds)
- public bool ListMeeting()
- public bool IsZrcsEnabled()
- public void Dispose()
- public void RunHealthCheck(string reason)
- public void SetOnline(bool online)
- public void ZoomRoom_Implements_IHasFeedback_Explicitly()
- public void ZoomRoom_Exposes_Call_State_Feedback(string name, string typeName)
- public void Monitored_Feedback_Is_Built_With_Its_Key(string property, string key)
- public void Monitored_Feedbacks_Are_The_Low_Churn_Call_State()
- public void Meeting_Reset_Fires_The_Feedbacks_Of_The_Fields_It_Clears(string feedback)
- public void Call_State_Feedback_Is_Fired_Where_Its_Source_Changes(string method, string feedback)
- public void A_Failed_Sdk_Start_Puts_The_Monitor_In_Error()
- public void ZoomRoom_Exposes_Room_Hook(string name, string parameterTypes)
- public void IZoomRoomController_Exists_In_Assembly()
- public void IZoomRoomController_Is_Interface()
- public void IZoomRoomController_Extends_IDisposable()
- public void IZoomRoomController_Has_Lifecycle_Method(string methodName)
- public void IZoomRoomController_Has_Meeting_Method(string methodName)
- public void IZoomRoomController_Has_Audio_Method(string methodName)
- public void IZoomRoomController_Has_Video_Method(string methodName)
- public void IZoomRoomController_Has_Layout_Method(string methodName)
- public void IZoomRoomController_Has_Share_Method(string methodName)
- public void IZoomRoomController_Has_Recording_Method(string methodName)
- public void IZoomRoomController_Has_Participant_Method(string methodName)
- public void IZoomRoomController_Has_WaitingRoom_Method(string methodName)
- public void IZoomRoomController_Has_Phone_Method(string methodName)
- public void IZoomRoomController_Has_Contacts_Method(string methodName)
- public void IZoomRoomController_Has_ListMeeting_Method()
- public void IZoomRoomController_Has_SelectMultipleCamera_Method()
- public void IZoomRoomController_Has_TryGetBreakoutEnabled_Method()
- public void IZoomRoomController_CommandMethod_Returns_Bool(string methodName)
- public void IZoomRoomController_GetConnectionState_Returns_Int()
- public void IZoomRoomController_Has_SdkEvent(string eventName)
- public void IZoomRoomController_Has_ParticipantEvent(string eventName)
- public void IZoomRoomController_Has_SharingStatusChanged_Event()
- public void IZoomRoomController_Has_VideoPageStatusChanged_Event()
- public void IZoomRoomController_Has_ContactListChanged_Event()
- public void IZoomRoomController_Has_MeetingListChanged_Event()
- public void IZoomRoomController_Has_MeetingRecordingInfoChanged_Event()
- public void IZoomRoomController_Has_JoinMeeting_Overload_Without_Password()
- public void IZoomRoomController_Has_JoinMeetingWithPassword_Overload()
- public void IZoomRoomController_Has_SendMeetingPassword()
- public void IZoomRoomController_Has_MeetingNeedsPassword_Event()
- public void ZoomRoomPropertiesConfig_Exists_In_Assembly()
- public void ZoomRoomPropertiesConfig_Has_Parameterless_Constructor()
- public void ZoomRoomPropertiesConfig_Property_Has_JsonPropertyAttribute(string jsonName)
- public void Assembly_Loads_Successfully()
- public void Assembly_Name_Matches_Expected()
- public void Factory_Count_Matches_Expected()
- public void Factory_Exists_ByName(string factoryClassName)
- public void All_Factories_Have_Parameterless_Constructor()
- public void Nothing_Known_Places_Children_By_Zoom_Number()
- public void Known_Camera_Takes_Its_Input_Tile_And_Moves_The_Child_That_Had_It()
- public void Tile_Of_A_Camera_Not_In_The_Meeting_Stays_Empty()
- public void Later_Child_Wins_When_Two_Claim_One_Input()
- public void Child_Without_A_Number_Takes_The_Lowest_Free_Tile()
- public void Known_Tile_1_Is_Ignored_Because_Tile_1_Is_The_Room()
- public void No_Children_Gives_No_Tiles()
- public void Factory_Source_Sets_MinimumEssentialsFrameworkVersion_To_3(string factoryClassName)
- public void Factory_Source_Assigns_TypeNames(string factoryClassName)
- public void Factory_Source_Contains_TypeName(string factoryClassName, string typeName)
- public void No_Duplicate_TypeNames_Across_Factories()
<!-- END Public Methods -->
<!-- START Bool Feedbacks -->
### Bool Feedbacks

- InCallFeedback
- ControllingFarEndCameraFeedback
- SelfviewIsOnFeedback
- CurrentDirectoryResultIsNotDirectoryRoot
- CameraIsOffFeedback
- CameraIsMutedFeedback
- CameraAutoModeIsOnFeedback
- ReceivingContent
- PhoneOffHookFeedback
- LayoutViewIsOnFirstPageFeedback
- LayoutViewIsOnLastPageFeedback
- CanSwapContentWithThumbnailFeedback
- ContentSwappedWithThumbnailFeedback
- MeetingIsLockedFeedback
- MeetingIsRecordingFeedback
- RecordConsentPromptIsVisible
<!-- END Bool Feedbacks -->
<!-- START Int Feedbacks -->
### Int Feedbacks

- ParticipantCountFeedback
- NumberOfScreensFeedback
<!-- END Int Feedbacks -->
<!-- START String Feedbacks -->
### String Feedbacks

- SelectedCameraFeedback
- SelfviewPipPositionFeedback
- SelfviewPipSizeFeedback
- CallerIdNameFeedback
- CallerIdNumberFeedback
- LocalLayoutFeedback
<!-- END String Feedbacks -->
