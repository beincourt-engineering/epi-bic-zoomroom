using System;
using Crestron.SimplSharp;
using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace PepperDash.Essentials.Plugins
{
    /// <summary>
    /// StatusMonitorBase backed by SDK connection state. Online/offline is driven both by SDK
    /// connection events (via <see cref="SetOnline"/>) and by a periodic liveness poll: the SDK's
    /// self-reported connection state can go stale on a silent/half-open drop (devcomm stuck IsOk),
    /// so this timer invokes an active health check that probes the link and self-heals.
    /// </summary>
    public class SdkConnectionMonitor : StatusMonitorBase
    {
        private const long PollIntervalMs = 30000;

        private readonly Action _pollHealthCheck;
        private CTimer _pollTimer;
        private bool _disposed;

        public SdkConnectionMonitor(IKeyed parent, Action pollHealthCheck)
            : base(parent, 30000, 60000)
        {
            _pollHealthCheck = pollHealthCheck;
        }

        public override void Start()
        {
            if (_disposed) return;
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = new CTimer(_ =>
            {
                if (_disposed) return;
                try { _pollHealthCheck?.Invoke(); }
                catch { /* controller logs its own exceptions */ }
            }, null, PollIntervalMs, PollIntervalMs);
        }

        public override void Stop()
        {
            _disposed = true;
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        /// <summary>
        /// Called by ZoomRoom when SDK connection state changes or the health watchdog updates truth.
        /// </summary>
        public void SetOnline(bool online)
        {
            if (online) _pairingProblem = null;
            // The message goes first, so the state change Central records already carries the reason.
            Message  = ConnectionMessage.ForState(online, _pairingProblem);
            IsOnline = online;
            Status   = online ? MonitorStatus.IsOk : MonitorStatus.InError;
        }

        /// <summary>
        /// Called when Zoom refuses the room's stored pairing. Shown as the reason while the room is
        /// offline, and cleared when it is back online.
        /// </summary>
        public void SetPairingProblem(string problem)
        {
            _pairingProblem = problem;
            if (!IsOnline && !string.IsNullOrEmpty(problem)) Message = problem;
        }

        private string _pairingProblem;
    }
}
