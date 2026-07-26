using System;
using System.Runtime.InteropServices;

namespace ATENtion.Core.Video
{
    /// <summary>Managed wrapper for ASPEED's MPL-2.0 reference AJPG decoder.</summary>
    internal static class AspeedNativeDecoder
    {
        // .NET Framework preserves the existing explicit Windows DLL lookup. Modern
        // .NET applies the platform suffix: .so on Linux and .dylib on macOS.
#if NETFRAMEWORK
        private const string LibraryName = "aspeed_codec.dll";
#else
        private const string LibraryName = "aspeed_codec";
#endif
        private static readonly object Sync = new object();
        private static bool _initialized;

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "aspeed_init")]
        private static extern void NativeInit();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "aspeed_decode")]
        private static extern void NativeDecode(
            IntPtr input, int inputLength, IntPtr output, int width, int height,
            uint mode420, uint selector, uint advanceSelector);

        internal static void VerifyAvailable()
        {
            lock (Sync)
            {
                EnsureInitialized();
            }
        }

        internal static void Decode(byte[] packet, FrameBuffer frame)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.Width > 1920 || frame.Height > 1200)
                throw new UnsupportedEncodingException(
                    $"ASPEED reference decoder supports at most 1920x1200, not {frame.Width}x{frame.Height}.",
                    default(VideoPacketHeader));

            AspeedPacketHeader header = AspeedPacketHeader.Parse(packet);
            GCHandle inputHandle = default(GCHandle);
            GCHandle outputHandle = default(GCHandle);

            lock (Sync)
            {
                try
                {
                    EnsureInitialized();

                    inputHandle = GCHandle.Alloc(packet, GCHandleType.Pinned);
                    outputHandle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
                    NativeDecode(
                        IntPtr.Add(inputHandle.AddrOfPinnedObject(), AspeedPacketHeader.Size),
                        packet.Length - AspeedPacketHeader.Size,
                        outputHandle.AddrOfPinnedObject(),
                        frame.Width,
                        frame.Height,
                        (uint)header.Mode420,
                        header.Selector,
                        header.AdvanceSelector);
                }
                finally
                {
                    if (outputHandle.IsAllocated) outputHandle.Free();
                    if (inputHandle.IsAllocated) inputHandle.Free();
                }
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            NativeInit();
            _initialized = true;
        }
    }
}
