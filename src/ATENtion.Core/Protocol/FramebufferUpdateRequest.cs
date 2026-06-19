namespace ATENtion.Core.Protocol
{
    /// <summary>
    /// Builds the client-to-server FramebufferUpdateRequest (message type 3) that asks the BMC to
    /// send video.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Produces the ten-byte request the BMC requires before it sends any video. The
    /// request also serves as a keep-alive: without a steady stream of these, the BMC drops the
    /// idle connection.
    /// </para>
    /// <para>
    /// OPERATION - A non-incremental request asks for the whole region as a keyframe. An
    /// incremental request asks only for what changed since the last update. The session sends one
    /// non-incremental request after ServerInit to obtain the first full frame, then an
    /// incremental request after each update to keep frames flowing. All coordinate fields are
    /// big-endian.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   [3][incremental : u8][x : u16 BE][y : u16 BE][w : u16 BE][h : u16 BE]   = 10 bytes
    /// </code>
    /// </para>
    /// <para>
    /// PROVENANCE - Native request builder iKVM64.dll FUN_180013060 (RFBScreen), called each frame
    /// cycle by runImage/getDecodeImage. VERIFIED LIVE.
    /// </para>
    /// </remarks>
    public static class FramebufferUpdateRequest
    {
        /// <summary>The RFB client-to-server message type for a FramebufferUpdateRequest.</summary>
        public const byte MessageType = 3;

        /// <summary>Builds a request for the given region.</summary>
        /// <param name="incremental">True for an incremental update, false for a full keyframe.</param>
        /// <param name="x">The region's left edge, in pixels.</param>
        /// <param name="y">The region's top edge, in pixels.</param>
        /// <param name="width">The region's width, in pixels.</param>
        /// <param name="height">The region's height, in pixels.</param>
        /// <returns>A new ten-byte request frame.</returns>
        public static byte[] Build(bool incremental, int x, int y, int width, int height)
        {
            var f = new byte[10];
            f[0] = MessageType;
            f[1] = (byte)(incremental ? 1 : 0);
            f[2] = (byte)(x >> 8); f[3] = (byte)x;            // x, big-endian
            f[4] = (byte)(y >> 8); f[5] = (byte)y;            // y, big-endian
            f[6] = (byte)(width >> 8); f[7] = (byte)width;    // width, big-endian
            f[8] = (byte)(height >> 8); f[9] = (byte)height;  // height, big-endian
            return f;
        }
    }
}
