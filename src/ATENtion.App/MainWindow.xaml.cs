using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ATENtion.App.Video;
using ATENtion.Core.Net;
using ATENtion.Core.Protocol;
using ATENtion.Core.Video;

namespace ATENtion.App
{
    /// <summary>
    /// The application's main window: it drives the connection lifecycle, presents decoded video,
    /// forwards keyboard and mouse input, and exposes the power, virtual-media, and key-macro actions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Hosts the console view and orchestrates everything around it: opening and tearing
    /// down a <see cref="KvmVideoSession"/>, auto-reconnecting after a drop, presenting frames, sending
    /// input, controlling chassis power, mounting an ISO as virtual media, taking screenshots, and
    /// persisting and restoring the window state.
    /// </para>
    /// <para>
    /// OPERATION - A background task performs the connect and handshake so the UI stays responsive. The
    /// session raises decoded frames on its pump thread. The window copies only the changed tiles into a
    /// present buffer and posts an asynchronous present, so the pump never blocks on the UI thread (a
    /// blocked pump would stop draining the socket and stall input). A one-second timer updates the
    /// status bar and drives the "waiting for video" overlay, and a reconnect timer re-establishes a
    /// dropped link. Input is captured through the tunneling Preview key events so navigation keys are
    /// not consumed by focus handling before they can be forwarded.
    /// </para>
    /// <para>
    /// DEPENDENCIES - A <see cref="KvmVideoSession"/> for the live connection, a
    /// <see cref="WpfFrameRenderer"/> for presentation, a <see cref="VirtualMediaSession"/> for mounted
    /// ISOs, the <see cref="ConnectWindow"/>, <see cref="SessionInfoWindow"/>,
    /// <see cref="CustomKeysWindow"/>, and <see cref="AboutWindow"/> dialogs, and
    /// <see cref="KeySymMap"/> and <see cref="HostKeys"/> for key translation.
    /// </para>
    /// <para>
    /// RESTRICTIONS - All UI work runs on the WPF thread. Session events that arrive on background
    /// threads are marshalled back with the dispatcher. The window must not block the pump thread, which
    /// is why the present path snapshots and returns rather than rendering inline.
    /// </para>
    /// </remarks>
    public partial class MainWindow : Window
    {
        // Every open tab. Exactly one is active; the rest keep running and reconnecting unseen.
        private readonly System.Collections.ObjectModel.ObservableCollection<SessionContext> _sessions =
            new System.Collections.ObjectModel.ObservableCollection<SessionContext>();
        private SessionContext _ctx = new SessionContext();

        // The per-session state below now lives on the active SessionContext. These forwarding
        // members keep the window's existing call sites reading as they did when there was only one
        // session, while actually operating on whichever tab is selected.
        private WpfFrameRenderer _renderer => _ctx.Renderer;
        private KvmVideoSession _session { get => _ctx.Session; set => _ctx.Session = value; }
        private VirtualMediaSession _vmedia { get => _ctx.Vmedia; set => _ctx.Vmedia = value; }
        private int _buttonMask { get => _ctx.ButtonMask; set => _ctx.ButtonMask = value; }

        private readonly DispatcherTimer _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private long _lastFrames { get => _ctx.LastFrames; set => _ctx.LastFrames = value; }
        private long _lastBytes { get => _ctx.LastBytes; set => _ctx.LastBytes = value; }
        private long _lastFps { get => _ctx.LastFps; set => _ctx.LastFps = value; }
        private long _lastRateBytes { get => _ctx.LastRateBytes; set => _ctx.LastRateBytes = value; }

        // "Waiting for video" overlay state. _liveConnected = handshake done. The stats timer then
        // owns the overlay (show after a short grace if no frames arrive, hide once they flow).
        // While connecting/reconnecting/disconnected the connection-state code owns the overlay text.
        private bool _liveConnected { get => _ctx.LiveConnected; set => _ctx.LiveConnected = value; }
        private bool _userDisconnected { get => _ctx.UserDisconnected; set => _ctx.UserDisconnected = value; }
        private bool _certHintShown;    // one-shot: only pop the expired-cert/clock hint dialog once per run
        private DateTime? _connectedAt { get => _ctx.ConnectedAt; set => _ctx.ConnectedAt = value; }
        private int _noFrameTicks { get => _ctx.NoFrameTicks; set => _ctx.NoFrameTicks = value; }

        /// <summary>Seconds without a decoded frame before the "Waiting for video" overlay shows.</summary>
        /// <remarks>
        /// Tracks the request interval: a frame cannot arrive more often than one is asked for, so a
        /// fixed grace would show the overlay for most of every interval.
        /// </remarks>
        private int WaitGraceTicks =>
            _fullFrameInterval == ContinuousRefresh ? 2 : Math.Max(2, _fullFrameInterval + 1);

        // Persistent connection-state line, plus a transient "flash" that reverts to it.
        private readonly DispatcherTimer _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private string _baseStatus = "Ready";
        private System.Windows.Media.Brush _baseBrush = StateNeutral;

        // Status-line state colors (the left status line is a colored connection-state indicator).
        // Single source of truth lives in StatusColors (shared with the Session Info dialog).
        private static readonly System.Windows.Media.Brush StateGreen = StatusColors.Controlling;
        private static readonly System.Windows.Media.Brush StateOrange = StatusColors.ViewOnly;
        private static readonly System.Windows.Media.Brush StateRed = StatusColors.Disconnected;
        private static readonly System.Windows.Media.Brush StateNeutral = StatusColors.Neutral;

        // Auto-reconnect: remembers the last connection so a dropped link can be re-established.
        private DispatcherTimer _reconnectTimer => _ctx.ReconnectTimer;
        private int _reconnectAttempts { get => _ctx.ReconnectAttempts; set => _ctx.ReconnectAttempts = value; }
        // Quick retries for a blip, then a slower pace that still outlasts a BMC reboot (a few
        // minutes) instead of giving up after the first minute.
        private const int MaxReconnectAttempts = 30;
        private static int ReconnectDelaySeconds(int attempt) => attempt <= 5 ? 5 : 30;
        private KvmConnectionOptions _connectOptions { get => _ctx.ConnectOptions; set => _ctx.ConnectOptions = value; }
        private ConnectSettings _connectProfile { get => _ctx.ConnectProfile; set => _ctx.ConnectProfile = value; }
        private bool _armViaWeb { get => _ctx.ArmViaWeb; set => _ctx.ArmViaWeb = value; }
        private string _bmcUser { get => _ctx.BmcUser; set => _ctx.BmcUser = value; }
        private string _bmcPassword { get => _ctx.BmcPassword; set => _ctx.BmcPassword = value; }

        // BMC mouse mode (parity with the ATEN client). See MouseMode.cs for the on-wire values.
        private MouseMode _mouseMode = MouseMode.Absolute;

        /// <summary>Builds the window, restores the saved UI state, and starts the timers.</summary>
        public MainWindow()
        {
            InitializeComponent();
            SetupLogging();
            RestoreUi();
            Loaded += OnLoaded;
            Closed += OnClosed;
            Deactivated += OnDeactivated;
            _statsTimer.Tick += OnStatsTick;
            _statsTimer.Start();
            _flashTimer.Tick += (s, e) => { _flashTimer.Stop(); StatusText.Text = _baseStatus; StatusText.Foreground = _baseBrush; };
            RegisterContext(_ctx);
            SessionTabs.ItemsSource = _sessions;
            SessionTabs.SelectedItem = _ctx;
        }

        // ---- tabs ----

        /// <summary>Adds a context to the tab strip and gives it its own reconnect timer.</summary>
        private SessionContext RegisterContext(SessionContext ctx)
        {
            ctx.ReconnectTimer.Tag = ctx;             // OnReconnectTick reads this to know whose tick it is
            ctx.ReconnectTimer.Tick += OnReconnectTick;
            if (!_sessions.Contains(ctx)) _sessions.Add(ctx);
            return ctx;
        }

        private void OnNewSessionTab(object sender, RoutedEventArgs e)
        {
            var ctx = RegisterContext(new SessionContext
            {
                // Inherit from the tab this was opened from rather than resetting to the defaults.
                FullFrameInterval = _ctx.FullFrameInterval,
                ImageMode = _ctx.ImageMode,
                ImageQuality = _ctx.ImageQuality,
            });
            SessionTabs.SelectedItem = ctx;           // raises OnSessionTabChanged, which swaps _ctx
            ShowConnectDialog();
        }

