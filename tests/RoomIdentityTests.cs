using FluentAssertions;
using Xunit;

namespace PepperDash.Essentials.Plugins.Zoom.Room.Tests;

/// <summary>
/// The Zoom Room's name and IP address as its device info. The ZRC SDK gives only the room's name, display name and
/// address, so the plugin reports those and nothing it can't read.
/// </summary>
public class RoomIdentityTests
{
    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, file));

    [Fact]
    public void The_Zoom_Room_Is_A_Device_Info_Provider()
    {
        var type = AssemblyFixture.PluginAssembly.GetTypes().Single(t => t.Name == "ZoomRoom");

        type.GetInterfaces().Select(i => i.Name).Should().Contain("IDeviceInfoProvider",
            "telemetry reads a device's identity from its IDeviceInfoProvider");
        type.GetMethod("UpdateDeviceInfo").Should().NotBeNull();
    }

    [Fact]
    public void The_Controller_Reads_The_Paired_Room()
    {
        var controller = AssemblyFixture.PluginAssembly.GetTypes().Single(t => t.Name == "IZoomRoomController");

        controller.GetMethod("GetLastZoomRoomInfo")!.ReturnType.Name.Should().Be("PairedRoomInfo");
    }

    [Fact]
    public void The_Name_And_Address_Are_Read_After_Connected_Off_The_Sdk_Thread()
    {
        // SDK calls made inside an SDK status event fail, so the connect handler only schedules the read.
        Source("ZoomRoom.cs").Should().Contain("ScheduleRoomIdentityRead();");

        var identity = Source("ZoomRoom.Identity.cs");
        identity.Should().Contain("_roomIdentityTimer = new CTimer(_ => ReadRoomIdentity(), RoomIdentityReadDelayMs);");
        identity.Should().Contain("_controller.GetLastZoomRoomInfo()");
        identity.Should().Contain("var name = info.RoomName ?? info.DisplayName;");
        identity.Should().Contain("_roomIdentity.HostName = name;");
        identity.Should().Contain("_roomIdentity.IpAddress = info.RoomAddress;");
        // nothing it can't read: no made-up model, serial or MAC
        identity.Should().NotContain("_roomIdentity.SerialNumber =");
        identity.Should().NotContain("_roomIdentity.MacAddress =");
    }
}
