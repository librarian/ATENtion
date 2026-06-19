using System;

namespace ATENtion.Core.Video
{
    /// <summary>
    /// The 256-entry colour palette the palette pixel modes index into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Holds 256 four-byte colour entries and resolves an index to its blue, green,
    /// red, and alpha components.
    /// </para>
    /// <para>
    /// OPERATION - Each entry is stored as {B, G, R, A}, the order the engine's pixel writer
    /// produces, so a palette lookup needs no channel reordering. The palette is replaced in one
    /// block from the 1024-byte region that follows the header in a palette packet.
    /// </para>
    /// <para>
    /// PROVENANCE - The native engine keeps this at state+0x30 as 256 x 4 bytes, loaded verbatim
    /// from the block after the header.
    /// </para>
    /// </remarks>
    public sealed class AtenPalette
    {
        /// <summary>The number of palette entries.</summary>
        public const int EntryCount = 256;
        /// <summary>The size of one palette entry, in bytes.</summary>
        public const int EntrySize = 4;
        /// <summary>The total palette size, in bytes (256 x 4 = 1024).</summary>
        public const int ByteSize = EntryCount * EntrySize;

        private readonly byte[] _entries = new byte[ByteSize];

        /// <summary>The blue component of the entry at the given index.</summary>
        /// <param name="index">The palette index, 0..255.</param>
        public byte B(int index) => _entries[index * EntrySize + 0];
        /// <summary>The green component of the entry at the given index.</summary>
        /// <param name="index">The palette index, 0..255.</param>
        public byte G(int index) => _entries[index * EntrySize + 1];
        /// <summary>The red component of the entry at the given index.</summary>
        /// <param name="index">The palette index, 0..255.</param>
        public byte R(int index) => _entries[index * EntrySize + 2];
        /// <summary>The alpha component of the entry at the given index.</summary>
        /// <param name="index">The palette index, 0..255.</param>
        public byte A(int index) => _entries[index * EntrySize + 3];

        /// <summary>Replaces the whole palette from a 1024-byte block.</summary>
        /// <param name="source">The buffer holding the palette block.</param>
        /// <param name="offset">The offset of the 1024-byte block within <paramref name="source"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="source"/> does not hold a full palette at the offset.</exception>
        public void Load(byte[] source, int offset)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (offset < 0 || offset + ByteSize > source.Length)
                throw new ArgumentException("Source does not contain a full 1024-byte palette.", nameof(source));
            Buffer.BlockCopy(source, offset, _entries, 0, ByteSize);
        }
    }
}
