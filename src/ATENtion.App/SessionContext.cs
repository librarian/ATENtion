using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ATENtion.App.Video;
using ATENtion.Core.Net;
using System.Windows.Media;

namespace ATENtion.App
{
    /// <summary>
    /// Everything belonging to one console session, so several can be open at once as tabs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OPERATION - Exactly one context is active; the window mirrors it into its controls. Status
    /// and overlay text are recorded here rather than written straight to a control, so a background
    /// session can update them and have them replayed when its tab is selected.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Read and written on the UI thread. Session callbacks arrive on background
    /// threads and must be marshalled first. <see cref="ReconnectTimer"/>'s Tag carries this context
    /// so the shared tick handler knows which session fired.
    /// </para>
    /// <para>
    /// LIFETIME - The context owns everything its tab starts: the connection attempt, the session
    /// and its threads, mounted media and the reconnect timer. <see cref="Dispose"/> ends all of them
    /// and marks the tab closed, so work still in flight for it discards its result.
    /// </para>
    /// </remarks>
    public sealed class SessionContext : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>The renderer for this tab's framebuffer. One per session, never shared.</summary>
        public WpfFrameRenderer Renderer { get; } = new WpfFrameRenderer();

        /// <summary>The live session, or null while disconnected.</summary>
        public KvmVideoSession Session;

        /// <summary>The ISO currently served to this session, or null.</summary>
        public VirtualMediaSession Vmedia;
        public EventHandler<Exception> VmFaulted;
        public EventHandler VmClosed;
        // The session whose console-session list KnownSessions came from, and that list; null until
        // the first list arrives. Used to announce users joining and leaving.
        internal KvmVideoSession SessionListOwner;
        internal IReadOnlyList<string> KnownSessions;

        // The BMC notice last reported for NoticeOwner, so each is shown once.
        internal KvmVideoSession NoticeOwner;
        internal PrivilegeRecordType? ShownNotice;

        /// <summary>True while an ISO is attaching in the background.</summary>
        public bool Mounting;
        /// <summary>Moves on whenever media is cleared, so a mount that finishes afterwards is dropped.</summary>
        public int MediaGeneration;

        // Connection details, kept so a dropped link can be re-established without prompting.
        public KvmConnectionOptions ConnectOptions;
        public ConnectSettings ConnectProfile;
        public bool ArmViaWeb;
        public string BmcUser;
        public string BmcPassword;

        /// <summary>Auto-reconnect timer for this session alone. Tag is this context.</summary>
        public readonly DispatcherTimer ReconnectTimer = new DispatcherTimer();
        public int ReconnectAttempts;

        /// <summary>The connection attempt in flight, and whether this tab has been closed.</summary>
        internal readonly ConnectAttempts Attempts = new ConnectAttempts();

        /// <summary>True once the tab is closed.</summary>
        public bool IsClosed => Attempts.IsClosed;

        // Presentation state: the snapshot the UI thread paints from, and the regions that changed.
        public byte[] Present;
        public int PresentW, PresentH, PresentStride;
        public readonly List<Int32Rect> PresentDirty = new List<Int32Rect>();
        public bool PresentFull;
        public bool RenderPending;
        public readonly object PresentLock = new object();

        // Connection/overlay state.
        public bool LiveConnected;
        public bool UserDisconnected;
        /// <summary>Report a failed first connection on the tab only, not in a dialog.</summary>
        /// <remarks>Set for tabs reopened at startup, where one dialog per unreachable server would stack.</remarks>
        public bool QuietFailures;
        public DateTime? ConnectedAt;
        public int NoFrameTicks;
        public int ButtonMask;
        /// <summary>Where the pointer last was on this host, so a held button can be released in place.</summary>
        public int LastMouseX, LastMouseY;

        // Status-bar and overlay text for this tab, replayed when it becomes visible.
        public string StatusText = "Ready";
        public System.Windows.Media.Brush StatusBrush = StatusColors.Neutral;
        public string InfoText = "";
        public string StatsText = "";
        public string OverlayText = "";
        public bool OverlayVisible;

        // Per session, not per window: each tab may be a different server on a different link, so
        // the bandwidth trade belongs to the connection.
        public int FullFrameInterval = 5;
        public ushort ImageMode = ATENtion.Core.Protocol.ScreenInfoRequest.EnhancedTextMode;
        public byte ImageQuality = ATENtion.Core.Protocol.ScreenInfoRequest.MaximumQuality;

        // Throughput bookkeeping for the status line.
        public long LastFrames, LastBytes, LastFps, LastRateBytes;

        private bool _powerOn;
        private string _powerTip = PowerStatus.Tip(VideoSignalState.Unknown, null);

        /// <summary>Host state as far as the video signal reveals it.</summary>
        public bool PowerOn => _powerOn;

        /// <summary>Sets the glyph's state and how long ago it was confirmed.</summary>
        /// <param name="checkedAgo">Null while the tab streams; the age of the last check while paused.</param>
        public void UpdatePower(HostPowerState power, VideoSignalState signal, TimeSpan? checkedAgo)
        {
            bool on = PowerStatus.IsOn(power, signal);
            if (_powerOn != on)
            {
                _powerOn = on;
                Raise(nameof(PowerOn));
                Raise(nameof(PowerBrush));
            }
            string tip = PowerStatus.Tip(power, signal, checkedAgo);
            if (tip != _powerTip)
            {
                _powerTip = tip;
                Raise(nameof(PowerTip));
            }
        }

