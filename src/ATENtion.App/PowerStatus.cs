using System;
using ATENtion.Core.Net;

namespace ATENtion.App
{
    /// <summary>Turns what a session knows about its host into what the tab's power glyph shows.</summary>
    /// <remarks>
    /// The BMC's own power report wins. Without one (a BMC that does not answer the host-status
    /// query), a picture proves the host is on and anything else is unknown.
    /// </remarks>
    internal static class PowerStatus
    {
        /// <summary>A reading is only trusted while the link is up.</summary>
        /// <remarks>After a drop the last reading says nothing about the host now.</remarks>
        internal static VideoSignalState Displayed(bool hasSession, bool liveConnected, VideoSignalState signal) =>
            hasSession && liveConnected ? signal : VideoSignalState.Unknown;

        /// <inheritdoc cref="Displayed(bool, bool, VideoSignalState)"/>
        internal static HostPowerState Displayed(bool hasSession, bool liveConnected, HostPowerState power) =>
            hasSession && liveConnected ? power : HostPowerState.Unknown;

        /// <summary>True when the glyph should show the host as on.</summary>
        internal static bool IsOn(HostPowerState power, VideoSignalState signal) =>
            power == HostPowerState.On || (power == HostPowerState.Unknown && signal == VideoSignalState.Present);

        /// <summary>The glyph's tooltip.</summary>
        /// <param name="checkedAgo">
        /// How long since the state was confirmed, for a tab that is only checked periodically; null
        /// for one that is streaming.
        /// </param>
        internal static string Tip(HostPowerState power, VideoSignalState signal, TimeSpan? checkedAgo)
        {
            string text;
            switch (power)
            {
                case HostPowerState.On:
                    text = signal == VideoSignalState.Absent
                        ? "Host on - no video signal (display off, or not sending video)"
                        : "Host on";
                    break;
                case HostPowerState.Off:
                    text = "Host off";
                    break;
                default:
                    return Tip(signal, checkedAgo);
            }
            return checkedAgo == null ? text : text + Checked(checkedAgo.Value);
        }

        private static string Checked(TimeSpan ago) => " (checked " + Math.Max(0, (int)ago.TotalSeconds) + "s ago)";

        /// <summary>The tooltip from the video signal alone, for a BMC that does not report power.</summary>
        internal static string Tip(VideoSignalState state, TimeSpan? checkedAgo)
        {
            string text;
            switch (state)
            {
                case VideoSignalState.Present:
                    text = "Host on - video signal present";
                    break;
                case VideoSignalState.Absent:
                    text = "No video signal - host is off, or on but not producing video";
                    break;
                default:
                    return "No video signal information yet - not connected, or no frame seen";
            }
            return checkedAgo == null ? text : text + Checked(checkedAgo.Value);
        }
    }
}
