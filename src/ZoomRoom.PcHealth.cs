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
		private bool _pcStatisticsUnreadLogged;
		private int _pcStatisticsMessages;
		private DateTime? _pcStatisticsLastAt;
		private static readonly TimeSpan PcStatisticsQuietSpell = TimeSpan.FromMinutes(5);

		private int? _pcCpuPercent;
		private int? _pcMemoryPercent;

		// In a call the Room PC's CPU moves with every statistics message (22-66% on JSC), and each change
		// re-sends the whole room record. Hold each figure until it moves by a real step.
		private const int PcCpuStep = 10;
		private const int PcMemoryStep = 5;
		private readonly SteadyReading _steadyPcCpu = new SteadyReading(PcCpuStep);
		private readonly SteadyReading _steadyPcMemory = new SteadyReading(PcMemoryStep);

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

			// Every message's raw figures, at debug level, to see what the SDK sends between Central's reads.
			var now = DateTime.UtcNow;
			_pcStatisticsMessages++;
			this.LogDebug(
				"Room PC statistics message {Count}: cpu overall {CpuOverall}, memory overall {MemOverall}, memory size {MemSize}",
				_pcStatisticsMessages, e.CpuOverallUsage, e.MemoryOverallUsage, e.MemorySize);

			// A long silence then a message: statistics had stopped, and the figure shown was stale.
			if (_pcStatisticsLastAt.HasValue && now - _pcStatisticsLastAt.Value >= PcStatisticsQuietSpell)
				this.LogInformation("Room PC statistics resumed after {Minutes:0} min with none", (now - _pcStatisticsLastAt.Value).TotalMinutes);
			_pcStatisticsLastAt = now;

			var memory = ZoomRoomPcHealth.MemoryPercent(e.MemoryOverallUsage, e.MemorySize);
			var cpu = ZoomRoomPcHealth.CpuPercent(e.CpuOverallUsage, memory);
			if ((!cpu.HasValue || !memory.HasValue) && !_pcStatisticsUnreadLogged)
			{
				_pcStatisticsUnreadLogged = true;
				this.LogInformation(
					"Room PC statistics message with a figure that can't be read (first this run), last figures kept: cpu overall {CpuOverall}, memory overall {MemOverall}, memory size {MemSize}",
					e.CpuOverallUsage, e.MemoryOverallUsage, e.MemorySize);
			}

			// A figure that can't be read keeps the one shown: only going offline clears them.
			_pcCpuPercent = _steadyPcCpu.Offer(cpu);
			_pcMemoryPercent = _steadyPcMemory.Offer(memory);
			CpuUsagePercentFeedback.FireUpdate();
			MemoryUsagePercentFeedback.FireUpdate();
		}

		// Offline: the last figures no longer describe the room.
		private void ClearPcStatistics()
		{
			lock (_pcStatisticsLock) _pcStatisticsTimer?.Stop();
			_pcCpuPercent = _steadyPcCpu.Next(null);
			_pcMemoryPercent = _steadyPcMemory.Next(null);
			CpuUsagePercentFeedback.FireUpdate();
			MemoryUsagePercentFeedback.FireUpdate();
		}
	}
}
