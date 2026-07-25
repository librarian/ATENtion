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
        private static readonly object CaptureGate = new object();
        private static bool _unsupportedFrameCaptured;
        private const int MaxUnsupportedFrameBytes = 16 * 1024 * 1024;

        /// <summary>Raised for each log line; the UI subscribes to display it.</summary>
        public static event Action<string> Message;

        /// <summary>When set, every line is also appended to this file.</summary>
        public static string FilePath;

        /// <summary>
        /// Optional exact path for the one-shot unsupported-video dump. When null, the path is
        /// derived from <see cref="FilePath"/>.
        /// </summary>
        public static string UnsupportedFrameFilePath;

        /// <summary>
        /// Writes the first unsupported video packet beside the diagnostic log for offline codec
        /// analysis. Capture is active only while logging is enabled and is bounded to 16 MiB.
        /// </summary>
        /// <param name="packet">The complete ATEN codec packet, including its ten-byte header.</param>
        /// <returns>The dump path when a packet was written; otherwise null.</returns>
        public static string TryCaptureUnsupportedFrame(byte[] packet)
            => TryCaptureFrame(packet, requireExplicitPath: false, description: "unsupported");

        /// <summary>
        /// Writes the first video packet to the explicitly configured capture path, including packets
        /// whose codec is supported. Used by the capture CLI's <c>--raw-output</c> diagnostic option.
        /// </summary>
        public static string TryCaptureRawFrame(byte[] packet)
            => TryCaptureFrame(packet, requireExplicitPath: true, description: "raw");

        private static string TryCaptureFrame(byte[] packet, bool requireExplicitPath, string description)
        {
            if (!Enabled || packet == null || packet.Length == 0 ||
                packet.Length > MaxUnsupportedFrameBytes || string.IsNullOrEmpty(FilePath) ||
                (requireExplicitPath && string.IsNullOrEmpty(UnsupportedFrameFilePath)))
                return null;

            lock (CaptureGate)
            {
                if (_unsupportedFrameCaptured) return null;
                _unsupportedFrameCaptured = true;

                try
                {
                    string path = UnsupportedFrameFilePath;
                    if (string.IsNullOrEmpty(path))
                    {
                        string directory = Path.GetDirectoryName(FilePath) ?? "";
                        string stem = Path.GetFileNameWithoutExtension(FilePath);
                        path = Path.Combine(directory, stem + "-unsupported-frame.bin");
                    }
                    File.WriteAllBytes(path, packet);
                    Write($"Captured first {description} video packet: {path} ({packet.Length} bytes). " +
                          "This file may contain sensitive console pixels.");
                    return path;
                }
                catch (Exception ex)
                {
                    Write($"Unable to capture {description} video packet: " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>Resets one-shot capture state for an isolated unit test.</summary>
        internal static void ResetUnsupportedFrameCaptureForTests()
        {
            lock (CaptureGate) _unsupportedFrameCaptured = false;
        }

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
