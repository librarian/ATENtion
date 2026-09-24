using System.Threading;

namespace ATENtion.App
{
    /// <summary>The connection attempt a tab has in flight, and whether the tab has been closed.</summary>
    /// <remarks>
    /// Connecting runs on a background task and can take many seconds. Starting a new attempt
    /// cancels the previous one, and closing the tab cancels whatever is running, so an attempt that
    /// finishes late sees its token cancelled and discards what it built instead of attaching a
    /// session to a tab that has moved on. Used from the UI thread only.
    /// </remarks>
    internal sealed class ConnectAttempts
    {
        private CancellationTokenSource _current;

        /// <summary>True once the tab is closed. No attempt started after that can succeed.</summary>
        public bool IsClosed { get; private set; }

        /// <summary>Cancels any running attempt and returns the token for a new one.</summary>
        /// <returns>An already-cancelled token if the tab is closed.</returns>
        public CancellationToken Begin()
        {
            Cancel();
            if (IsClosed) return new CancellationToken(true);
            _current = new CancellationTokenSource();
            return _current.Token;
        }

        /// <summary>True while an attempt has been started and has neither finished nor been cancelled.</summary>
        public bool IsRunning => _current != null;

        /// <summary>Records that the attempt holding <paramref name="token"/> has finished.</summary>
        /// <remarks>Ignored for an attempt that has already been superseded.</remarks>
        public void Complete(CancellationToken token)
        {
            if (_current != null && _current.Token == token) _current = null;
        }

        /// <summary>Cancels the running attempt, if there is one.</summary>
        /// <remarks>
        /// The source is not disposed: the background task may still be registering on its token,
        /// which would throw after disposal. It holds no unmanaged resources until a wait handle is
        /// requested, and none is.
        /// </remarks>
        public void Cancel()
        {
            var current = _current;
            _current = null;
            current?.Cancel();
        }

        /// <summary>Marks the tab closed and cancels the running attempt.</summary>
        public void Close()
        {
            IsClosed = true;
            Cancel();
        }
    }
}
