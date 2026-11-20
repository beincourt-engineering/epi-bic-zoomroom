using System.Text.RegularExpressions;
using FluentAssertions;

namespace PepperDash.Essentials.Plugins.Zoom.Room.Tests;

/// <summary>
/// Court Central shows a device's status message as the reason it is down. When Zoom refuses the
/// room's stored pairing, the reason must say the room needs a new activation code and how to give it
/// one, not a bare "not responding". Telemetry keeps the last message it was given, so the room says it
/// is connected again once it is, and a plain drop never shows an old refusal.
/// </summary>
public class ConnectionMessageTests
{
    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, relative));

    [Fact]
    public void A_Room_Refused_Twice_Needs_A_New_Activation_Code()
    {
        var message = ConnectionMessage.ForRefusal(2, 100000401, "zoomRoom-zrc", tryingConfiguredCode: false);

        message.Should().Contain("needs a new activation code");
        message.Should().Contain("Zoom portal");
        message.Should().Contain("pairZoomRoom zoomRoom-zrc <code>");
        message.Should().Contain("100000401");
        message.Should().Contain("every 10 min");
    }

    [Fact]
    public void Later_Refusals_Say_The_Same_As_The_Second()
    {
        ConnectionMessage.ForRefusal(7, 100000401, "zoomRoom-zrc", false)
            .Should().Be(ConnectionMessage.ForRefusal(2, 100000401, "zoomRoom-zrc", false));
    }

    [Fact]
    public void A_First_Refusal_Says_It_Is_Checked_Again()
    {
        var message = ConnectionMessage.ForRefusal(1, 100000401, "zoomRoom-zrc", tryingConfiguredCode: false);

        message.Should().Contain("refused the stored pairing");
        message.Should().Contain("checking again in 1 min");
        message.Should().NotContain("new activation code");
    }

    [Fact]
    public void A_First_Refusal_With_An_Unused_Code_Says_The_Code_Is_Being_Tried()
    {
        ConnectionMessage.ForRefusal(1, 5002, "zoomRoom-zrc", tryingConfiguredCode: true)
            .Should().Contain("trying the configured activation code");
    }

    [Fact]
    public void Online_Says_Connected_And_Never_The_Old_Problem()
    {
        ConnectionMessage.ForState(true, "a refusal").Should().Be(ConnectionMessage.Connected);
    }

    [Fact]
    public void Offline_Shows_The_Pairing_Problem_When_There_Is_One()
    {
        ConnectionMessage.ForState(false, "the refusal text").Should().Be("the refusal text");
        ConnectionMessage.ForState(false, null).Should().Be(ConnectionMessage.Reconnecting);
        ConnectionMessage.ForState(false, "").Should().Be(ConnectionMessage.Reconnecting);
    }

    [Fact]
    public void Every_Message_Is_Non_Empty_So_Telemetry_Replaces_The_Last_One()
    {
        // Telemetry ignores an empty message and keeps the previous one.
        ConnectionMessage.Connected.Should().NotBeNullOrWhiteSpace();
        ConnectionMessage.Reconnecting.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_Controller_Reports_Each_Refusal_As_A_Pairing_Problem()
    {
        var source = Source(Path.Combine("Controller", "ZrcSdkController.cs"));
        var body = Regex.Match(source, @"private void OnStoredPairingRefused\(int errorCode\)(?<body>.*?)RescheduleReconnect\(delayMs\);",
            RegexOptions.Singleline);

        body.Success.Should().BeTrue();
        body.Groups["body"].Value.Should().Contain("ConnectionMessage.ForRefusal(");
        body.Groups["body"].Value.Should().Contain("PairingProblemChanged?.Invoke(this,");
    }

    [Fact]
    public void The_Room_Hands_The_Pairing_Problem_To_Its_Monitor()
    {
        Source(Path.Combine("Controller", "IZoomRoomController.cs"))
            .Should().Contain("event EventHandler<string> PairingProblemChanged;");
        Source("ZoomRoom.cs")
            .Should().MatchRegex(@"_controller\.PairingProblemChanged \+= \(s, problem\) => _sdkMonitor\.SetPairingProblem\(problem\);");
    }

    [Fact]
    public void The_Monitor_Sets_Its_Message_Before_Its_Status()
    {
        // The message goes first, so the state change Central records already carries the reason.
        var source = Source(Path.Combine("Controller", "SdkConnectionMonitor.cs"));
        var body = Regex.Match(source, @"public void SetOnline\(bool online\)(?<body>.*?)\n        \}", RegexOptions.Singleline);

        body.Success.Should().BeTrue();
        var text = body.Groups["body"].Value;
        var message = Regex.Match(text, @"\bMessage\s+=\s+ConnectionMessage\.ForState\(online, _pairingProblem\);");
        var status = Regex.Match(text, @"\bStatus\s+=");
        message.Success.Should().BeTrue();
        status.Success.Should().BeTrue();
        message.Index.Should().BeLessThan(status.Index);
    }
}
