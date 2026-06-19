using System.Text;
using ATENtion.Core.Net;

namespace ATENtion.Core.Protocol
{
    /// <summary>
    /// The RFB ServerInit block: framebuffer dimensions, pixel format, and desktop name, plus
    /// the ATEN-specific trailer that follows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Reads the ServerInit message that closes the handshake and exposes its fields.
    /// </para>
    /// <para>
    /// OPERATION - The standard ServerInit is a width, a height, a pixel-format block, and a
    /// length-prefixed desktop name. The ATEN firmware appends a twelve-byte trailer after the
    /// name (four bytes, a u32, four bytes) that the native privilege-channel setup consumes.
    /// Those twelve bytes are read and discarded. If they are not consumed, every subsequent
    /// message is twelve bytes out of phase and the stream is unrecoverable.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   [width : u16 BE][height : u16 BE][pixel format : 16 bytes]
    ///   [name length : u32 BE][name : UTF-8 x length]
    ///   [ATEN trailer : 12 bytes]
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads through a <see cref="BufferedRfbStream"/>. The pixel-format block is
    /// parsed by <see cref="Protocol.PixelFormat"/>.
    /// </para>
    /// <para>
    /// RESTRICTIONS - <see cref="Width"/> and <see cref="Height"/> are a placeholder, frequently
    /// a portrait 480x640. The native reader loads them into a scratch buffer and immediately
    /// overwrites it. The real resolution arrives with the first video keyframe (the full-screen
    /// rectangle, the native changeScreenInfo). These fields must not be treated as the display
    /// resolution. The decoder may be sized from them as an initial guess only, since it resizes
    /// on the first keyframe.
    /// </para>
    /// <para>
    /// PROVENANCE - Native ServerInit reader iKVM64.dll FUN_180012550.
    /// VERIFIED LIVE, including the placeholder-dimension behaviour and the twelve-byte trailer.
    /// </para>
    /// </remarks>
    public sealed class ServerInit
    {
        /// <summary>Placeholder framebuffer width; see the type's restrictions, not the display width.</summary>
        public int Width { get; private set; }
        /// <summary>Placeholder framebuffer height; see the type's restrictions, not the display height.</summary>
        public int Height { get; private set; }
        /// <summary>The server's pixel format as declared at ServerInit.</summary>
        public PixelFormat PixelFormat { get; private set; }
        /// <summary>The desktop name reported by the server.</summary>
        public string Name { get; private set; }

        /// <summary>Reads a ServerInit block, including the ATEN trailer, from the stream.</summary>
        /// <param name="stream">The handshake stream, positioned at the ServerInit width field.</param>
        /// <returns>The parsed ServerInit.</returns>
        public static ServerInit Read(BufferedRfbStream stream)
        {
            var init = new ServerInit
            {
                Width = stream.ReadU16BE(),
                Height = stream.ReadU16BE(),
                PixelFormat = PixelFormat.Read(stream),
            };

            uint nameLength = stream.ReadU32BE();
            byte[] nameBytes = stream.ReadExact((int)nameLength);
            init.Name = Encoding.UTF8.GetString(nameBytes);

            // ATEN ServerInit trailer: after the desktop name the server sends four bytes, a u32,
            // and four more bytes, consumed by the native privilege-channel setup. These twelve
            // bytes must be read or the message loop runs twelve bytes out of phase.
            byte[] ext = stream.ReadExact(12);
            Diagnostics.KvmLog.Write("ServerInit ATEN tail (12 bytes): " + Diagnostics.KvmLog.Hex(ext));
            return init;
        }
    }
}
