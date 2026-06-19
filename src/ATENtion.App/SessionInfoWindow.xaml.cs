using System;
using System.Windows;

namespace ATENtion.App
{
    /// <summary>A snapshot of the current session's surfaceable state, all from client-side data.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the fields the Session Info dialog displays, captured at one instant so the
    /// dialog can render without reaching back into the live session.
    /// </para>
    /// </remarks>
    internal sealed class SessionInfoSnapshot
    {
        /// <summary>True when a session is connected; the remaining fields are meaningful only then.</summary>
        public bool Connected;
        /// <summary>The server's desktop name from ServerInit.</summary>
        public string ServerName;
        /// <summary>The host and port, as "host:port".</summary>
        public string Endpoint;
        /// <summary>The transport, "TLS" or "plain".</summary>
        public string Transport;
        /// <summary>The current resolution, as "WxH", or a dash when not yet known.</summary>
        public string Resolution;
        /// <summary>The pointer mode name.</summary>
        public string MouseMode;
        /// <summary>The control state: "Controlling", "View-only", or a dash.</summary>
        public string Control;
        /// <summary>The server's role/session string.</summary>
        public string Role;
        /// <summary>The session uptime, formatted.</summary>
        public string Uptime;
        /// <summary>The frames-per-second over the last second.</summary>
        public long Fps;
        /// <summary>The bytes received over the last second.</summary>
        public long RateBytes;
        /// <summary>The total frames decoded.</summary>
        public long Frames;
        /// <summary>The total video bytes received.</summary>
        public long Bytes;
        /// <summary>The age of the last decoded frame, formatted.</summary>
        public string LastFrameAge;
        /// <summary>The reconnect state: "stable" or an attempt count.</summary>
        public string Reconnect;
    }

    /// <summary>A live, read-only details panel for the current connection.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Shows the connection's endpoint, transport, resolution, control state, throughput,
    /// and totals, refreshing once per second while it is open.
    /// </para>
    /// <para>
    /// OPERATION - The dialog subscribes to the main window's per-second status tick and, on each tick
    /// and at construction, pulls a fresh <see cref="SessionInfoSnapshot"/> and writes it into the
    /// fields. When disconnected, the fields show placeholders. The subscription is removed when the
    /// dialog closes.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads from its owning <see cref="MainWindow"/> through GetSessionSnapshot and the
    /// StatusTick event. It uses <see cref="StatusColors"/> and <see cref="StatusFormat"/> for display.
    /// </para>
    /// </remarks>
    public partial class SessionInfoWindow : Window
    {
        private readonly MainWindow _owner;

        /// <summary>Builds the dialog, fills it once, and subscribes to the owner's status tick.</summary>
        /// <param name="owner">The main window to read session state from.</param>
        public SessionInfoWindow(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            Refresh();
            _owner.StatusTick += OnStatusTick;
            Closed += (s, e) => _owner.StatusTick -= OnStatusTick;
        }

        // Refreshes the panel on each per-second status tick.
        private void OnStatusTick(object sender, EventArgs e) => Refresh();

        // Pulls a fresh snapshot and writes it into the dialog's fields, showing placeholders when
        // disconnected.
        private void Refresh()
        {
            var s = _owner.GetSessionSnapshot();
            if (!s.Connected)
            {
                ServerText.Text = "(not connected)";
                EndpointText.Text = TransportText.Text = ResolutionText.Text = MouseText.Text =
                    ControlText.Text = RoleText.Text = UptimeText.Text = ThroughputText.Text =
                    TotalsText.Text = LastFrameText.Text = ReconnectText.Text = "-";
                return;
            }
            ServerText.Text = string.IsNullOrEmpty(s.ServerName) ? "-" : s.ServerName;
            EndpointText.Text = s.Endpoint;
            TransportText.Text = s.Transport;
            ResolutionText.Text = s.Resolution;
            MouseText.Text = s.MouseMode;
            ControlText.Text = s.Control;
            ControlText.Foreground = s.Control == "Controlling" ? StatusColors.Controlling
                : s.Control == "View-only" ? StatusColors.ViewOnly
                : System.Windows.Media.Brushes.Gainsboro;
            RoleText.Text = s.Role;
            UptimeText.Text = s.Uptime;
            ThroughputText.Text = $"{s.Fps} fps · {StatusFormat.Rate(s.RateBytes)}";
            TotalsText.Text = $"{s.Frames:n0} frames · {StatusFormat.Size(s.Bytes)}";
            LastFrameText.Text = s.LastFrameAge;
            ReconnectText.Text = s.Reconnect;
        }
    }
}
