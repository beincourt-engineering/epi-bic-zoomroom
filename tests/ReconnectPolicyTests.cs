using FluentAssertions;

namespace PepperDash.Essentials.Plugins.Zoom.Room.Tests;

/// <summary>
/// When a Zoom Room asks Zoom again with its stored pairing: failures keep retrying for good, a first
/// refusal is checked once more, a room refused twice slows down, and rooms sharing the SDK never ask
/// at the same moment.
/// </summary>
public class ReconnectPolicyTests
{
    [Fact]
    public void The_First_Room_Asks_At_Once_And_Each_Room_After_It_Waits_Five_Seconds_More()
    {
        new ReconnectPolicy(0).OffsetMs.Should().Be(0);
        new ReconnectPolicy(1).OffsetMs.Should().Be(5000);
        new ReconnectPolicy(3).OffsetMs.Should().Be(15000);
    }

    [Fact]
    public void A_Negative_Slot_Is_Treated_As_The_First_Room()
    {
        new ReconnectPolicy(-1).OffsetMs.Should().Be(0);
    }

    [Fact]
    public void Failures_Retry_After_5_10_20_And_30_Seconds_Then_Every_Minute()
    {
        var policy = new ReconnectPolicy(0);
        var delays = Enumerable.Range(0, 8).Select(_ => policy.NextAfterFailure()).ToArray();
        delays.Should().Equal(5000, 10000, 20000, 30000, 60000, 60000, 60000, 60000);
    }

    [Fact]
    public void Every_Retry_Keeps_The_Rooms_Offset()
    {
        var policy = new ReconnectPolicy(2);
        policy.NextAfterFailure().Should().Be(15000);
        policy.NextAfterFailure().Should().Be(20000);
        policy.NextAfterRefusal().Should().Be(70000);
        policy.NextAfterRefusal().Should().Be(610000);
    }

    [Fact]
    public void A_First_Refusal_Is_Checked_Again_After_A_Minute()
    {
        var policy = new ReconnectPolicy(0);
        policy.NextAfterRefusal().Should().Be(60000);
        policy.ConsecutiveRefusals.Should().Be(1);
        policy.IsRefused.Should().BeFalse();
    }

    [Fact]
    public void A_Room_Refused_Twice_Asks_Only_Every_Ten_Minutes()
    {
        var policy = new ReconnectPolicy(0);
        policy.NextAfterRefusal();
        policy.NextAfterRefusal().Should().Be(600000);
        policy.NextAfterRefusal().Should().Be(600000);
        policy.IsRefused.Should().BeTrue();
        policy.ConsecutiveRefusals.Should().Be(3);
    }

    [Fact]
    public void Connecting_Starts_The_Schedule_Over()
    {
        var policy = new ReconnectPolicy(0);
        policy.NextAfterFailure();
        policy.NextAfterFailure();
        policy.NextAfterRefusal();
        policy.NextAfterRefusal();

        policy.Connected();

        policy.IsRefused.Should().BeFalse();
        policy.ConsecutiveRefusals.Should().Be(0);
        policy.NextAfterFailure().Should().Be(5000);
        policy.NextAfterRefusal().Should().Be(60000);
    }

    [Fact]
    public void Slots_Are_Handed_Out_One_Apart_In_Creation_Order()
    {
        var first = ReconnectPolicy.TakeSlot();
        var second = ReconnectPolicy.TakeSlot();
        second.Should().Be(first + 1);
        first.Should().BeGreaterThanOrEqualTo(0);
    }
}
