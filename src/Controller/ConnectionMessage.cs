namespace PepperDash.Essentials.Plugins
{
    /// <summary>
    /// The words the Zoom Room's communication monitor carries as its status message. Court Central
    /// shows that message as the reason a device is down, so each one reads as a reason. Telemetry keeps
    /// the last non-empty message it was given, so every state has one: an old refusal never outlives
    /// the reconnect that fixed it.
    /// </summary>
    public static class ConnectionMessage
    {
        /// <summary>The room is connected and paired.</summary>
        public const string Connected = "connected to the Zoom Room";

        /// <summary>The room dropped for a reason that is not a refused pairing; the controller keeps asking.</summary>
        public const string Reconnecting = "not connected to the Zoom Room; reconnecting";

        /// <summary>
        /// The message for a refusal of the stored pairing. A first refusal can be false and is checked
        /// again; from the second in a row the room needs a new activation code.
        /// </summary>
        /// <param name="consecutiveRefusals">Refusals in a row, this one included.</param>
        /// <param name="errorCode">Zoom's PairRoomResult code.</param>
        /// <param name="controllerKey">The key the pairZoomRoom console command takes.</param>
        /// <param name="tryingConfiguredCode">True when an unused configured activation code is being tried.</param>
        public static string ForRefusal(int consecutiveRefusals, int errorCode, string controllerKey, bool tryingConfiguredCode)
        {
            if (consecutiveRefusals <= 1)
            {
                return tryingConfiguredCode
                    ? string.Format("Zoom refused the stored pairing (code {0}); trying the configured activation code", errorCode)
                    : string.Format("Zoom refused the stored pairing (code {0}); checking again in {1} min",
                        errorCode, ReconnectPolicy.RefusalRecheckMs / 60000);
            }

            return string.Format(
                "Zoom refused the pairing: this Zoom Room needs a new activation code. Generate one in the Zoom portal, then run pairZoomRoom {0} <code> on the processor (Zoom code {1}; asking again every {2} min)",
                controllerKey, errorCode, ReconnectPolicy.RefusedRetryMs / 60000);
        }

        /// <summary>
        /// The message for a change of connection state: connected, the pairing problem when there is
        /// one, or reconnecting.
        /// </summary>
        public static string ForState(bool online, string pairingProblem)
        {
            if (online) return Connected;
            return string.IsNullOrEmpty(pairingProblem) ? Reconnecting : pairingProblem;
        }
    }
}
