using System.Collections.Generic;
using ATENtion.Core.Net;
using ATENtion.Core.Video;

namespace ATENtion.Core.Protocol
{
    /// <summary>
    /// The result of consuming exactly one server-to-client message: what kind it was,
    /// any frame it produced, and any side information (resolution change, privilege grant).
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries back to the receive pump everything it needs to know about the
    /// message that was just read, without the pump having to re-inspect the stream.
    /// </para>
    /// <para>
    /// PROVENANCE - Field set follows the native pump handlers; see
    /// <see cref="ServerMessageReader"/> for the per-field origin.
    /// </para>
    /// </remarks>
    public struct ServerMessageResult
    {
        /// <summary>True when the message was a FramebufferUpdate that produced decoded pixels.</summary>
        public bool IsFrame;
        /// <summary>The regions changed by this frame. Populated only when <see cref="IsFrame"/> is true.</summary>
        public IReadOnlyList<DirtyRect> Dirty;
        /// <summary>The message-type byte that was consumed.</summary>
        public byte Type;
        /// <summary>Count of video payload bytes received for this message, for the bandwidth readout.</summary>
        public int PayloadBytes;
        /// <summary>
        /// True when this message resized the decode surface (a resolution change). The pump
        /// responds by requesting one full keyframe so the new-size surface is repainted.
        /// </summary>
        public bool Resized;
        /// <summary>True when this message was a privilege/control grant (message type 0x39).</summary>
        public bool HasPrivilege;
        /// <summary>
        /// Meaningful only when <see cref="HasPrivilege"/> is true: true if this session holds
        /// input control, false if it is view-only. See <see cref="ServerMessageReader"/> for the
        /// native control test, which is inverted relative to the obvious reading.
        /// </summary>
        public bool Controlling;
        /// <summary>
        /// Meaningful only when <see cref="HasPrivilege"/> is true: the server's role/session
        /// string, of the form <c>&lt;sid&gt; ROLE &lt;clientip&gt;</c>.
        /// </summary>
        public string PrivilegeInfo;
    }

