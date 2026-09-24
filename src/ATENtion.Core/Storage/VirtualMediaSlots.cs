using System;
using System.Collections.Generic;

namespace ATENtion.Core.Storage
{
    /// <summary>Which of the BMC's virtual-media slots hold media, from its health reply.</summary>
    /// <remarks>
    /// The reply to a health exchange (control type 8, answered with 9) needs no credentials. Its
    /// payload is a slot count followed by one byte per slot: 0xFF for empty, otherwise the media
    /// type. On an X10 board it read 03 FF FF FF with nothing mounted and 03 FF 03 FF while another
    /// session had a CD in slot 2, the slot this client attaches to.
    /// </remarks>
    public sealed class VirtualMediaSlots
    {
        /// <summary>The slot this client's attach record claims.</summary>
        public const int OwnSlot = 2;

        private const byte Empty = 0xFF;
        private readonly byte[] _media;

        private VirtualMediaSlots(byte[] media) { _media = media; }

        /// <summary>Number of slots the BMC reported.</summary>
        public int Count => _media.Length;

        /// <summary>True if the given slot (1-based) holds media.</summary>
        public bool InUse(int slot) => slot >= 1 && slot <= _media.Length && _media[slot - 1] != Empty;

        /// <summary>True if any slot holds media.</summary>
        public bool AnyInUse => Array.Exists(_media, m => m != Empty);

        /// <summary>Reads a health reply; null if it does not have the expected shape.</summary>
        public static VirtualMediaSlots Parse(byte[] payload)
        {
            if (payload == null || payload.Length < 1) return null;
            int count = payload[0];
            if (count < 1 || payload.Length < 1 + count) return null;
            var media = new byte[count];
            Array.Copy(payload, 1, media, 0, count);
            return new VirtualMediaSlots(media);
        }
    }

    /// <summary>The BMC refused to attach an image, with the status it gave.</summary>
    public sealed class VirtualMediaRefusedException : Exception
    {
        public VirtualMediaRefusedException(int status, string message) : base(message) { Status = status; }

        /// <summary>The BMC's status code, or -1 when refused before asking.</summary>
        public int Status { get; }

        /// <summary>Status 2: seen when another session already had media attached.</summary>
        public const int Busy = 2;

        internal static VirtualMediaRefusedException ForStatus(int status) => new VirtualMediaRefusedException(status,
            status == Busy
                ? "The BMC refused the disc because another session already has virtual media attached. " +
                  "Only one can be mounted at a time."
                : $"The BMC refused the virtual-media attach (status {status}).");

        internal static VirtualMediaRefusedException SlotTaken() => new VirtualMediaRefusedException(-1,
            "Another session already has a disc mounted on this BMC. Only one can be mounted at a time; " +
            "it must be unmounted there first.");
    }
}
