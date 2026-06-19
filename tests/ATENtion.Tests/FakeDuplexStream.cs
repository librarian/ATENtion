using System.IO;

namespace ATENtion.Tests
{
    /// <summary>
    /// A test transport stream: reads come from a scripted server-to-client buffer, and writes are
    /// captured in a separate client-to-server buffer for inspection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Stands in for a socket so a test can drive the full handshake and message exchange
    /// without a network, feeding the code a scripted server response and recording exactly what the
    /// client wrote.
    /// </para>
    /// <para>
    /// OPERATION - The constructor takes the bytes the server would send. Reads draw from those. Writes
    /// accumulate in a second buffer exposed by <see cref="Written"/>. Keeping the two directions on
    /// separate buffers means a read-ahead on the read side cannot consume or reorder the captured
    /// writes, which the buffered-stream tests rely on.
    /// </para>
    /// </remarks>
    internal sealed class FakeDuplexStream : Stream
    {
        private readonly MemoryStream _incoming;
        private readonly MemoryStream _outgoing = new MemoryStream();

        /// <summary>Creates the stream with the bytes the server is scripted to send.</summary>
        /// <param name="serverToClient">The bytes a read will draw from.</param>
        public FakeDuplexStream(byte[] serverToClient)
        {
            _incoming = new MemoryStream(serverToClient);
        }

        /// <summary>The bytes the client has written so far, for assertion.</summary>
        public byte[] Written => _outgoing.ToArray();

        /// <summary>Reads from the scripted server-to-client buffer.</summary>
        public override int Read(byte[] buffer, int offset, int count) => _incoming.Read(buffer, offset, count);
        /// <summary>Captures a client write into the client-to-server buffer.</summary>
        public override void Write(byte[] buffer, int offset, int count) => _outgoing.Write(buffer, offset, count);

        /// <summary>Always true; the stream is readable.</summary>
        public override bool CanRead => true;
        /// <summary>Always true; the stream is writable.</summary>
        public override bool CanWrite => true;
        /// <summary>Always false; the stream is not seekable.</summary>
        public override bool CanSeek => false;
        /// <summary>No-op; writes are captured immediately.</summary>
        public override void Flush() { }
        /// <summary>The length of the scripted server-to-client buffer.</summary>
        public override long Length => _incoming.Length;
        /// <summary>Unused position; the underlying buffers track their own offsets.</summary>
        public override long Position { get; set; }
        /// <summary>No-op; the stream does not support seeking.</summary>
        public override long Seek(long offset, SeekOrigin origin) => 0;
        /// <summary>No-op; the stream does not support resizing.</summary>
        public override void SetLength(long value) { }
    }
}
