using System;
using System.Collections.Generic;

namespace ATENtion.Core.Video
{
    /// <summary>
    /// Decodes ATEN iKVM video packets into a 32-bit BGRA <see cref="FrameBuffer"/>, across all of
    /// the codec's keyframe and incremental encodings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Takes one codec packet at a time and writes its pixels into the framebuffer,
    /// returning the regions it changed so the UI can repaint only those. It dispatches on the
    /// packet header to the correct decode path and reuses scratch buffers to keep the hot path
    /// free of per-frame allocation.
    /// </para>
    /// <para>
    /// OPERATION - The packet header selects the path. Encoding type 0 is the Hermon codec the
    /// target BMC uses: it carries both a full keyframe (a raw RGB555 image) and incremental
    /// 16x16 tile updates, and it is checked first so that a type-0 incremental does not fall
    /// through to the bit-plane block and hit its unsupported-encoding throw. The remaining paths
    /// cover the palette keyframes (4-bit and 8-bit) and the bit-plane RGB keyframes and tiles
    /// (type 2 = RGB555, type 4 = RGB32), each of which runs RLE expansion, then the bit-plane
    /// transpose, then pixel conversion.
    /// </para>
    /// <para>
    /// Implementation status: the type-0 keyframe and incremental
    /// paths are VERIFIED LIVE against the target. The palette keyframes are PORTED FAITHFULLY and
    /// unit-tested. The bit-plane RGB paths are PORTED FAITHFULLY end to end and their transpose
    /// and RLE are unit-tested, but their wire-layout constants are RE-derived and not yet
    /// hardware-confirmed. The only encodings that still throw are type 5 and type 1 with a
    /// non-zero subtype.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Uses <see cref="AtenRle"/> for decompression,
    /// <see cref="BitPlaneDeinterleave"/> for the planar transpose, <see cref="PixelConverter"/>
    /// and <see cref="AtenPalette"/> for pixel conversion, and <see cref="TileBlitter"/> to place
    /// tiles. An <see cref="UnsupportedEncodingException"/> it throws is caught and skipped by
    /// <see cref="ATENtion.Core.Protocol.ServerMessageReader"/>, so an unexpected encoding stalls a
    /// single frame rather than the pump.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Not thread-safe. The receive pump is the sole caller, and the scratch buffers
    /// and the returned dirty-rectangle list are reused across calls. The list must be consumed
    /// before the next <see cref="DecodePacket"/>. The FrameDecoded handler honours this by
    /// consuming the regions synchronously.
    /// </para>
    /// <para>
    /// PROVENANCE - Port of the native decoder iKVM64.dll FUN_18000b630 and its helpers
    /// FUN_18000d180 (pixel convert) and FUN_18000d340 (tile blit), with the Hermon paths from
    /// FUN_18000a9e0 / FUN_18000ad30.
    /// </para>
    /// </remarks>
    public sealed class AtenTileDecoder
    {
        // Sentinel returned by full-frame paths: a single rectangle with the full-screen marker
        // coordinates, which the renderer reads as "upload the whole surface".
        private static readonly DirtyRect[] FullScreen = { new DirtyRect(0xffff, 0xffff, 16, 16) };

        private readonly AtenPalette _palette = new AtenPalette();

        // Scratch buffers reused across frames to keep the decode hot path allocation-free. The pump
        // thread is the sole caller, and the returned dirty list is consumed synchronously by the
        // FrameDecoded handler before the next DecodePacket, so single-buffer reuse is safe.
        private readonly List<DirtyRect> _dirty = new List<DirtyRect>();   // returned for incremental paths
        private readonly byte[] _hermonTile = new byte[TileSize * TileSize * 4]; // fixed 16x16 BGRA tile
        private byte[] _planes;     // RLE-expanded bit planes (full frame and incremental), grown on demand
        private byte[] _tilePlanes; // one tile's planes (bit-plane incremental)
        private byte[] _tileBgra;   // one tile's BGRA output (bit-plane incremental)
        private byte[] _rgb16;      // one tile's RGB555 words (type-2 incremental)

