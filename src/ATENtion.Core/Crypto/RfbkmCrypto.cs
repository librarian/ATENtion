using System;
using System.Security.Cryptography;

namespace ATENtion.Core.Crypto
{
    /// <summary>
    /// The AES-128 cipher the ATEN protocol uses to encrypt input events, applied one block at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Encrypts a single sixteen-byte block under a 128-bit key, the operation the native
    /// input path uses to protect an encrypted mouse, keyboard, or privilege event.
    /// </para>
    /// <para>
    /// OPERATION - The cipher is AES in ECB mode with no chaining and no padding, so each call
    /// transforms exactly one block independently. The native encrypt entry is the same: a single
    /// sixteen-byte ECB block. The key is supplied by the caller through <see cref="SetKey"/>.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Built on the framework AES implementation. Used by <see cref="ATENtion.Core.Hid.MouseEncoder"/>
    /// when it builds an encrypted PointerEvent.
    /// </para>
    /// <para>
    /// RESTRICTIONS - The 128-bit key derivation from the session token is not yet reversed, so the
    /// key must be set explicitly. Until a key is set, <see cref="EncryptBlock"/> throws. The instance owns
    /// unmanaged cipher state and must be disposed. Not thread-safe.
    /// </para>
    /// <para>
    /// PROVENANCE - Port of the native RFBKMCryto object, whose encrypt entry is
    /// vtable[0x10](in16, out16, 0x10). The key derivation gap is
    /// RE-DERIVED, UNCONFIRMED.
    /// </para>
    /// </remarks>
    public sealed class RfbkmCrypto : IDisposable
    {
        /// <summary>The cipher block size, in bytes (128 bits).</summary>
        public const int BlockSize = 16;

        private Aes _aes;
        private ICryptoTransform _encryptor;

        /// <summary>True once a key has been set and the cipher is ready to encrypt.</summary>
        public bool HasKey => _encryptor != null;

        /// <summary>Sets the 128-bit key, replacing any previous key.</summary>
        /// <param name="key16">The sixteen-byte key.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key16"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="key16"/> is not sixteen bytes.</exception>
        public void SetKey(byte[] key16)
        {
            if (key16 == null) throw new ArgumentNullException(nameof(key16));
            if (key16.Length != BlockSize)
                throw new ArgumentException("RFBKMCryto uses a 128-bit (16-byte) key.", nameof(key16));

            _encryptor?.Dispose();
            _aes?.Dispose();

            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.KeySize = 128;
            _aes.Key = key16;
            _encryptor = _aes.CreateEncryptor();
        }

        /// <summary>Encrypts exactly one sixteen-byte block.</summary>
        /// <param name="plain16">The sixteen plaintext bytes.</param>
        /// <returns>A new sixteen-byte ciphertext block.</returns>
        /// <exception cref="InvalidOperationException">No key has been set.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="plain16"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="plain16"/> is not sixteen bytes.</exception>
        public byte[] EncryptBlock(byte[] plain16)
        {
            if (_encryptor == null) throw new InvalidOperationException("Key not set (see RfbkmCrypto.SetKey).");
            if (plain16 == null) throw new ArgumentNullException(nameof(plain16));
            if (plain16.Length != BlockSize)
                throw new ArgumentException("Input must be exactly 16 bytes.", nameof(plain16));

            var output = new byte[BlockSize];
            _encryptor.TransformBlock(plain16, 0, BlockSize, output, 0);
            return output;
        }

        /// <summary>Releases the cipher state.</summary>
        public void Dispose()
        {
            _encryptor?.Dispose();
            _aes?.Dispose();
        }
    }
}
