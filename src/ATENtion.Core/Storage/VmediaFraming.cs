using System;

namespace ATENtion.Core.Storage
{
    /// <summary>
    /// Frames and parses the ATEN virtual-media wire protocol: USB Mass-Storage Bulk-Only Transport
    /// (CBW/CSW with SCSI) tunnelled over the virtual-media transport, each message prefixed by an
    /// eight-byte ATEN header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Builds the eight-byte frame header and the thirteen-byte Command Status Wrapper, and
    /// reads the payload length and logical unit out of an incoming header.
    /// </para>
    /// <para>
    /// OPERATION - The frame header carries a marker byte, the logical unit, a flag distinguishing a
    /// data frame from a status frame, and a little-endian payload length. The parser also reads the
    /// first four header bytes big-endian as a "type" used to detect control frames. A SCSI command
    /// arrives as a header with a payload length of 31 followed by a standard USB Command Block
    /// Wrapper.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   frame header (8 bytes): [0x22][0x00][LUN][flag][payloadLen : u32 LE]
    ///     flag 0x00 = DATA frame, 0xFF = STATUS/CSW frame
    ///   CSW (13 bytes): "USBS"[tag : u32 LE][residue : u32 LE][status]
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Pairs with <see cref="CommandBlockWrapper"/> for the inbound command and with
    /// <see cref="ScsiCdRomTarget"/> for the command result the CSW reports.
    /// </para>
    /// <para>
    /// PROVENANCE - Native serve loop and response builders iKVM64.dll FUN_180008bf0 (header seed),
    /// FUN_180001b90 / FUN_180001cc0 (data and CSW frames), FUN_1800085a0 / FUN_180002180 (header and
    /// CBW parse). PORTED FAITHFULLY.
    /// </para>
    /// </remarks>
    public static class VmediaFraming
    {
        /// <summary>The frame marker byte (header byte 0).</summary>
        public const byte Marker = 0x22;
        /// <summary>The flag value (header byte 3) for a data-in frame.</summary>
        public const byte FlagData = 0x00;
        /// <summary>The flag value (header byte 3) for a CSW/status frame.</summary>
        public const byte FlagStatus = 0xFF;

        /// <summary>The fixed frame-header length, in bytes.</summary>
        public const int HeaderSize = 8;
        /// <summary>The fixed Command Block Wrapper length, in bytes.</summary>
        public const int CbwSize = 31;
        /// <summary>The fixed Command Status Wrapper length, in bytes.</summary>
        public const int CswSize = 13;
        /// <summary>The server command header is marker/kind/LUN/flags/length, eight bytes total.</summary>
        public const int CommandHeaderSize = 8;
        /// <summary>The server marker byte carrying a USB command block.</summary>
        public const byte CommandMarker = 0x11;

        /// <summary>The USB CBW signature, "USBC", little-endian.</summary>
        public const uint CbwSignature = 0x43425355;
        /// <summary>The USB CSW signature, "USBS", little-endian.</summary>
        public const uint CswSignature = 0x53425355;

        /// <summary>Builds the eight-byte ATEN frame header for a payload of the given length.</summary>
        /// <param name="lun">The logical unit number to stamp.</param>
        /// <param name="flag">The frame flag (<see cref="FlagData"/> or <see cref="FlagStatus"/>).</param>
        /// <param name="payloadLen">The payload length, in bytes (written little-endian).</param>
        /// <returns>A new eight-byte header.</returns>
        public static byte[] BuildHeader(byte lun, byte flag, int payloadLen)
        {
            return new[]
            {
                Marker, (byte)0x00, lun, flag,
                (byte)payloadLen, (byte)(payloadLen >> 8),
                (byte)(payloadLen >> 16), (byte)(payloadLen >> 24),
            };
        }

        /// <summary>Reads the little-endian payload length from an eight-byte header (bytes 4..7).</summary>
        /// <param name="header">The eight-byte frame header.</param>
        /// <returns>The payload length, in bytes.</returns>
        /// <exception cref="ArgumentException"><paramref name="header"/> is shorter than eight bytes.</exception>
        public static int ReadPayloadLength(byte[] header)
        {
            if (header == null || header.Length < HeaderSize)
                throw new ArgumentException("Header must be 8 bytes.", nameof(header));
            return header[4] | (header[5] << 8) | (header[6] << 16) | (header[7] << 24);
        }

        /// <summary>Reads the logical unit number from a header (byte 2), echoed in response headers.</summary>
        /// <param name="header">The frame header.</param>
        /// <returns>The logical unit number.</returns>
        public static byte ReadLun(byte[] header) => header[2];

        /// <summary>Reads the LE payload length from a server's eight-byte command header.</summary>
        public static int ReadCommandPayloadLength(byte[] header)
        {
            if (header == null || header.Length < CommandHeaderSize)
                throw new ArgumentException("Command header must be 8 bytes.", nameof(header));
            return header[4] | (header[5] << 8) | (header[6] << 16) | (header[7] << 24);
        }

        /// <summary>Reads the server command header's LUN byte.</summary>
        public static byte ReadCommandLun(byte[] header)
        {
            if (header == null || header.Length < CommandHeaderSize)
                throw new ArgumentException("Command header must be 8 bytes.", nameof(header));
            return header[2];
        }

