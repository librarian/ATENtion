using System;
using ATENtion.Core.Net;

namespace ATENtion.App
{
    /// <summary>Turns a session's video signal into what the tab's power glyph shows.</summary>
    internal static class PowerStatus
    {
        /// <summary>The state to show for a tab.</summary>
        /// <remarks>
        /// A reading is only trusted while the link is up. After a drop the last signal seen says
        /// nothing about the host now, so the glyph falls back to unknown until a new frame arrives.
        /// </remarks>
        internal static VideoSignalState Displayed(bool hasSession, bool liveConnected, VideoSignalState signal) =>
            hasSession && liveConnected ? signal : VideoSignalState.Unknown;

        /// <summary>The glyph's tooltip.</summary>
        /// <param name="state">The state being shown.</param>
        /// <param name="checkedAgo">
        /// How long since the state was confirmed, for a tab that is only checked periodically; null
        /// for one that is streaming.
        /// </param>
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
            if (checkedAgo == null) return text;
            int seconds = Math.Max(0, (int)checkedAgo.Value.TotalSeconds);
            return text + " (checked " + seconds + "s ago)";
        }
    }
}
