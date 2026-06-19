using System.IO;
using ATENtion.Core.Net;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the buffered stream's big-endian primitives, large reads, and read/write independence.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="BufferedRfbStream"/> writes and reads the u8, u16, and u32
    /// primitives big-endian, satisfies a read larger than its internal buffer, and keeps the buffered
    /// read side from disturbing interleaved writes.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the stream primitives.
    /// </para>
    /// </remarks>
    public class BufferedRfbStreamTests
    {
        [Fact]
        public void Writes_And_Reads_BigEndian()
        {
            var ms = new MemoryStream();
            var w = new BufferedRfbStream(ms);
            w.WriteU8(0xAB);
            w.WriteU16BE(0x1234);
            w.WriteU32BE(0xDEADBEEF);
            w.WriteBytes(new byte[] { 1, 2, 3 });
            w.Flush();

            Assert.Equal(new byte[] { 0xAB, 0x12, 0x34, 0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3 },
                         ms.ToArray());

            ms.Position = 0;
            var r = new BufferedRfbStream(ms);
            Assert.Equal(0xAB, r.ReadU8());
            Assert.Equal(0x1234, r.ReadU16BE());
            Assert.Equal(0xDEADBEEFu, r.ReadU32BE());
            Assert.Equal(new byte[] { 1, 2, 3 }, r.ReadExact(3));
        }

        [Fact]
        public void ReadExact_Returns_All_Bytes_For_Reads_Larger_Than_The_Read_Buffer()
        {
            // Larger than the 64 KiB internal BufferedStream so the read path can't satisfy it from
            // a single buffer fill - exercises the loop + large-read bypass.
            var data = new byte[200_000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);

            var r = new BufferedRfbStream(new MemoryStream(data));
            Assert.Equal(data, r.ReadExact(data.Length));
        }

        [Fact]
        public void Buffered_Reads_Do_Not_Disturb_The_Write_Side()
        {
            // FakeDuplexStream keeps incoming (read) and outgoing (write) on separate buffers, so a
            // read-ahead on the read side must never consume or reorder what the client writes. The
            // first read fills the read buffer (read-ahead). Interleaved writes must still land verbatim.
            var fake = new FakeDuplexStream(new byte[] { 0x10, 0x20, 0x30, 0x40 });
            var s = new BufferedRfbStream(fake);

            Assert.Equal(0x10, s.ReadU8());
            s.WriteU8(0xAA);
            Assert.Equal(0x20, s.ReadU8());
            s.WriteU16BE(0xBBCC);
            Assert.Equal(new byte[] { 0x30, 0x40 }, s.ReadExact(2));

            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, fake.Written);
        }
    }
}
