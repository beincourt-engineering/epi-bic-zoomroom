using System;
using System.Threading;

namespace PepperDash.Essentials.Plugins
{
    /// <summary>
    /// When a Zoom Room asks Zoom to reconnect with its stored pairing. Pure timing, with no SDK or
    /// Crestron calls, so the schedule can be unit tested.
    /// </summary>
    /// <remarks>
    /// <para>Every request carries the room's offset (5 s per room ahead of it on the shared SDK), so
    /// rooms that drop together never ask Zoom at the same moment. Several rooms asking at once have
    /// been seen to get a false refusal for one room and no answer at all for another.</para>
    /// <para>Failures that are not a refusal (no answer, room or cloud unreachable, a dropped link)
    /// retry after 5, 10, 20 and 30 s, then every 60 s, for as long as it takes.</para>
    /// <para>A refusal of the stored pairing is checked once more after 60 s, since a first refusal can
    /// be false. After a second refusal the room asks only every 10 minutes: a pairing Zoom has really
    /// cancelled never comes back by retrying, and frequent refused requests disturb the other rooms
    /// on the same SDK.</para>
    /// </remarks>
    public sealed class ReconnectPolicy
    {
        /// <summary>How long the first request after a program start may go unanswered.</summary>
        public const int StartupAnswerTimeoutMs = 30000;

        /// <summary>The gap between rooms sharing one SDK.</summary>
        public const int RoomOffsetMs = 5000;

        /// <summary>The single recheck after a first refusal.</summary>
        public const int RefusalRecheckMs = 60000;

        /// <summary>The pace once Zoom has refused the stored pairing twice in a row.</summary>
        public const int RefusedRetryMs = 600000;

        private static readonly int[] FailureStepsMs = { 5000, 10000, 20000, 30000, 60000 };

        private static int _nextSlot = -1;

        private int _failures;
        private int _refusals;

        /// <param name="slot">The room's place among the rooms sharing the SDK, from 0.</param>
        public ReconnectPolicy(int slot)
        {
            Slot = Math.Max(0, slot);
        }

        /// <summary>Hands out slots 0, 1, 2... in the order the rooms are created in this program.</summary>
        public static int TakeSlot() => Interlocked.Increment(ref _nextSlot);

        /// <summary>The room's place among the rooms sharing the SDK.</summary>
        public int Slot { get; }

        /// <summary>Added to every request, including the first one after a program start.</summary>
        public int OffsetMs => Slot * RoomOffsetMs;

        /// <summary>Refusals of the stored pairing in a row, since the room last connected.</summary>
        public int ConsecutiveRefusals => _refusals;

        /// <summary>True once Zoom has refused the stored pairing twice in a row.</summary>
        public bool IsRefused => _refusals >= 2;

        /// <summary>Delay to the next request after a failure that is not a refusal.</summary>
        public int NextAfterFailure()
        {
            var step = FailureStepsMs[Math.Min(_failures, FailureStepsMs.Length - 1)];
            if (_failures < FailureStepsMs.Length) _failures++;
            return step + OffsetMs;
        }

        /// <summary>Records a refusal of the stored pairing and returns the delay to the next request.</summary>
        public int NextAfterRefusal()
        {
            if (_refusals < int.MaxValue) _refusals++;
            return (_refusals == 1 ? RefusalRecheckMs : RefusedRetryMs) + OffsetMs;
        }

        /// <summary>The room connected: the next problem starts the schedule from the beginning.</summary>
        public void Connected()
        {
            _failures = 0;
            _refusals = 0;
        }
    }
}
