using System;
using System.Collections.Generic;
using System.Windows;

namespace ATENtion.App
{
    /// <summary>Lists the console sessions the BMC reports, refreshed while the dialog is open.</summary>
    /// <remarks>
    /// The BMC volunteers one privilege record per session whenever the set changes, so this follows
    /// the window's once-a-second tick and re-reads the collected list rather than polling the BMC.
    /// </remarks>
    public partial class UserListWindow : Window
    {
        private readonly MainWindow _owner;

        internal UserListWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            _owner.StatusTick += OnTick;
            Closed += (s, e) => _owner.StatusTick -= OnTick;
            Refresh();
        }

        private void OnTick(object sender, EventArgs e) => Refresh();

        private void Refresh()
        {
            IReadOnlyList<string> sessions = _owner.CurrentSessions;
            if (sessions == null || sessions.Count == 0)
            {
                // The BMC only volunteers the list on connect and on change, so an empty list means
                // "not reported yet" rather than "nobody connected".
                SessionList.ItemsSource = new[] { "(no session list reported yet)" };
                CountText.Text = "";
                return;
            }

            SessionList.ItemsSource = sessions;
            CountText.Text = sessions.Count == 1 ? "1 session" : $"{sessions.Count} sessions";
        }
    }
}
