using System;

namespace ATENtion.Core.Protocol
{
    /// <summary>Builds ATEN's image-quality request.</summary>
    /// <remarks>
    /// The vendor viewer calls this operation <c>changeScreenInfo(level, qualityMode)</c>.
    /// Its quality slider uses levels 0 through 11, while the two chroma modes are decimal
    /// 422 (<c>0x01a6</c>, YUV420/Normal) and 444 (<c>0x01bc</c>, YUV444/Enhanced Text).
    /// A plaintext capture of the vendor viewer verified the complete five-byte record.
    /// </remarks>
    public static class ScreenInfoRequest
    {
        public const ushort NormalMode = 422;
        public const ushort EnhancedTextMode = 444;
        public const byte MaximumQuality = 11;

        /// <summary>Builds <c>[0x32][0][quality][mode u16 BE]</c>.</summary>
        public static byte[] Build(byte quality, ushort mode)
        {
            if (quality > MaximumQuality)
                throw new ArgumentOutOfRangeException(nameof(quality), "Image quality must be from 0 through 11.");
            if (mode != NormalMode && mode != EnhancedTextMode)
                throw new ArgumentOutOfRangeException(nameof(mode), "Image mode must be 422 (Normal) or 444 (Enhanced Text).");

            return new[]
            {
                RfbMessageType.SetScreenInfo,
                (byte)0,
                quality,
                (byte)(mode >> 8),
                (byte)mode
            };
        }
    }
}