        /// <summary>Reads the big-endian message type from the first four bytes.</summary>
        public static uint ReadType(byte[] header)
        {
            if (header == null || header.Length < 4)
                throw new ArgumentException("Message prefix must be at least 4 bytes.", nameof(header));
            return ((uint)header[0] << 24) | ((uint)header[1] << 16) |
                   ((uint)header[2] << 8) | header[3];
        }

        /// <summary>
        /// Builds the thirteen-byte Command Status Wrapper: the "USBS" signature, the echoed command
        /// tag, the residue, and the status byte.
        /// </summary>
        /// <param name="tag">The command tag to echo (dCSWTag = the CBW's tag).</param>
        /// <param name="residue">The data residue (requested length minus bytes transferred).</param>
        /// <param name="status">The status byte: 0 for good, 1 for check-condition.</param>
        /// <returns>A new thirteen-byte CSW.</returns>
        public static byte[] BuildCsw(uint tag, uint residue, byte status)
        {
            return new[]
            {
                (byte)'U', (byte)'S', (byte)'B', (byte)'S',
                (byte)tag, (byte)(tag >> 8), (byte)(tag >> 16), (byte)(tag >> 24),
                (byte)residue, (byte)(residue >> 8), (byte)(residue >> 16), (byte)(residue >> 24),
                status,
            };
        }
    }

    /// <summary>
    /// A parsed USB Bulk-Only Transport Command Block Wrapper: the 31-byte structure that carries one
    /// SCSI command from the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Exposes the fields of a CBW the target needs: the tag to echo, the expected transfer
    /// length and direction, the logical unit, and the SCSI command descriptor block.
    /// </para>
    /// <para>
    /// WIRE FORMAT - 31 bytes: signature (u32 LE), tag (u32 LE), dCBWDataTransferLength (u32 LE),
    /// bmCBWFlags (1, bit 0x80 = data-in), LUN (1, low nibble), CDB length (1, low five bits), and a
    /// 16-byte CDB.
    /// </para>
    /// <para>
    /// PROVENANCE - USB BOT CBW layout, parsed by the native iKVM64.dll FUN_180002180.
    /// </para>
    /// </remarks>
    public sealed class CommandBlockWrapper
    {
        /// <summary>The CBW signature word (expected to be the "USBC" signature).</summary>
        public uint Signature { get; private set; }
        /// <summary>The command tag, echoed back in the CSW.</summary>
        public uint Tag { get; private set; }
        /// <summary>The number of bytes the host expects to transfer (dCBWDataTransferLength).</summary>
        public uint DataTransferLength { get; private set; }
        /// <summary>The bmCBWFlags byte; bit 0x80 set means data-in (device to host, a READ).</summary>
        public byte Flags { get; private set; }
        /// <summary>The logical unit number (low nibble of the LUN field).</summary>
        public byte Lun { get; private set; }
        /// <summary>The valid CDB length, in bytes (low five bits of the length field).</summary>
        public byte CdbLength { get; private set; }
        /// <summary>The SCSI command descriptor block (16 bytes; only the first <see cref="CdbLength"/> are valid).</summary>
        public byte[] Cdb { get; private set; }

        /// <summary>True when this is a data-in command (device to host).</summary>
        public bool IsDataIn => (Flags & 0x80) != 0;
        /// <summary>The SCSI operation code (CDB byte 0), or 0 when there is no CDB.</summary>
        public byte Opcode => Cdb != null && Cdb.Length > 0 ? Cdb[0] : (byte)0;

        /// <summary>Parses a 31-byte CBW from a buffer at the given offset.</summary>
        /// <param name="p">The buffer holding the CBW.</param>
        /// <param name="offset">The offset of the CBW within <paramref name="p"/>.</param>
        /// <returns>The parsed wrapper.</returns>
        /// <exception cref="ArgumentException"><paramref name="p"/> holds fewer than 31 bytes at the offset.</exception>
        public static CommandBlockWrapper Parse(byte[] p, int offset = 0)
        {
            if (p == null || p.Length - offset < VmediaFraming.CbwSize)
                throw new ArgumentException("CBW must be 31 bytes.", nameof(p));

            var cbw = new CommandBlockWrapper
            {
                Signature = (uint)(p[offset] | (p[offset + 1] << 8) | (p[offset + 2] << 16) | (p[offset + 3] << 24)),
                Tag = (uint)(p[offset + 4] | (p[offset + 5] << 8) | (p[offset + 6] << 16) | (p[offset + 7] << 24)),
                DataTransferLength = (uint)(p[offset + 8] | (p[offset + 9] << 8) | (p[offset + 10] << 16) | (p[offset + 11] << 24)),
                Flags = p[offset + 12],
                Lun = (byte)(p[offset + 13] & 0x0F),
                CdbLength = (byte)(p[offset + 14] & 0x1F),
            };
            var cdb = new byte[16];
            Array.Copy(p, offset + 15, cdb, 0, 16);
            cbw.Cdb = cdb;
            return cbw;
        }
    }
}