        // Grows the referenced buffer to at least the requested size, reusing it when already large enough.
        private static byte[] Ensure(ref byte[] buf, int size)
        {
            if (buf == null || buf.Length < size) buf = new byte[size];
            return buf;
        }

        /// <summary>Creates a decoder sized to an initial framebuffer of the given dimensions.</summary>
        /// <param name="width">The initial framebuffer width, in pixels.</param>
        /// <param name="height">The initial framebuffer height, in pixels.</param>
        public AtenTileDecoder(int width, int height)
        {
            Frame = new FrameBuffer(width, height);
        }

        /// <summary>The framebuffer the decoder writes into.</summary>
        public FrameBuffer Frame { get; }
        /// <summary>The current framebuffer width, in pixels.</summary>
        public int Width => Frame.Width;
        /// <summary>The current framebuffer height, in pixels.</summary>
        public int Height => Frame.Height;

        /// <summary>Resizes the framebuffer, for a mid-session resolution change.</summary>
        /// <param name="width">The new width, in pixels.</param>
        /// <param name="height">The new height, in pixels.</param>
        public void Resize(int width, int height) => Frame.Resize(width, height);

        /// <summary>
        /// Decodes one codec packet into <see cref="Frame"/> and returns the regions it changed.
        /// </summary>
        /// <param name="packet">The codec packet, beginning with the ten-byte header.</param>
        /// <returns>The changed regions: the full-screen sentinel for a keyframe, or one
        /// rectangle per tile for an incremental update.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="packet"/> is null.</exception>
        /// <exception cref="ArgumentException">A declared payload length exceeds the packet.</exception>
        /// <exception cref="UnsupportedEncodingException">The encoding has no decode path yet.</exception>
        public IReadOnlyList<DirtyRect> DecodePacket(byte[] packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));

            var header = VideoPacketHeader.Parse(packet);

            if (header.HasPalette)
                _palette.Load(packet, VideoPacketHeader.PaletteOffset);

            // Encoding type 0 is the Hermon codec the target BMC uses (verified live).
            // It carries both full keyframes (frameMode == 1) and incremental tile updates
            // (frameMode == 0), so it is handled before the bit-plane incremental block below;
            // otherwise a type-0 incremental would fall into that block and hit its throw.
            if (header.EncodingType == 0)
            {
                if (header.IsFullFrame) // frameMode == 1: a raw RGB555 keyframe
                {
                    int off0 = header.PayloadOffset;
                    int avail = packet.Length - off0;
                    int pixels = System.Math.Min(Width * Height, avail / 2);
                    PixelConverter.RawRgb555(packet, off0, pixels, Frame.Pixels, 0);
                    return FullScreen;
                }
                return DecodeHermonIncremental(packet); // changed 16x16 tiles only
            }

            if (!header.IsFullFrame)
            {
                if (header.EncodingType == (byte)AtenEncodingType.Type4)
                    return DecodeIncremental(packet, header, type4: true);
                if (header.EncodingType == (byte)AtenEncodingType.Type2)
                    return DecodeIncremental(packet, header, type4: false);
                throw new UnsupportedEncodingException(
                    "Incremental tile updates are ported for bit-plane types 2 and 4.", header);
            }

            int payloadOffset = header.PayloadOffset;
            int payloadLength = header.PayloadLength;
            if (payloadLength < 0 || payloadOffset + payloadLength > packet.Length)
                throw new ArgumentException("Declared payload length exceeds packet size.", nameof(packet));

            // Full-frame palette paths (no RLE, no bit-plane): a direct palette expansion across the
            // whole framebuffer (FUN_18000b630, subtype == 2 and type 1 / subtype 0).
            if (header.Subtype == 2)
            {
                PixelConverter.Palette4(packet, payloadOffset, payloadLength, _palette, Frame.Pixels, 0);
                return FullScreen;
            }

            if (header.Subtype == 0 && header.EncodingType == (byte)AtenEncodingType.Type1)
            {
                PixelConverter.Palette8(packet, payloadOffset, payloadLength, _palette, Frame.Pixels, 0);
                return FullScreen;
            }