        /// <summary>Green when the host is definitely on, grey otherwise.</summary>
        /// <remarks>
        /// Two colours, not three: only "on" is certain. No picture is usually a powered-down host
        /// but equally matches one running without video output, so red would overstate it.
        /// </remarks>
        public Brush PowerBrush =>
            _powerOn ? StatusColors.Controlling : PowerUnknownBrush;

        public string PowerTip => _powerTip;

        // Lifted off the tab strip's #252526 so it reads as "no picture", not a missing glyph.
        private static readonly Brush PowerUnknownBrush = MakeFrozen(0x55, 0x55, 0x55);

        private static Brush MakeFrozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();   // shared across threads and tabs
            return brush;
        }

        private string _displayName = "New session";

        /// <summary>The tab caption: the host name, or a placeholder before the first connect.</summary>
        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; Raise(nameof(DisplayName)); } }
        }

        /// <summary>A short state marker shown next to the caption, for tabs that are not visible.</summary>
        public string TabState
        {
            get
            {
                if (Session == null) return Attempts.IsRunning ? "…" : "–";
                if (!LiveConnected) return "…";
                return Vmedia != null ? "● CD" : "●";
            }
        }

        public void RaiseTabState() => Raise(nameof(TabState));

        /// <summary>Names the tab after the saved profile, falling back to the host.</summary>
        /// <remarks>Keeps tabs narrow; the address stays available in <see cref="TabTip"/>.</remarks>
        public void UpdateDisplayName()
        {
            string name = ConnectProfile?.Name;
            string host = ConnectOptions?.Host;
            DisplayName = !string.IsNullOrWhiteSpace(name) ? name.Trim()
                        : !string.IsNullOrWhiteSpace(host) ? host
                        : "New session";
            Raise(nameof(TabTip));
        }

        /// <summary>Ends everything this tab started and marks it closed.</summary>
        /// <remarks>
        /// Handlers are detached before the media session is disposed so its closing does not report
        /// back into a tab that no longer exists. The KVM session's handlers check
        /// <see cref="IsClosed"/> instead, since they are lambdas with no field to unsubscribe.
        /// </remarks>
        public void Dispose()
        {
            Attempts.Close();
            ReconnectTimer.Stop();

            var media = Vmedia;
            Vmedia = null;
            if (media != null)
            {
                if (VmFaulted != null) media.Faulted -= VmFaulted;
                if (VmClosed != null) media.Closed -= VmClosed;
                DisposeInBackground(media);
            }
            VmFaulted = null;
            VmClosed = null;

            var session = Session;
            Session = null;
            EndSession(session, WebLogin);
            WebLogin = null;
            LiveConnected = false;
        }

        /// <summary>The BMC web login that armed <see cref="Session"/>, or null for a manual token.</summary>
        internal ArmingResult WebLogin;

        /// <summary>Closes a console session, then the web login that armed it, off the UI thread.</summary>
        /// <remarks>
        /// The BMC ties a console to the web login that armed it: logging out tells it the console
        /// has ended, and the vendor viewer closes when told so. The login is therefore kept for as
        /// long as the console runs and closed afterwards, so reconnects do not leave web sessions
        /// behind. A mounted disc is unaffected: tested on an X10 board, it kept serving after the
        /// login that armed it was logged out.
        /// </remarks>
        internal static void EndSession(KvmVideoSession session, ArmingResult login)
        {
            if (session == null && login == null) return;
            DisposeInBackground(new Ending(session, login));
        }

        private sealed class Ending : IDisposable
        {
            private readonly KvmVideoSession _session;
            private readonly ArmingResult _login;
            public Ending(KvmVideoSession session, ArmingResult login) { _session = session; _login = login; }
            public void Dispose()
            {
                try { _session?.Dispose(); } catch { }
                BmcArmingClient.Logout(_login);
            }
        }

        // Every session disposal still running, so the window can wait for them briefly on exit.
        private static readonly List<System.Threading.Tasks.Task> PendingDisposals = new List<System.Threading.Tasks.Task>();

        /// <summary>Disposes off the UI thread, which would otherwise wait while the threads stop.</summary>
        internal static void DisposeInBackground(IDisposable item)
        {
            var task = System.Threading.Tasks.Task.Run(() => { try { item.Dispose(); } catch { } });
            lock (PendingDisposals)
            {
                PendingDisposals.RemoveAll(t => t.IsCompleted);
                PendingDisposals.Add(task);
            }
        }

        /// <summary>Waits up to <paramref name="timeout"/> for background disposals to finish.</summary>
        /// <remarks>Used on exit, so an unmount reaches the BMC before the process ends.</remarks>
        internal static void WaitForDisposals(TimeSpan timeout)
        {
            System.Threading.Tasks.Task[] pending;
            lock (PendingDisposals) pending = PendingDisposals.ToArray();
            try { System.Threading.Tasks.Task.WaitAll(pending, timeout); } catch { }
        }

        /// <summary>Hover text for the tab: the profile name and the address it resolves to.</summary>
        public string TabTip
        {
            get
            {
                string host = ConnectOptions?.Host;
                if (string.IsNullOrWhiteSpace(host)) return "Not connected";
                int port = ConnectOptions?.Port ?? 0;
                string endpoint = port > 0 ? host + ":" + port : host;
                string name = ConnectProfile?.Name;
                return string.IsNullOrWhiteSpace(name) ? endpoint : name.Trim() + "  -  " + endpoint;
            }
        }
    }
}
