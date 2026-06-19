using ATENtion.Core.Crypto;
using ATENtion.Core.Hid;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Verifies the AES cipher and the byte layouts of the input and request records.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Pins down that <see cref="RfbkmCrypto"/> matches the FIPS-197 AES-128 known-answer
    /// vector, that the plaintext and encrypted mouse records carry their fields in the expected
    /// positions (the encrypted block round-trips on decryption), and that the keyboard KeyEvent and
    /// FramebufferUpdateRequest records have the exact on-wire layout.
    /// </para>
    /// <para>
    /// PROVENANCE - Covers the input wire formats.
    /// </para>
    /// </remarks>
    public class CryptoHidTests
    {
        // FIPS-197 AES-128 known-answer vector.
        [Fact]
        public void Aes128_Block_Matches_Fips197_Vector()
        {
            byte[] key = HexToBytes("000102030405060708090a0b0c0d0e0f");
            byte[] plain = HexToBytes("00112233445566778899aabbccddeeff");
            byte[] expected = HexToBytes("69c4e0d86a7b0430d8cdb78070b4c55a");

            using (var c = new RfbkmCrypto())
            {
                c.SetKey(key);
                Assert.Equal(expected, c.EncryptBlock(plain));
            }
        }

        [Fact]
        public void Mouse_Plaintext_Frame_Layout()
        {
            var enc = new MouseEncoder();
            byte[] f = enc.BuildPlaintext(0x1234, 0x5678, buttonMask: 0x04);

            Assert.Equal(18, f.Length);
            Assert.Equal(5, f[0]);     // message type
            Assert.Equal(0, f[1]);     // not encrypted
            Assert.Equal(0x04, f[2]);  // button
            Assert.Equal(0x12, f[3]);  // x hi
            Assert.Equal(0x34, f[4]);  // x lo
            Assert.Equal(0x56, f[5]);  // y hi
            Assert.Equal(0x78, f[6]);  // y lo
            for (int i = 7; i < 18; i++) Assert.Equal(0, f[i]);
        }

        [Fact]
        public void Mouse_Encrypted_Frame_RoundTrips_Block()
        {
            byte[] key = HexToBytes("000102030405060708090a0b0c0d0e0f");
            byte[] fixedPad = new byte[11]; // deterministic pad for the test
            for (int i = 0; i < fixedPad.Length; i++) fixedPad[i] = (byte)(0xA0 + i);

            using (var crypto = new RfbkmCrypto())
            {
                crypto.SetKey(key);
                var enc = new MouseEncoder(() => fixedPad);
                byte[] f = enc.BuildEncrypted(0x1234, 0x5678, buttonMask: 0x02, crypto);

                Assert.Equal(18, f.Length);
                Assert.Equal(5, f[0]);
                Assert.Equal(1, f[1]); // encrypted

                // Decrypt the 16-byte block and confirm the field layout.
                byte[] cipher = new byte[16];
                System.Buffer.BlockCopy(f, 2, cipher, 0, 16);
                byte[] block = DecryptBlock(key, cipher);

                Assert.Equal(0x02, block[0]); // button
                Assert.Equal(0x12, block[1]); // x hi
                Assert.Equal(0x34, block[2]); // x lo
                Assert.Equal(0x56, block[3]); // y hi
                Assert.Equal(0x78, block[4]); // y lo
                for (int i = 0; i < 11; i++) Assert.Equal(fixedPad[i], block[5 + i]);
            }
        }

        [Fact]
        public void FramebufferUpdateRequest_Layout()
        {
            byte[] f = ATENtion.Core.Protocol.FramebufferUpdateRequest.Build(true, 0, 0, 0x0280, 0x01E0);
            Assert.Equal(10, f.Length);
            Assert.Equal(3, f[0]);        // type
            Assert.Equal(1, f[1]);        // incremental
            Assert.Equal(0x02, f[6]); Assert.Equal(0x80, f[7]); // width 640 BE
            Assert.Equal(0x01, f[8]); Assert.Equal(0xE0, f[9]); // height 480 BE
        }

        [Fact]
        public void Keyboard_KeyEvent_Frame_Layout()
        {
            var enc = new KeyboardEncoder();
            byte[] f = enc.BuildKeyEvent(0x0041_BEEF, down: true);

            Assert.Equal(18, f.Length);
            Assert.Equal(4, f[0]);     // message type 4 (KeyEvent)
            Assert.Equal(0, f[1]);
            Assert.Equal(1, f[2]);     // down
            Assert.Equal(0, f[3]);
            Assert.Equal(0, f[4]);
            Assert.Equal(0x00, f[5]);  // keysym u32 BE
            Assert.Equal(0x41, f[6]);
            Assert.Equal(0xBE, f[7]);
            Assert.Equal(0xEF, f[8]);
            for (int i = 9; i < 18; i++) Assert.Equal(0, f[i]);
        }

        private static byte[] DecryptBlock(byte[] key, byte[] cipher)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Mode = System.Security.Cryptography.CipherMode.ECB;
                aes.Padding = System.Security.Cryptography.PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = key;
                using (var dec = aes.CreateDecryptor())
                {
                    var outBuf = new byte[16];
                    dec.TransformBlock(cipher, 0, 16, outBuf, 0);
                    return outBuf;
                }
            }
        }

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