            // Full-frame 24-bit bit-plane (type 4): RLE-decompress to planes, then transpose.
            if (header.Subtype == 0 && header.EncodingType == (byte)AtenEncodingType.Type4)
            {
                int numPixels = Width * Height;
                int planeBytes = BitPlaneDeinterleave.PlaneSlots24 * ((numPixels + 7) / 8);
                byte[] planes = Ensure(ref _planes, planeBytes);
                AtenRle.Decode(packet, payloadOffset, payloadLength, planes);
                BitPlaneDeinterleave.Decode24(planes, numPixels, Frame.Pixels, 0);
                return FullScreen;
            }

            // Full-frame 16-bit bit-plane (type 2): RLE to 16 planes, to RGB555 words, to BGRA.
            if (header.Subtype == 0 && header.EncodingType == (byte)AtenEncodingType.Type2)
            {
                int numPixels = Width * Height;
                int planeBytes = BitPlaneDeinterleave.PlaneSlots16 * ((numPixels + 7) / 8);
                byte[] planes = Ensure(ref _planes, planeBytes);
                AtenRle.Decode(packet, payloadOffset, payloadLength, planes);
                byte[] rgb16 = Ensure(ref _rgb16, numPixels * 2);
                BitPlaneDeinterleave.Decode16(planes, numPixels, rgb16, 0);
                PixelConverter.Rgb555(rgb16, 0, numPixels * 2, Frame.Pixels, 0);
                return FullScreen;
            }

