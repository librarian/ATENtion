using System;
using ATENtion.Core.Crypto;

namespace ATENtion.Core.Hid
{
    /// <summary>
    /// Builds the ATEN/RFB PointerEvent record for a mouse move or click, in either the plaintext or
    /// the AES-encrypted wire form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Produces the client-to-server record that reports an absolute pointer position and
    /// button state. The plaintext form carries the fields in the clear. The encrypted form packs
    /// them into a single AES block.
    /// </para>
    /// <para>
    /// OPERATION - Both forms are message type 5 followed by an encryption flag. The plaintext record
    /// is a fixed eighteen bytes: the type, a zero flag, the button mask, the big-endian X and Y, and
    /// reserved padding. The encrypted record carries the button mask and coordinates plus eleven pad
    /// bytes in one sixteen-byte block, which is encrypted by <see cref="RfbkmCrypto"/> and emitted
    /// after the type and the encryption flag. Coordinates are absolute and big-endian.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   plaintext:  [5][0][button][x : u16 BE][y : u16 BE][11 x 00]              = 18 bytes
    ///   encrypted:  [5][1][ AES16( button, x : u16 BE, y : u16 BE, 11 pad ) ]    = 18 bytes
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - The encrypted form uses an <see cref="RfbkmCrypto"/> with a key already set. The
    /// pad bytes come from an injectable provider, a cryptographic RNG by default, so tests can make
    /// the encrypted output deterministic.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Stateless. The returned array is owned by the caller. The session sends the
    /// plaintext form. The encrypted form is provided for parity with the native channel.
    /// </para>
    /// <para>
    /// PROVENANCE - Native mouse channel iKVM64.dll FUN_180011700. The native mouseAction supplies
    /// {x, y, state, -wheel}, of which this encoder uses x, y, and the button/state byte. The plaintext form is VERIFIED LIVE.
    /// </para>
    /// </remarks>
    public sealed class MouseEncoder
    {
        /// <summary>The RFB client-to-server message type for a PointerEvent record.</summary>
        public const int MessageType = 5;

        private readonly Func<byte[]> _padProvider;

        /// <summary>Creates a mouse encoder, optionally with a deterministic pad provider for tests.</summary>
        /// <param name="padProvider">Supplies the eleven pad bytes for encrypted frames; defaults to a
        /// cryptographic RNG.</param>
        public MouseEncoder(Func<byte[]> padProvider = null)
        {
            _padProvider = padProvider ?? DefaultPad;
        }

        /// <summary>Builds the eighteen-byte plaintext PointerEvent record.</summary>
        /// <param name="x">The absolute X coordinate, in pixels.</param>
        /// <param name="y">The absolute Y coordinate, in pixels.</param>
        /// <param name="buttonMask">The button state mask.</param>
        /// <returns>A new eighteen-byte record.</returns>
        public byte[] BuildPlaintext(int x, int y, int buttonMask)
        {
            var frame = new byte[18];
            frame[0] = MessageType;
            frame[1] = 0; // not encrypted
            frame[2] = (byte)buttonMask;
            frame[3] = (byte)(x >> 8); // X, big-endian
            frame[4] = (byte)x;
            frame[5] = (byte)(y >> 8); // Y, big-endian
            frame[6] = (byte)y;
            // frame[7..17]: reserved padding, already zero.
            return frame;
        }

        /// <summary>Builds the encrypted PointerEvent record, packing the fields into one AES block.</summary>
        /// <param name="x">The absolute X coordinate, in pixels.</param>
        /// <param name="y">The absolute Y coordinate, in pixels.</param>
        /// <param name="buttonMask">The button state mask.</param>
        /// <param name="crypto">The cipher, with a key already set.</param>
        /// <returns>A new eighteen-byte record (type, flag, and the sixteen-byte ciphertext).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="crypto"/> is null.</exception>
        public byte[] BuildEncrypted(int x, int y, int buttonMask, RfbkmCrypto crypto)
        {
            if (crypto == null) throw new ArgumentNullException(nameof(crypto));

            var block = new byte[RfbkmCrypto.BlockSize];
            block[0] = (byte)buttonMask;
            block[1] = (byte)(x >> 8);
            block[2] = (byte)x;
            block[3] = (byte)(y >> 8);
            block[4] = (byte)y;

            byte[] pad = _padProvider();
            Buffer.BlockCopy(pad, 0, block, 5, RfbkmCrypto.BlockSize - 5);

            byte[] cipher = crypto.EncryptBlock(block);

            var frame = new byte[2 + RfbkmCrypto.BlockSize];
            frame[0] = MessageType;
            frame[1] = 1; // encrypted
            Buffer.BlockCopy(cipher, 0, frame, 2, RfbkmCrypto.BlockSize);
            return frame;
        }

        // Generates the eleven pad bytes for an encrypted block from a cryptographic RNG.
        private static byte[] DefaultPad()
        {
            var pad = new byte[RfbkmCrypto.BlockSize - 5];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(pad);
            return pad;
        }
    }
}
