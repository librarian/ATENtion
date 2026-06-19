using System;

namespace ATENtion.Core.Video
{
    /// <summary>
    /// Builds synthetic ATEN video packets, the inverse of <see cref="VideoPacketHeader.Parse"/>,
    /// for offline decoder validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Produces well-formed codec packets from supplied pixel and tile data so the decode
    /// paths can be exercised by unit tests and the demo frame without a live capture. It is the
    /// encode counterpart of the decode-only production paths.
    /// </para>
    /// <para>
    /// OPERATION - Each builder lays out the ten-byte header with the appropriate frame mode,
    /// encoding, and big-endian total length, then appends the palette and payload that the matching
    /// decode path expects. The incremental builder emits the plane stream as a single literal RLE
    /// chunk, which is why its tile plane bytes must avoid the 0x55 and 0xAA escape lead bytes.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Layout constants come from <see cref="VideoPacketHeader"/> and
    /// <see cref="AtenPalette"/>. The output is consumed by <see cref="AtenTileDecoder"/>.
    /// </para>
    /// <para>
    /// RESTRICTIONS - A validation aid, not part of a live session. The incremental layout it builds
    /// matches the RE-derived tile format and shares that format's unconfirmed status.
    /// </para>
    /// <para>
    /// PROVENANCE - Inverse of the native packet layout parsed by iKVM64.dll FUN_18000b630.
    /// </para>
    /// </remarks>
    public static class AtenPacketBuilder
    {
        /// <summary>
        /// Builds a self-contained full keyframe in the 8-bit palette mode (type 1, subtype 0): a
        /// ten-byte header, a 1024-byte palette, then one palette index per pixel.
        /// </summary>
        /// <param name="palette">The palette, 256 entries of four bytes {B, G, R, A} = 1024 bytes.</param>
        /// <param name="indices">The width * height palette indices, row-major.</param>
        /// <returns>A complete codec packet.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="palette"/> or <paramref name="indices"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="palette"/> is not 1024 bytes.</exception>
        public static byte[] BuildPalette8Keyframe(byte[] palette, byte[] indices)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (palette.Length != AtenPalette.ByteSize)
                throw new ArgumentException($"Palette must be {AtenPalette.ByteSize} bytes.", nameof(palette));

            int payloadOffset = VideoPacketHeader.PayloadOffsetWithPalette; // 0x40a
            uint totalLength = (uint)(payloadOffset + indices.Length);
            var packet = new byte[payloadOffset + indices.Length];

            packet[0] = 1; // frameMode 1: full keyframe
            packet[1] = (byte)AtenEncodingType.Type1;
            packet[2] = 0; // subtype 0
            // bytes 3..5 reserved
            packet[6] = (byte)(totalLength >> 24); // total length, big-endian
            packet[7] = (byte)(totalLength >> 16);
            packet[8] = (byte)(totalLength >> 8);
            packet[9] = (byte)totalLength;

            Buffer.BlockCopy(palette, 0, packet, VideoPacketHeader.PaletteOffset, AtenPalette.ByteSize);
            Buffer.BlockCopy(indices, 0, packet, payloadOffset, indices.Length);
            return packet;
        }

        /// <summary>One tile for <see cref="BuildIncremental"/>.</summary>
        public struct Tile
        {
            /// <summary>The tile's left, top, width, and height, in pixels.</summary>
            public int X, Y, W, H;
            /// <summary>The tile's plane data: 32 plane slots times ceil(W * H / 8) bytes. The bytes
            /// must avoid 0x55 and 0xAA so the RLE stream stays a single literal chunk.</summary>
            public byte[] Planes;
        }

        /// <summary>
        /// Builds an incremental type-4 packet in the layout
        /// <see cref="AtenTileDecoder"/>'s incremental path parses.
        /// </summary>
        /// <remarks>
        /// The plane stream is emitted as a single literal RLE chunk, so each tile's plane bytes
        /// must avoid the 0x55 and 0xAA escape lead bytes.
        /// </remarks>
        /// <param name="tiles">The tiles to encode.</param>
        /// <param name="encodingType">The encoding type byte to stamp (4 for the 32-bit path).</param>
        /// <returns>A complete codec packet.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="tiles"/> is null.</exception>
        public static byte[] BuildIncremental(Tile[] tiles, byte encodingType = 4)
        {
            if (tiles == null) throw new ArgumentNullException(nameof(tiles));

            // Concatenate the tile plane data into one all-literal RLE payload.
            int planeTotal = 0;
            foreach (var t in tiles) planeTotal += t.Planes.Length;
            var planeData = new byte[planeTotal];
            int pp = 0;
            foreach (var t in tiles) { Buffer.BlockCopy(t.Planes, 0, planeData, pp, t.Planes.Length); pp += t.Planes.Length; }

            // payload = [count : u16 LE][tiles x {y, x, w, h}][pad : u16][rleCount : u32 LE][planeData]
            int headerTableLen = tiles.Length * 4;
            int payloadLen = 2 + headerTableLen + 2 + 4 + planeData.Length;
            int total = VideoPacketHeader.HeaderSize + payloadLen;
            var packet = new byte[total];

            packet[0] = 0; // frameMode 0: incremental
            packet[1] = encodingType;
            packet[2] = 0;
            packet[6] = (byte)((uint)total >> 24); // total length, big-endian
            packet[7] = (byte)((uint)total >> 16);
            packet[8] = (byte)((uint)total >> 8);
            packet[9] = (byte)total;

            int o = VideoPacketHeader.HeaderSize;
            packet[o + 0] = (byte)tiles.Length;        // tile count, u16 little-endian
            packet[o + 1] = (byte)(tiles.Length >> 8);
            int ht = o + 2;
            for (int t = 0; t < tiles.Length; t++)
            {
                packet[ht + t * 4 + 0] = (byte)tiles[t].Y;
                packet[ht + t * 4 + 1] = (byte)tiles[t].X;
                packet[ht + t * 4 + 2] = (byte)tiles[t].W;
                packet[ht + t * 4 + 3] = (byte)tiles[t].H;
            }
            int rle = ht + headerTableLen + 2; // skip the two-byte pad
            packet[rle + 0] = (byte)planeData.Length; // RLE chunk byte count, u32 little-endian
            packet[rle + 1] = (byte)(planeData.Length >> 8);
            packet[rle + 2] = (byte)(planeData.Length >> 16);
            packet[rle + 3] = (byte)(planeData.Length >> 24);
            Buffer.BlockCopy(planeData, 0, packet, rle + 4, planeData.Length);
            return packet;
        }
    }
}
