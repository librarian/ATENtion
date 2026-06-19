using System;
using System.IO;

namespace ATENtion.Core.Storage
{
    /// <summary>
    /// A read-only block view over a local ISO file, presented as 2048-byte CD-ROM sectors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Serves sectors from an ISO image to the virtual-media target. It reports the
    /// image's capacity in whole sectors and reads a requested run of sectors on demand.
    /// </para>
    /// <para>
    /// OPERATION - The image is opened read-only and shared, since it may stay mounted for a long
    /// session. Capacity is the file length rounded up to whole 2048-byte sectors. A read seeks to the
    /// requested logical block and returns exactly the requested number of sectors. A read that runs
    /// past the end of the file is zero-filled, matching a real CD reader, which returns the requested
    /// length even for a short trailing sector.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Backs <see cref="ScsiCdRomTarget"/>, whose READ CAPACITY and READ commands read
    /// through it.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Read-only. Reads serialise on an internal lock, since the seek-then-read pair
    /// must be atomic against other reads. The handle is held until the instance is disposed.
    /// </para>
    /// <para>
    /// PROVENANCE - The native reader seeks and reads the file directly (ReadFile/fread in iKVM64.dll
    /// FUN_1800071b0 / FUN_18000be40); this mirrors that with no driver.
    /// </para>
    /// </remarks>
    public sealed class IsoBlockSource : IDisposable
    {
        /// <summary>The CD-ROM logical block size; ISO-9660 images are always 2048-byte sectors.</summary>
        public const int BlockSize = 2048;

        private readonly FileStream _file;
        private readonly object _ioLock = new object();

        /// <summary>Opens an ISO image as a read-only block source.</summary>
        /// <param name="path">The path to the ISO file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null or empty.</exception>
        /// <exception cref="InvalidDataException">The image is empty.</exception>
        public IsoBlockSource(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            Path = path;
            // Read-only and shared: the image may stay mounted for a long session, so allow other
            // readers.
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            long len = _file.Length;
            // CD-ROM capacity is reported in whole sectors. Round up a trailing partial sector. Real
            // images are sector-aligned, but the rounding is defensive.
            TotalBlocks = (len + BlockSize - 1) / BlockSize;
            if (TotalBlocks == 0)
            {
                _file.Dispose(); // do not leak the handle when rejecting the image
                throw new InvalidDataException("ISO image is empty.");
            }
        }

        /// <summary>The path of the open image.</summary>
        public string Path { get; }

        /// <summary>The total number of addressable 2048-byte sectors.</summary>
        public long TotalBlocks { get; }

        /// <summary>The last addressable logical block address (<see cref="TotalBlocks"/> - 1), as READ CAPACITY(10) reports.</summary>
        public uint LastLba => (uint)(TotalBlocks - 1);

        /// <summary>The image length, in bytes.</summary>
        public long LengthBytes => _file.Length;

        /// <summary>
        /// Reads a run of sectors into a new buffer, zero-filling any part that lies past the end of
        /// the image.
        /// </summary>
        /// <remarks>
        /// The result is always exactly <paramref name="blockCount"/> * <see cref="BlockSize"/> bytes:
        /// a read past the end of the image returns zeros for the missing sectors, as a CD reader does
        /// for a short trailing sector.
        /// </remarks>
        /// <param name="lba">The starting logical block address.</param>
        /// <param name="blockCount">The number of sectors to read.</param>
        /// <returns>A new buffer of <paramref name="blockCount"/> sectors.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="lba"/> or
        /// <paramref name="blockCount"/> is negative.</exception>
        public byte[] ReadBlocks(long lba, int blockCount)
        {
            if (lba < 0) throw new ArgumentOutOfRangeException(nameof(lba));
            if (blockCount < 0) throw new ArgumentOutOfRangeException(nameof(blockCount));

            var buffer = new byte[checked(blockCount * BlockSize)];
            if (blockCount == 0) return buffer;

            long offset = lba * BlockSize;
            lock (_ioLock)
            {
                if (offset >= _file.Length) return buffer; // entirely past the end: all zeros
                _file.Seek(offset, SeekOrigin.Begin);
                int want = buffer.Length;
                int got = 0;
                while (got < want)
                {
                    int n = _file.Read(buffer, got, want - got);
                    if (n <= 0) break; // end of file: the remainder stays zero-filled
                    got += n;
                }
            }
            return buffer;
        }

        /// <summary>Closes the image file.</summary>
        public void Dispose() => _file?.Dispose();
    }
}
