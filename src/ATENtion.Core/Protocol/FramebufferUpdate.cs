using System.Collections.Generic;
using ATENtion.Core.Net;

namespace ATENtion.Core.Protocol
{
    /// <summary>One changed rectangle within a FramebufferUpdate, with its codec payload.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the geometry and encoding of a single rectangle plus the raw ATEN codec
    /// packet for it, for the decoder to consume.
    /// </para>
    /// <para>
    /// PROVENANCE - Rectangle layout from the native FramebufferUpdate handler. The payload begins
    /// with the ten-byte codec header described.
    /// </para>
    /// </remarks>
    public sealed class FramebufferRect
    {
        /// <summary>The rectangle's left, top, width, and height, in pixels.</summary>
        public int X, Y, Width, Height;
        /// <summary>The encoding identifier for this rectangle.</summary>
        public uint Encoding;
        /// <summary>The codec mode word for this rectangle.</summary>
        public uint Mode;
        /// <summary>The ATEN codec packet for this rectangle (it begins with the ten-byte codec header).</summary>
        public byte[] Payload;
    }

    /// <summary>
    /// A server FramebufferUpdate message (type 0): a count of rectangles, each with geometry,
    /// encoding, and a codec payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Parses the FramebufferUpdate envelope into a list of <see cref="FramebufferRect"/>
    /// for the decoder, consuming exactly the message's bytes.
    /// </para>
    /// <para>
    /// OPERATION - A one-byte pad and a rectangle count are followed by that many rectangle
    /// records. A count of zero is a valid "nothing changed" update, not an error: the BMC sends
    /// these continuously while the console image is static. The parser therefore reads the count
    /// and loops, rather than assuming a single rectangle, so an empty update keeps the stream
    /// aligned instead of desynchronising it.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   [pad : u8][numRects : u16 BE]
    ///   per rect: [x : u16][y : u16][w : u16][h : u16]
    ///             [encoding : u32][mode : u32][dataLen : u32][payload : dataLen bytes]
    ///   (all multi-byte fields big-endian)
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads through a <see cref="BufferedRfbStream"/>. The decoding of each
    /// payload is the caller's concern (see <see cref="ServerMessageReader"/>).
    /// </para>
    /// <para>
    /// PROVENANCE - Native FramebufferUpdate handler iKVM64.dll FUN_180013b00. VERIFIED LIVE: the framing, including the empty-update case, matches the target BMC.
    /// </para>
    /// </remarks>
    public sealed class FramebufferUpdate
    {
        /// <summary>The number of rectangles this update declared.</summary>
        public int RectCount { get; private set; }
        /// <summary>The parsed rectangles, in the order the server sent them.</summary>
        public List<FramebufferRect> Rects { get; } = new List<FramebufferRect>();

        /// <summary>Reads and parses one FramebufferUpdate message from the stream.</summary>
        /// <param name="stream">The stream, positioned just after the message-type byte.</param>
        /// <returns>The parsed update, possibly with zero rectangles.</returns>
        public static FramebufferUpdate Read(BufferedRfbStream stream)
        {
            var u = new FramebufferUpdate();
            stream.ReadU8();                       // padding
            u.RectCount = stream.ReadU16BE();      // number of rectangles

            for (int i = 0; i < u.RectCount; i++)
            {
                var r = new FramebufferRect
                {
                    X = stream.ReadU16BE(),
                    Y = stream.ReadU16BE(),
                    Width = stream.ReadU16BE(),
                    Height = stream.ReadU16BE(),
                    Encoding = stream.ReadU32BE(),
                    Mode = stream.ReadU32BE(),
                };
                uint dataLen = stream.ReadU32BE();
                Diagnostics.KvmLog.Write(
                    $"  rect[{i}] ({r.X},{r.Y} {r.Width}x{r.Height}) enc=0x{r.Encoding:x8} mode=0x{r.Mode:x8} dataLen={dataLen}");
                r.Payload = dataLen == 0 ? new byte[0] : stream.ReadExact((int)dataLen);
                u.Rects.Add(r);
            }
            return u;
        }
    }
}
