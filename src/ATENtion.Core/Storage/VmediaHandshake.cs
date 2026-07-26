using System;
using System.Security.Cryptography;
using System.Text;

namespace ATENtion.Core.Storage
{
    /// <summary>Builds the authenticated ATEN virtual-media attach and control records.</summary>
    /// <remarks>
    /// The BMC accepts an ISO only after a 246-byte device record. It contains the two temporary
    /// JNLP credentials in separate fixed fields, a USB CD-ROM descriptor, and an HMAC-SHA1 over
    /// the first 226 bytes keyed by the concatenated credentials.
    /// </remarks>
    public static class VmediaHandshake
    {
        public const int AttachRecordSize = 246;
        public const int AuthenticatedSize = 226;

        // Descriptor emitted by ATEN SharedLibrary64.dll for device slot 2 / "ISO Image".
        private static readonly byte[] CdRomDescriptor = Hex(
            "121201000200000040a00e111100020000000127090227000101008064" +
            "090400000308055000070501020002ff070582020002ff0705830302000104" +
            "0403090422220346006c0061007300680020004400690073006b0020002000" +
            "20002000200020002222033400450038004600300039003200430033004600" +
            "4400370046003800460037001a1a0353004e00300030003000500051004900" +
            "300030003900200001000a0a060002000000400100");

        /// <summary>Builds a control frame: big-endian type followed by LE payload length.</summary>
        public static byte[] BuildControlFrame(uint type, byte[] payload = null)
        {
            payload = payload ?? ScsiResult.NoData;
            var frame = new byte[8 + payload.Length];
            WriteU32Be(frame, 0, type);
            WriteU32Le(frame, 4, (uint)payload.Length);
            if (payload.Length > 0) Array.Copy(payload, 0, frame, 8, payload.Length);
            return frame;
        }

        /// <summary>Builds the vendor's device-slot-2 ISO attach record.</summary>
        public static byte[] BuildAttachRecord(string username, string password)
        {
            var timestamp = new byte[4];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(timestamp);
            uint value = (uint)(timestamp[0] | (timestamp[1] << 8) |
                                (timestamp[2] << 16) | (timestamp[3] << 24));
            return BuildAttachRecord(username, password, value);
        }

        internal static byte[] BuildAttachRecord(string username, string password, uint sessionTimestamp)
        {
            byte[] user = CredentialBytes(username, nameof(username));
            byte[] pass = CredentialBytes(password, nameof(password));
            var record = new byte[AttachRecordSize];

            // Native record prefix and its two separate 16-byte credential fields.
            record[1] = 0x80;
            record[3] = 0x01;
            record[4] = 0x2c;
            Array.Copy(user, 0, record, 8, user.Length);
            Array.Copy(pass, 0, record, 24, pass.Length);
            WriteU32Le(record, 44, sessionTimestamp);
            record[48] = 0x85; // one device, SID authentication, native slot 2
            record[49] = 0x03; // CD-ROM media
            Array.Copy(CdRomDescriptor, 0, record, 52, CdRomDescriptor.Length);

            byte[] key = Encoding.ASCII.GetBytes((username ?? "") + (password ?? ""));
            using (var hmac = new HMACSHA1(key))
            {
                byte[] digest = hmac.ComputeHash(record, 0, AuthenticatedSize);
                Array.Copy(digest, 0, record, AuthenticatedSize, digest.Length);
            }
            return record;
        }

        private static byte[] CredentialBytes(string value, string paramName)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("A temporary JNLP credential is required.", paramName);
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            if (bytes.Length > 15)
                throw new ArgumentException("ATEN virtual-media credentials must fit a 15-byte field.", paramName);
            return bytes;
        }

        private static byte[] Hex(string value)
        {
            var bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static void WriteU32Be(byte[] p, int o, uint value)
        {
            p[o] = (byte)(value >> 24);
            p[o + 1] = (byte)(value >> 16);
            p[o + 2] = (byte)(value >> 8);
            p[o + 3] = (byte)value;
        }

        private static void WriteU32Le(byte[] p, int o, uint value)
        {
            p[o] = (byte)value;
            p[o + 1] = (byte)(value >> 8);
            p[o + 2] = (byte)(value >> 16);
            p[o + 3] = (byte)(value >> 24);
        }
    }
}
