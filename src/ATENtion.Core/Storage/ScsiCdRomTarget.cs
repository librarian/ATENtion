using System;

namespace ATENtion.Core.Storage
{
    /// <summary>
    /// The outcome of executing one SCSI command: the data-in bytes to return to the host
    /// and the status byte for the Command Status Wrapper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pairs the data phase of a SCSI response with its status phase so the caller
    /// can frame both onto the wire. A command that returns no data carries an empty
    /// <see cref="Data"/> and a status-only response.
    /// </para>
    /// <para>
    /// PROVENANCE - The status byte is the USB Mass-Storage CSW bCSWStatus field; see
    /// <see cref="VmediaFraming"/>.
    /// </para>
    /// </remarks>
    public sealed class ScsiResult
    {
        /// <summary>Shared empty data-in payload for status-only and no-data results.</summary>
        public static readonly byte[] NoData = new byte[0];

        /// <summary>Creates a result from a data-in payload and a CSW status byte.</summary>
        /// <param name="data">The data-in bytes; null is treated as <see cref="NoData"/>.</param>
        /// <param name="status">The CSW status: 0 for good, 1 for check-condition.</param>
        public ScsiResult(byte[] data, byte status)
        {
            Data = data ?? NoData;
            Status = status;
        }

        /// <summary>The data-in payload returned to the host; empty for non-data commands.</summary>
        public byte[] Data { get; }
        /// <summary>The CSW bCSWStatus byte: 0 = good, 1 = check-condition.</summary>
        public byte Status { get; }

        /// <summary>A good (status 0) result carrying the given data-in payload, or no data.</summary>
        public static ScsiResult Good(byte[] data = null) => new ScsiResult(data ?? NoData, 0);
        /// <summary>A check-condition (status 1) result with no data; sense is set separately.</summary>
        public static ScsiResult Check() => new ScsiResult(NoData, 1);
    }

    /// <summary>
    /// A read-only CD-ROM SCSI target, backed by an <see cref="IsoBlockSource"/>, that answers
    /// the command descriptor blocks the BMC forwards over the ATEN virtual-media channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Presents a mounted ISO image to the host as a virtual CD-ROM. Each call to
    /// <see cref="Execute"/> runs one SCSI command and yields the data-in bytes plus the status
    /// the host's USB mass-storage driver expects.
    /// </para>
    /// <para>
    /// OPERATION - The command opcode (the first byte of the CDB) selects the handler. The
    /// device-identity and capacity commands return fixed or ISO-derived descriptors. The read
    /// commands map a logical block address and length onto the ISO and return 2048-byte sectors.
    /// A command the target does not implement completes with CHECK CONDITION. The sense data for
    /// the failure is held until the host issues a REQUEST SENSE, as the SCSI error protocol
    /// requires, and is then cleared.
    /// </para>
    /// <para>
    /// The INQUIRY response is byte-for-byte the native client's, so the host enumerates the same
    /// device it would see under the original viewer (vendor "ATEN", product "Virtual CDROM",
    /// revision "YS0J").
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads sectors from an <see cref="IsoBlockSource"/>. The command blocks and
    /// the surrounding framing are parsed and built by <see cref="VmediaFraming"/>. This type is
    /// concerned only with the SCSI command set, not the transport.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Read-only: there is no WRITE path. A single unit of sense is retained, so a
    /// second failing command before the host reads sense overwrites the first. It is not
    /// thread-safe, and the serve loop drives it from one thread.
    /// </para>
    /// <para>
    /// PROVENANCE - Command set and the verbatim INQUIRY data from the native CD command processor
    /// iKVM64.dll FUN_180004c80. The command repertoire is PORTED
    /// FAITHFULLY. The end-to-end attach and boot of an ISO is not yet confirmed against hardware.
    /// </para>
    /// </remarks>
    public sealed class ScsiCdRomTarget
    {
        // --- SCSI operation codes (CDB byte 0). Only the commands a read-only CD-ROM target needs
        //     are recognised; everything else returns CHECK CONDITION with ILLEGAL REQUEST sense. ---

