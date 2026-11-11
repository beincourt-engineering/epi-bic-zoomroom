using System.Text.RegularExpressions;
using FluentAssertions;

namespace PepperDash.Essentials.Plugins.Zoom.Room.Tests;

/// <summary>
/// Monitoring consumers read a device's values from IHasFeedback.Feedbacks: keyed feedbacks only,
/// read from their cached value and subscribed once after activation. A feedback that is off the list,
/// has no key, or isn't fired when its value changes reports nothing or a stale value, with no error
/// anywhere. ZoomRoom's constructor needs Crestron, so these inspect the compiled assembly and the
/// source rather than a live instance.
/// </summary>
public class MonitoringFeedbackTests
{
    private static System.Reflection.TypeInfo ZoomRoomType =>
        AssemblyFixture.PluginAssembly.DefinedTypes.Single(t => t.Name == "ZoomRoom");

    private static readonly string ZoomRoomSource =
        File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "ZoomRoom.cs"));

    [Fact]
    public void ZoomRoom_Implements_IHasFeedback_Explicitly()
    {
        ZoomRoomType.ImplementedInterfaces.Select(i => i.FullName)
            .Should().Contain("PepperDash.Essentials.Core.IHasFeedback");

        ZoomRoomType.DeclaredProperties
            .Should().Contain(p => p.Name == "PepperDash.Essentials.Core.IHasFeedback.Feedbacks"
                    && p.PropertyType.Name == "FeedbackCollection`1",
                "the interface member is implemented explicitly");

        // The codec base class has its own public Feedbacks list, which isn't part of any interface.
        ZoomRoomType.GetProperties().Where(p => p.Name == "Feedbacks")
            .Should().ContainSingle().Which.PropertyType.Name
            .Should().Be("List`1", "the base class's public Feedbacks member is left as it was");
    }

    [Theory]
    [InlineData("InCallFeedback", "BoolFeedback")]
    [InlineData("ParticipantCountFeedback", "IntFeedback")]
    public void ZoomRoom_Exposes_Call_State_Feedback(string name, string typeName)
    {
        ZoomRoomType.GetProperties()
            .Should().Contain(p => p.Name == name && p.PropertyType.Name == typeName);
    }

    [Theory]
    [InlineData("InCallFeedback", "inCall")]
    [InlineData("ParticipantCountFeedback", "participantCount")]
    [InlineData("MeetingIsRecordingFeedback", "meetingIsRecording")]
    [InlineData("MeetingIsLockedFeedback", "meetingIsLocked")]
    [InlineData("PhoneOffHookFeedback", "phoneOffHook")]
    public void Monitored_Feedback_Is_Built_With_Its_Key(string property, string key)
    {
        // Unkeyed feedbacks are skipped by monitoring consumers, and the keys are what they are
        // configured to follow. (privacyModeIsOn and sharingContentIsOn are keyed by the base class.)
        ZoomRoomSource.Should().MatchRegex($@"\b{property} = new (Bool|Int|String)Feedback\(""{key}"",");
    }

    [Fact]
    public void Monitored_Feedbacks_Are_The_Low_Churn_Call_State()
    {
        // Any change to a listed feedback re-sends the device's whole state, so the list holds only
        // values that change a few times per meeting - never a roster or other large or busy value.
        var match = Regex.Match(ZoomRoomSource,
            @"_monitoredFeedbacks = new FeedbackCollection<Feedback>\s*\{(?<items>[^}]*)\}");
        match.Success.Should().BeTrue("the constructor builds the monitored feedback list");

        match.Groups["items"].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
            .Should().BeEquivalentTo(
                "InCallFeedback",
                "ParticipantCountFeedback",
                "PrivacyModeIsOnFeedback",
                "SharingContentIsOnFeedback",
                "MeetingIsRecordingFeedback",
                "MeetingIsLockedFeedback",
                "PhoneOffHookFeedback");
    }

    [Theory]
    [InlineData("MeetingIsLockedFeedback")]
    [InlineData("MeetingIsRecordingFeedback")]
    [InlineData("SharingContentIsOnFeedback")]
    [InlineData("SharingSourceFeedback")]
    [InlineData("PhoneOffHookFeedback")]
    [InlineData("ParticipantCountFeedback")]
    public void Meeting_Reset_Fires_The_Feedbacks_Of_The_Fields_It_Clears(string feedback)
    {
        // The reset clears these fields directly, without the SDK notification that normally fires
        // them, so without this the cached values keep the ended meeting's state.
        MethodBody("private void ResetMeetingState()")
            .Should().Contain($"{feedback}.FireUpdate();");
    }

    [Theory]
    [InlineData("protected override void OnCallStatusChange(CodecActiveCallItem item)", "InCallFeedback")]
    [InlineData("private void ApplyParticipantEvent(ParticipantListEventArgs e, bool isLeaveEvent)", "ParticipantCountFeedback")]
    public void Call_State_Feedback_Is_Fired_Where_Its_Source_Changes(string method, string feedback)
    {
        MethodBody(method).Should().Contain($"{feedback}.FireUpdate();");
    }

    [Fact]
    public void A_Failed_Sdk_Start_Puts_The_Monitor_In_Error()
    {
        // A start that fails never raises a connection event, so the monitor must be set here or it
        // reads Unknown for the whole run.
        var body = MethodBody("protected override void Initialize()");
        var failure = Regex.Match(body,
            @"if \(!_controller\.Initialize\(_props\.SdkConfigPath\)\)\s*\{(?<branch>.*?)\r?\n\t\t\t\}",
            RegexOptions.Singleline);

        failure.Success.Should().BeTrue("Initialize() must handle a failed SDK start");
        failure.Groups["branch"].Value.Should().Contain("_sdkMonitor.SetOnline(false);");
    }

    /// <summary>The body of a ZoomRoom.cs method, from its signature to its closing brace.</summary>
    private static string MethodBody(string signature)
    {
        var match = Regex.Match(ZoomRoomSource,
            Regex.Escape(signature) + @"\s*\{(?<body>.*?)\r?\n\t\t\}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("ZoomRoom.cs must contain {0}", signature);
        return match.Groups["body"].Value;
    }
}
