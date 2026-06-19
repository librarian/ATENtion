using System;
using System.IO;

namespace ATENtion.Core.Net
{
    /// <summary>
    /// A big-endian, buffered reader and writer over a transport stream, providing the field-level
    /// primitives the RFB protocol is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Reads and writes the u8, u16, u32, and byte-block primitives in big-endian order,
    /// the byte order RFB uses on the wire, over a NetworkStream or SslStream.
    /// </para>
    /// <para>
    /// OPERATION - RFB messages are read field by field, often a single byte or short header at a
    /// time. An unbuffered read per field would be one system call per field, so the read path is
    /// wrapped in a BufferedStream that batches reads from the transport. The transport is strictly
    /// request-response, so read-ahead never consumes bytes the write side still needs. The write
    /// path goes straight to the raw transport with an explicit flush. It is deliberately not routed
    /// through a BufferedStream wrapping the same underlying stream, because two buffers over one
    /// stream would desynchronise. The read primitives retry until the requested count is satisfied,
    /// matching the native receive loops.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Wraps any transport <see cref="Stream"/>. Used by the handshake, the receive
    /// pump, and the input send paths.
    /// </para>
    /// <para>
    /// RESTRICTIONS - A read and a write may proceed concurrently because they use separate buffers,
    /// but two concurrent reads, or two concurrent writes, are not safe. The session serialises
    /// writes on its own send lock. A read that reaches end of stream throws.
    /// </para>
    /// <para>
    /// PROVENANCE - Mirrors the native stream primitives: put u8 (FUN_180009960), put u16 BE
    /// (FUN_180009a10), put bytes (FUN_180009bc0), read u32 BE (FUN_180009880), and the
    /// recv-with-retry loops.
    /// </para>
    /// </remarks>
    public sealed class BufferedRfbStream
    {
        private readonly Stream _stream;       // write side: straight to the transport, flushed explicitly
        private readonly Stream _readStream;   // read side: a BufferedStream so each RFB field is not a syscall

        /// <summary>Wraps a transport stream for buffered, big-endian RFB access.</summary>
        /// <param name="stream">The underlying transport (NetworkStream or SslStream).</param>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public BufferedRfbStream(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            // The read path is buffered so reading a message field by field does not cost one syscall
            // per field. The transport is request-response, so the buffer's read-ahead never consumes
            // bytes the write side still needs. The write path stays on the raw transport with an
            // explicit Flush(). Routing writes through a second buffer over the same stream would
            // desynchronise the two.
            _readStream = new BufferedStream(stream, 65536);
        }

        // ---- writes (sent to the transport directly; TCP and the transport coalesce them) ----

        /// <summary>Writes one byte.</summary>
        /// <param name="value">The byte value (low eight bits used).</param>
        public void WriteU8(int value) => _stream.WriteByte((byte)value);

        /// <summary>Writes a 16-bit value big-endian.</summary>
        /// <param name="value">The value (low sixteen bits used).</param>
        public void WriteU16BE(int value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        /// <summary>Writes a 32-bit value big-endian.</summary>
        /// <param name="value">The value.</param>
        public void WriteU32BE(uint value)
        {
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        /// <summary>Writes an entire byte array.</summary>
        /// <param name="data">The bytes to write.</param>
        public void WriteBytes(byte[] data) => WriteBytes(data, 0, data.Length);

        /// <summary>Writes a span of a byte array.</summary>
        /// <param name="data">The source array.</param>
        /// <param name="offset">The starting offset.</param>
        /// <param name="count">The number of bytes to write.</param>
        public void WriteBytes(byte[] data, int offset, int count) => _stream.Write(data, offset, count);

        /// <summary>Writes a run of zero bytes.</summary>
        /// <param name="count">The number of zero bytes to write.</param>
        public void WriteZeros(int count)
        {
            for (int i = 0; i < count; i++) _stream.WriteByte(0);
        }

        /// <summary>Flushes buffered writes to the transport.</summary>
        public void Flush() => _stream.Flush();

        // ---- reads (each retries until the requested count is read, like the native loops) ----

        /// <summary>Reads one byte.</summary>
        /// <returns>The byte read.</returns>
        /// <exception cref="EndOfStreamException">The stream closed during the read.</exception>
        public byte ReadU8()
        {
            int b = _readStream.ReadByte();
            if (b < 0) throw new EndOfStreamException("Socket closed during read.");
            return (byte)b;
        }

        /// <summary>Reads a 16-bit value big-endian.</summary>
        /// <returns>The value read.</returns>
        public ushort ReadU16BE()
        {
            byte hi = ReadU8();
            byte lo = ReadU8();
            return (ushort)((hi << 8) | lo);
        }

        /// <summary>Reads a 32-bit value big-endian.</summary>
        /// <returns>The value read.</returns>
        public uint ReadU32BE()
        {
            byte[] b = ReadExact(4);
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        /// <summary>Reads exactly the requested number of bytes into a new array.</summary>
        /// <param name="count">The number of bytes to read.</param>
        /// <returns>A new array of the requested length.</returns>
        /// <exception cref="EndOfStreamException">The stream closed before the count was read.</exception>
        public byte[] ReadExact(int count)
        {
            var buffer = new byte[count];
            ReadExact(buffer, 0, count);
            return buffer;
        }

        /// <summary>Reads and discards a number of bytes.</summary>
        /// <param name="count">The number of bytes to skip; non-positive values do nothing.</param>
        public void Skip(int count)
        {
            if (count <= 0) return;
            var scratch = new byte[Math.Min(count, 4096)];
            int left = count;
            while (left > 0)
            {
                int chunk = Math.Min(left, scratch.Length);
                ReadExact(scratch, 0, chunk);
                left -= chunk;
            }
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes into a buffer, retrying short reads.</summary>
        /// <param name="buffer">The destination buffer.</param>
        /// <param name="offset">The offset in <paramref name="buffer"/> to write from.</param>
        /// <param name="count">The number of bytes to read.</param>
        /// <exception cref="EndOfStreamException">The stream closed before the count was read.</exception>
        public void ReadExact(byte[] buffer, int offset, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = _readStream.Read(buffer, offset + got, count - got);
                if (n <= 0) throw new EndOfStreamException("Socket closed during read.");
                got += n;
            }
        }
    }
}
