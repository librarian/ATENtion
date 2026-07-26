using System;

namespace ATENtion.Core.Video
{
    /// <summary>The four-byte ATEN wrapper in front of an ASPEED/AJPG bitstream.</summary>
    internal readonly struct AspeedPacketHeader
    {
        internal const int Size = 4;
        internal const ushort Mode420Marker = 0x01a6;
        internal const ushort Mode444Marker = 0x01bc;

        internal AspeedPacketHeader(byte selector, byte advanceSelector, int mode420)
        {
            Selector = selector;
            AdvanceSelector = advanceSelector;
            Mode420 = mode420;
        }

        internal byte Selector { get; }
        internal byte AdvanceSelector { get; }
        internal int Mode420 { get; }

        internal static AspeedPacketHeader Parse(byte[] packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (packet.Length < Size + 8)
                throw new ArgumentException("ASPEED packet is too short.", nameof(packet));

            ushort marker = (ushort)((packet[2] << 8) | packet[3]);
            int mode420;
            if (marker == Mode420Marker) mode420 = 1;
            else if (marker == Mode444Marker) mode420 = 0;
            else
                throw new UnsupportedEncodingException(
                    $"Unknown ASPEED stream marker 0x{marker:x4}.", default(VideoPacketHeader));

            if (packet[0] > 11 || packet[1] > 11)
                throw new ArgumentException("ASPEED quality selector is outside the supported range 0..11.", nameof(packet));

            return new AspeedPacketHeader(packet[0], packet[1], mode420);
        }
    }
}
