using System;

namespace ATENtion.App
{
    /// <summary>Formats throughput, size, and duration values for the status bar and Session Info dialog.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Turns raw byte and time values into the short human-readable strings the UI shows,
    /// choosing units by magnitude.
    /// </para>
    /// </remarks>
    internal static class StatusFormat
    {
        /// <summary>Formats a throughput, for example "305.2 KB/s" or "1.4 MB/s".</summary>
        /// <param name="bytesPerSec">The rate in bytes per second.</param>
        /// <returns>The formatted rate.</returns>
        public static string Rate(long bytesPerSec) =>
            bytesPerSec >= 1024 * 1024 ? $"{bytesPerSec / (1024.0 * 1024.0):0.0} MB/s"
                                       : $"{bytesPerSec / 1024.0:0.0} KB/s";

        /// <summary>Formats a cumulative size, auto-scaled to KB, MB, or GB.</summary>
        /// <param name="bytes">The size in bytes.</param>
        /// <returns>The formatted size.</returns>
        public static string Size(long bytes) =>
            bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.00} GB"
            : bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB"
                                   : $"{bytes / 1024.0:0.0} KB";

        /// <summary>Formats a session uptime, for example "3h05m", "4m20s", or "12s".</summary>
        /// <param name="t">The elapsed time.</param>
        /// <returns>The formatted uptime.</returns>
        public static string Uptime(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m"
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s"
                                  : $"{t.Seconds}s";

        /// <summary>Formats an elapsed-since age for the health readout, for example "just now", "5s", or "1m20s".</summary>
        /// <param name="t">The time since the event.</param>
        /// <returns>The formatted age.</returns>
        public static string Age(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return "just now";
            return Uptime(t);
        }
    }
}
