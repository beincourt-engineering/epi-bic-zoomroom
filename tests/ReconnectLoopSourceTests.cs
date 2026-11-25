using System.Text.RegularExpressions;
using FluentAssertions;

namespace PepperDash.Essentials.Plugins.Zoom.Room.Tests;

/// <summary>
/// The reconnect loop is one self-perpetuating timer: ScheduleReconnect does nothing while the timer
/// is set. So every way the loop can stop must clear the timer, or the next real drop starts no
/// reconnect at all and the room stays offline until the program restarts. The case that used to
/// miss it: the health probe finds the link alive and marks the room connected, the loop's pending
/// attempt then sees it connected and returns, and the timer was left set. ZrcSdkController needs
/// Crestron and the native SDK, so these read the source.
/// </summary>
public class ReconnectLoopSourceTests
{
    private static readonly string Source =
        File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "Controller", "ZrcSdkController.cs"));

    private static string Method(string signature)
    {
        var start = Source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{signature} exists");
        // The method body runs to the next member at the class's indentation.
        var end = Regex.Match(Source.Substring(start + signature.Length), @"\r?\n        (private|public|internal|protected) ").Index;
        return Source.Substring(start, signature.Length + end);
    }

    [Fact]
    public void An_Attempt_That_Finds_The_Room_Connected_Ends_The_Loop()
    {
        var body = Method("private void RunReconnectAttempt()");

        body.Should().NotContain("_isConnected || _reconnectTimer == null) return;",
            "returning there left the timer set, so later reconnects were no-ops");
        Regex.IsMatch(body, @"if \(_isConnected\)\s*\{[^}]*EndLoopLocked\(\);\s*return;", RegexOptions.Singleline)
            .Should().BeTrue("a room already back online ends the loop and clears its timer");
    }

    [Fact]
    public void Ending_The_Loop_Clears_Its_Timer_And_Starts_The_Count_Over()
    {
        var body = Method("private void EndLoopLocked()");

        body.Should().Contain("_reconnectTimer?.Dispose();");
        body.Should().Contain("_reconnectTimer = null;");
        body.Should().Contain("_reconnectAttempt = 0;");
    }

    [Fact]
    public void Cancelling_The_Loop_Uses_The_Same_Ending()
    {
        Method("private void CancelReconnect()").Should().Contain("EndLoopLocked();");
    }

    [Fact]
    public void A_Probe_That_Finds_The_Link_Alive_Stops_The_Loop_Like_A_Connected_Event()
    {
        var body = Method("public void RunHealthCheck(string reason)");
        var recovered = Regex.Match(body, @"if \(!_isConnected\)\s*\{(?<block>.*?)SafeRaise\(\(\) => HealthStateChanged\?\.Invoke\(this, true\)\);",
            RegexOptions.Singleline);

        recovered.Success.Should().BeTrue("the probe's recovery path marks the room online");
        recovered.Groups["block"].Value.Should().Contain("CancelReconnect();");
        recovered.Groups["block"].Value.Should().Contain("_policy.Connected();");
    }
}
