using System;
using Crestron.SimplSharp;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.ZoomRoom.Sdk;

namespace PepperDash.Essentials.Plugins
{
	// The Room PC's own CPU and memory use, under the keys Court Central charts for any device
	// (cpuUsagePercent, memoryUsagePercent). The ZRC SDK sends them once its statistics are switched on.
	public partial class ZoomRoom
	{
		// Statistics are switched on a moment after Connected, from a timer thread: SDK calls made from
		// inside an SDK status event fail.
		private const int PcStatisticsEnableDelayMs = 3000;
		private readonly object _pcStatisticsLock = new object();
		private CTimer _pcStatisticsTimer;
		private bool _pcStatisticsLogged;

		private int? _pcCpuPercent;
		private int? _pcMemoryPercent;

		/// <summary>The Room PC's CPU use, percent. 0 until the room reports it.</summary>
		public IntFeedback CpuUsagePercentFeedback { get; private set; }

		/// <summary>The Room PC's memory use, percent. 0 until the room reports it.</summary>
		public IntFeedback MemoryUsagePercentFeedback { get; private set; }

		private void SchedulePcStatisticsEnable()
		{
			lock (_pcStatisticsLock)
			{
				_pcStatisticsTimer?.Stop();
				_pcStatisticsTimer = new CTimer(_ => EnablePcStatistics(), PcStatisticsEnableDelayMs);
			}
		}

		private void EnablePcStatistics()
		{
			try
			{
				if ((ConnectionState)_controller.GetConnectionState() != ConnectionState.Connected) return;

				var ok = _controller.EnableStatisticalInfo(true);
				if (ok)
					this.LogDebug("Room PC statistics switched on");
				else
					this.LogWarning("Room PC statistics could not be switched on; CPU and memory stay unreported");
			}
			catch (Exception ex)
			{
				this.LogWarning("Room PC statistics not switched on: {Message}", ex.Message);
			}
		}

		private void OnControllerStatisticalInfoChanged(object sender, StatisticalInfoEventArgs e)
		{
			if (e == null) return;

			// Once per run, the raw figures: the SDK documents no units.
			if (!_pcStatisticsLogged)
			{
				_pcStatisticsLogged = true;
				this.LogInformation(
					"Room PC statistics: cpu overall {CpuOverall}, cpu zoom rooms {CpuZr}, memory overall {MemOverall}, memory zoom rooms {MemZr}, memory size {MemSize}, cores {Cores}",
					e.CpuOverallUsage, e.CpuZoomRoomsUsage, e.MemoryOverallUsage, e.MemoryZoomRoomsUsage, e.MemorySize, e.CpuCoreCount);
			}

			_pcCpuPercent = ZoomRoomPcHealth.CpuPercent(e.CpuOverallUsage);
			_pcMemoryPercent = ZoomRoomPcHealth.MemoryPercent(e.MemoryOverallUsage, e.MemorySize);
			CpuUsagePercentFeedback.FireUpdate();
			MemoryUsagePercentFeedback.FireUpdate();
		}

		// Offline: the last figures no longer describe the room.
		private void ClearPcStatistics()
		{
			lock (_pcStatisticsLock) _pcStatisticsTimer?.Stop();
			_pcCpuPercent = null;
			_pcMemoryPercent = null;
			CpuUsagePercentFeedback.FireUpdate();
			MemoryUsagePercentFeedback.FireUpdate();
		}
	}
}
