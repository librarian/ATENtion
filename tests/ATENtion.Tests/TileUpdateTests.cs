using ATENtion.Core.Video;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies tile blitting (placement, clipping) and incremental-tile decode with buffer reuse.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="TileBlitter"/> places a tile at an offset and clips one that
    /// overflows the surface, that the decoder reuses its scratch buffers across consecutive incrementals
    /// without leaking stale pixels, and that the type-4 and type-2 incremental paths decode a tile to
    /// the correct position and colour.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers tile blitting and the incremental tile format.
    /// </para>
    /// </remarks>
    public class TileUpdateTests
    {
        [Fact]
        public void Blitter_Places_Tile_At_Offset_And_Clips()
        {
            var frame = new FrameBuffer(4, 4);
            // 2x2 tile, distinct per-pixel blue values.
            var tile = new byte[2 * 2 * 4];
            tile[0] = 0x11; tile[3] = 0xFF; // (0,0) B=0x11
            tile[4] = 0x22; tile[7] = 0xFF; // (1,0) B=0x22
            tile[8] = 0x33; tile[11] = 0xFF; // (0,1) B=0x33
            tile[12] = 0x44; tile[15] = 0xFF; // (1,1) B=0x44

            TileBlitter.Blit(frame, tile, 2, 2, 1, 1);

            // Tile lands at (1,1)..(2,2).
            Assert.Equal(0x11, frame.Pixels[(1 * 4 + 1) * 4 + 0]);
            Assert.Equal(0x22, frame.Pixels[(1 * 4 + 2) * 4 + 0]);
            Assert.Equal(0x33, frame.Pixels[(2 * 4 + 1) * 4 + 0]);
            Assert.Equal(0x44, frame.Pixels[(2 * 4 + 2) * 4 + 0]);
            // Origin untouched.
            Assert.Equal(0x00, frame.Pixels[0]);
        }

        [Fact]
        public void Blitter_Clips_Tile_That_Overflows_The_Surface()
        {
            // 4x4 tile placed at (2,2) on a 4x4 surface: only the tile's top-left 2x2 is in bounds.
            // Exercises the per-pixel clip (slow) path. It must place in-bounds pixels and never throw.
            var frame = new FrameBuffer(4, 4);
            var tile = new byte[4 * 4 * 4];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                {
                    int i = (r * 4 + c) * 4;
                    tile[i + 0] = (byte)(r * 4 + c + 1); // distinct blue per tile pixel
                    tile[i + 3] = 0xFF;
                }

            TileBlitter.Blit(frame, tile, 4, 4, 2, 2);

            Assert.Equal(1, frame.Pixels[(2 * 4 + 2) * 4]); // tile(col0,row0)
            Assert.Equal(2, frame.Pixels[(2 * 4 + 3) * 4]); // tile(col1,row0)
            Assert.Equal(5, frame.Pixels[(3 * 4 + 2) * 4]); // tile(col0,row1)
            Assert.Equal(6, frame.Pixels[(3 * 4 + 3) * 4]); // tile(col1,row1)
            Assert.Equal(0, frame.Pixels[0]);               // outside the blit region, untouched
        }

        [Fact]
        public void Decoder_Reuses_Scratch_Buffers_Across_Consecutive_Incrementals()
        {
            // Two incrementals decoded by the SAME decoder must each produce correct, independent
            // pixels - guards against stale data leaking through the now-reused scratch buffers.
            var decoder = new AtenTileDecoder(16, 16);

            var planesA = new byte[32]; planesA[15] = 0x01; // pixel0 Blue bit0
            var pktA = AtenPacketBuilder.BuildIncremental(
                new[] { new AtenPacketBuilder.Tile { X = 2, Y = 3, W = 8, H = 1, Planes = planesA } }, 4);
            var dirtyA = decoder.DecodePacket(pktA);
            Assert.Single(dirtyA);
            Assert.Equal(2, dirtyA[0].X);
            int idxA = (3 * 16 + 2) * 4;
            Assert.Equal(0x01, decoder.Frame.Pixels[idxA + 0]);

            var planesB = new byte[32]; planesB[15] = 0x01;
            var pktB = AtenPacketBuilder.BuildIncremental(
                new[] { new AtenPacketBuilder.Tile { X = 5, Y = 6, W = 8, H = 1, Planes = planesB } }, 4);
            var dirtyB = decoder.DecodePacket(pktB);
            Assert.Single(dirtyB);
            Assert.Equal(5, dirtyB[0].X);
            Assert.Equal(6, dirtyB[0].Y);
            int idxB = (6 * 16 + 5) * 4;
            Assert.Equal(0x01, decoder.Frame.Pixels[idxB + 0]);
            // The first tile's pixels persist (an incremental updates only its own tiles).
            Assert.Equal(0x01, decoder.Frame.Pixels[idxA + 0]);
        }

        [Fact]
        public void Incremental_Type4_Decodes_Tile_To_Position()
        {
            // One 8x1 tile placed at (2,3). bytesPerPlane = 1, 32 plane slots = 32 bytes.
            var planes = new byte[32];
            planes[15] = 0x01; // plane 15, byte0, bit0 -> tile pixel 0, Blue bit0

            var tiles = new[]
            {
                new AtenPacketBuilder.Tile { X = 2, Y = 3, W = 8, H = 1, Planes = planes }
            };
            byte[] packet = AtenPacketBuilder.BuildIncremental(tiles, encodingType: 4);

            var decoder = new AtenTileDecoder(16, 16);
            var dirty = decoder.DecodePacket(packet);

            Assert.Single(dirty);
            Assert.Equal(2, dirty[0].X);
            Assert.Equal(3, dirty[0].Y);
            Assert.Equal(8, dirty[0].Width);
            Assert.Equal(1, dirty[0].Height);

            // Tile pixel 0 (Blue bit0 set) lands at framebuffer (2,3).
            int idx = (3 * 16 + 2) * 4;
            Assert.Equal(0x01, decoder.Frame.Pixels[idx + 0]); // B
            Assert.Equal(0x00, decoder.Frame.Pixels[idx + 1]); // G
            Assert.Equal(0xFF, decoder.Frame.Pixels[idx + 3]); // A
        }

        [Fact]
        public void Incremental_Type2_Decodes_Rgb555_Tile()
        {
            // One 8x1 tile at (4,5). type-2 uses 16 plane slots; bytesPerPlane = 1.
            var planes = new byte[16];
            planes[15] = 0x01; // plane 15 -> v0 bit0 (pixel 0) => Rgb555 Blue = (v0 & 0x1f) << 3 = 0x08

            var tiles = new[]
            {
                new AtenPacketBuilder.Tile { X = 4, Y = 5, W = 8, H = 1, Planes = planes }
            };
            byte[] packet = AtenPacketBuilder.BuildIncremental(tiles, encodingType: 2);

            var decoder = new AtenTileDecoder(16, 16);
            var dirty = decoder.DecodePacket(packet);

            Assert.Single(dirty);
            Assert.Equal(4, dirty[0].X);
            Assert.Equal(5, dirty[0].Y);

            int idx = (5 * 16 + 4) * 4;
            Assert.Equal(0x08, decoder.Frame.Pixels[idx + 0]); // B = 1 << 3
            Assert.Equal(0xFF, decoder.Frame.Pixels[idx + 3]); // A
        }
    }
}
