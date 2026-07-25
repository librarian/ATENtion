using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private readonly WpfFrameRenderer _renderer = new WpfFrameRenderer();
        private KvmVideoSession _session;
        private VirtualMediaSession _vmedia;
        private int _buttonMask;

        private readonly DispatcherTimer _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private long _lastFrames, _lastBytes;
        private long _lastFps, _lastRateBytes; // last 1s deltas, for the Session Info dialog

        // "Waiting for video" overlay state. _liveConnected = handshake done. The stats timer then
        // owns the overlay (show after a short grace if no frames arrive, hide once they flow).
        // While connecting/reconnecting/disconnected the connection-state code owns the overlay text.
        private bool _liveConnected;
        private bool _userDisconnected; // true after an explicit Disconnect - suppresses auto-reconnect
        private bool _certHintShown;    // one-shot: only pop the expired-cert/clock hint dialog once per run
        private DateTime? _connectedAt; // handshake time, for the session-uptime readout
        private int _noFrameTicks;
        private const int WaitGraceTicks = 2; // ~2s of no frames before showing "Waiting for video..."

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
        private readonly DispatcherTimer _reconnectTimer = new DispatcherTimer();
        private int _reconnectAttempts;
        private const int ReconnectDelaySeconds = 5;
        private const int MaxReconnectAttempts = 10;
        private KvmConnectionOptions _connectOptions;
        private bool _armViaWeb;
        private string _bmcUser, _bmcPassword;

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
            _reconnectTimer.Tick += OnReconnectTick;
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
            // Relative(2)/Single(3) are disabled (they need cursor capture the client does not do yet),
            // so always start in Absolute regardless of any stale saved value - the BMC must never be
            // sent a mode whose coordinates the client cannot produce correctly.
            _mouseMode = MouseMode.Absolute;
            UpdateMouseModeMenu();
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
            _reconnectTimer.Stop();
            _flashTimer.Stop();
            SaveUi();
            _session?.Dispose();
            _vmedia?.Dispose();
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

        /// <summary>Show a temporary message that reverts to the persistent status after a few seconds.</summary>
        private void FlashStatus(string text)
        {
            StatusText.Text = text;
            _flashTimer.Stop();
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
            var s = _session;
            if (s == null) { StatsText.Text = ""; InfoText.Text = ""; return; }
            long frames = s.FramesDecoded, bytes = s.VideoBytes;
            long df = frames - _lastFrames, db = bytes - _lastBytes;
            _lastFrames = frames; _lastBytes = bytes;
            if (df < 0) df = 0; if (db < 0) db = 0;
            _lastFps = df; _lastRateBytes = db; // expose to the Session Info dialog snapshot
            // Right: live throughput + cumulative session totals.
            StatsText.Text = $"{df} fps · {StatusFormat.Rate(db)} · {frames:n0} frames · {StatusFormat.Size(bytes)} total";

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
                InfoText.Text = $"{_connectOptions.Host}:{_connectOptions.Port} · {tls} · {res} · up {up}{media}{health}";
            }

            // Once handshaken, drive the overlay off frame flow: show "Waiting for video..." after a
            // short grace with no frames, hide it as soon as frames arrive. (Connecting/reconnecting/
            // disconnected states set their own overlay text and clear _liveConnected.)
            if (_liveConnected)
            {
                if (df > 0) { _noFrameTicks = 0; HideOverlay(); }
                else if (++_noFrameTicks >= WaitGraceTicks) ShowOverlay("Waiting for video...");
            }
        }

        private void SetupLogging()
        {
            // Name the log after the actually-running executable (rename the exe -> the log,
            // "Open Log File", and "Clear Log" all follow), resolved at run-time, not compile-time.
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string path = System.IO.Path.ChangeExtension(exe, ".log");
            Core.Diagnostics.KvmLog.FilePath = path;
            Core.Diagnostics.KvmLog.Message += line =>
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    LogBox.AppendText(line + System.Environment.NewLine);
                    LogBox.ScrollToEnd();
                }));
            Core.Diagnostics.KvmLog.Write("=== ATENtion started; log file: " + path + " ===");
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!ShowConnectDialog())
                ShowDemoFrame();
        }

        /// <summary>Tear down any current session, prompt for connection details (pre-filled
        /// from the saved settings) and connect. Returns false if the user cancelled.</summary>
        private bool ShowConnectDialog()
        {
            var dialog = new ConnectWindow { Owner = this };
            bool? ok = dialog.ShowDialog();
            if (ok != true || string.IsNullOrWhiteSpace(dialog.Options.Host))
                return false;

            // Remember these inputs so a dropped link can be auto-reconnected.
            _connectOptions = dialog.Options;
            _armViaWeb = dialog.ArmViaWeb;
            _bmcUser = dialog.BmcUser;
            _bmcPassword = dialog.BmcPassword;

            TearDownSession();
            _reconnectTimer.Stop();
            _reconnectAttempts = 0;

            ConnectLive(_connectOptions, _armViaWeb, _bmcUser, _bmcPassword);
            return true;
        }

        private void TearDownSession()
        {
            if (_session != null)
            {
                try { _session.FrameDecoded -= OnFrameDecoded; } catch { }
                try { _session.PrivilegeChanged -= OnPrivilegeChanged; } catch { }
                try { _session.Dispose(); } catch { }
                _session = null;
            }
            _connectedAt = null;
            InfoText.Text = "";
            SetStatus("● Disconnected", StateRed);
            _lastFrames = _lastBytes = 0;
        }

        /// <summary>The server reported the input-control state (message 0x39): show whether this
        /// session is driving the host or only watching, so the user knows whether their keyboard and
        /// mouse will reach the machine.</summary>
        private void OnPrivilegeChanged(object sender, EventArgs e)
        {
            var s = _session;
            if (s == null) return;
            Dispatcher.Invoke(() =>
            {
                if (s.Controlling == true) SetStatus("● Controlling", StateGreen);
                else SetStatus("● View-only", StateOrange);
                UpdateTitle();
            });
        }

        private void OnAbout(object sender, RoutedEventArgs e) =>
            new AboutWindow { Owner = this }.ShowDialog();

        private SessionInfoWindow _sessionInfo;

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
                ConnectLive(_connectOptions, _armViaWeb, _bmcUser, _bmcPassword);
            }
            else ShowConnectDialog();
        }

        /// <summary>Explicit user disconnect: tear down and STAY down (cancels any pending auto-reconnect).</summary>
        private void OnDisconnect(object sender, RoutedEventArgs e)
        {
            if (_session == null && !_liveConnected && !_reconnectTimer.IsEnabled)
            {
                FlashStatus("Not connected."); return;
            }
            _userDisconnected = true;
            _reconnectTimer.Stop();
            _reconnectAttempts = 0;
            TearDownSession();
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

        private void ScheduleReconnect(string why)
        {
            if (_userDisconnected) return; // user asked to stay disconnected - ignore the teardown fault
            UpdateTitle();
            if (!AutoReconnectItem.IsChecked || _connectOptions == null)
            {
                SetStatus("● Disconnected", StateRed);
                ShowOverlay("Disconnected: " + why + "  -  use Connection ▸ Reconnect.");
                return;
            }
            if (_reconnectAttempts >= MaxReconnectAttempts)
            {
                SetStatus("● Disconnected", StateRed);
                ShowOverlay($"Reconnect gave up after {_reconnectAttempts} attempts: {why}  -  use Connection ▸ Reconnect.");
                return;
            }
            _reconnectAttempts++;
            SetStatus("● Reconnecting...", StateNeutral);
            ShowOverlay($"Disconnected: {why} - reconnecting in {ReconnectDelaySeconds}s " +
                        $"(attempt {_reconnectAttempts}/{MaxReconnectAttempts})...");
            _reconnectTimer.Interval = TimeSpan.FromSeconds(ReconnectDelaySeconds);
            _reconnectTimer.Stop();
            _reconnectTimer.Start();
        }

        private void OnReconnectTick(object sender, EventArgs e)
        {
            _reconnectTimer.Stop();
            if (_connectOptions == null) return;
            TearDownSession();
            _liveConnected = false;
            SetStatus("● Reconnecting...", StateNeutral);
            ShowOverlay($"Reconnecting (attempt {_reconnectAttempts}/{MaxReconnectAttempts})...");
            ConnectLive(_connectOptions, _armViaWeb, _bmcUser, _bmcPassword);
        }

        // ---- live session ----

        private void ConnectLive(KvmConnectionOptions options, bool armViaWeb, string bmcUser, string bmcPassword)
        {
            _liveConnected = false;
            _userDisconnected = false; // a (re)connect attempt clears the explicit-disconnect state
            SetStatus("● Connecting...", StateNeutral);
            UpdateTitle();
            ShowOverlay($"Connecting to {options.Host}...");
            Core.Diagnostics.KvmLog.Write($"Connect requested: host={options.Host} port={options.Port} " +
                $"tls={options.UseTls} armViaWeb={armViaWeb} credentialLengths=" +
                $"{(options.KvmUsername ?? "").Length}/{(options.KvmPassword ?? "").Length}");

            Task.Run(() =>
            {
                try
                {
                    if (armViaWeb)
                    {
                        var arming = new Core.Net.BmcArmingClient().Arm(options.Host, bmcUser, bmcPassword);
                        options.KvmUsername = arming.KvmUsername;
                        options.KvmPassword = arming.KvmPassword;
                        // Mirror the original viewer's transport choice exactly: when the
                        // JNLP's stunEnable (arg8) is set it tunnels TLS to the server TLS port (arg9,
                        // e.g. 5900); otherwise it talks plaintext to the iKVM port (arg4, e.g. 63630).
                        if (arming.PreferredPort > 0) options.Port = arming.PreferredPort;
                        options.UseTls = arming.UseTls;
                        Core.Diagnostics.KvmLog.Write($"Arming complete: connecting to port {options.Port} " +
                            $"(iKVM {arming.KvmPort}, TLS {arming.VncPort}, stunEnable {arming.StunEnable}), TLS={options.UseTls}.");
                    }

                    _session = new KvmVideoSession(options)
                    {
                        MouseMode = (byte)_mouseMode, // enum value == on-wire mode byte
                        LogInput = Core.Diagnostics.KvmLog.Enabled, // skip per-packet hex build when logging is off
                    };
                    _session.FrameDecoded += OnFrameDecoded;
                    _session.PrivilegeChanged += OnPrivilegeChanged;
                    // Faulted fires on the session's pump/watchdog thread; Dispatcher.Invoke marshals
                    // the reconnect handling back onto the UI thread.
                    _session.Faulted += (s, ex) => Dispatcher.Invoke(() =>
                    {
                        _liveConnected = false;
                        StatsText.Text = "";
                        ScheduleReconnect(ex.Message);
                    });

                    _session.Open();
                    Dispatcher.Invoke(() =>
                    {
                        _reconnectAttempts = 0; // healthy connection - reset the retry budget
                        _certHintShown = false; // allow the cert/clock hint again if a future drop needs it
                        // The state line stays "● Connecting..." until the server's privilege grant (0x39)
                        // flips it to "● Controlling"/"● View-only" (OnPrivilegeChanged). Server name +
                        // resolution live in the center InfoText / Session Info dialog, not the state line.
                        _liveConnected = true; _noFrameTicks = 0; _connectedAt = DateTime.Now;
                        UpdateTitle();
                        ShowOverlay("Waiting for video...");
                        WireInput();
                    });
                    _session.StartPump();
                }
                catch (Exception ex)
                {
                    Core.Diagnostics.KvmLog.Error("connect/handshake", ex);
                    Dispatcher.Invoke(() =>
                    {
                        string why = ex.Message;
                        if (IsCertClockError(ex))
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
                        ScheduleReconnect(why);
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
        private byte[] _present;
        private int _presentW, _presentH, _presentStride;
        private readonly System.Collections.Generic.List<Int32Rect> _presentDirty = new System.Collections.Generic.List<Int32Rect>();
        private bool _presentFull;
        private readonly object _presentLock = new object();
        private volatile bool _renderPending;
        private const int MaxDirtyRegions = 24; // beyond this a single full blit beats many WritePixels

        // Called on the PUMP thread. Copies only changed regions and never blocks the pump.
        private void OnFrameDecoded(object sender, FrameDecodedEventArgs e)
        {
            var f = e.Frame;
            lock (_presentLock)
            {
                bool sizeChanged = _present == null || _present.Length != f.Pixels.Length
                                   || _presentW != f.Width || _presentH != f.Height;
                if (sizeChanged)
                {
                    _present = new byte[f.Pixels.Length];
                    _presentW = f.Width; _presentH = f.Height; _presentStride = f.Stride;
                }
                // Full copy on resize / keyframe / whole-screen update / too many changed tiles;
                // otherwise copy just the changed tiles and remember them for a partial blit.
                bool full = sizeChanged || e.Dirty == null || e.Dirty.Count == 0 || IsFullScreen(e.Dirty)
                            || _presentFull || _presentDirty.Count + e.Dirty.Count > MaxDirtyRegions;
                if (full)
                {
                    System.Buffer.BlockCopy(f.Pixels, 0, _present, 0, f.Pixels.Length);
                    _presentFull = true;
                    _presentDirty.Clear();
                }
                else
                {
                    foreach (var d in e.Dirty)
                    {
                        int x = Clamp(d.X, 0, _presentW), y = Clamp(d.Y, 0, _presentH);
                        int rw = Clamp(d.Width, 0, _presentW - x), rh = Clamp(d.Height, 0, _presentH - y);
                        if (rw <= 0 || rh <= 0) continue;
                        for (int row = 0; row < rh; row++)
                        {
                            int off = (y + row) * _presentStride + x * 4;
                            System.Buffer.BlockCopy(f.Pixels, off, _present, off, rw * 4);
                        }
                        _presentDirty.Add(new Int32Rect(x, y, rw, rh));
                    }
                }
            }
            if (_renderPending) return;        // coalesce: a present is already queued
            _renderPending = true;
            Dispatcher.BeginInvoke(new System.Action(PresentFrame));
        }

        // The decoder's whole-screen sentinel (AtenTileDecoder.FullScreen).
        private static bool IsFullScreen(System.Collections.Generic.IReadOnlyList<DirtyRect> dirty)
        {
            for (int i = 0; i < dirty.Count; i++)
                if (dirty[i].X == 0xffff && dirty[i].Y == 0xffff) return true;
            return false;
        }

        // Called on the UI thread (async). Blits only the changed regions of the snapshot.
        private void PresentFrame()
        {
            _renderPending = false;
            int w, h;
            lock (_presentLock)
            {
                if (_present == null) return;
                w = _presentW; h = _presentH;
                _renderer.EnsureSize(w, h);
                if (_presentFull)
                    _renderer.WriteFull(_present, w, h, _presentStride);
                else
                    _renderer.WriteRegions(_present, _presentStride, _presentDirty); // one lock for all tiles
                _presentFull = false;
                _presentDirty.Clear();
            }
            if (!ReferenceEquals(VideoImage.Source, _renderer.Bitmap))
                VideoImage.Source = _renderer.Bitmap;
            _noFrameTicks = 0;
            HideOverlay(); // frames are flowing
            // Live resolution is shown in the center InfoText + Session Info dialog (the left status
            // line is the colored connection-state indicator now).
        }

        private bool _inputWired;

        private void WireInput()
        {
            if (_inputWired) return; // handlers read _session live, so wire only once
            _inputWired = true;
            VideoImage.MouseMove += (s, e) => SendMouse(e, isMove: true);   // coalesced (paced)
            VideoImage.MouseDown += (s, e) => { UpdateButtons(e); VideoImage.Focus(); SendMouse(e, isMove: false); };
            VideoImage.MouseUp += (s, e) => { UpdateButtons(e); SendMouse(e, isMove: false); };
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
            _lastMouseX = x; _lastMouseY = y; // remembered so focus-loss can release a held button in place
            _session.SendMouse(x, y, _buttonMask, coalesce: isMove);
        }
        private int _lastMouseX, _lastMouseY;

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
        private void OnDeactivated(object sender, EventArgs e)
        {
            var s = _session;
            if (s == null) return;
            s.ReleaseHeldKeys();
            if (_buttonMask != 0)
            {
                s.SendMouse(_lastMouseX, _lastMouseY, 0); // button-up in place (no move)
                _buttonMask = 0;
            }
        }

        private void SendKey(KeyEventArgs e, bool down)
        {
            if (_session == null) return;
            // WPF delivers some keys as Key.System (with e.SystemKey set), e.g. Alt-combos.
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            uint keysym = KeySymMap.ToKeySym(key);
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
            try
            {
                string path = Core.Diagnostics.KvmLog.FilePath;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, "");
            }
            catch { }
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
            // Confirm the disruptive actions. Powering on needs no guard.
            if (cmd != PowerCommand.On)
            {
                var answer = MessageBox.Show(this,
                    $"Send '{cmd}' to the server now?\nThis affects the running machine immediately.",
                    "Confirm power action", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) { FlashStatus("Power action cancelled."); return; }
            }
            try { _session.SetPower(cmd); FlashStatus("Sent power: " + cmd); }
            catch (Exception ex) { FlashStatus("Power failed: " + ex.Message); }
        }

        private void OnHotPlug(object s, RoutedEventArgs e)
        {
            if (_session == null) { FlashStatus("Not connected."); return; }
            _session.SendHotPlug();
            FlashStatus("Sent virtual keyboard/mouse hot-plug.");
        }

        // ---- clipboard paste + send-keys (inject keystrokes the host can't otherwise receive) ----

        /// <summary>Types the clipboard's text into the host as keystrokes, since the host shares no
        /// clipboard with this client. Useful for passwords and commands. US layout, via
        /// <see cref="HostKeys"/>.</summary>
        private void OnPasteToHost(object sender, RoutedEventArgs e)
        {
            if (_session == null) { FlashStatus("Not connected."); return; }
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (string.IsNullOrEmpty(text)) { FlashStatus("Clipboard has no text to paste."); return; }
            if (_session.Controlling == false)
            {
                FlashStatus("View-only session - keystrokes won't reach the host."); return;
            }
            int n = 0;
            foreach (var (hid, down) in HostKeys.TypeSequence(text))
            {
                _session.SendKey(hid, down);
                if (down) n++;
            }
            FlashStatus($"Pasted {n} character(s) as keystrokes.");
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
        private void MountIso(string path)
        {
            if (_connectOptions == null || string.IsNullOrWhiteSpace(_connectOptions.Host))
            {
                FlashStatus("Connect to a BMC first."); return;
            }
            if (_vmedia != null) { FlashStatus("An ISO is already mounted - unmount first."); return; }

            var vm = new VirtualMediaSession(new VirtualMediaOptions
            {
                Host = _connectOptions.Host,
                ImagePath = path,
            });
            // Keep the handlers in fields so ClearVmedia can detach them before Dispose - otherwise
            // the lambdas (which close over this window) stay attached to the disposed session.
            _vmFaulted = (sender, ex) => Dispatcher.Invoke(() =>
            {
                FlashStatus("Virtual media error: " + ex.Message);
                ClearVmedia();
            });
            _vmClosed = (sender, args) => Dispatcher.Invoke(() =>
            {
                FlashStatus("Virtual media channel closed.");
                ClearVmedia();
            });
            vm.Faulted += _vmFaulted;
            vm.Closed += _vmClosed;

            try
            {
                vm.Open();
                vm.StartServing();
                _vmedia = vm;
                string name = System.IO.Path.GetFileName(path);
                MountIsoItem.IsEnabled = false;
                UnmountIsoItem.IsEnabled = true;
                UnmountIsoItem.Header = $"_Unmount ({name})";
                FlashStatus($"Mounted {name} as virtual CD-ROM.");
            }
            catch (Exception ex)
            {
                try { vm.Dispose(); } catch { }
                FlashStatus("Mount failed: " + ex.Message);
            }
        }

        private void OnUnmountIso(object s, RoutedEventArgs e)
        {
            if (_vmedia == null) { FlashStatus("No ISO mounted."); return; }
            ClearVmedia();
            FlashStatus("Unmounted virtual CD-ROM.");
        }

        private void ClearVmedia()
        {
            if (_vmedia != null)
            {
                if (_vmFaulted != null) try { _vmedia.Faulted -= _vmFaulted; } catch { }
                if (_vmClosed != null) try { _vmedia.Closed -= _vmClosed; } catch { }
                try { _vmedia.Dispose(); } catch { }
                _vmedia = null;
            }
            _vmFaulted = null;
            _vmClosed = null;
            MountIsoItem.IsEnabled = true;
            UnmountIsoItem.IsEnabled = false;
            UnmountIsoItem.Header = "_Unmount";
        }

        private EventHandler<Exception> _vmFaulted;
        private EventHandler _vmClosed;

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
