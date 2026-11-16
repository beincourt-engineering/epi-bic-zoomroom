using System.Text.RegularExpressions;
using FluentAssertions;

namespace PepperDash.Essentials.Plugins.Zoom.Room.Tests;

/// <summary>
/// The Room PC's CPU and memory use, from the ZRC SDK statistics. The SDK documents no units, so the
/// figures are kept only when they can be a percentage.
/// </summary>
public class PcHealthTests
{
    private static readonly string ZoomRoomSource =
        File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "ZoomRoom.cs"));

    private static readonly string PcHealthSource =
        File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "ZoomRoom.PcHealth.cs"));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(31, 31)]
    [InlineData(100, 100)]
    public void Cpu_Use_Is_Kept_As_A_Percentage(int sdk, int expected)
    {
        ZoomRoomPcHealth.CpuPercent(sdk).Should().Be(expected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(3100)]
    public void Cpu_Use_Outside_0_To_100_Is_Unread(int sdk)
    {
        ZoomRoomPcHealth.CpuPercent(sdk).Should().BeNull();
    }

    [Theory]
    [InlineData(47, 16384, 47)]   // a percentage
    [InlineData(47, 0, 47)]       // a percentage needs no size
    [InlineData(8192, 16384, 50)] // an amount in the size's unit
    [InlineData(6000, 16000, 37)] // rounded down
    public void Memory_Use_Reads_As_A_Percentage(int usage, int size, int expected)
    {
        ZoomRoomPcHealth.MemoryPercent(usage, size).Should().Be(expected);
    }

    [Theory]
    [InlineData(-1, 16384)]
    [InlineData(8192, 0)]          // an amount with no size to divide by
    [InlineData(8192, 8)]          // amount and size in different units: more than all of it
    [InlineData(int.MaxValue, 1)]  // no overflow
    public void Memory_Use_That_Cannot_Be_A_Percentage_Is_Unread(int usage, int size)
    {
        ZoomRoomPcHealth.MemoryPercent(usage, size).Should().BeNull();
    }

    [Fact]
    public void Statistics_Are_Switched_On_From_A_Timer_After_Connect()
    {
        // SDK calls made inside an SDK status event fail, so the connect handler only schedules it.
        MethodBody(ZoomRoomSource, "private void OnControllerConnectionStateChanged(object sender, SdkEventArgs e)")
            .Should().Contain("SchedulePcStatisticsEnable();")
            .And.NotContain("EnableStatisticalInfo(");
        PcHealthSource.Should().Contain("new CTimer(_ => EnablePcStatistics(), PcStatisticsEnableDelayMs)");
    }

    [Theory]
    [InlineData("private void OnControllerConnectionStateChanged(object sender, SdkEventArgs e)")]
    [InlineData("private void OnControllerHealthStateChanged(object sender, bool online)")]
    public void Going_Offline_Clears_The_Pc_Figures(string handler)
    {
        MethodBody(ZoomRoomSource, handler).Should().Contain("ClearPcStatistics();");
    }

    [Fact]
    public void The_Statistics_Event_Is_Subscribed()
    {
        MethodBody(ZoomRoomSource, "protected override void Initialize()")
            .Should().Contain("_controller.StatisticalInfoChanged += OnControllerStatisticalInfoChanged;");
    }

    [Fact]
    public void Statistics_And_The_Offline_Clear_Go_Through_The_Steady_Readings()
    {
        var handler = MethodBody(PcHealthSource,
            "private void OnControllerStatisticalInfoChanged(object sender, StatisticalInfoEventArgs e)");
        handler.Should().Contain("_pcCpuPercent = _steadyPcCpu.Next(")
            .And.Contain("_pcMemoryPercent = _steadyPcMemory.Next(");
        MethodBody(PcHealthSource, "private void ClearPcStatistics()")
            .Should().Contain("_steadyPcCpu.Next(null)")
            .And.Contain("_steadyPcMemory.Next(null)");
    }

    private static string MethodBody(string source, string signature)
    {
        var match = Regex.Match(source,
            Regex.Escape(signature) + @"\s*\{(?<body>.*?)\r?\n\t\t\}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue("the source must contain {0}", signature);
        return match.Groups["body"].Value;
    }
}