            throw new UnsupportedEncodingException("Unhandled full-frame encoding.", header);
        }

        /// <summary>
        /// Decodes an incremental bit-plane tile update (type 2 or type 4) and blits its tiles.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Payload layout, derived from the tile branch of the native decoder:
        /// <code>
        ///   [tileCount : u16 LE]
        ///   [tileCount x {y : u8, x : u8, w : u8, h : u8}]   tile headers
        ///   [pad : u16]
        ///   [RLE plane stream]                              all tiles concatenated
        /// </code>
        /// Each tile is bit-plane transposed and blitted at its (x, y). One dirty rectangle is
        /// emitted per tile.
        /// </para>
        /// <para>
        /// The wire layout here (the count endianness, the header field order, the coordinate units)
        /// is RE-derived and not yet confirmed against a real BMC frame. The transpose and blit it
        /// builds on are unit-tested.
        /// </para>
        /// </remarks>
        private IReadOnlyList<DirtyRect> DecodeIncremental(byte[] packet, VideoPacketHeader header, bool type4)
        {
            int planeSlots = type4 ? BitPlaneDeinterleave.PlaneSlots24 : BitPlaneDeinterleave.PlaneSlots16;
            int payloadOffset = header.PayloadOffset;
            int tileCount = packet[payloadOffset] | (packet[payloadOffset + 1] << 8); // u16 LE
            int headerTable = payloadOffset + 2;
            int rleStart = payloadOffset + tileCount * 4 + 4;
            int rleLen = (payloadOffset + header.PayloadLength) - rleStart;
            if (rleLen < 0) rleLen = 0;

            int totalPlaneBytes = 0;
            for (int t = 0; t < tileCount; t++)
            {
                int w = packet[headerTable + t * 4 + 2];
                int h = packet[headerTable + t * 4 + 3];
                totalPlaneBytes += PlaneBytes(w * h, planeSlots);
            }

            byte[] planes = Ensure(ref _planes, totalPlaneBytes);
            AtenRle.Decode(packet, rleStart, rleLen, planes);

            _dirty.Clear();
            int planePos = 0;
            for (int t = 0; t < tileCount; t++)
            {
                int y = packet[headerTable + t * 4 + 0];
                int x = packet[headerTable + t * 4 + 1];
                int w = packet[headerTable + t * 4 + 2];
                int h = packet[headerTable + t * 4 + 3];
                int pixels = w * h;
                if (pixels == 0) continue;

                int tilePlaneBytes = PlaneBytes(pixels, planeSlots);
                byte[] tilePlanes = Ensure(ref _tilePlanes, tilePlaneBytes);
                Buffer.BlockCopy(planes, planePos, tilePlanes, 0, Math.Min(tilePlaneBytes, planes.Length - planePos));
                planePos += tilePlaneBytes;

                byte[] tileBgra = Ensure(ref _tileBgra, pixels * 4);
                if (type4)
                {
                    BitPlaneDeinterleave.Decode24(tilePlanes, pixels, tileBgra, 0);
                }
                else
                {
                    byte[] rgb16 = Ensure(ref _rgb16, pixels * 2);
                    BitPlaneDeinterleave.Decode16(tilePlanes, pixels, rgb16, 0);
                    PixelConverter.Rgb555(rgb16, 0, pixels * 2, tileBgra, 0);
                }
                TileBlitter.Blit(Frame, tileBgra, w, h, x, y);
                _dirty.Add(new DirtyRect(x, y, w, h));
            }
            return _dirty;
        }

        // Bytes occupied by one tile's planes: one byte per eight pixels, times the plane count.
        private static int PlaneBytes(int pixels, int planeSlots) => planeSlots * ((pixels + 7) / 8);

        // Hermon incremental update (type 0, frameMode 0; native FUN_18000a9e0 / FUN_18000ad30). A ten-byte header, then tileCount (big-endian, bytes 2..5) tiles, each:
        //   [6-byte tile header: row = byte 4, col = byte 5][512 bytes = 16x16 RGB555 pixels]
        // placed at pixel (col * 16, row * 16). Only changed tiles are sent.
        private const int TileSize = 16;
        private const int TileHeaderBytes = 6;
        private const int TilePixelBytes = TileSize * TileSize * 2; // 512 bytes (RGB555)

        private IReadOnlyList<DirtyRect> DecodeHermonIncremental(byte[] packet)
        {
            int tileCount = (packet[2] << 24) | (packet[3] << 16) | (packet[4] << 8) | packet[5];
            int off = VideoPacketHeader.HeaderSize; // 10
            _dirty.Clear();
            byte[] tile = _hermonTile; // fixed 16x16 BGRA scratch, reused across frames

            for (int t = 0; t < tileCount; t++)
            {
                if (off + TileHeaderBytes + TilePixelBytes > packet.Length) break;
                int row = packet[off + 4];
                int col = packet[off + 5];
                off += TileHeaderBytes;

                PixelConverter.RawRgb555(packet, off, TileSize * TileSize, tile, 0);
                off += TilePixelBytes;

                int px = col * TileSize, py = row * TileSize;
                // Edge tiles legitimately extend past a non-16-aligned resolution (800x600, for
                // example, is 37.5 tile rows); TileBlitter clips the overflow. An out-of-bounds tile
                // does NOT trigger a keyframe request: doing so stormed full frames at the BMC.
                TileBlitter.Blit(Frame, tile, TileSize, TileSize, px, py);
                _dirty.Add(new DirtyRect(px, py, TileSize, TileSize));
            }
            return _dirty;
        }
    }

    /// <summary>Raised for an ATEN encoding whose decode path is not yet implemented.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Reports an unhandled encoding, carrying the header fields that identify it so the
    /// stream can record which encoding was seen. Caught and skipped by
    /// <see cref="ATENtion.Core.Protocol.ServerMessageReader"/>, so it stalls one frame, not the pump.
    /// </para>
    /// </remarks>
    public sealed class UnsupportedEncodingException : Exception
    {
        /// <summary>Creates the exception from a description and the offending packet header.</summary>
        /// <param name="message">What is unsupported.</param>
        /// <param name="header">The header whose frame mode, type, and subtype are unhandled.</param>
        public UnsupportedEncodingException(string message, VideoPacketHeader header)
            : base($"{message} [frameMode={header.FrameMode}, type={header.EncodingType}, subtype={header.Subtype}]")
        {
            FrameMode = header.FrameMode;
            EncodingType = header.EncodingType;
            Subtype = header.Subtype;
        }

        /// <summary>The frame mode of the unsupported packet.</summary>
        public byte FrameMode { get; }
        /// <summary>The encoding type of the unsupported packet.</summary>
        public byte EncodingType { get; }
        /// <summary>The subtype of the unsupported packet.</summary>
        public byte Subtype { get; }
    }
}
