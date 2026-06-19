using ATENtion.Core.Net;

namespace ATENtion.Core.Protocol
{
    /// <summary>
    /// The sixteen-byte RFB PIXEL_FORMAT structure carried inside ServerInit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Parses the server's declared pixel format and exposes its fields. For the ATEN
    /// palette and RGB code paths the field that matters is <see cref="BitsPerPixel"/>. The
    /// decoder produces 32-bit BGRA regardless of the rest.
    /// </para>
    /// <para>
    /// OPERATION - The block is read field by field in wire order: bits-per-pixel, depth, the
    /// big-endian and true-colour flags, the three channel maxima, the three channel shifts, and
    /// three bytes of padding. The maxima and shifts describe how colour components are packed for
    /// the standard RFB encodings. The ATEN codec does not rely on them.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   [bpp : u8][depth : u8][bigEndian : u8][trueColor : u8]
    ///   [redMax : u16 BE][greenMax : u16 BE][blueMax : u16 BE]
    ///   [redShift : u8][greenShift : u8][blueShift : u8][pad : 3 bytes]   = 16 bytes
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Read by <see cref="ServerInit"/> through a <see cref="BufferedRfbStream"/>.
    /// </para>
    /// <para>
    /// PROVENANCE - Native ServerInit reader iKVM64.dll FUN_180012550.
    /// </para>
    /// </remarks>
    public readonly struct PixelFormat
    {
        /// <summary>The fixed on-wire length of the structure, in bytes.</summary>
        public const int WireLength = 16;

        /// <summary>Bits per pixel as declared by the server.</summary>
        public byte BitsPerPixel { get; }
        /// <summary>Colour depth (significant bits per pixel).</summary>
        public byte Depth { get; }
        /// <summary>True if multi-byte pixels are big-endian on the wire.</summary>
        public bool BigEndian { get; }
        /// <summary>True if pixels are true-colour rather than palette-indexed.</summary>
        public bool TrueColor { get; }
        /// <summary>Maximum value of the red channel (true-colour).</summary>
        public ushort RedMax { get; }
        /// <summary>Maximum value of the green channel (true-colour).</summary>
        public ushort GreenMax { get; }
        /// <summary>Maximum value of the blue channel (true-colour).</summary>
        public ushort BlueMax { get; }
        /// <summary>Bit position of the red channel within a pixel (true-colour).</summary>
        public byte RedShift { get; }
        /// <summary>Bit position of the green channel within a pixel (true-colour).</summary>
        public byte GreenShift { get; }
        /// <summary>Bit position of the blue channel within a pixel (true-colour).</summary>
        public byte BlueShift { get; }

        private PixelFormat(byte bpp, byte depth, bool bigEndian, bool trueColor,
                            ushort rMax, ushort gMax, ushort bMax, byte rSh, byte gSh, byte bSh)
        {
            BitsPerPixel = bpp; Depth = depth; BigEndian = bigEndian; TrueColor = trueColor;
            RedMax = rMax; GreenMax = gMax; BlueMax = bMax;
            RedShift = rSh; GreenShift = gSh; BlueShift = bSh;
        }

        /// <summary>Reads a sixteen-byte pixel-format block from the stream.</summary>
        /// <param name="stream">The stream, positioned at the bits-per-pixel field.</param>
        /// <returns>The parsed pixel format.</returns>
        public static PixelFormat Read(BufferedRfbStream stream)
        {
            byte bpp = stream.ReadU8();
            byte depth = stream.ReadU8();
            bool bigEndian = stream.ReadU8() != 0;
            bool trueColor = stream.ReadU8() != 0;
            ushort rMax = stream.ReadU16BE();
            ushort gMax = stream.ReadU16BE();
            ushort bMax = stream.ReadU16BE();
            byte rSh = stream.ReadU8();
            byte gSh = stream.ReadU8();
            byte bSh = stream.ReadU8();
            stream.ReadExact(3); // three bytes of padding
            return new PixelFormat(bpp, depth, bigEndian, trueColor, rMax, gMax, bMax, rSh, gSh, bSh);
        }
    }
}