    /// <summary>
    /// Reads a single server-to-client message from the RFB stream and consumes exactly its
    /// bytes, so the stream stays aligned for the next read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Dispatches on the leading message-type byte and consumes the body of that
    /// one message, decoding it when it is a FramebufferUpdate and otherwise skipping past it
    /// by the exact length the native handler would. Returns a <see cref="ServerMessageResult"/>
    /// describing what was read.
    /// </para>
    /// <para>
    /// OPERATION - The first byte selects the handler. Only the FramebufferUpdate (type 0) is
    /// decoded into pixels. The cursor, status, and privilege messages exist on the wire and
    /// must be drained byte-for-byte to keep the stream aligned, but their contents are not
    /// otherwise used. Each non-frame branch skips exactly the field count its native
    /// counterpart reads. An unrecognised type cannot be skipped, because its length is
    /// unknown, so it is a fatal protocol error.
    /// </para>
    /// <para>
    /// Within a FramebufferUpdate, a rectangle anchored at the top-left corner (x = 0, y = 0)
    /// carries the current screen resolution. This holds for both keyframes and incremental
    /// updates: an incremental rectangle reports the live resolution, not a dirty bounding box.
    /// The reader resizes the decoder when that resolution changes. A sanity test guards the
    /// resize, so heartbeat rectangles with nonsense dimensions cannot drive it.
    /// </para>
    /// <para>
    /// WIRE FORMAT - Selected message types and the bytes each consumes:
    /// <code>
    ///   0x00 FramebufferUpdate  decoded (see FramebufferUpdate.Read)
    ///   0x04 cursor shape       4 x u32, then a bitmap when the flag word is 1
    ///   0x16 status             1 byte
    ///   0x35 keyboard+mouse     5 bytes
    ///   0x37 mouse status       3 bytes
    ///   0x39 privilege grant    2 x u32 BE, then a 256-byte role string
    ///   0x3c screen status      8 bytes (2 x u32)
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads through a <see cref="BufferedRfbStream"/> and decodes frames into a
    /// caller-owned <see cref="AtenTileDecoder"/>, which it may resize. Frame bytes are parsed by
    /// <see cref="FramebufferUpdate"/>.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Consumes exactly one message per call. It is not thread-safe against
    /// concurrent reads on the same stream, and the receive pump is its sole caller. A decode
    /// failure on a single rectangle is logged and skipped, so one bad rectangle does not
    /// desynchronise the stream. An unknown message type is unrecoverable and throws.
    /// </para>
    /// <para>
    /// PROVENANCE - Native receive pump iKVM64.dll FUN_180012cb0 and its per-type handlers
    /// (FramebufferUpdate FUN_180013b00, cursor FUN_1800139c0, status FUN_180009830, privilege
    /// FUN_180011d80). VERIFIED LIVE against the target BMC.
    /// </para>
    /// </remarks>
    public static class ServerMessageReader
    {
        /// <summary>
        /// Reads and consumes one server message, decoding it when it is a FramebufferUpdate.
        /// </summary>
        /// <param name="s">The RFB stream, positioned at a message-type byte.</param>
        /// <param name="decoder">The decode surface; may be resized when the resolution changes.</param>
        /// <returns>A description of the message that was consumed.</returns>
        /// <exception cref="RfbProtocolException">The message-type byte is not recognised, so its
        /// length is unknown and the stream can no longer be kept aligned.</exception>
        public static ServerMessageResult ConsumeOne(BufferedRfbStream s, AtenTileDecoder decoder)
        {
            byte type = s.ReadU8();
            ATENtion.Core.Diagnostics.KvmLog.Write($"recv: message-type byte 0x{type:x2}");
            var result = new ServerMessageResult { Type = type };

            switch (type)
            {
                case RfbMessageType.FramebufferUpdate: // 0
                    var fbu = FramebufferUpdate.Read(s);
                    var dirty = new List<DirtyRect>();
                    foreach (var rect in fbu.Rects)
                    {
                        result.PayloadBytes += rect.Payload.Length;
                        if (rect.Payload.Length == 0) continue;
                        // A rectangle anchored at the top-left corner carries the current resolution,
                        // on both keyframes and incrementals (an incremental rect reports the live
                        // resolution, e.g. 800x600, not a dirty bounding box). Resize the decoder when
                        // it changes. The dimension test rejects heartbeat rectangles whose width and
                        // height are garbage (observed as 64896x65056), which must never drive a resize.
                        if (rect.X == 0 && rect.Y == 0 &&
                            rect.Width > 0 && rect.Width <= 4096 && rect.Height > 0 && rect.Height <= 4096 &&
                            (rect.Width != decoder.Width || rect.Height != decoder.Height))
                        {
                            ATENtion.Core.Diagnostics.KvmLog.Write($"  resizing decoder to {rect.Width}x{rect.Height}");
                            decoder.Resize(rect.Width, rect.Height);
                            result.Resized = true;
                        }
                        try
                        {
                            var d = decoder.DecodePacket(rect.Payload);
                            if (d != null) dirty.AddRange(d);
                            result.IsFrame = true;
                        }
                        catch (UnsupportedEncodingException ex)
                        {
                            // One rectangle used an encoding the decoder does not handle. Skip it
                            // rather than aborting: its bytes are already consumed, so the stream
                            // stays aligned and the remaining rectangles still decode.
                            ATENtion.Core.Diagnostics.KvmLog.Write("  decode skipped: " + ex.Message);
                        }
                    }
                    result.Dirty = dirty;
                    break;

                case RfbMessageType.ScreenUpdate4: // 4 - cursor shape (FUN_1800139c0)
                    s.ReadU32BE();                 // x
                    s.ReadU32BE();                 // y
                    uint cw = s.ReadU32BE();       // width
                    uint ch = s.ReadU32BE();       // height
                    uint flag = s.ReadU32BE();
                    if (flag == 1)
                    {
                        s.ReadU32BE();              // extra word, present only when the flag is set
                        s.Skip((int)(cw * ch * 2)); // cursor bitmap, two bytes per pixel
                    }
                    break;

                case RfbMessageType.Server0x16: s.Skip(1); break;          // FUN_180009830
                case RfbMessageType.Keyboard0x35: s.Skip(5); break;        // keyboard(2) + mouse(3) fields
                case RfbMessageType.Screen0x37: s.Skip(3); break;          // mouse status
                case RfbMessageType.Privilege0x39: // privilege/control grant (native FUN_180011d80)
                    {
                        // Two u32 words followed by a 256-byte "<sid> ROLE <clientip>" string. The native
                        // computes an input-enable flag as (b == 4 && a == 1) ? 0 : 1: the single case
                        // (a == 1 && b == 4) is the restricted, non-controlling branch, and EVERY other
                        // combination is controlling. The test is therefore inverted relative to the obvious
                        // reading - controlling = !(a == 1 && b == 4). Confirmed live: an in-control ADMIN
                        // session reports a = 1, b = 1, which is not the special case and so is controlling.
                        uint a = s.ReadU32BE();
                        uint b = s.ReadU32BE();
                        byte[] info = s.ReadExact(256);
                        int z = System.Array.IndexOf(info, (byte)0);
                        string text = System.Text.Encoding.ASCII.GetString(info, 0, z < 0 ? info.Length : z);
                        result.HasPrivilege = true;
                        result.Controlling = !(a == 1 && b == 4);
                        result.PrivilegeInfo = text.Trim();
                        ATENtion.Core.Diagnostics.KvmLog.Write(
                            $"  privilege 0x39: a={a} b={b} controlling={result.Controlling} info=\"{result.PrivilegeInfo}\"");
                        break;
                    }
                case RfbMessageType.Screen0x3c: s.Skip(8); break;          // 2 x u32

                default:
                    // The length of an unknown message is unknown, so the stream can no longer be
                    // advanced safely. Fail loudly rather than desynchronise.
                    throw new RfbProtocolException($"Unknown server message type 0x{type:x2}.");
            }

            return result;
        }
    }
}
