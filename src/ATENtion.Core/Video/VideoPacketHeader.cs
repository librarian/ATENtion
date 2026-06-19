using System;

namespace ATENtion.Core.Video
{
    /// <summary>
    /// The ten-byte header that precedes every ATEN video payload, carrying the frame mode,
    /// encoding, and total length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Parses the leading header of a codec packet and exposes the fields the decoder
    /// dispatches on, along with the computed offsets of the optional palette block and the payload.
    /// </para>
    /// <para>
    /// OPERATION - The first byte distinguishes a full keyframe from an incremental tile update.
    /// The encoding and subtype bytes together select the decode path and determine whether a
    /// 1024-byte palette block follows the header. The total length at offset 6 is big-endian and
    /// counts the header, the optional palette, and the payload, from which the payload offset and
    /// length are derived.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   off 0  : u8      frameMode   (1 = full keyframe, otherwise incremental tile update)
    ///   off 1  : u8      encoding    (type class: 1, 2, 4, 5)
    ///   off 2  : u8      subtype
    ///   off 3  : u8 x 3  reserved
    ///   off 6  : u32 BE  totalLength (header + optional palette + payload)
    ///   off 10 : payload, or a 1024-byte palette then payload when a palette is present
    /// </code>
    /// </para>
    /// <para>
    /// PROVENANCE - Header parse at the top of the native decoder iKVM64.dll FUN_18000b630.
    /// </para>
    /// </remarks>
    public readonly struct VideoPacketHeader
    {
        /// <summary>The fixed header length, in bytes.</summary>
        public const int HeaderSize = 10;
        /// <summary>The size of the optional palette block, in bytes.</summary>
        public const int PaletteSize = 1024;
        /// <summary>The offset of the palette block: it directly follows the header.</summary>
        public const int PaletteOffset = HeaderSize;
        /// <summary>The payload offset when a palette block is present (header + palette = 0x40a).</summary>
        public const int PayloadOffsetWithPalette = HeaderSize + PaletteSize;

        /// <summary>The frame mode byte: 1 is a full keyframe, anything else an incremental update.</summary>
        public byte FrameMode { get; }
        /// <summary>The encoding type class (1, 2, 4, or 5).</summary>
        public byte EncodingType { get; }
        /// <summary>The subtype byte, which further qualifies the encoding.</summary>
        public byte Subtype { get; }
        /// <summary>The total packet length declared in the header, in bytes.</summary>
        public uint TotalLength { get; }

        private VideoPacketHeader(byte frameMode, byte encoding, byte subtype, uint totalLength)
        {
            FrameMode = frameMode;
            EncodingType = encoding;
            Subtype = subtype;
            TotalLength = totalLength;
        }

        /// <summary>True when this packet is a full keyframe rather than an incremental update.</summary>
        public bool IsFullFrame => FrameMode == 1;

        /// <summary>True when a 1024-byte palette block follows the header (types (1, 0) and 5).</summary>
        public bool HasPalette => (EncodingType == 1 && Subtype == 0) || EncodingType == 5;

        /// <summary>The byte offset of the payload within the packet, past any palette block.</summary>
        public int PayloadOffset => HasPalette ? PayloadOffsetWithPalette : HeaderSize;

        /// <summary>The payload length: the total length less everything before the payload.</summary>
        public int PayloadLength => (int)TotalLength - PayloadOffset;

        /// <summary>Parses a video header from a packet at the given offset.</summary>
        /// <param name="packet">The packet bytes.</param>
        /// <param name="offset">The offset of the header within <paramref name="packet"/>.</param>
        /// <returns>The parsed header.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="packet"/> is null.</exception>
        /// <exception cref="ArgumentException">The packet is too small to hold a header.</exception>
        public static VideoPacketHeader Parse(byte[] packet, int offset = 0)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (offset < 0 || offset + HeaderSize > packet.Length)
                throw new ArgumentException("Packet too small for a video header.", nameof(packet));

            // Bytes 6..9, big-endian (the concatenation chain in the decompiled header parse).
            uint totalLength =
                (uint)(packet[offset + 6] << 24
                     | packet[offset + 7] << 16
                     | packet[offset + 8] << 8
                     | packet[offset + 9]);

            return new VideoPacketHeader(
                packet[offset + 0],
                packet[offset + 1],
                packet[offset + 2],
                totalLength);
        }
    }
}
