using System;
using System.IO;
using ATENtion.Core.Storage;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the CD-ROM SCSI target's command responses against a known 10-sector ISO.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="ScsiCdRomTarget"/> returns the native ATEN INQUIRY data
    /// (honouring the allocation length), reports the ISO's capacity, reads the requested sectors,
    /// raises CHECK CONDITION with the right sense for an out-of-range read and an unknown opcode (and
    /// clears the sense once read), answers TEST UNIT READY good, and builds a READ TOC response.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the SCSI command set.
    /// </para>
    /// </remarks>
    public class ScsiCdRomTargetTests : IDisposable
    {
        private readonly string _path;
        private readonly IsoBlockSource _iso;
        private readonly ScsiCdRomTarget _target;
        private const int Sectors = 10;

        public ScsiCdRomTargetTests()
        {
            _path = Path.GetTempFileName();
            // 10 sectors; fill each with its LBA in every byte so reads are verifiable.
            var data = new byte[Sectors * IsoBlockSource.BlockSize];
            for (int lba = 0; lba < Sectors; lba++)
                for (int i = 0; i < IsoBlockSource.BlockSize; i++)
                    data[lba * IsoBlockSource.BlockSize + i] = (byte)lba;
            File.WriteAllBytes(_path, data);
            _iso = new IsoBlockSource(_path);
            _target = new ScsiCdRomTarget(_iso);
        }

        private static CommandBlockWrapper Cbw(uint tag, uint xferLen, bool dataIn, params byte[] cdb)
        {
            var p = new byte[31];
            p[0] = 0x55; p[1] = 0x53; p[2] = 0x42; p[3] = 0x43;
            p[4] = (byte)tag; p[5] = (byte)(tag >> 8); p[6] = (byte)(tag >> 16); p[7] = (byte)(tag >> 24);
            p[8] = (byte)xferLen; p[9] = (byte)(xferLen >> 8); p[10] = (byte)(xferLen >> 16); p[11] = (byte)(xferLen >> 24);
            p[12] = (byte)(dataIn ? 0x80 : 0x00);
            p[14] = (byte)cdb.Length;
            Array.Copy(cdb, 0, p, 15, Math.Min(cdb.Length, 16));
            return CommandBlockWrapper.Parse(p);
        }

        [Fact]
        public void Inquiry_ReturnsNativeAtenData()
        {
            var r = _target.Execute(Cbw(1, 36, true, 0x12, 0, 0, 0, 36, 0));
            Assert.Equal(0, r.Status);
            Assert.Equal(ScsiCdRomTarget.InquiryData, r.Data);
            Assert.Equal((byte)0x05, r.Data[0]);  // peripheral device type = CD/DVD
            Assert.Equal((byte)0x80, r.Data[1]);  // RMB removable
            // vendor "IPMI", product begins "Virtual", revision "3000"
            Assert.Equal("IPMI", System.Text.Encoding.ASCII.GetString(r.Data, 8, 4));
            Assert.Equal("Virtual", System.Text.Encoding.ASCII.GetString(r.Data, 16, 7));
            Assert.Equal("3000", System.Text.Encoding.ASCII.GetString(r.Data, 32, 4));
        }

        [Fact]
        public void Inquiry_RespectsAllocationLength()
        {
            var r = _target.Execute(Cbw(1, 8, true, 0x12, 0, 0, 0, 8, 0));
            Assert.Equal(8, r.Data.Length);
        }

        [Fact]
        public void ReadCapacity10_ReturnsLastLbaAndBlockSize()
        {
            var r = _target.Execute(Cbw(2, 8, true, 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            Assert.Equal(0, r.Status);
            Assert.Equal(8, r.Data.Length);
            uint lastLba = (uint)((r.Data[0] << 24) | (r.Data[1] << 16) | (r.Data[2] << 8) | r.Data[3]);
            uint blk = (uint)((r.Data[4] << 24) | (r.Data[5] << 16) | (r.Data[6] << 8) | r.Data[7]);
            Assert.Equal((uint)(Sectors - 1), lastLba);
            Assert.Equal((uint)IsoBlockSource.BlockSize, blk);
        }

        [Fact]
        public void Read10_ReturnsRequestedSectors()
        {
            // READ(10): LBA=3, length=2 blocks.
            var r = _target.Execute(Cbw(3, 2 * 2048, true, 0x28, 0, 0, 0, 0, 3, 0, 0, 2, 0));
            Assert.Equal(0, r.Status);
            Assert.Equal(2 * IsoBlockSource.BlockSize, r.Data.Length);
            Assert.Equal((byte)3, r.Data[0]);                              // first sector is LBA 3
            Assert.Equal((byte)4, r.Data[IsoBlockSource.BlockSize]);       // second sector is LBA 4
        }

        [Fact]
        public void Read10_PastEnd_IsCheckCondition()
        {
            // LBA 9 + 5 blocks runs past the 10-sector image.
            var r = _target.Execute(Cbw(4, 5 * 2048, true, 0x28, 0, 0, 0, 0, 9, 0, 0, 5, 0));
            Assert.Equal(1, r.Status);

            // REQUEST SENSE should now report LBA OUT OF RANGE (0x21).
            var sense = _target.Execute(Cbw(5, 18, true, 0x03, 0, 0, 0, 18, 0));
            Assert.Equal(0x05, sense.Data[2]);  // ILLEGAL REQUEST
            Assert.Equal(0x21, sense.Data[12]); // LBA OUT OF RANGE
        }

        [Fact]
        public void UnknownOpcode_IsCheckConditionThenSenseCleared()
        {
            var r = _target.Execute(Cbw(6, 0, false, 0xEE));
            Assert.Equal(1, r.Status);

            var sense1 = _target.Execute(Cbw(7, 18, true, 0x03, 0, 0, 0, 18, 0));
            Assert.Equal(0x20, sense1.Data[12]); // INVALID COMMAND OPERATION CODE

            // Sense is consumed: a second REQUEST SENSE reports no error.
            var sense2 = _target.Execute(Cbw(8, 18, true, 0x03, 0, 0, 0, 18, 0));
            Assert.Equal(0x00, sense2.Data[2]);
        }

        [Fact]
        public void TestUnitReady_IsGoodNoData()
        {
            var r = _target.Execute(Cbw(9, 0, false, 0x00));
            Assert.Equal(0, r.Status);
            Assert.Empty(r.Data);
        }

        [Fact]
        public void ReadToc_ReturnsHeaderAndTrackOne()
        {
            var r = _target.Execute(Cbw(10, 20, true, 0x43, 0, 0, 0, 0, 0, 0, 0, 20, 0));
            Assert.Equal(0, r.Status);
            Assert.Equal(0x01, r.Data[2]); // first track
            Assert.Equal(0x01, r.Data[3]); // last track
            Assert.Equal(0x14, r.Data[5]); // ADR/control data track
            Assert.Equal(0x01, r.Data[6]); // track number 1
        }

        [Fact]
        public void LinuxMmcDiscoveryCommands_AreSupported()
        {
            var config = _target.Execute(Cbw(11, 152, true, 0x46, 0, 0, 0, 0, 0, 0, 0, 152, 0));
            Assert.Equal(0, config.Status);
            Assert.Equal(152, config.Data.Length);

            var disc = _target.Execute(Cbw(12, 34, true, 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0));
            Assert.Equal(0, disc.Status);
            Assert.Equal(34, disc.Data.Length);

            var track = _target.Execute(Cbw(13, 30, true, 0x52, 1, 0, 0, 0, 1, 0, 0, 30, 0));
            Assert.Equal(0, track.Status);
            Assert.Equal(28, track.Data.Length);
            Assert.Equal((byte)Sectors, track.Data[27]);

            var events = _target.Execute(Cbw(14, 8, true, 0x4A, 1, 0, 0, 0x10, 0, 0, 0, 8, 0));
            Assert.Equal(0, events.Status);
            Assert.Equal(new byte[] { 0, 6, 4, 0x5E, 0, 2, 0, 0 }, events.Data);
        }

        public void Dispose()
        {
            _iso.Dispose();
            try { File.Delete(_path); } catch { }
        }
    }
}