        /// <summary>TEST UNIT READY (0x00): the host polls medium readiness; answered good.</summary>
        private const byte TEST_UNIT_READY = 0x00;
        /// <summary>REQUEST SENSE (0x03): the host reads the sense data for the last failure.</summary>
        private const byte REQUEST_SENSE = 0x03;
        /// <summary>INQUIRY (0x12): the host reads the device identity; answered with <see cref="InquiryData"/>.</summary>
        private const byte INQUIRY = 0x12;
        /// <summary>MODE SENSE(6) (0x1A): the host reads mode parameters; answered with a minimal header.</summary>
        private const byte MODE_SENSE_6 = 0x1A;
        /// <summary>START STOP UNIT (0x1B): spin-up/eject control; accepted and answered good (no medium to move).</summary>
        private const byte START_STOP_UNIT = 0x1B;
        /// <summary>PREVENT ALLOW MEDIUM REMOVAL (0x1E): media-lock control; accepted and answered good.</summary>
        private const byte PREVENT_ALLOW_REMOVAL = 0x1E;
        /// <summary>READ CAPACITY(10) (0x25): the host reads last-LBA and block length; derived from the ISO.</summary>
        private const byte READ_CAPACITY_10 = 0x25;
        /// <summary>READ(10) (0x28): the host reads sectors; 32-bit LBA, 16-bit transfer length.</summary>
        private const byte READ_10 = 0x28;
        /// <summary>READ TOC/PMA/ATIP (0x43): the host reads the disc table of contents; a single data track.</summary>
        private const byte READ_TOC = 0x43;
        /// <summary>GET CONFIGURATION (0x46): MMC feature query; not implemented (CHECK CONDITION).</summary>
        private const byte GET_CONFIGURATION = 0x46;
        /// <summary>MODE SENSE(10) (0x5A): the host reads mode parameters; answered with a minimal header.</summary>
        private const byte MODE_SENSE_10 = 0x5A;
        /// <summary>READ(12) (0xA8): the host reads sectors; 32-bit LBA, 32-bit transfer length.</summary>
        private const byte READ_12 = 0xA8;

        // --- Sense data: the key and additional-sense codes used to describe a failure to the host. ---

        /// <summary>Sense key ILLEGAL REQUEST (0x05): the command or one of its fields was not valid.</summary>
        private const byte SK_ILLEGAL_REQUEST = 0x05;
        /// <summary>Additional sense: INVALID COMMAND OPERATION CODE (0x20).</summary>
        private const byte ASC_INVALID_OPCODE = 0x20;
        /// <summary>Additional sense: LOGICAL BLOCK ADDRESS OUT OF RANGE (0x21).</summary>
        private const byte ASC_LBA_OUT_OF_RANGE = 0x21;
        /// <summary>Additional sense: INVALID FIELD IN CDB (0x24).</summary>
        private const byte ASC_INVALID_FIELD_IN_CDB = 0x24;

        /// <summary>
        /// The native ATEN standard INQUIRY response (36 bytes), transcribed verbatim from
        /// iKVM64.dll FUN_180004c80.
        /// </summary>
        /// <remarks>
        /// Peripheral device type 0x05 (CD/DVD), removable medium (RMB set in byte 1), vendor
        /// identification "ATEN", product identification "Virtual CDROM", product revision "YS0J".
        /// Returning the original viewer's exact bytes makes the host enumerate the identical device.
        /// </remarks>
        public static readonly byte[] InquiryData =
        {
            0x05, 0x80, 0x00, 0x21, 0x1F, 0x00, 0x00, 0x00,
            (byte)'A', (byte)'T', (byte)'E', (byte)'N', (byte)' ', (byte)' ', (byte)' ', (byte)' ',
            (byte)'V', (byte)'i', (byte)'r', (byte)'t', (byte)'u', (byte)'a', (byte)'l', (byte)' ',
            (byte)'C', (byte)'D', (byte)'R', (byte)'O', (byte)'M', (byte)' ', (byte)' ', (byte)' ',
            (byte)'Y', (byte)'S', (byte)'0', (byte)'J',
        };

        private readonly IsoBlockSource _iso;

        // Current fixed-format sense, returned by the next REQUEST SENSE and then cleared. A failing
        // command sets these three. A successful REQUEST SENSE reads and resets them to zero.
        private byte _senseKey;
        private byte _senseAsc;
        private byte _senseAscq;

