using System;
using System.IO;
using System.Text;

namespace ATENtion.Core.Diagnostics
{
    /// <summary>
    /// The diagnostic log shared across the protocol stack: a single sink that raises lines to the UI
    /// and optionally appends them to a file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Records timestamped diagnostic lines for tracing a live BMC connection. Each line
    /// is raised to <see cref="Message"/> for the on-screen panel and, when <see cref="FilePath"/> is
    /// set, appended to that file.
    /// </para>
    /// <para>
    /// OPERATION - Logging is gated by <see cref="Enabled"/>, which is off by default because the
    /// per-frame stream is verbose and would otherwise cost file and UI work in normal use. When off,
    /// <see cref="Write"/> returns immediately. File appends serialise on an internal lock, and every
    /// path swallows its own exceptions so that logging can never itself fault the caller.
    /// </para>
    /// <para>
    /// DEPENDENCIES - The UI subscribes to <see cref="Message"/> to display lines and sets
    /// <see cref="FilePath"/> and <see cref="Enabled"/> from its Logging menu.
    /// </para>
    /// <para>
    /// RESTRICTIONS - A static, process-wide sink with a single subscriber model. File writes are
    /// serialised. The message event is invoked on the calling thread.
    /// </para>
    /// </remarks>
    public static class KvmLog
    {
        private static readonly object Gate = new object();

        /// <summary>Raised for each log line; the UI subscribes to display it.</summary>
        public static event Action<string> Message;

        /// <summary>When set, every line is also appended to this file.</summary>
        public static string FilePath;

        /// <summary>
        /// The master switch. Off by default: logging is opt-in through the UI's Logging menu so the
        /// verbose per-frame stream does not cost file or UI work in normal use.
        /// </summary>
        public static bool Enabled;

        /// <summary>Writes one timestamped line, if logging is enabled.</summary>
        /// <param name="message">The text to log.</param>
        public static void Write(string message)
        {
            if (!Enabled) return;
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
            try { Message?.Invoke(line); } catch { /* logging must never throw */ }

            if (!string.IsNullOrEmpty(FilePath))
            {
                lock (Gate)
                {
                    try { File.AppendAllText(FilePath, line + Environment.NewLine); }
                    catch { /* best effort: a failed append must not fault the caller */ }
                }
            }
        }

        /// <summary>Writes an exception with a context label, plus its detail and any inner exception.</summary>
        /// <param name="context">A short label for where the error occurred.</param>
        /// <param name="ex">The exception to record.</param>
        public static void Error(string context, Exception ex)
        {
            Write("ERROR " + context + ": " + ex.GetType().Name + ": " + ex.Message);
            Write(ex.ToString());
            if (ex.InnerException != null)
                Write("  inner: " + ex.InnerException);
        }

        /// <summary>Formats a span of bytes as hexadecimal followed by printable ASCII, for wire diagnostics.</summary>
        /// <param name="data">The bytes to format.</param>
        /// <param name="offset">The starting offset.</param>
        /// <param name="count">The number of bytes to format.</param>
        /// <returns>A "hex | ascii" rendering of the span.</returns>
        public static string Hex(byte[] data, int offset, int count)
        {
            var sb = new StringBuilder();
            int end = Math.Min(offset + count, data.Length);
            for (int i = offset; i < end; i++) sb.Append(data[i].ToString("x2")).Append(' ');
            sb.Append(" | ");
            for (int i = offset; i < end; i++)
            {
                byte b = data[i];
                sb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
            }
            return sb.ToString();
        }

        /// <summary>Formats an entire byte array as hexadecimal followed by printable ASCII.</summary>
        /// <param name="data">The bytes to format.</param>
        /// <returns>A "hex | ascii" rendering of the array.</returns>
        public static string Hex(byte[] data) => Hex(data, 0, data.Length);
    }
}
