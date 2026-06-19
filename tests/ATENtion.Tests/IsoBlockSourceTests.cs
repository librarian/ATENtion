using System;
using System.IO;
using ATENtion.Core.Storage;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the ISO block source: capacity rounding, EOF zero-fill, and the empty-image guard.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="IsoBlockSource"/> rounds a partial trailing sector up into
    /// the capacity, zero-fills a read that runs past the end of the image, and rejects an empty image.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the read-only sector view.
    /// </para>
    /// </remarks>
    public class IsoBlockSourceTests
    {
        private static string WriteTemp(byte[] data)
        {
            string p = Path.GetTempFileName();
            File.WriteAllBytes(p, data);
            return p;
        }

        [Fact]
        public void Capacity_RoundsUpPartialTrailingSector()
        {
            // 2.5 sectors -> 3 total blocks, last LBA 2.
            string p = WriteTemp(new byte[IsoBlockSource.BlockSize * 2 + 100]);
            try
            {
                using (var iso = new IsoBlockSource(p))
                {
                    Assert.Equal(3, iso.TotalBlocks);
                    Assert.Equal((uint)2, iso.LastLba);
                }
            }
            finally { File.Delete(p); }
        }

        [Fact]
        public void ReadBlocks_ZeroFillsPastEof()
        {
            var data = new byte[IsoBlockSource.BlockSize];
            for (int i = 0; i < data.Length; i++) data[i] = 0xAB;
            string p = WriteTemp(data);
            try
            {
                using (var iso = new IsoBlockSource(p))
                {
                    // Read 2 blocks though only 1 exists: first is data, second is zero-filled.
                    byte[] got = iso.ReadBlocks(0, 2);
                    Assert.Equal(2 * IsoBlockSource.BlockSize, got.Length);
                    Assert.Equal((byte)0xAB, got[0]);
                    Assert.Equal((byte)0x00, got[IsoBlockSource.BlockSize]);
                }
            }
            finally { File.Delete(p); }
        }

        [Fact]
        public void EmptyImage_Throws()
        {
            string p = WriteTemp(new byte[0]);
            try { Assert.Throws<InvalidDataException>(() => new IsoBlockSource(p)); }
            finally { File.Delete(p); }
        }
    }
}
