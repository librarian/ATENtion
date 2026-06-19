using System;
using System.Globalization;
using System.Text;
using ATENtion.Core.Net;

namespace ATENtion.Core.Protocol
{
    /// <summary>
    /// The RFB ProtocolVersion message: the fixed twelve-byte ASCII banner
    /// <c>"RFB 003.00x\n"</c> exchanged at the very start of the handshake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Parses the server's version banner and writes the client's, carrying the major
    /// and minor version numbers in between.
    /// </para>
    /// <para>
    /// OPERATION - The banner is a fixed-width ASCII string: the literal "RFB ", a three-digit
    /// zero-padded major number, a dot, a three-digit zero-padded minor number, and a newline.
    /// Reading validates the prefix and width and parses the two numbers. Writing reproduces the
    /// same fixed layout from <see cref="ToString"/>. The native side parses the banner with the
    /// format "RFB %03d.%03d".
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   "RFB 003.008\n"   = 12 ASCII bytes
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads and writes through a <see cref="BufferedRfbStream"/>.
    /// </para>
    /// <para>
    /// PROVENANCE - Native banner parser iKVM64.dll FUN_180012290.
    /// VERIFIED LIVE: the target reports RFB 003.008.
    /// </para>
    /// </remarks>
    public readonly struct ProtocolVersion
    {
        /// <summary>The fixed on-wire length of the banner, in bytes.</summary>
        public const int WireLength = 12;

        /// <summary>Creates a version from its major and minor numbers.</summary>
        /// <param name="major">The major version number.</param>
        /// <param name="minor">The minor version number.</param>
        public ProtocolVersion(int major, int minor)
        {
            Major = major;
            Minor = minor;
        }

        /// <summary>The major version number (the "003" field).</summary>
        public int Major { get; }
        /// <summary>The minor version number (the "008" field, for example).</summary>
        public int Minor { get; }

        /// <summary>Reads and parses the twelve-byte version banner from the stream.</summary>
        /// <param name="stream">The stream, positioned at the start of the banner.</param>
        /// <returns>The parsed version.</returns>
        /// <exception cref="FormatException">The banner is too short or does not begin with "RFB ".</exception>
        public static ProtocolVersion Read(BufferedRfbStream stream)
        {
            byte[] raw = stream.ReadExact(WireLength);
            Diagnostics.KvmLog.Write("ProtocolVersion raw: " + Diagnostics.KvmLog.Hex(raw));
            string s = Encoding.ASCII.GetString(raw);
            // Expected: "RFB 003.008\n".
            if (s.Length < 11 || !s.StartsWith("RFB ", StringComparison.Ordinal))
                throw new FormatException($"Unexpected RFB version banner: '{s.TrimEnd()}'");

            int major = int.Parse(s.Substring(4, 3), CultureInfo.InvariantCulture);
            int minor = int.Parse(s.Substring(8, 3), CultureInfo.InvariantCulture);
            return new ProtocolVersion(major, minor);
        }

        /// <summary>Writes this version as the twelve-byte banner and flushes the stream.</summary>
        /// <param name="stream">The stream to write the banner to.</param>
        public void Write(BufferedRfbStream stream)
        {
            stream.WriteBytes(Encoding.ASCII.GetBytes(ToString()));
            stream.Flush();
        }

        /// <summary>Formats this version as the fixed twelve-byte banner string, newline included.</summary>
        /// <returns>The banner, for example "RFB 003.008\n".</returns>
        public override string ToString() =>
            $"RFB {Major:000}.{Minor:000}\n";
    }
}
