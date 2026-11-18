using System;
using Crestron.SimplSharp;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core.DeviceInfo;
using PepperDash.ZoomRoom.Sdk;

namespace PepperDash.Essentials.Plugins
{
	// What the Zoom Room says it is, for monitoring. The ZRC SDK carries only the room's name, display name and IP
	// address (ZoomRoomInfo); it has no model, serial number, MAC or Zoom Rooms version for the room, so those stay
	// empty and come from the Room PC's scan in CourtConstruct instead.
	public partial class ZoomRoom : IDeviceInfoProvider
	{
		private readonly DeviceInfo _roomIdentity = new DeviceInfo();

		// Read a moment after Connected, from a timer thread, as the Room PC statistics are: SDK calls made from
		// inside an SDK status event fail.
		private const int RoomIdentityReadDelayMs = 3000;
		private readonly object _roomIdentityLock = new object();
		private CTimer _roomIdentityTimer;

		/// <summary>The Zoom Room's name (as its host name) and IP address; empty until the room has been read.</summary>
		public DeviceInfo DeviceInfo
		{
			get { return _roomIdentity; }
		}

		public event DeviceInfoChangeHandler DeviceInfoChanged;

		/// <summary>Reads the room's name and address again, when the room is connected.</summary>
		public void UpdateDeviceInfo()
		{
			ReadRoomIdentity();
		}

		private void ScheduleRoomIdentityRead()
		{
			lock (_roomIdentityLock)
			{
				_roomIdentityTimer?.Stop();
				_roomIdentityTimer = new CTimer(_ => ReadRoomIdentity(), RoomIdentityReadDelayMs);
			}
		}

		private void ReadRoomIdentity()
		{
			try
			{
				if ((ConnectionState)_controller.GetConnectionState() != ConnectionState.Connected) return;

				var info = _controller.GetLastZoomRoomInfo();
				if (info == null || !info.Any)
				{
					this.LogDebug("The Zoom Room sent no name or address");
					return;
				}

				// Its name in Zoom is what the room goes by; the display name only when it has none.
				var name = info.RoomName ?? info.DisplayName;
				var changed = !string.Equals(_roomIdentity.HostName, name, StringComparison.Ordinal)
					|| !string.Equals(_roomIdentity.IpAddress, info.RoomAddress, StringComparison.Ordinal);
				if (!changed) return;

				_roomIdentity.HostName = name;
				_roomIdentity.IpAddress = info.RoomAddress;
				this.LogInformation("Zoom Room identity: name {Name}, address {Address}", name, info.RoomAddress);

				var handler = DeviceInfoChanged;
				if (handler != null) handler(this, new DeviceInfoEventArgs(_roomIdentity));
			}
			catch (Exception ex)
			{
				this.LogWarning("Zoom Room name and address not read: {Message}", ex.Message);
			}
		}
	}
}