        /// <summary>Creates a CD-ROM target serving the given ISO block source.</summary>
        /// <param name="iso">The mounted ISO image to present as a CD-ROM.</param>
        /// <exception cref="ArgumentNullException"><paramref name="iso"/> is null.</exception>
        public ScsiCdRomTarget(IsoBlockSource iso)
        {
            _iso = iso ?? throw new ArgumentNullException(nameof(iso));
        }

        /// <summary>
        /// Executes one SCSI command and returns its data-in bytes and CSW status.
        /// </summary>
        /// <param name="cbw">The parsed Command Block Wrapper carrying the CDB and the host's
        /// allocation length.</param>
        /// <returns>The data to return and the status byte; a CHECK CONDITION result for any
        /// unsupported command, with sense recorded for the host's following REQUEST SENSE.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="cbw"/> is null.</exception>
        public ScsiResult Execute(CommandBlockWrapper cbw)
        {
            if (cbw == null) throw new ArgumentNullException(nameof(cbw));
            byte[] cdb = cbw.Cdb;
            byte op = cbw.Opcode;
            int alloc = (int)cbw.DataTransferLength;

            switch (op)
            {
                case TEST_UNIT_READY:
                case START_STOP_UNIT:
                case PREVENT_ALLOW_REMOVAL:
                    // Medium-readiness and medium-movement controls. There is no physical medium to
                    // move and the ISO is always ready, so each is accepted with a good status.
                    return ScsiResult.Good();

                case REQUEST_SENSE:
                    return ScsiResult.Good(Cap(BuildRequestSense(), alloc));

                case INQUIRY:
                    // Enabled vital-product-data pages (CDB[1] bit 0) are not emulated here. The BMC's
                    // own firmware answers those in practice. Return the standard INQUIRY data.
                    return ScsiResult.Good(Cap(InquiryData, alloc));

                case READ_CAPACITY_10:
                    return ScsiResult.Good(Cap(BuildReadCapacity10(), alloc));

                case READ_10:
                    return ExecuteRead(ReadBe32(cdb, 2), ReadBe16(cdb, 7), alloc);

                case READ_12:
                    return ExecuteRead(ReadBe32(cdb, 2), ReadBe32(cdb, 6), alloc);

                case MODE_SENSE_6:
                    return ScsiResult.Good(Cap(BuildModeSense6(), alloc));

                case MODE_SENSE_10:
                    return ScsiResult.Good(Cap(BuildModeSense10(), alloc));

                case READ_TOC:
                    return ScsiResult.Good(Cap(BuildReadToc(cdb), alloc));

                default:
                    // Unsupported command. Report CHECK CONDITION and record ILLEGAL REQUEST /
                    // INVALID COMMAND OPERATION CODE for the host's following REQUEST SENSE.
                    return Fail(SK_ILLEGAL_REQUEST, ASC_INVALID_OPCODE, 0x00);
            }
        }

        /// <summary>
        /// Serves a READ(10)/READ(12): returns the requested run of sectors from the ISO, bounded
        /// by the host's allocation length.
        /// </summary>
        /// <param name="lba">The starting logical block address.</param>
        /// <param name="blocks">The number of 2048-byte blocks to read.</param>
        /// <param name="alloc">The host's allocation length; the returned data is capped to it.</param>
        /// <returns>The sector data, or CHECK CONDITION when the range falls outside the ISO.</returns>
        private ScsiResult ExecuteRead(long lba, long blocks, int alloc)
        {
            if (blocks == 0) return ScsiResult.Good(); // a zero-length read is a valid no-op
            if (lba < 0 || lba + blocks > _iso.TotalBlocks)
                return Fail(SK_ILLEGAL_REQUEST, ASC_LBA_OUT_OF_RANGE, 0x00);

            byte[] data = _iso.ReadBlocks(lba, (int)blocks);
            // Honour the host's allocation length when it asked for fewer bytes than the block math.
            if (alloc > 0 && alloc < data.Length) data = Cap(data, alloc);
            return ScsiResult.Good(data);
        }

