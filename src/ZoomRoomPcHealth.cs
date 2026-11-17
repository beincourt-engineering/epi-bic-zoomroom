namespace PepperDash.Essentials.Plugins
{
	/// <summary>
	/// The Room PC's CPU and memory use, as whole percentages, from the ZRC SDK's statistics
	/// (StatisticalOverallInfo: cpuOverallUsage, memoryOverallUsage, memorySize). Null when a figure
	/// can't be a percentage. No Crestron or SDK code, so the tests compile this file directly.
	/// </summary>
	/// <remarks>
	/// The SDK documents no units. CPU use is taken as a percentage. Memory use is taken as a percentage
	/// when it is 0-100; above that it is read as an amount in the unit of memorySize (no Room PC uses
	/// under 100 MB), and kept only when that share is itself 0-100. Not yet checked against a live room.
	/// </remarks>
	public static class ZoomRoomPcHealth
	{
		public static int? CpuPercent(int cpuOverallUsage)
		{
			return cpuOverallUsage >= 0 && cpuOverallUsage <= 100 ? cpuOverallUsage : (int?)null;
		}

		/// <summary>
		/// CPU use from a whole message: 0% beside memory that can't be read is a message with nothing in
		/// it, not an idle PC. 0% beside a real memory figure is kept.
		/// </summary>
		public static int? CpuPercent(int cpuOverallUsage, int? memoryPercent)
		{
			return cpuOverallUsage == 0 && !memoryPercent.HasValue ? null : CpuPercent(cpuOverallUsage);
		}

		/// <summary>Memory use, percent. 0 is never a reading: a running PC always uses some memory.</summary>
		public static int? MemoryPercent(int memoryOverallUsage, int memorySize)
		{
			if (memoryOverallUsage <= 0)
				return null;
			if (memoryOverallUsage <= 100)
				return memoryOverallUsage;
			if (memorySize <= 0)
				return null;

			var share = (long)memoryOverallUsage * 100 / memorySize;
			return share <= 100 ? (int)share : (int?)null;
		}
	}
}
