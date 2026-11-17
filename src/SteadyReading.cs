using System;

namespace PepperDash.Essentials.Plugins
{
	/// <summary>
	/// Holds a jittery number steady so its feedback changes only on a real step. Telemetry sends the
	/// whole device record each time one of its feedbacks changes, so a Room PC CPU figure that moves
	/// with every statistics message would otherwise re-send the room every 30 s during a call.
	/// Same rule as the Magewell plugin's SteadyReading. No Crestron or SDK code, so the tests compile
	/// this file directly.
	/// </summary>
	public sealed class SteadyReading
	{
		private readonly object _lock = new object();
		private readonly int _step;
		private int? _shown;

		/// <param name="step">How far a reading must move from the one shown before it is shown.</param>
		public SteadyReading(int step)
		{
			if (step < 1)
				throw new ArgumentOutOfRangeException(nameof(step), "step must be at least 1");
			_step = step;
		}

		/// <summary>
		/// The value to show for <paramref name="reading"/>: the one shown before while the reading stays
		/// within a step of it, the reading itself otherwise. No reading shows nothing, and the next
		/// reading after that shows at once.
		/// </summary>
		public int? Next(int? reading)
		{
			lock (_lock)
			{
				if (!reading.HasValue)
					_shown = null;
				else if (!_shown.HasValue || Math.Abs((long)reading.Value - _shown.Value) >= _step)
					_shown = reading;
				return _shown;
			}
		}

		/// <summary>
		/// Like <see cref="Next"/>, but no reading (a message whose figure couldn't be read) keeps the one
		/// shown instead of clearing it. Use <see cref="Next"/> with null to clear, for example when the
		/// device goes offline.
		/// </summary>
		public int? Offer(int? reading)
		{
			lock (_lock)
			{
				return reading.HasValue ? Next(reading) : _shown;
			}
		}
	}
}