        // READ CAPACITY(10) response: [last LBA : u32 BE][block length : u32 BE].
        private byte[] BuildReadCapacity10()
        {
            uint last = _iso.LastLba;
            uint blk = IsoBlockSource.BlockSize;
            return new[]
            {
                (byte)(last >> 24), (byte)(last >> 16), (byte)(last >> 8), (byte)last,
                (byte)(blk >> 24), (byte)(blk >> 16), (byte)(blk >> 8), (byte)blk,
            };
        }

        // REQUEST SENSE response: fixed-format sense, 18 bytes. The held sense is cleared once read,
        // as the SCSI error model requires.
        private byte[] BuildRequestSense()
        {
            var s = new byte[18];
            s[0] = 0x70;          // response code 0x70: current error, fixed format
            s[2] = _senseKey;     // sense key
            s[7] = 10;            // additional sense length (bytes beyond this field)
            s[12] = _senseAsc;    // additional sense code
            s[13] = _senseAscq;   // additional sense code qualifier
            _senseKey = _senseAsc = _senseAscq = 0; // sense is consumed by REQUEST SENSE
            return s;
        }

        // MODE SENSE(6) response: a 4-byte mode parameter header only, with no block descriptors
        // and no pages. Mode data length 3, medium type 0, no write protection.
        private static byte[] BuildModeSense6()
        {
            return new byte[] { 0x03, 0x00, 0x00, 0x00 };
        }

        // MODE SENSE(10) response: an 8-byte mode parameter header only.
        private static byte[] BuildModeSense10()
        {
            return new byte[] { 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        }

        // READ TOC (format 0): a TOC header, a single track-1 (data) descriptor, and the lead-out.
        private byte[] BuildReadToc(byte[] cdb)
        {
            bool msf = (cdb[1] & 0x02) != 0; // address format: minute/second/frame vs logical block
            // 4-byte header followed by two 8-byte descriptors (track 1 and lead-out).
            var toc = new byte[4 + 8 + 8];
            int len = toc.Length - 2;
            toc[0] = (byte)(len >> 8); // TOC data length (excludes this 2-byte field), big-endian
            toc[1] = (byte)len;
            toc[2] = 0x01; // first track number
            toc[3] = 0x01; // last track number

            // Track 1 descriptor.
            toc[5] = 0x14;     // ADR = 1, control = 4 (data track)
            toc[6] = 0x01;     // track number
            WriteTocAddress(toc, 8, 0, msf);

            // Lead-out descriptor (track number 0xAA) at the end of the disc.
            toc[13] = 0x14;
            toc[14] = 0xAA;
            WriteTocAddress(toc, 16, _iso.TotalBlocks, msf);
            return toc;
        }

        // Writes a TOC track address at the given offset, in either MSF or logical-block form.
        private static void WriteTocAddress(byte[] buf, int offset, long lba, bool msf)
        {
            if (msf)
            {
                long f = lba + 150; // 150 frames = the standard 2-second lead-in pre-gap
                buf[offset] = 0;
                buf[offset + 1] = (byte)(f / (75 * 60)); // minutes (75 frames per second)
                buf[offset + 2] = (byte)((f / 75) % 60); // seconds
                buf[offset + 3] = (byte)(f % 75);        // frames
            }
            else
            {
                buf[offset] = (byte)(lba >> 24);
                buf[offset + 1] = (byte)(lba >> 16);
                buf[offset + 2] = (byte)(lba >> 8);
                buf[offset + 3] = (byte)lba;
            }
        }

        // Records sense for the host's following REQUEST SENSE and returns a CHECK CONDITION result.
        private ScsiResult Fail(byte key, byte asc, byte ascq)
        {
            _senseKey = key;
            _senseAsc = asc;
            _senseAscq = ascq;
            return ScsiResult.Check();
        }

        // Caps a response to the host's allocation length: a SCSI target must never return more
        // bytes than the host asked for. Returns the original array when no trimming is needed.
        private static byte[] Cap(byte[] data, int alloc)
        {
            if (alloc <= 0 || alloc >= data.Length) return data;
            var trimmed = new byte[alloc];
            Array.Copy(data, trimmed, alloc);
            return trimmed;
        }

        // Reads a big-endian 32-bit field from a CDB.
        private static long ReadBe32(byte[] b, int o) =>
            ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];

        // Reads a big-endian 16-bit field from a CDB.
        private static int ReadBe16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    }
}
