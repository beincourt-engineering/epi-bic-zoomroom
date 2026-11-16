using FluentAssertions;

namespace PepperDash.Essentials.Plugins.Zoom.Room.Tests;

/// <summary>
/// The Room PC's CPU and memory are held steady so a figure that moves with every statistics message
/// doesn't re-send the whole room record each time.
/// </summary>
public class SteadyReadingTests
{
    [Fact]
    public void The_First_Reading_Shows_At_Once()
    {
        new SteadyReading(10).Next(46).Should().Be(46);
    }

    [Fact]
    public void A_Reading_Within_A_Step_Holds_The_One_Shown()
    {
        var cpu = new SteadyReading(10);
        cpu.Next(46);
        cpu.Next(40).Should().Be(46);
        cpu.Next(55).Should().Be(46);
    }

    [Fact]
    public void A_Full_Step_Shows_The_New_Reading()
    {
        var cpu = new SteadyReading(10);
        cpu.Next(46);
        cpu.Next(56).Should().Be(56);
        cpu.Next(46).Should().Be(46);
    }

    [Fact]
    public void Small_Moves_Measure_From_The_Shown_Value_Not_The_Last_Reading()
    {
        // Creeping up a few points at a time still shows once it is a step from what's shown.
        var cpu = new SteadyReading(10);
        cpu.Next(20);
        cpu.Next(26).Should().Be(20);
        cpu.Next(29).Should().Be(20);
        cpu.Next(31).Should().Be(31);
    }

    [Fact]
    public void No_Reading_Clears_And_The_Next_Reading_Shows_At_Once()
    {
        var memory = new SteadyReading(5);
        memory.Next(80);
        memory.Next(null).Should().BeNull();
        memory.Next(79).Should().Be(79);
    }

    [Fact]
    public void Jsc_In_Call_Cpu_Changes_Less_Often()
    {
        // Twelve in-call readings from JSC's Room PC, 2026-11-16 20:03-20:09 UTC.
        int[] readings = { 46, 34, 29, 48, 29, 24, 22, 34, 53, 38, 28, 26 };
        var cpu = new SteadyReading(10);
        int? shown = null;
        var changes = 0;
        foreach (var r in readings)
        {
            var next = cpu.Next(r);
            if (next != shown) changes++;
            shown = next;
        }
        changes.Should().Be(7); // 12 without the hold
    }

    [Fact]
    public void A_Spike_Still_Shows()
    {
        var cpu = new SteadyReading(10);
        cpu.Next(35);
        cpu.Next(100).Should().Be(100);
    }

    [Fact]
    public void Flat_Memory_Never_Changes()
    {
        int[] readings = { 78, 78, 77, 78, 79, 80, 80, 79, 80 };
        var memory = new SteadyReading(5);
        foreach (var r in readings)
            memory.Next(r).Should().Be(78);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_Step_Under_1_Is_Refused(int step)
    {
        var make = () => new SteadyReading(step);
        make.Should().Throw<ArgumentOutOfRangeException>();
    }
}