        private void OnSessionTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SessionTabs.SelectedItem is SessionContext ctx) SwitchTo(ctx);
        }

        /// <summary>Makes a tab the visible one. Every route to a different tab comes through here.</summary>
        /// <remarks>
        /// Input still held on the tab being left is released there: its key-up and button-up
        /// events will now go to the new tab's host, and the old host would otherwise keep the key
        /// down. Streaming is then brought into line for every tab.
        /// </remarks>
        private void SwitchTo(SessionContext next)
        {
            if (next == null || ReferenceEquals(next, _ctx)) return;
            var leaving = _ctx;
            if (leaving != null) ReleaseHeldInput(leaving);
            if (VideoImage.IsMouseCaptured) VideoImage.ReleaseMouseCapture();
            _ctx = next;
            if (!ReferenceEquals(SessionTabs.SelectedItem, next)) SessionTabs.SelectedItem = next;
            ApplyTabStreaming();
            ActivateContext();
        }

        private void OnToggleStreamAllTabs(object sender, RoutedEventArgs e) => ApplyTabStreaming();

        // Bring every session into line with the toggle: all streaming, or only the visible one.
        private void ApplyTabStreaming()
        {
            foreach (var c in _sessions)
                if (c.Session != null) ApplyStreaming(c, c.Session);
        }

        /// <summary>Sets whether a session streams or only checks in, from the UI thread.</summary>
        private void ApplyStreaming(SessionContext ctx, KvmVideoSession session)
        {
            session.PausedCheckSeconds = HiddenTabCheckSeconds;
            session.MinFrameIntervalMs = _frameRateLimit > 0 ? 1000 / _frameRateLimit : 0;
            session.VideoPaused = !StreamAllTabsItem.IsChecked && !ReferenceEquals(ctx, _ctx);
        }

        /// <summary>Seconds between the checks a hidden tab makes on its host.</summary>
        /// <remarks>
        /// A few bytes on a BMC that reports host status, a full frame on one that does not, so the
        /// glyph stays current and a dropped link is noticed.
        /// </remarks>
        private const int HiddenTabCheckSeconds = 10;

        // ---- frame rate limit ----

        /// <summary>Most frames a second a tab asks for; 0 for no limit.</summary>
        private int _frameRateLimit;

        private IEnumerable<KeyValuePair<int, MenuItem>> FrameRateItems()
        {
            yield return new KeyValuePair<int, MenuItem>(0, FpsUnlimitedItem);
            yield return new KeyValuePair<int, MenuItem>(60, Fps60Item);
            yield return new KeyValuePair<int, MenuItem>(30, Fps30Item);
            yield return new KeyValuePair<int, MenuItem>(10, Fps10Item);
        }

        private void OnFrameRateLimit(object sender, RoutedEventArgs e)
        {
            if (!(sender is MenuItem item) || !int.TryParse(item.Tag as string, out int fps)) return;
            _frameRateLimit = fps;
            SyncFrameRateMenu();
            ApplyTabStreaming();
            FlashStatus(fps > 0 ? $"Frame rate limited to {fps} fps." : "Frame rate: unlimited.");
        }

        private void SyncFrameRateMenu()
        {
            foreach (var entry in FrameRateItems())
                entry.Value.IsChecked = entry.Key == _frameRateLimit;
        }

        /// <summary>Replays the newly selected session's state into the window's shared controls.</summary>
        private void ActivateContext()
        {
            var ctx = _ctx;
            SetStatus(ctx.StatusText, ctx.StatusBrush);
            StatsText.Text = ctx.StatsText;
            InfoText.Text = ctx.InfoText;
            if (ctx.OverlayVisible) ShowOverlay(ctx.OverlayText); else HideOverlay();
            RefreshStorageMenu(ctx);
            // Re-tick for the tab being shown. Sync only - it already has these values.
            SyncFullRefreshMenu();
            SyncDisplayPreferenceMenu();
            RepaintActive();
            UpdateTitle();
        }

        // ---- tab reordering ----

        private SessionContext _dragTab;
        private Point _dragOrigin;
        private bool _draggingTab;

        // The press is handled here rather than by the ListBox. On a click the ListBox captures the
        // mouse and then selects every item the pointer crosses while the button is held, which would
        // switch sessions as a tab is dragged past its neighbours.
        private void OnTabPointerDown(object sender, MouseButtonEventArgs e)
        {
            ResetTabDrag();
            var source = e.OriginalSource as DependencyObject;
            if (FindAncestor<ButtonBase>(source) != null) return;     // close and "+" buttons
            if (!(FindAncestor<ListBoxItem>(source)?.DataContext is SessionContext tab)) return;

            SessionTabs.SelectedItem = tab;
            _dragTab = tab;
            _dragOrigin = e.GetPosition(SessionTabs);
            e.Handled = true;
        }

        private void OnTabPointerMove(object sender, MouseEventArgs e)
        {
            if (_dragTab == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { ResetTabDrag(); return; }

            Point position = e.GetPosition(SessionTabs);
            if (!_draggingTab)
            {
                if (Math.Abs(position.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance)
                    return;
                // Capture on the strip: a tab's own container can be regenerated when the collection
                // moves, which would drop the capture mid-drag.
                if (!TabStrip.CaptureMouse()) { ResetTabDrag(); return; }
                _draggingTab = true;
            }

            int from = _sessions.IndexOf(_dragTab);
            var midpoints = OtherTabMidpoints(from);
            if (from < 0 || midpoints == null) return;

            int to = TabReorder.TargetIndex(midpoints, position.X);
            if (to == from) return;
            _sessions.Move(from, to);
            if (!ReferenceEquals(SessionTabs.SelectedItem, _dragTab)) SessionTabs.SelectedItem = _dragTab;
            SessionTabs.UpdateLayout();
        }

        private void OnTabPointerUp(object sender, MouseButtonEventArgs e) => ResetTabDrag();

        private void OnTabLostCapture(object sender, MouseEventArgs e) => ResetTabDrag();

        private void ResetTabDrag()
        {
            bool release = _draggingTab;
            _dragTab = null;
            _draggingTab = false;
            if (release && TabStrip.IsMouseCaptured) TabStrip.ReleaseMouseCapture();
        }

        // Midpoints of every tab except the one at skipIndex, in the ListBox's coordinates. Null if a
        // container is not laid out yet.
        private List<double> OtherTabMidpoints(int skipIndex)
        {
            var midpoints = new List<double>(_sessions.Count);
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (i == skipIndex) continue;
                if (!(SessionTabs.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement item) ||
                    !item.IsVisible)
                    return null;
                Rect bounds = item.TransformToAncestor(SessionTabs).TransformBounds(new Rect(item.RenderSize));
                midpoints.Add(bounds.Left + bounds.Width / 2);
            }
            return midpoints;
        }

        private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            while (node != null && !(node is T))
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            return node as T;
        }

        private void OnCloseSessionTab(object sender, RoutedEventArgs e)
        {
            var ctx = (sender as FrameworkElement)?.Tag as SessionContext;
            if (ctx == null) return;
            CloseContext(ctx);
        }

        private void CloseContext(SessionContext ctx)
        {
            if (ReferenceEquals(_ctx, ctx))
            {
                // Move off the tab first, through the normal path, so the replacement resumes
                // streaming and the closing tab's held input is released before it goes.
                SessionContext next;
                if (_sessions.Count == 1)
                {
                    // Always keep one tab: closing the last session leaves an empty one to connect from.
                    next = RegisterContext(new SessionContext
                    {
                        FullFrameInterval = ctx.FullFrameInterval,
                        ImageMode = ctx.ImageMode,
                        ImageQuality = ctx.ImageQuality,
                    });
                }
                else
                {
                    int index = _sessions.IndexOf(ctx);
                    next = _sessions[TabReorder.NeighbourAfterClose(index, _sessions.Count)];
                }
                SwitchTo(next);
            }

            ctx.ReconnectTimer.Tick -= OnReconnectTick;
            ctx.Dispose();
            _sessions.Remove(ctx);
        }

        private void RestoreUi()
        {
            var s = UiSettings.Load();
            // Geometry - only if it lands on the visible virtual desktop.
            if (!double.IsNaN(s.Width) && !double.IsNaN(s.Height) && s.Width > 200 && s.Height > 150)
            {
                Width = s.Width; Height = s.Height;
                if (!double.IsNaN(s.Left) && !double.IsNaN(s.Top) && OnScreen(s.Left, s.Top, s.Width, s.Height))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = s.Left; Top = s.Top;
                }
            }
            if (s.Maximized) WindowState = WindowState.Maximized;

            // View prefs (setting IsChecked fires the same handlers as the menu).
            EnableLoggingItem.IsChecked = s.EnableLogging; // fires OnToggleLogging -> KvmLog.Enabled
            ShowLogItem.IsChecked = s.ShowLog;
            ActualSizeItem.IsChecked = s.ActualSize;
            SmoothScalingItem.IsChecked = s.SmoothScaling; // fires OnToggleSmoothScaling
            AutoReconnectItem.IsChecked = s.AutoReconnect;
            ReopenTabsItem.IsChecked = s.ReopenTabs;
            // Relative(2)/Single(3) are disabled (they need cursor capture the client does not do yet),
            // so always start in Absolute regardless of any stale saved value - the BMC must never be
            // sent a mode whose coordinates the client cannot produce correctly.
            _mouseMode = MouseMode.Absolute;
            UpdateMouseModeMenu();
            _fullFrameInterval = s.FullFrameIntervalSeconds;
            ApplyFullRefreshInterval();
            _imageMode = s.ImageMode == ScreenInfoRequest.NormalMode
                ? ScreenInfoRequest.NormalMode : ScreenInfoRequest.EnhancedTextMode;
            _imageQuality = s.ImageQuality > ScreenInfoRequest.MaximumQuality
                ? ScreenInfoRequest.MaximumQuality : (byte)s.ImageQuality;
            ApplyDisplayPreference();
            StreamAllTabsItem.IsChecked = s.StreamAllTabs;
            _frameRateLimit = FrameRateItems().Any(i => i.Key == s.FrameRateLimit) ? s.FrameRateLimit : 0;
            SyncFrameRateMenu();
            ApplyTabStreaming();
            OnToggleLog(null, null);
            OnFitModeChanged(null, null);
            OnToggleSmoothScaling(null, null);
        }

        private static bool OnScreen(double l, double t, double w, double h)
        {
            double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            // Require the title-bar area to be within the virtual screen so the window stays grabbable.
            return l + w > vx + 40 && l < vx + vw - 40 && t >= vy - 1 && t < vy + vh - 40;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _statsTimer.Stop();
            _logFlushTimer.Stop();
            _reconnectTimer.Stop();
            _flashTimer.Stop();
            try { SaveUi(); }
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("saving settings on exit", ex); }
            // Tear down every tab, not just the visible one.
            foreach (var ctx in _sessions) ctx.Dispose();
            // Give the disposals a moment, so each mounted disc is released at the BMC rather than
            // dropped mid-read when the process ends.
            SessionContext.WaitForDisposals(TimeSpan.FromSeconds(3));
        }

        private void SaveUi()
        {
            var s = new UiSettings
            {
                Maximized = WindowState == WindowState.Maximized,
                ShowLog = ShowLogItem.IsChecked,
                ActualSize = ActualSizeItem.IsChecked,
                SmoothScaling = SmoothScalingItem.IsChecked,
                AutoReconnect = AutoReconnectItem.IsChecked,
                EnableLogging = EnableLoggingItem.IsChecked,
                MouseMode = (int)_mouseMode,
                FullFrameIntervalSeconds = _fullFrameInterval,
                ImageMode = _imageMode,
                ImageQuality = _imageQuality,
                StreamAllTabs = StreamAllTabsItem.IsChecked,
                FrameRateLimit = _frameRateLimit,
                ReopenTabs = ReopenTabsItem.IsChecked,
                OpenProfileIds = _sessions
                    .Select(c => c.ConnectProfile?.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToList(),
                ActiveProfileId = _ctx.ConnectProfile?.Id ?? "",
            };
            // RestoreBounds is the normal-state rect in every window state (incl. maximized).
            var r = RestoreBounds;
            if (!r.IsEmpty)
            {
                s.Left = r.Left; s.Top = r.Top; s.Width = r.Width; s.Height = r.Height;
            }
            s.Save();
        }

        /// <summary>Set the persistent status line (generic text, neutral color). Cancels any flash.</summary>
        private void SetStatus(string text) => SetStatus(text, StateNeutral);

        /// <summary>Set the persistent status line with an explicit color. Cancels any pending flash.</summary>
        private void SetStatus(string text, System.Windows.Media.Brush brush)
        {
            _baseStatus = text;
            _baseBrush = brush;
            _flashTimer.Stop();
            StatusText.Text = text;
            StatusText.Foreground = brush;
        }

        // Context-aware variants. A background tab records its state here and the window replays it
        // when that tab is selected, so an unseen session can reconnect without hijacking the UI.
        private void SetStatus(SessionContext ctx, string text, System.Windows.Media.Brush brush)
        {
            ctx.StatusText = text;
            ctx.StatusBrush = brush;
            ctx.RaiseTabState();
            if (ReferenceEquals(ctx, _ctx)) SetStatus(text, brush);
        }

        private void ShowOverlay(SessionContext ctx, string text)
        {
            ctx.OverlayText = text;
            ctx.OverlayVisible = true;
            if (ReferenceEquals(ctx, _ctx)) ShowOverlay(text);
        }

        private void HideOverlay(SessionContext ctx)
        {
            ctx.OverlayVisible = false;
            if (ReferenceEquals(ctx, _ctx)) HideOverlay();
        }

        private void SetStats(SessionContext ctx, string text)
        {
            ctx.StatsText = text;
            if (ReferenceEquals(ctx, _ctx)) StatsText.Text = text;
        }

        /// <summary>Show a temporary message that reverts to the persistent status after a few seconds.</summary>
        private void FlashStatus(string text) => FlashStatus(text, TimeSpan.FromSeconds(4));

        private void FlashStatus(string text, TimeSpan duration)
        {
            StatusText.Text = text;
            _flashTimer.Stop();
            _flashTimer.Interval = duration;
            _flashTimer.Start();
        }

        /// <summary>Show the centered "no video" overlay with the given message.</summary>
        private void ShowOverlay(string text)
        {
            WaitOverlayText.Text = text;
            WaitOverlay.Visibility = Visibility.Visible;
        }

        private void HideOverlay() => WaitOverlay.Visibility = Visibility.Collapsed;

        /// <summary>Fired once per second after the status bar updates - the Session Info dialog
        /// subscribes to refresh itself live while open.</summary>
        internal event EventHandler StatusTick;

        private void OnStatsTick(object sender, EventArgs e)
        {
            StatusTick?.Invoke(this, EventArgs.Empty);
            foreach (var c in _sessions) c.RaiseTabState();
            RefreshPowerStates();
            var ctx = _ctx;
            var s = ctx.Session;
            if (s == null)
            {
                ctx.StatsText = ""; ctx.InfoText = "";
                StatsText.Text = ""; InfoText.Text = "";
                return;
            }
            long frames = s.FramesDecoded, bytes = s.VideoBytes;
            long df = frames - _lastFrames, db = bytes - _lastBytes;
            _lastFrames = frames; _lastBytes = bytes;
            if (df < 0) df = 0; if (db < 0) db = 0;
            _lastFps = df; _lastRateBytes = db; // expose to the Session Info dialog snapshot
            // Right: live throughput + cumulative session totals.
            ctx.StatsText = $"{df} fps · {StatusFormat.Rate(db)} · {frames:n0} frames · {StatusFormat.Size(bytes)} total";
            StatsText.Text = ctx.StatsText;

            // Center: connection details (host:port, transport, resolution, mouse mode, uptime) + a
            // connection-health note when video has stalled (frames stopped flowing while connected).
            if (_connectOptions != null)
            {
                string tls = _connectOptions.UseTls ? "TLS" : "plain";
                string res = (s.Decoder != null && s.Decoder.Width > 0)
                    ? $"{s.Decoder.Width}x{s.Decoder.Height}" : "-";
                string up = _connectedAt.HasValue ? StatusFormat.Uptime(DateTime.Now - _connectedAt.Value) : "-";
                string health = "";
                if (_liveConnected && frames > 0 && s.LastFrameUtc != default(DateTime))
                {
                    var age = DateTime.UtcNow - s.LastFrameUtc;
                    if (age.TotalSeconds >= 3) health = $" · ⚠ stale {StatusFormat.Age(age)}";
                }
                string media = _vmedia != null ? $" · CD {System.IO.Path.GetFileName(_vmedia.ImagePath)}" : "";
                ctx.InfoText = $"{_connectOptions.Host}:{_connectOptions.Port} · {tls} · {res} · up {up}{media}{health}";
                InfoText.Text = ctx.InfoText;
            }

            // Once handshaken, drive the overlay off frame flow: show "Waiting for video..." after a
            // short grace with no frames, hide it as soon as frames arrive. (Connecting/reconnecting/
            // disconnected states set their own overlay text and clear _liveConnected.)
            if (_liveConnected)
            {
                if (df > 0) { _noFrameTicks = 0; HideOverlay(ctx); }
                else if (++_noFrameTicks >= WaitGraceTicks) ShowOverlay(ctx, NoPictureText(s));
            }
        }

        // Why nothing is on screen, as far as the stream says.
        private static string NoPictureText(KvmVideoSession s)
        {
            if (s.LastDecodeError != null) return "The video from this BMC can't be decoded: " + s.LastDecodeError;
            if (s.VideoSignal == Core.Net.VideoSignalState.Absent)
                return "No video signal - the host is off, or on but not sending video.";
            return "Waiting for video...";
        }

        // Read straight off the video stream (KvmVideoSession.VideoSignal): no dependency on Redfish
        // or IPMI being available. A hidden tab keeps it current with its periodic check.
        private void RefreshPowerStates()
        {
            foreach (var ctx in _sessions)
            {
                var s = ctx.Session;
                bool live = s != null && ctx.LiveConnected;
                var power = PowerStatus.Displayed(s != null, ctx.LiveConnected, s?.HostPower ?? HostPowerState.Unknown);
                var signal = PowerStatus.Displayed(s != null, ctx.LiveConnected,
                    s?.VideoSignal ?? Core.Net.VideoSignalState.Unknown);
                // A hidden tab's reading is only as fresh as its last check.
                TimeSpan? age = null;
                if (live && s.VideoPaused)
                {
                    DateTime seen = s.HostStatusSupported ? s.HostPowerUtc : s.VideoSignalUtc;
                    if (seen != default(DateTime)) age = DateTime.UtcNow - seen;
                }
                ctx.UpdatePower(power, signal, age);
            }
        }

        private void SetupLogging()
        {
            // Name the log after the actually-running executable (rename the exe -> the log,
            // "Open Log File", and "Clear Log" all follow), resolved at run-time, not compile-time.
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string path = System.IO.Path.ChangeExtension(exe, ".log");
            Core.Diagnostics.KvmLog.FilePath = path;
            // Lines arrive from every session's threads, often many a second. They are queued and
            // added to the panel in batches, rather than one dispatcher call per line.
            Core.Diagnostics.KvmLog.Message += line => _pendingLog.Enqueue(line);
            _logFlushTimer.Tick += (s, e) => FlushLogPanel();
            _logFlushTimer.Start();
            Core.Diagnostics.KvmLog.Write("=== ATENtion started; log file: " + path + " ===");
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLog =
            new System.Collections.Concurrent.ConcurrentQueue<string>();
        private readonly DispatcherTimer _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        /// <summary>Lines kept in the log panel. The file keeps everything; the panel only the recent part.</summary>
        private const int LogPanelMaxChars = 400_000;

        private void FlushLogPanel()
        {
            if (_pendingLog.IsEmpty) return;
            var batch = new System.Text.StringBuilder();
            while (batch.Length < LogPanelMaxChars && _pendingLog.TryDequeue(out string line))
                batch.Append(line).Append(Environment.NewLine);
            LogBox.AppendText(batch.ToString());
            if (LogBox.Text.Length > LogPanelMaxChars)
            {
                string text = LogBox.Text;
                int cut = text.IndexOf('\n', text.Length - LogPanelMaxChars / 2);
                LogBox.Text = cut < 0 ? "" : text.Substring(cut + 1);
            }
            LogBox.ScrollToEnd();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!RestoreTabs() && !ShowConnectDialog())
                ShowDemoFrame();
        }

        /// <summary>Tear down any current session, prompt for connection details (pre-filled
        /// from the saved settings) and connect. Returns false if the user cancelled.</summary>
        private bool ShowConnectDialog()
        {
            var dialog = new ConnectWindow(_connectProfile) { Owner = this };
            bool? ok = dialog.ShowDialog();
            if (ok != true || string.IsNullOrWhiteSpace(dialog.Options.Host))
                return false;

            // Media mounted from the previous server cannot follow the tab to a different one.
            if (!string.Equals(_connectOptions?.Host, dialog.Options.Host, StringComparison.OrdinalIgnoreCase))
                ClearVmedia();

            // Remember these inputs so a dropped link can be auto-reconnected.
            _connectOptions = dialog.Options;
            _connectProfile = dialog.Profile;
            _armViaWeb = dialog.ArmViaWeb;
            _bmcUser = dialog.BmcUser;
            _bmcPassword = dialog.BmcPassword;

            TearDownSession();
            _reconnectTimer.Stop();
            _reconnectAttempts = 0;

            ConnectLive(_ctx, _connectOptions, _armViaWeb, _bmcUser, _bmcPassword);
            return true;
        }

        private void TearDownSession() => TearDownSession(_ctx);

        // The session's events are wired as lambdas that capture the context, so disposing the
        // session is what detaches them; there is nothing to unsubscribe by name.
        private void TearDownSession(SessionContext ctx)
        {
            ctx.Attempts.Cancel();   // a connect still in flight would otherwise attach itself later
            var old = ctx.Session;
            ctx.Session = null;
            // Off the UI thread: disposing waits for the session's threads to stop. The web login
            // that armed it is closed after it; see SessionContext.EndSession.
            SessionContext.EndSession(old, ctx.WebLogin);
            ctx.WebLogin = null;
            // Mounted media is left alone. Its channel is separate, and tested on an X10 board it
            // kept serving the host through the video session closing, a fresh web login and a new
            // video session, so a video reconnect need not pull the disc out of an install.
            ctx.ConnectedAt = null;
            ctx.InfoText = "";
            ctx.LastFrames = ctx.LastBytes = 0;
            ctx.RaiseTabState();
            SetStatus(ctx, "● Disconnected", StateRed);
            if (ReferenceEquals(ctx, _ctx)) InfoText.Text = "";
        }

        /// <summary>The server reported the input-control state (message 0x39): show whether this
        /// session is driving the host or only watching, so the user knows whether their keyboard and
        /// mouse will reach the machine.</summary>
        private void OnPrivilegeChanged(SessionContext ctx, KvmVideoSession s)
        {
            // BeginInvoke: this runs on the pump, which a teardown on the UI thread may be joining.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(ctx.Session, s)) return;   // a session since replaced or closed
                if (s.Controlling == true) SetStatus(ctx, "● Controlling", StateGreen);
                else SetStatus(ctx, "● View-only", StateOrange);
                if (ReferenceEquals(ctx, _ctx)) UpdateTitle();
                NoteSessionListChange(ctx, s);
                NoteBmcNotice(ctx, s);
            }));
        }

        /// <summary>How long a "user connected/disconnected" notice stays in the status bar.</summary>
        private static readonly TimeSpan SessionNoticeDuration = TimeSpan.FromSeconds(5);

        // Announces users joining or leaving this tab's BMC. The first list a session receives is
        // who was already there, ourselves included, so it is only remembered.
        private void NoteSessionListChange(SessionContext ctx, KvmVideoSession s)
        {
            var list = s.Sessions;
            if (!ReferenceEquals(ctx.SessionListOwner, s))
            {
                ctx.SessionListOwner = s;
                ctx.KnownSessions = null;
            }
            if (ctx.KnownSessions == null)
            {
                if (list.Count > 0) ctx.KnownSessions = list;
                return;
            }
            string notice = SessionListChanges.Notice(ctx.KnownSessions, list);
            ctx.KnownSessions = list;
            if (notice == null) return;
            // Every tab shares the status bar, so name the server unless it is the only tab.
            string text = _sessions.Count > 1 ? $"{ctx.DisplayName}: {notice}" : notice;
            Core.Diagnostics.KvmLog.Write("Console sessions: " + text);
            FlashStatus(text, SessionNoticeDuration);
        }

        // Reports a notice the BMC sent about this console, once per notice.
        private void NoteBmcNotice(SessionContext ctx, KvmVideoSession s)
        {
            var notice = s.LastNotice;
            if (notice == null || (ReferenceEquals(ctx.NoticeOwner, s) && ctx.ShownNotice == notice)) return;
            ctx.NoticeOwner = s;
            ctx.ShownNotice = notice;
            string text;
            switch (notice.Value)
            {
                case PrivilegeRecordType.SessionLimit:
                    text = "The BMC has reached its maximum number of console sessions";
                    SetStatus(ctx, "● Refused: session limit", StateRed);
                    break;
                case PrivilegeRecordType.BiosUpgrade: text = "The BMC is disconnecting consoles for a BIOS upgrade"; break;
                case PrivilegeRecordType.FirmwareUpgrade: text = "The BMC is disconnecting consoles for a firmware upgrade"; break;
                case PrivilegeRecordType.RaidUpgrade: text = "The BMC is disconnecting consoles for a RAID upgrade"; break;
                default: return;   // a web logout: logged by the session, and the console carries on
            }
            FlashStatus(_sessions.Count > 1 ? $"{ctx.DisplayName}: {text}" : text, SessionNoticeDuration);
        }

        // ---- virtual media state ----

        // Asks the visible tab's BMC which virtual-media slots are in use, when the Storage menu opens.
        private async void OnStorageMenuOpened(object sender, RoutedEventArgs e)
        {
            var ctx = _ctx;
            RefreshStorageMenu(ctx);
            var options = ctx.ConnectOptions;
            if (ctx.Vmedia != null)
            {
                VmediaStateItem.Header = "Virtual media: this tab has " + System.IO.Path.GetFileName(ctx.Vmedia.ImagePath) + " mounted";
                return;
            }
            if (ctx.Session == null || options == null || !options.VirtualMediaEnabled)
            {
                VmediaStateItem.Header = ctx.Session == null ? "Virtual media: not connected" : "Virtual media: not offered by this BMC";
                return;
            }
            VmediaStateItem.Header = "Virtual media: checking...";
            var probe = new VirtualMediaOptions
            {
                Host = options.Host,
                Port = options.VirtualMediaPort,
                UseTls = options.VirtualMediaUseTls,
                ClientCertificate = options.ClientCertificate,
                ConnectTimeoutMs = 5000,
            };
            Core.Storage.VirtualMediaSlots slots = null;
            try { slots = await Task.Run(() => VirtualMediaSession.ProbeSlots(probe)); }
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("virtual media status", ex); }
            if (!ReferenceEquals(ctx, _ctx) || ctx.Vmedia != null || ctx.Mounting) return;   // moved on meanwhile
            bool taken = slots?.InUse(Core.Storage.VirtualMediaSlots.OwnSlot) == true;
            VmediaStateItem.Header = slots == null ? "Virtual media: status unavailable"
                : taken ? "Virtual media: another session has a disc mounted"
                : slots.AnyInUse ? "Virtual media: another session has a device attached; a mount may be refused"
                : "Virtual media: free";
            if (taken) MountIsoItem.IsEnabled = false;
        }

        private void OnAbout(object sender, RoutedEventArgs e) =>
            new AboutWindow { Owner = this }.ShowDialog();

        private SessionInfoWindow _sessionInfo;

        /// <summary>The active session's reported console sessions, for the User List dialog.</summary>
        internal IReadOnlyList<string> CurrentSessions => _session?.Sessions;

        private UserListWindow _userList;

        private void OnUserList(object sender, RoutedEventArgs e)
        {
            if (_userList != null) { _userList.Activate(); return; }
            _userList = new UserListWindow(this) { Owner = this };
            _userList.Closed += (s2, e2) => _userList = null;
            _userList.Show();
        }

        /// <summary>Open (or focus) the live Session Info dialog.</summary>
        private void OnSessionInfo(object sender, RoutedEventArgs e)
        {
            if (_sessionInfo != null) { _sessionInfo.Activate(); return; }
            _sessionInfo = new SessionInfoWindow(this) { Owner = this };
            _sessionInfo.Closed += (s2, e2) => _sessionInfo = null;
            _sessionInfo.Show();
        }

        /// <summary>Builds a snapshot of the current session state for the Session Info dialog (empty
        /// fields when disconnected), reading only data already held client-side.</summary>
        internal SessionInfoSnapshot GetSessionSnapshot()
        {
            var s = _session;
            var snap = new SessionInfoSnapshot();
            if (s == null || _connectOptions == null) return snap;
            snap.Connected = true;
            snap.ServerName = s.Session?.ServerInit.Name;
            snap.Endpoint = $"{_connectOptions.Host}:{_connectOptions.Port}";
            snap.Transport = _connectOptions.UseTls ? "TLS" : "plain";
            snap.Resolution = (s.Decoder != null && s.Decoder.Width > 0)
                ? $"{s.Decoder.Width}x{s.Decoder.Height}" : "-";
            snap.MouseMode = MouseModeName(_mouseMode);
            snap.Control = s.Controlling == null ? "-" : s.Controlling == true ? "Controlling" : "View-only";
            snap.Role = string.IsNullOrEmpty(s.PrivilegeInfo) ? "-" : s.PrivilegeInfo;
            snap.Uptime = _connectedAt.HasValue ? StatusFormat.Uptime(DateTime.Now - _connectedAt.Value) : "-";
            snap.Fps = _lastFps;
            snap.RateBytes = _lastRateBytes;
            snap.Frames = s.FramesDecoded;
            snap.Bytes = s.VideoBytes;
            snap.LastFrameAge = (s.LastFrameUtc != default(DateTime))
                ? StatusFormat.Age(DateTime.UtcNow - s.LastFrameUtc) : "-";
            snap.Reconnect = _reconnectAttempts > 0
                ? $"attempt {_reconnectAttempts}/{MaxReconnectAttempts}" : "stable";
            return snap;
        }

        private void OnReconnect(object sender, RoutedEventArgs e)
        {
            // Manual reconnect: reuse prior connection details silently when they exist, otherwise
            // prompt. Either way, cancel any pending auto-reconnect.
            _reconnectTimer.Stop();
            _reconnectAttempts = 0;
            if (_connectOptions != null)
            {
                TearDownSession();
                ConnectLive(_ctx, _connectOptions, _armViaWeb, _bmcUser, _bmcPassword);
            }
            else ShowConnectDialog();
        }

        /// <summary>Open the editable saved-server dialog even when a current connection exists.</summary>
        private void OnConnectOrChange(object sender, RoutedEventArgs e) => ShowConnectDialog();

        /// <summary>Explicit user disconnect: tear down and STAY down (cancels any pending auto-reconnect).</summary>
        private void OnDisconnect(object sender, RoutedEventArgs e)
        {
            if (_session == null && !_liveConnected && !_reconnectTimer.IsEnabled && !_ctx.Attempts.IsRunning)
            {
                FlashStatus("Not connected."); return;
            }
            _userDisconnected = true;
            _reconnectTimer.Stop();
            _reconnectAttempts = 0;
            TearDownSession();
            ClearVmedia();   // an explicit disconnect ends everything, the mounted disc included
            _liveConnected = false;
            StatsText.Text = "";
            SetStatus("● Disconnected", StateRed);
            ShowOverlay("Disconnected - use Connection ▸ Reconnect.");
            UpdateTitle();
        }

        /// <summary>Reflect the connection phase + control state in the window title.</summary>
        private void UpdateTitle()
        {
            string host = _connectOptions?.Host;
            if (string.IsNullOrEmpty(host)) { Title = "ATENtion"; return; }
            string state;
            if (_userDisconnected) state = "Disconnected";
            else if (!_liveConnected || _session == null) state = "connecting...";
            else if (_session.Controlling == true) state = "Controlling";
            else if (_session.Controlling == false) state = "View-only";
            else state = "connected";
            Title = $"{host} - {state} - ATENtion";
        }

        // ---- auto-reconnect ----

        private void ScheduleReconnect(SessionContext ctx, string why)
        {
            if (ctx.IsClosed || ctx.UserDisconnected) return; // closed, or asked to stay disconnected
            if (ReferenceEquals(ctx, _ctx)) UpdateTitle();
            if (!AutoReconnectItem.IsChecked || ctx.ConnectOptions == null)
            {
                SetStatus(ctx, "● Disconnected", StateRed);
                ShowOverlay(ctx, "Disconnected: " + why + "  -  use Connection ▸ Reconnect.");
                return;
            }
            if (ctx.ReconnectAttempts >= MaxReconnectAttempts)
            {
                SetStatus(ctx, "● Disconnected", StateRed);
                ShowOverlay(ctx, $"Reconnect gave up after {ctx.ReconnectAttempts} attempts: {why}  -  use Connection ▸ Reconnect.");
                return;
            }
            ctx.ReconnectAttempts++;
            SetStatus(ctx, "● Reconnecting...", StateNeutral);
            int delay = ReconnectDelaySeconds(ctx.ReconnectAttempts);
            ShowOverlay(ctx, $"Disconnected: {why} - reconnecting in {delay}s " +
                             $"(attempt {ctx.ReconnectAttempts}/{MaxReconnectAttempts})...");
            ctx.ReconnectTimer.Interval = TimeSpan.FromSeconds(delay);
            ctx.ReconnectTimer.Stop();
            ctx.ReconnectTimer.Start();
        }

        // Shared by every tab's reconnect timer; the Tag says which session fired.
        private void OnReconnectTick(object sender, EventArgs e)
        {
            var timer = sender as DispatcherTimer;
            var ctx = timer?.Tag as SessionContext;
            if (ctx == null) return;
            ctx.ReconnectTimer.Stop();
            if (ctx.IsClosed || ctx.ConnectOptions == null) return;
            TearDownSession(ctx);
            ctx.LiveConnected = false;
            SetStatus(ctx, "● Reconnecting...", StateNeutral);
            ShowOverlay(ctx, $"Reconnecting (attempt {ctx.ReconnectAttempts}/{MaxReconnectAttempts})...");
            ConnectLive(ctx, ctx.ConnectOptions, ctx.ArmViaWeb, ctx.BmcUser, ctx.BmcPassword);
        }

        // ---- live session ----

        private void ConnectLive(SessionContext ctx, KvmConnectionOptions options, bool armViaWeb, string bmcUser, string bmcPassword)
        {
            ctx.LiveConnected = false;
            ctx.UserDisconnected = false; // a (re)connect attempt clears the explicit-disconnect state
            SetStatus(ctx, "● Connecting...", StateNeutral);
            ctx.UpdateDisplayName();
            if (ReferenceEquals(ctx, _ctx)) UpdateTitle();
            ShowOverlay(ctx, $"Connecting to {options.Host}...");
            Core.Diagnostics.KvmLog.Write($"Connect requested: host={options.Host} port={options.Port} " +
                $"tls={options.UseTls} armViaWeb={armViaWeb} credentialLengths=" +
                $"{(options.KvmUsername ?? "").Length}/{(options.KvmPassword ?? "").Length}");
            bool isAutomaticRetry = ctx.ReconnectAttempts > 0;
            // Supersedes any attempt still running for this tab. Every step that would touch the tab
            // checks the token first, so a late attempt cannot attach a session the tab no longer wants.
            var token = ctx.Attempts.Begin();

            Task.Run(() =>
            {
                bool armingCompleted = !armViaWeb;
                Core.Net.ArmingResult arming = null;
                KvmVideoSession session = null;
                CancellationTokenRegistration abort = default(CancellationTokenRegistration);
                try
                {
                    if (armViaWeb)
                    {
                        arming = new Core.Net.BmcArmingClient().Arm(options.Host, bmcUser, bmcPassword);
                        armingCompleted = true;
                        options.KvmUsername = arming.KvmUsername;
                        options.KvmPassword = arming.KvmPassword;
                        // Mirror the original viewer's transport choice exactly: when the
                        // JNLP's stunEnable (arg8) is set it tunnels TLS to the server TLS port (arg9,
                        // e.g. 5900); otherwise it talks plaintext to the iKVM port (arg4, e.g. 63630).
                        if (arming.PreferredPort > 0) options.Port = arming.PreferredPort;
                        options.UseTls = arming.UseTls;
                        if (arming.VirtualMediaPort > 0)
                            options.VirtualMediaPort = arming.VirtualMediaPort;
                        options.VirtualMediaUseTls = arming.UseTls;
                        options.VirtualMediaEnabled = arming.VirtualMediaEnabled != 0;
                        Core.Diagnostics.KvmLog.Write($"Arming complete: connecting to port {options.Port} " +
                            $"(iKVM {arming.KvmPort}, TLS {arming.VncPort}, stunEnable {arming.StunEnable}), TLS={options.UseTls}; " +
                            $"vmedia server port {options.VirtualMediaPort}, enabled={options.VirtualMediaEnabled}.");
                    }

                    if (token.IsCancellationRequested) return;

                    session = new KvmVideoSession(options)
                    {
                        MouseMode = (byte)_mouseMode, // enum value == on-wire mode byte
                        // From ctx, not the forwarding properties: this runs on a background task
                        // and may be a tab that is not the visible one.
                        ImageQuality = ctx.ImageQuality, // View > Display preference
                        ImageMode = ctx.ImageMode,
                        // View > Full frame requests. Continuous (-1) asks for every frame in full.
                        AlwaysRequestFullFrames = ctx.FullFrameInterval == ContinuousRefresh,
                        // 1, not 0, in continuous: see ApplyFullRefreshInterval.
                        FullRefreshIntervalTicks =
                            ctx.FullFrameInterval == ContinuousRefresh ? 1 : ctx.FullFrameInterval,
                        LogInput = Core.Diagnostics.KvmLog.Enabled, // skip per-packet hex build when logging is off
                    };
                    var owned = session;
                    // Cancelling closes the socket, so a close or reconnect does not wait out a slow
                    // handshake. Runs at once if the token is already cancelled.
                    abort = token.Register(() => { try { owned.Dispose(); } catch { } });
                    session.FrameDecoded += (s, e2) => OnFrameDecoded(ctx, e2);
                    session.PrivilegeChanged += (s, e2) => OnPrivilegeChanged(ctx, owned);
                    // Faulted fires on the pump or a timer thread. BeginInvoke, not Invoke: a teardown
                    // on the UI thread may be joining that thread.
                    session.Faulted += (s, ex) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!ReferenceEquals(ctx.Session, owned)) return;   // replaced or closed since
                        ctx.LiveConnected = false;
                        SetStats(ctx, "");
                        ScheduleReconnect(ctx, ex.Message);
                    }));

                    session.Open();

                    // Publish and start on the UI thread, in one step, so a close or a newer attempt
                    // either happens entirely before (and this one is dropped) or entirely after (and
                    // tears down a session that is fully running).
                    bool published = Dispatcher.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return false;
                        ctx.Attempts.Complete(token);
                        abort.Dispose();
                        ctx.Session = owned;
                        ctx.WebLogin = arming;     // closed when this session ends (TearDownSession)
                        ctx.ReconnectAttempts = 0; // healthy connection - reset the retry budget
                        ctx.QuietFailures = false;
                        _certHintShown = false; // allow the cert/clock hint again if a future drop needs it
                        // The state line stays "● Connecting..." until the server's privilege grant (0x39)
                        // flips it to "● Controlling"/"● View-only" (OnPrivilegeChanged). Server name +
                        // resolution live in the center InfoText / Session Info dialog, not the state line.
                        ctx.LiveConnected = true; ctx.NoFrameTicks = 0; ctx.ConnectedAt = DateTime.Now;
                        ctx.RaiseTabState();
                        if (ReferenceEquals(ctx, _ctx)) UpdateTitle();
                        ShowOverlay(ctx, "Waiting for video...");
                        WireInput();
                        ApplyStreaming(ctx, owned);   // decided here, against the tab visible now
                        owned.StartPump();
                        return true;
                    });
                    if (!published)
                    {
                        owned.Dispose();
                        Core.Net.BmcArmingClient.Logout(arming);
                    }
                }
                catch (Exception ex)
                {
                    abort.Dispose();
                    Core.Net.BmcArmingClient.Logout(arming);   // the attempt failed: nothing uses this login
                    if (token.IsCancellationRequested)
                    {
                        // Superseded or closed: the failure is expected and belongs to no one.
                        try { session?.Dispose(); } catch { }
                        return;
                    }
                    Core.Diagnostics.KvmLog.Error("connect/handshake", ex);
                    Dispatcher.Invoke(() =>
                    {
                        try { session?.Dispose(); } catch { }
                        if (token.IsCancellationRequested) return;
                        ctx.Attempts.Complete(token);
                        // Quiet applies to the first attempt only; later ones are the user's own.
                        bool quiet = ctx.QuietFailures;
                        ctx.QuietFailures = false;
                        string why = ex.Message;
                        bool certClockError = IsCertClockError(ex);
                        if (certClockError)
                        {
                            why = "TLS handshake failed (SSPI) - the BMC's embedded client certificate has expired. "
                                + "Wind the BMC/IPMI clock back to before the cert expiry (≈ mid-2026; e.g. set it to 2024) "
                                + "via the BMC web UI (Configuration ▸ Date & Time) or IPMI, then reconnect.";
                            if (!_certHintShown)
                            {
                                _certHintShown = true;
                                MessageBox.Show(this, why, "Certificate expired - roll back the BMC clock",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                        bool loginSetupFailed = armViaWeb && !armingCompleted;
                        if (loginSetupFailed || !isAutomaticRetry)
                        {
                            ctx.ReconnectTimer.Stop();
                            ctx.ReconnectAttempts = 0;
                            ctx.UserDisconnected = true;
                            TearDownSession(ctx);
                            ctx.LiveConnected = false;
                            SetStats(ctx, "");
                            SetStatus(ctx, loginSetupFailed ? "● Login failed" : "● Connection failed", StateRed);
                            ShowOverlay(ctx, (loginSetupFailed ? "BMC login/session setup failed: " : "Connection failed: ") + why
                                + "  -  use Connection ▸ Connect / Change server to edit the saved profile.");
                            if (ReferenceEquals(ctx, _ctx)) UpdateTitle();
                            if (!certClockError && !quiet)
                            {
                                MessageBox.Show(this,
                                    (loginSetupFailed
                                        ? "The BMC web login or session setup failed:\n\n"
                                        : "The connection attempt failed:\n\n")
                                    + why + "\n\nIt will not be retried automatically. Use Connection ▸ "
                                    + "Connect / Change server to edit the saved profile and try again.",
                                    loginSetupFailed ? "BMC login failed" : "Connection failed",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                        else ScheduleReconnect(ctx, why);
                    });
                }
            });
        }

        /// <summary>True if the exception looks like a TLS/Schannel handshake failure - almost always the
        /// expired vendor client cert vs the BMC's clock (the fix is to roll the BMC clock back). Scans the
        /// whole inner-exception chain for SSPI/Schannel/cert/auth markers.</summary>
        private static bool IsCertClockError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Security.Authentication.AuthenticationException) return true;
                string m = e.Message ?? "";
                if (Has(m, "SSPI") || Has(m, "Schannel") || Has(m, "message received was unexpected")
                    || Has(m, "certificate") || Has(m, "authentication failed") || Has(m, "0x80090"))
                    return true;
            }
            return false;
            bool Has(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Decoupled present buffer with dirty-rect rendering. The receive pump must NOT block on the UI
        // thread, or it stops draining the socket and the BMC flow-control-stalls the input channel. So
        // the pump thread copies only the CHANGED tiles into _present and posts an async present, and the
        // UI thread blits just those regions. This keeps the pump unblocked and avoids re-uploading and
        // re-compositing the whole scaled image every frame (smoother video).
        // Presentation state also belongs to the session, so a background tab keeps its own decoded
        // snapshot and can be shown instantly when its tab is selected.
        private byte[] _present { get => _ctx.Present; set => _ctx.Present = value; }
        private int _presentW { get => _ctx.PresentW; set => _ctx.PresentW = value; }
        private int _presentH { get => _ctx.PresentH; set => _ctx.PresentH = value; }
        private int _presentStride { get => _ctx.PresentStride; set => _ctx.PresentStride = value; }
        private System.Collections.Generic.List<Int32Rect> _presentDirty => _ctx.PresentDirty;
        private bool _presentFull { get => _ctx.PresentFull; set => _ctx.PresentFull = value; }
        private object _presentLock => _ctx.PresentLock;
        private bool _renderPending { get => _ctx.RenderPending; set => _ctx.RenderPending = value; }
        private const int MaxDirtyRegions = 24; // beyond this a single full blit beats many WritePixels

        // Called on the PUMP thread of the session that owns ctx - which may not be the visible
        // tab. Copies into that session's own snapshot and never blocks the pump.
        private void OnFrameDecoded(SessionContext ctx, FrameDecodedEventArgs e)
        {
            // An update that changed nothing: most replies on a still screen. Copying and presenting
            // them would redraw the same picture many times a second.
            if (e.Dirty != null && e.Dirty.Count == 0) return;
            var f = e.Frame;
            lock (ctx.PresentLock)
            {
                bool sizeChanged = ctx.Present == null || ctx.Present.Length != f.Pixels.Length
                                   || ctx.PresentW != f.Width || ctx.PresentH != f.Height;
                if (sizeChanged)
                {
                    ctx.Present = new byte[f.Pixels.Length];
                    ctx.PresentW = f.Width; ctx.PresentH = f.Height; ctx.PresentStride = f.Stride;
                }
                // Full copy on resize / keyframe / whole-screen update / too many changed tiles;
                // otherwise copy just the changed tiles and remember them for a partial blit.
                bool full = sizeChanged || e.Dirty == null || IsFullScreen(e.Dirty)
                            || ctx.PresentFull || ctx.PresentDirty.Count + e.Dirty.Count > MaxDirtyRegions;
                if (full)
                {
                    System.Buffer.BlockCopy(f.Pixels, 0, ctx.Present, 0, f.Pixels.Length);
                    ctx.PresentFull = true;
                    ctx.PresentDirty.Clear();
                }
                else
                {
                    foreach (var d in e.Dirty)
                    {
                        int x = Clamp(d.X, 0, ctx.PresentW), y = Clamp(d.Y, 0, ctx.PresentH);
                        int rw = Clamp(d.Width, 0, ctx.PresentW - x), rh = Clamp(d.Height, 0, ctx.PresentH - y);
                        if (rw <= 0 || rh <= 0) continue;
                        for (int row = 0; row < rh; row++)
                        {
                            int off = (y + row) * ctx.PresentStride + x * 4;
                            System.Buffer.BlockCopy(f.Pixels, off, ctx.Present, off, rw * 4);
                        }
                        ctx.PresentDirty.Add(new Int32Rect(x, y, rw, rh));
                    }
                }
            }
            if (ctx.RenderPending) return;        // coalesce: a present is already queued
            ctx.RenderPending = true;
            Dispatcher.BeginInvoke(new System.Action(() => PresentFrame(ctx)));
        }

        // The decoder's whole-screen sentinel (AtenTileDecoder.FullScreen).
        private static bool IsFullScreen(System.Collections.Generic.IReadOnlyList<DirtyRect> dirty)
        {
            for (int i = 0; i < dirty.Count; i++)
                if (dirty[i].X == 0xffff && dirty[i].Y == 0xffff) return true;
            return false;
        }

        // Called on the UI thread (async). Blits only the changed regions of the snapshot.
        private void PresentFrame(SessionContext ctx)
        {
            ctx.RenderPending = false;
            // A background tab keeps decoding into its own snapshot but paints nothing. Its dirty
            // state is deliberately left untouched so that selecting the tab repaints it in full.
            if (!ReferenceEquals(ctx, _ctx)) return;
            int w, h;
            lock (ctx.PresentLock)
            {
                if (ctx.Present == null) return;
                w = ctx.PresentW; h = ctx.PresentH;
                ctx.Renderer.EnsureSize(w, h);
                if (ctx.PresentFull)
                    ctx.Renderer.WriteFull(ctx.Present, w, h, ctx.PresentStride);
                else
                    ctx.Renderer.WriteRegions(ctx.Present, ctx.PresentStride, ctx.PresentDirty);
                ctx.PresentFull = false;
                ctx.PresentDirty.Clear();
            }
            if (!ReferenceEquals(VideoImage.Source, ctx.Renderer.Bitmap))
                VideoImage.Source = ctx.Renderer.Bitmap;
            ctx.NoFrameTicks = 0;
            HideOverlay(ctx); // frames are flowing
        }

        /// <summary>Repaints the whole of the newly selected tab's snapshot, since its incremental
        /// regions were not blitted while it was in the background.</summary>
        private void RepaintActive()
        {
            var ctx = _ctx;
            lock (ctx.PresentLock)
            {
                if (ctx.Present == null) { VideoImage.Source = null; return; }
                ctx.Renderer.EnsureSize(ctx.PresentW, ctx.PresentH);
                ctx.Renderer.WriteFull(ctx.Present, ctx.PresentW, ctx.PresentH, ctx.PresentStride);
                ctx.PresentFull = false;
                ctx.PresentDirty.Clear();
            }
            VideoImage.Source = ctx.Renderer.Bitmap;
        }

        private bool _inputWired;

        private void WireInput()
        {
            if (_inputWired) return; // handlers read _session live, so wire only once
            _inputWired = true;
            VideoImage.MouseMove += (s, e) => SendMouse(e, isMove: true);   // coalesced (paced)
            // The video holds the mouse while a button is down, so a drag that ends outside it still
            // sends the release rather than leaving the button held on the host.
            VideoImage.MouseDown += (s, e) =>
            {
                UpdateButtons(e); VideoImage.Focus(); VideoImage.CaptureMouse(); SendMouse(e, isMove: false);
            };
            VideoImage.MouseUp += (s, e) =>
            {
                UpdateButtons(e); SendMouse(e, isMove: false);
                if (_buttonMask == 0) VideoImage.ReleaseMouseCapture();
            };
            // Capture taken away mid-drag (a dialog, another window): the release will not come here.
            VideoImage.LostMouseCapture += (s, e) => { if (_buttonMask != 0) ReleaseHeldButtons(_ctx); };
            VideoImage.MouseWheel += (s, e) => SendWheel(e);
            VideoImage.Focusable = true;
            // Use the tunneling Preview events: WPF consumes KeyDown for focus navigation (arrows, Tab,
            // Esc, and so on) before the bubbling KeyDown fires, so the bubbling handlers would otherwise
            // see only KeyUp and send orphaned key-releases. PreviewKeyDown sees every key first.
            PreviewKeyDown += (s, e) => SendKey(e, true);
            PreviewKeyUp += (s, e) => SendKey(e, false);
        }

        private void SendMouse(MouseEventArgs e, bool isMove)
        {
            if (_session?.Decoder == null) return;
            Point p = e.GetPosition(VideoImage);
            double sx = VideoImage.ActualWidth <= 0 ? 0 : _session.Decoder.Width / VideoImage.ActualWidth;
            double sy = VideoImage.ActualHeight <= 0 ? 0 : _session.Decoder.Height / VideoImage.ActualHeight;
            int x = Clamp((int)(p.X * sx), 0, _session.Decoder.Width - 1);
            int y = Clamp((int)(p.Y * sy), 0, _session.Decoder.Height - 1);
            _ctx.LastMouseX = x; _ctx.LastMouseY = y; // so a held button can be released in place
            _session.SendMouse(x, y, _buttonMask, coalesce: isMove);
        }

        private void UpdateButtons(MouseButtonEventArgs e)
        {
            int bit = e.ChangedButton switch
            {
                MouseButton.Left => 1,
                MouseButton.Middle => 2,
                MouseButton.Right => 4,
                _ => 0,
            };
            if (e.ButtonState == MouseButtonState.Pressed) _buttonMask |= bit;
            else _buttonMask &= ~bit;
        }

        /// <summary>The window lost activation (Alt-Tab, a click away, a dialog opening): release
        /// anything still held on the host so it cannot stick - every held key (mirroring the original's
        /// releasePressedKeys) and any held mouse button. The release event would otherwise
        /// be delivered to whatever took focus, leaving a latched modifier or a stuck drag.</summary>
        private void OnDeactivated(object sender, EventArgs e) => ReleaseHeldInput(_ctx);

        // RFB reports the wheel as buttons 4 (up) and 5 (down), each notch a press and release.
        private void SendWheel(MouseWheelEventArgs e)
        {
            if (_session?.Decoder == null) return;
            SendMouse(e, isMove: true);   // updates the remembered position first
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);   // high-resolution wheels send smaller steps
            int bit = notches > 0 ? 0x08 : 0x10;
            for (int i = 0; i < Math.Min(Math.Abs(notches), 10); i++)
            {
                _session.SendMouse(_ctx.LastMouseX, _ctx.LastMouseY, _buttonMask | bit);
                _session.SendMouse(_ctx.LastMouseX, _ctx.LastMouseY, _buttonMask);
            }
            e.Handled = true;
        }

        private static void ReleaseHeldButtons(SessionContext ctx)
        {
            if (ctx.ButtonMask != 0)
                ctx.Session?.SendMouse(ctx.LastMouseX, ctx.LastMouseY, 0); // button-up in place (no move)
            ctx.ButtonMask = 0;
        }

        /// <summary>Sends an up for every key and mouse button still held on a tab's host.</summary>
        /// <remarks>Used when the window loses focus and when the user moves to another tab.</remarks>
        private static void ReleaseHeldInput(SessionContext ctx)
        {
            ctx.Session?.ReleaseHeldKeys();
            ReleaseHeldButtons(ctx);
        }

        private void SendKey(KeyEventArgs e, bool down)
        {
            if (_session == null) return;
            // WPF reports some keys under a stand-in: Alt combinations as Key.System, a dead key
            // (the accent keys on many layouts) as DeadCharProcessed, and keys taken by an input
            // method as ImeProcessed. The real key is in a separate property.
            Key key = e.Key == Key.System ? e.SystemKey
                    : e.Key == Key.DeadCharProcessed ? e.DeadCharProcessedKey
                    : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
                    : e.Key;
            uint keysym = KeySymFor(key);
            if (keysym != 0)
            {
                keysym = ApplyLockKeyMarker(keysym);
                // Forward the OS auto-repeat flag + the event's own timestamp: held-key repeat is kept,
                // but the core can shed repeats that go stale if the send path stalls / UI saturates,
                // so a backlog never bursts onto the host (Core.KvmVideoSession.RepeatMaxAgeMs).
                _session.SendKey(keysym, down, autoRepeat: down && e.IsRepeat, stampMs: e.Timestamp);
                e.Handled = true;
            }
            else if (down)
                Core.Diagnostics.KvmLog.Write($"key {key} (WPF) has no keysym mapping - not sent.");
        }

        /// <summary>Native parity (keyboardAction @0x8e50): for the three lock keys, when the
        /// corresponding lock is currently OFF the native ORs <c>0xFF00</c> into the usage before sending -
        /// the BMC uses that marker to keep the host's Caps/Num/Scroll lock state in sync with ours. When the
        /// lock is on, the raw usage is sent. Non-lock keys are unchanged.</summary>
        private static uint ApplyLockKeyMarker(uint keysym)
        {
            int vk;
            switch (keysym)
            {
                case 0x39: vk = 0x14; break; // Caps Lock usage   -> VK_CAPITAL
                case 0x47: vk = 0x91; break; // Scroll Lock usage -> VK_SCROLL
                case 0x53: vk = 0x90; break; // Num Lock usage    -> VK_NUMLOCK
                default: return keysym;
            }
            bool lockOn = (GetKeyState(vk) & 0x0001) != 0; // low bit = toggle state
            return lockOn ? keysym : (keysym | 0xFF00u);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        private const uint MAPVK_VK_TO_VSC = 0;

        // Layout-dependent keys go by where they are on the keyboard (see KeyPositions); the active
        // layout says which physical key produced the virtual key. Everything else, and anything
        // the layout cannot place, uses the fixed table.
        private static uint KeySymFor(Key key)
        {
            if (KeySymMap.IsLayoutDependent(key))
            {
                int vk = KeyInterop.VirtualKeyFromKey(key);
                uint scan = MapVirtualKeyEx((uint)vk, MAPVK_VK_TO_VSC, GetKeyboardLayout(0));
                uint hid = KeyPositions.HidFromScanCode(scan);
                if (hid != 0) return hid;
            }
            return KeySymMap.ToKeySym(key);
        }

        private void OnRefresh(object s, RoutedEventArgs e)
        {
            if (_session == null) { FlashStatus("Not connected."); return; }
            _session.RequestFullRefresh();
            FlashStatus("Refreshing frame...");
        }

        // ---- view / window ----

        private WindowStyle _savedStyle;
        private WindowState _savedState;
        private ResizeMode _savedResize;
        private bool _fullscreen;

        // Fullscreen keeps the (thin) menu bar visible so it can be toggled back off without
        // capturing any key - the guest keeps every keystroke (per the no-key-capture preference).
        private void OnToggleFullscreen(object sender, RoutedEventArgs e)
        {
            if (!_fullscreen)
            {
                _savedStyle = WindowStyle; _savedState = WindowState; _savedResize = ResizeMode;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;   // toggle so the maximize covers the taskbar
                WindowState = WindowState.Maximized;
                LogBox.Visibility = Visibility.Collapsed;
                StatusBarCtl.Visibility = Visibility.Collapsed;
                _fullscreen = true;
                FullscreenItem.Header = "Exit _Fullscreen";
            }
            else
            {
                WindowStyle = _savedStyle; ResizeMode = _savedResize; WindowState = _savedState;
                StatusBarCtl.Visibility = Visibility.Visible;
                LogBox.Visibility = ShowLogItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                _fullscreen = false;
                FullscreenItem.Header = "_Fullscreen";
            }
        }

        private void OnToggleLog(object sender, RoutedEventArgs e)
        {
            if (LogBox != null)
                LogBox.Visibility = ShowLogItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        }

        // Master logging switch (off by default). Gates the verbose per-frame log (file I/O + UI
        // dispatch) so it costs nothing in normal use. Opt in here to diagnose.
        private void OnToggleLogging(object sender, RoutedEventArgs e)
        {
            bool on = EnableLoggingItem.IsChecked;
            Core.Diagnostics.KvmLog.Enabled = on;
            if (_session != null) _session.LogInput = on;
            if (on)
            {
                if (IsLoaded && !ShowLogItem.IsChecked) ShowLogItem.IsChecked = true; // reveal the panel
                Core.Diagnostics.KvmLog.Write("=== logging enabled ===");
            }
        }

        private void OnOpenLogFile(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = Core.Diagnostics.KvmLog.FilePath;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                else
                    FlashStatus("No log file yet - enable logging first.");
            }
            catch (Exception ex) { FlashStatus("Open log failed: " + ex.Message); }
        }

        private void OnClearLog(object sender, RoutedEventArgs e)
        {
            LogBox.Clear();
            try { Core.Diagnostics.KvmLog.Clear(); } catch { }
            FlashStatus("Log cleared.");
        }

        // Fit (Uniform, letterboxed to the window) vs Actual Size (1:1 pixels, scrollable).
        private void OnFitModeChanged(object sender, RoutedEventArgs e)
        {
            if (VideoImage == null) return;
            bool actual = ActualSizeItem.IsChecked;
            VideoImage.Stretch = actual ? System.Windows.Media.Stretch.None : System.Windows.Media.Stretch.Uniform;
            var bars = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            VideoScroll.HorizontalScrollBarVisibility = bars;
            VideoScroll.VerticalScrollBarVisibility = bars;
        }

        /// <summary>Resize the window so the video area is exactly the host's resolution (1:1).</summary>
        private void OnSizeToHost(object sender, RoutedEventArgs e)
        {
            var dec = _session?.Decoder;
            if (dec == null || dec.Width <= 0) { FlashStatus("No video yet."); return; }
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            ActualSizeItem.IsChecked = true; // 1:1 implies actual size (no scaling)
            // Grow the window by the difference between the desired video size and the current viewport.
            UpdateLayout();
            double chromeW = ActualWidth - VideoScroll.ActualWidth;
            double chromeH = ActualHeight - VideoScroll.ActualHeight;
            Width = dec.Width + chromeW;
            Height = dec.Height + chromeH;
            FlashStatus($"Sized to {dec.Width}x{dec.Height}.");
        }

        /// <summary>Toggle bitmap scaling quality for upscaled video (crisp NearestNeighbor vs smooth).</summary>
        // Seconds between full frame requests, mirrored into the live session's
        // FullRefreshIntervalTicks (the session's timer ticks once a second). 0 = incremental only.
        private int _fullFrameInterval { get => _ctx.FullFrameInterval; set => _ctx.FullFrameInterval = value; }

        /// <summary>Sentinel for <see cref="_fullFrameInterval"/>: ask for a full frame every time.</summary>
        private const int ContinuousRefresh = -1;

        private IEnumerable<KeyValuePair<int, MenuItem>> FullRefreshItems()
        {
            yield return new KeyValuePair<int, MenuItem>(ContinuousRefresh, RefreshContinuousItem);
            yield return new KeyValuePair<int, MenuItem>(1, Refresh1Item);
            yield return new KeyValuePair<int, MenuItem>(2, Refresh2Item);
            yield return new KeyValuePair<int, MenuItem>(3, Refresh3Item);
            yield return new KeyValuePair<int, MenuItem>(5, Refresh5Item);
            yield return new KeyValuePair<int, MenuItem>(10, Refresh10Item);
            yield return new KeyValuePair<int, MenuItem>(0, RefreshOffItem);
        }

        private void OnFullRefreshInterval(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            if (item == null) return;
            int seconds;
            if (!int.TryParse(item.Tag as string, out seconds)) return;
            _fullFrameInterval = seconds;
            ApplyFullRefreshInterval();
            FlashStatus(seconds == ContinuousRefresh
                ? "Full frame requests: continuous - every frame (~5 Mbps)."
                : seconds > 0
                    ? $"Full frame requests: every {seconds}s."
                    : "Full frame requests: off - incremental only.");
        }

        // Checks the selected entry (these are radio-style: IsCheckable drives the tick, but only one
        // may be set) and pushes the value to the running session so it takes effect without a reconnect.
        /// <summary>Ticks the menu entry matching the visible tab, without touching the session.</summary>
        private void SyncFullRefreshMenu()
        {
            foreach (var entry in FullRefreshItems())
                if (entry.Value != null) entry.Value.IsChecked = entry.Key == _fullFrameInterval;
        }

        private void ApplyFullRefreshInterval()
        {
            SyncFullRefreshMenu();
            var session = _session;
            if (session == null) return;
            bool continuous = _fullFrameInterval == ContinuousRefresh;
            session.AlwaysRequestFullFrames = continuous;
            // Continuous keeps the timer rather than disabling it: the other branch only fires when
            // nothing is in flight, so interval 0 left a dropped request with no recovery.
            session.FullRefreshIntervalTicks = continuous ? 1 : _fullFrameInterval;
        }

        // Display preference. Both values go to the BMC in one changeScreenInfo (0x32); the session
        // keeps them so a reconnect re-sends the same choice.
        private ushort _imageMode { get => _ctx.ImageMode; set => _ctx.ImageMode = value; }
        private byte _imageQuality { get => _ctx.ImageQuality; set => _ctx.ImageQuality = value; }

        private IEnumerable<KeyValuePair<ushort, MenuItem>> ImageModeItems()
        {
            yield return new KeyValuePair<ushort, MenuItem>(ScreenInfoRequest.EnhancedTextMode, Mode444Item);
            yield return new KeyValuePair<ushort, MenuItem>(ScreenInfoRequest.NormalMode, Mode422Item);
        }

        private IEnumerable<KeyValuePair<byte, MenuItem>> ImageQualityItems()
        {
            yield return new KeyValuePair<byte, MenuItem>(11, Quality11Item);
            yield return new KeyValuePair<byte, MenuItem>(9, Quality9Item);
            yield return new KeyValuePair<byte, MenuItem>(6, Quality6Item);
            yield return new KeyValuePair<byte, MenuItem>(3, Quality3Item);
            yield return new KeyValuePair<byte, MenuItem>(0, Quality0Item);
        }

        private void OnImageMode(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            ushort mode;
            if (item == null || !ushort.TryParse(item.Tag as string, out mode)) return;
            _imageMode = mode;
            ApplyDisplayPreference();
            FlashStatus(mode == ScreenInfoRequest.EnhancedTextMode
                ? "Compression: Enhanced text (4:4:4)."
                : "Compression: Normal (4:2:2) - may return stale frames on ATEN/ASPEED firmware.");
        }

        private void OnImageQuality(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            byte quality;
            if (item == null || !byte.TryParse(item.Tag as string, out quality)) return;
            _imageQuality = quality;
            ApplyDisplayPreference();
            FlashStatus($"Image quality: {quality}/11.");
        }

        /// <summary>Ticks the menu entries matching the visible tab, without touching the session.</summary>
        private void SyncDisplayPreferenceMenu()
        {
            foreach (var entry in ImageModeItems())
                if (entry.Value != null) entry.Value.IsChecked = entry.Key == _imageMode;
            foreach (var entry in ImageQualityItems())
                if (entry.Value != null) entry.Value.IsChecked = entry.Key == _imageQuality;
        }

        private void ApplyDisplayPreference()
        {
            SyncDisplayPreferenceMenu();

            var session = _session;
            if (session == null) return;
            session.ImageQuality = _imageQuality;   // remembered for the next (re)connect
            session.ImageMode = _imageMode;
            try { session.SendScreenInfo(_imageQuality, _imageMode); }   // and applied now
            catch (Exception ex) { Core.Diagnostics.KvmLog.Error("changing display preference", ex); }
        }

        private void OnToggleSmoothScaling(object sender, RoutedEventArgs e)
        {
            if (VideoImage == null) return;
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(VideoImage,
                SmoothScalingItem.IsChecked ? System.Windows.Media.BitmapScalingMode.HighQuality
                                            : System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // ---- power + key macros ----

        private void OnPowerOn(object s, RoutedEventArgs e) => Power(PowerCommand.On);
        private void OnPowerOff(object s, RoutedEventArgs e) => Power(PowerCommand.Off);
        private void OnPowerReset(object s, RoutedEventArgs e) => Power(PowerCommand.Reset);
        private void OnSoftOff(object s, RoutedEventArgs e) => Power(PowerCommand.SoftOff);

        private void Power(PowerCommand cmd)
        {
            if (_session == null) { FlashStatus("Not connected."); return; }
            // With several tabs open, "the server" is ambiguous: name the one this goes to.
            string target = PowerTarget(_ctx);
            // Confirm the disruptive actions. Powering on needs no guard.
            if (cmd != PowerCommand.On)
            {
                var answer = MessageBox.Show(this,
                    $"Send '{cmd}' to {target} now?\nThis affects the running machine immediately.",
                    "Confirm power action - " + target, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) { FlashStatus("Power action cancelled."); return; }
            }
            try { _session.SetPower(cmd); FlashStatus($"Sent power {cmd} to {target}"); }
            catch (Exception ex) { FlashStatus("Power failed: " + ex.Message); }
        }

        // The tab's name, with the address when the name is a profile name rather than the host.
        private static string PowerTarget(SessionContext ctx)
        {
            string host = ctx.ConnectOptions?.Host;
            string name = ctx.DisplayName;
            return string.IsNullOrEmpty(host) || string.Equals(name, host, StringComparison.OrdinalIgnoreCase)
                ? name : $"{name} ({host})";
        }

        private void OnHotPlug(object s, RoutedEventArgs e)
        {
            if (_session == null) { FlashStatus("Not connected."); return; }
            _session.SendHotPlug();
            FlashStatus("Sent virtual keyboard/mouse hot-plug.");
        }

        // ---- clipboard paste + send-keys (inject keystrokes the host can't otherwise receive) ----

        /// <summary>Pause after each typed character, so the host's keyboard buffer keeps up.</summary>
        private const int PasteKeyGapMs = 8;

        /// <summary>Pastes of more characters than this ask first: at the paced rate they take a while.</summary>
        private const int PasteConfirmChars = 2000;

        /// <summary>Types the clipboard's text into the host as keystrokes, since the host shares no
        /// clipboard with this client. Useful for passwords and commands. US layout, via
        /// <see cref="HostKeys"/>.</summary>
        private async void OnPasteToHost(object sender, RoutedEventArgs e)
        {
            if (_session == null) { FlashStatus("Not connected."); return; }
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (string.IsNullOrEmpty(text)) { FlashStatus("Clipboard has no text to paste."); return; }
            if (_session.Controlling == false)
            {
                FlashStatus("View-only session - keystrokes won't reach the host."); return;
            }
            var keys = HostKeys.TypeSequence(text).ToList();
            int typed = keys.Count(k => k.down && k.hid != HostKeys.LShift);
            int skipped = HostKeys.CountUntypable(text);
            if (typed == 0) { FlashStatus("Nothing in the clipboard can be typed (US keyboard characters only)."); return; }
            if (typed > PasteConfirmChars &&
                MessageBox.Show(this, $"Type {typed:n0} characters into {PowerTarget(_ctx)}? This takes about " +
                        $"{typed * PasteKeyGapMs / 1000 + 1} seconds.", "Paste to host",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // Typed at a steady pace off the UI thread. Sent all at once, a long paste arrived
            // faster than the host's keyboard buffer drained and characters went missing. Stops if
            // the tab reconnects or closes, rather than typing the rest into a new session.
            var ctx = _ctx;
            var session = _session;
            FlashStatus($"Pasting {typed:n0} character(s)...");
            await Task.Run(() =>
            {
                foreach (var (hid, down) in keys)
                {
                    if (!ReferenceEquals(ctx.Session, session)) break;
                    session.SendKey(hid, down);
                    if (!down) Thread.Sleep(PasteKeyGapMs);
                }
            });
            if (ReferenceEquals(ctx, _ctx))
                FlashStatus($"Pasted {typed:n0} character(s)" +
                            (skipped > 0 ? $"; skipped {skipped} that a US keyboard can't type." : "."));
        }

        /// <summary>Press the given HID usages in order, then release them in reverse - a key combo.</summary>
        private void SendCombo(string label, params uint[] hids)
        {
            if (_session == null) { FlashStatus("Not connected."); return; }
            for (int i = 0; i < hids.Length; i++) _session.SendKey(hids[i], true);
            for (int i = hids.Length - 1; i >= 0; i--) _session.SendKey(hids[i], false);
            FlashStatus("Sent " + label);
        }

        private void OnSendAltTab(object s, RoutedEventArgs e) => SendCombo("Alt+Tab", HostKeys.LAlt, 0x2B);
        private void OnSendAltF4(object s, RoutedEventArgs e) => SendCombo("Alt+F4", HostKeys.LAlt, HostKeys.F4);
        private void OnSendWin(object s, RoutedEventArgs e) => SendCombo("Win", HostKeys.LWin);
        private void OnSendCtrlEsc(object s, RoutedEventArgs e) => SendCombo("Ctrl+Esc", HostKeys.LCtrl, HostKeys.Esc);
        private void OnSendPrtScn(object s, RoutedEventArgs e) => SendCombo("PrtScn", HostKeys.PrintScreen);
        private void OnSendAltSpace(object s, RoutedEventArgs e) => SendCombo("Alt+Space", HostKeys.LAlt, HostKeys.Space);

        private void OnSendCustomKeys(object sender, RoutedEventArgs e)
        {
            if (_session == null) { FlashStatus("Not connected."); return; }
            var dlg = new CustomKeysWindow { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Combo.Length > 0)
                SendCombo(dlg.ComboLabel, dlg.Combo);
        }

        // ---- screenshot ----

        /// <summary>Save the current decoded frame to a PNG (e.g. to capture a BIOS/POST/error screen).</summary>
        private void OnScreenshot(object sender, RoutedEventArgs e)
        {
            var bmp = _renderer.Bitmap;
            if (bmp == null) { FlashStatus("No video to capture yet."); return; }
            string host = _connectOptions?.Host ?? "screen";
            string suggested = $"ATENtion_{host}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save screenshot",
                Filter = "PNG image (*.png)|*.png",
                FileName = suggested,
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(dlg.FileName)) encoder.Save(fs);
                FlashStatus("Saved screenshot: " + System.IO.Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) { FlashStatus("Screenshot failed: " + ex.Message); }
        }

        // ---- virtual storage (read-only ISO -> CD-ROM over the ATEN vmedia channel) ----

        private void OnMountIso(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Mount ISO as virtual CD-ROM",
                Filter = "Disc images (*.iso)|*.iso|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            MountIso(dlg.FileName);
        }

        /// <summary>Mount a local .iso as a read-only virtual CD-ROM (shared by the menu and drag-drop).</summary>
        private async void MountIso(string path)
        {
            if (_connectOptions == null || string.IsNullOrWhiteSpace(_connectOptions.Host))
            {
                FlashStatus("Connect to a BMC first."); return;
            }
            if (_vmedia != null) { FlashStatus("An ISO is already mounted - unmount first."); return; }
            if (_ctx.Mounting) { FlashStatus("A mount is already in progress."); return; }
            if (!_connectOptions.VirtualMediaEnabled)
            {
                FlashStatus("This BMC session does not advertise virtual-media support."); return;
            }

            var owner = _ctx;
            var vm = new VirtualMediaSession(new VirtualMediaOptions
            {
                Host = _connectOptions.Host,
                Port = _connectOptions.VirtualMediaPort,
                UseTls = _connectOptions.VirtualMediaUseTls,
                ClientCertificate = _connectOptions.ClientCertificate,
                Username = _connectOptions.KvmUsername,
                Password = _connectOptions.KvmPassword,
                ImagePath = path,
            });
            // These fire on the media thread, possibly long after the user switched tabs, so they
            // capture the owning tab. BeginInvoke, because closing the tab joins that thread. Each
            // ignores a session that is no longer the tab's own, including one still attaching.
            EventHandler<Exception> faulted = (sender, ex) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(owner.Vmedia, vm)) return;
                if (ReferenceEquals(owner, _ctx))
                    FlashStatus("Virtual media error: " + ex.Message);
                ClearVmedia(owner);
            }));
            EventHandler closed = (sender, args) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(owner.Vmedia, vm)) return;
                if (ReferenceEquals(owner, _ctx))
                    FlashStatus("Virtual media channel closed.");
                ClearVmedia(owner);
            }));
            vm.Faulted += faulted;
            vm.Closed += closed;

            // Attaching opens four connections in turn, each allowed ten seconds, so it runs off the
            // UI thread. Unmounting, disconnecting or closing the tab meanwhile moves the generation
            // on, and the finished mount is then released instead of attached.
            string name = System.IO.Path.GetFileName(path);
            int generation = owner.MediaGeneration;
            owner.Mounting = true;
            RefreshStorageMenu(owner);
            if (ReferenceEquals(owner, _ctx)) FlashStatus($"Mounting {name}...");
            Exception failure = null;
            try { await Task.Run(() => { vm.Open(); vm.StartServing(); }); }
            catch (Exception ex) { failure = ex; }
            owner.Mounting = false;

            if (failure != null || owner.IsClosed || owner.MediaGeneration != generation)
            {
                SessionContext.DisposeInBackground(vm);
                RefreshStorageMenu(owner);
                if (failure is Core.Storage.VirtualMediaRefusedException && ReferenceEquals(owner, _ctx))
                    MessageBox.Show(this, failure.Message, "Can't mount " + name, MessageBoxButton.OK, MessageBoxImage.Information);
                else if (failure != null && ReferenceEquals(owner, _ctx))
                    FlashStatus("Mount failed: " + failure.Message);
                return;
            }

            owner.Vmedia = vm;
            owner.VmFaulted = faulted;
            owner.VmClosed = closed;
            owner.RaiseTabState();
            RefreshStorageMenu(owner);
            // Media attached changes what the refresh interval costs, so say so once.
            bool slow = owner.FullFrameInterval == 0 || owner.FullFrameInterval > 2;
            if (slow)
                Core.Diagnostics.KvmLog.Write(
                    "Virtual media attached. This firmware only re-encodes the screen for a full " +
                    "frame request, so the update rate is whatever View > Full frame requests is " +
                    "set to. Choose Continuous for real motion.");
            if (ReferenceEquals(owner, _ctx))
                FlashStatus(slow
                    ? $"Mounted {name}. For smoother video set View ▸ Full frame requests ▸ Continuous."
                    : $"Mounted {name} as virtual CD-ROM.");
        }

        /// <summary>Brings the Storage menu into line with a tab, if it is the visible one.</summary>
        private void RefreshStorageMenu(SessionContext ctx)
        {
            if (!ReferenceEquals(ctx, _ctx)) return;
            MountIsoItem.IsEnabled = ctx.Vmedia == null && !ctx.Mounting;
            UnmountIsoItem.IsEnabled = ctx.Vmedia != null;
            UnmountIsoItem.Header = ctx.Vmedia != null
                ? $"_Unmount ({System.IO.Path.GetFileName(ctx.Vmedia.ImagePath)})"
                : "_Unmount";
        }

        private void OnUnmountIso(object s, RoutedEventArgs e)
        {
            if (_vmedia == null) { FlashStatus("No ISO mounted."); return; }
            ClearVmedia();
            FlashStatus("Unmounted virtual CD-ROM.");
        }

        private void ClearVmedia() => ClearVmedia(_ctx);

        private void ClearVmedia(SessionContext ctx)
        {
            ctx.MediaGeneration++;   // a mount still attaching is now unwanted
            if (ctx.Vmedia != null)
            {
                if (ctx.VmFaulted != null) try { ctx.Vmedia.Faulted -= ctx.VmFaulted; } catch { }
                if (ctx.VmClosed != null) try { ctx.Vmedia.Closed -= ctx.VmClosed; } catch { }
                SessionContext.DisposeInBackground(ctx.Vmedia);
                ctx.Vmedia = null;
            }
            ctx.VmFaulted = null;
            ctx.VmClosed = null;
            ctx.RaiseTabState();
            RefreshStorageMenu(ctx);
        }

        // Accept a single .iso dragged onto the video to mount it as a virtual CD-ROM.
        private void OnVideoDragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsSingleIsoDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnVideoDrop(object sender, DragEventArgs e)
        {
            if (!IsSingleIsoDrop(e)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            MountIso(files[0]);
        }

        private static bool IsSingleIsoDrop(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Length == 1 &&
                   files[0].EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        }

        // ---- mouse mode (parity with the ATEN client: Absolute=1 / Relative=2 / Single=3) ----

        private void OnMouseModeAbsolute(object s, RoutedEventArgs e) => SetMouseMode(MouseMode.Absolute);
        private void OnMouseModeRelative(object s, RoutedEventArgs e) => SetMouseMode(MouseMode.Relative);
        private void OnMouseModeSingle(object s, RoutedEventArgs e) => SetMouseMode(MouseMode.Single);

        private void SetMouseMode(MouseMode mode)
        {
            _mouseMode = mode;
            UpdateMouseModeMenu();
            if (_session != null)
            {
                _session.MouseMode = (byte)mode;
                _session.SendMouseMode((byte)mode);
            }
            FlashStatus($"Mouse mode: {MouseModeName(mode)}");
        }

        // Radio-group behaviour for the three checkable items.
        private void UpdateMouseModeMenu()
        {
            MouseAbsoluteItem.IsChecked = _mouseMode == MouseMode.Absolute;
            MouseRelativeItem.IsChecked = _mouseMode == MouseMode.Relative;
            MouseSingleItem.IsChecked = _mouseMode == MouseMode.Single;
        }

        private static string MouseModeName(MouseMode m) => m.ToString();

        private void OnCtrlAltDel(object s, RoutedEventArgs e)
        {
            if (_session == null) return;
            const uint ctrl = 0xE0, alt = 0xE2, del = 0x4C; // raw HID LeftCtrl/LeftAlt/Delete
            _session.SendKey(ctrl, true); _session.SendKey(alt, true); _session.SendKey(del, true);
            _session.SendKey(del, false); _session.SendKey(alt, false); _session.SendKey(ctrl, false);
            FlashStatus("Sent Ctrl+Alt+Del");
        }

        // ---- offline demo (when no host is entered) ----

        /// <summary>Reopens the tabs that were open at exit. Returns false if there was nothing to reopen.</summary>
        /// <remarks>
        /// Tabs whose profile has since been deleted are skipped. A profile that does not arm via the
        /// web has no persisted token, so its tab reopens without connecting and says what it needs.
        /// </remarks>
        private bool RestoreTabs()
        {
            if (!ReopenTabsItem.IsChecked) return false;
            var saved = UiSettings.Load();
            var profiles = ConnectSettings.LoadProfiles()
                .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
            var wanted = saved.OpenProfileIds.Where(profiles.ContainsKey).Select(id => profiles[id]).ToList();
            if (wanted.Count == 0) return false;

            var contexts = new List<SessionContext>();
            for (int i = 0; i < wanted.Count; i++)
            {
                var ctx = i == 0 ? _ctx : RegisterContext(new SessionContext
                {
                    FullFrameInterval = _ctx.FullFrameInterval,
                    ImageMode = _ctx.ImageMode,
                    ImageQuality = _ctx.ImageQuality,
                });
                ctx.ConnectProfile = wanted[i];
                ctx.ConnectOptions = ConnectWindow.OptionsFor(wanted[i]);
                ctx.ArmViaWeb = wanted[i].Arm;
                ctx.BmcUser = wanted[i].User;
                ctx.BmcPassword = wanted[i].Password;
                ctx.QuietFailures = true;
                ctx.UpdateDisplayName();
                contexts.Add(ctx);
            }

            // Select the visible tab before connecting, so each session starts paused or streaming
            // according to whether it is the one on screen.
            var active = contexts.FirstOrDefault(c => c.ConnectProfile.Id == saved.ActiveProfileId) ?? contexts[0];
            if (!ReferenceEquals(active, _ctx)) SessionTabs.SelectedItem = active;

            foreach (var ctx in contexts)
            {
                if (ctx.ArmViaWeb)
                {
                    ConnectLive(ctx, ctx.ConnectOptions, ctx.ArmViaWeb, ctx.BmcUser, ctx.BmcPassword);
                }
                else
                {
                    SetStatus(ctx, "● Disconnected", StateNeutral);
                    ShowOverlay(ctx, "This server connects with a token, which is not saved - " +
                                     "use Connection ▸ Connect / Change server to enter one.");
                }
            }
            return true;
        }

        private void ShowDemoFrame()
        {
            const int width = 256, height = 192;
            var decoder = new AtenTileDecoder(width, height);
            byte[] packet = AtenPacketBuilder.BuildPalette8Keyframe(BuildDemoPalette(), BuildDemoIndices(width, height));
            decoder.DecodePacket(packet);
            _renderer.Update(decoder.Frame);
            VideoImage.Source = _renderer.Bitmap;
            _liveConnected = false;
            HideOverlay();
            SetStatus($"Offline demo: decoded synthetic {width}x{height} palette keyframe.");
        }

        private static byte[] BuildDemoPalette()
        {
            var pal = new byte[AtenPalette.ByteSize];
            for (int i = 0; i < AtenPalette.EntryCount; i++)
            {
                pal[i * 4 + 0] = (byte)(255 - i);
                pal[i * 4 + 1] = (byte)(i < 128 ? i * 2 : (255 - i) * 2);
                pal[i * 4 + 2] = (byte)i;
                pal[i * 4 + 3] = 0xFF;
            }
            return pal;
        }

        private static byte[] BuildDemoIndices(int width, int height)
        {
            var idx = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    idx[y * width + x] = (byte)((x + y) & 0xff);
            return idx;
        }
    }
}
