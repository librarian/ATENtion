namespace ATENtion.App
{
    /// <summary>The window geometry and view preferences, persisted between runs.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the non-secret UI state the application restores on the next run: the
    /// window's position and size, whether it was maximised, and the view toggles.
    /// </para>
    /// <para>
    /// OPERATION - A thin data carrier mapped onto <see cref="AppSettingsStore"/>, the user.config
    /// store. <see cref="Load"/> reads the stored values; <see cref="Save"/> writes them back. The
    /// public field surface matches the earlier file-backed version, so existing call sites are
    /// unchanged.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Backed by <see cref="AppSettingsStore"/>. All values are non-secret;
    /// credentials live in <see cref="ConnectSettings"/>.
    /// </para>
    /// </remarks>
    public sealed class UiSettings
    {
        /// <summary>The window's left edge; 0 means unset (the restore guards on width and on-screen position).</summary>
        public double Left;
        /// <summary>The window's top edge; 0 means unset.</summary>
        public double Top;
        /// <summary>The window's width; 0 means unset.</summary>
        public double Width;
        /// <summary>The window's height; 0 means unset.</summary>
        public double Height;
        /// <summary>True if the window was maximised.</summary>
        public bool Maximized;
        /// <summary>True to show the log panel; off by default, persisted once the user enables it.</summary>
        public bool ShowLog = false;
        /// <summary>True to display the framebuffer at its actual size rather than fitting the window.</summary>
        public bool ActualSize;
        /// <summary>True for smooth scaling; false for crisp nearest-neighbour (the default).</summary>
        public bool SmoothScaling = false;
        /// <summary>True to auto-reconnect after a dropped or failed connection.</summary>
        public bool AutoReconnect = true;
        /// <summary>True to enable diagnostic logging; off by default, opt-in through the Logging menu.</summary>
        public bool EnableLogging = false;
        /// <summary>The BMC pointer mode: 1 = Absolute, 2 = Relative (NORMAL), 3 = Single. See <see cref="MouseMode"/>.</summary>
        public int MouseMode = 1;

        /// <summary>Loads the UI settings from the store.</summary>
        /// <returns>The persisted settings, or the defaults on first run.</returns>
        public static UiSettings Load()
        {
            var st = AppSettingsStore.Get();
            return new UiSettings
            {
                Left = st.WinLeft,
                Top = st.WinTop,
                Width = st.WinWidth,
                Height = st.WinHeight,
                Maximized = st.Maximized,
                ShowLog = st.ShowLog,
                ActualSize = st.ActualSize,
                SmoothScaling = st.SmoothScaling,
                AutoReconnect = st.AutoReconnect,
                EnableLogging = st.EnableLogging,
                MouseMode = st.MouseMode,
            };
        }

        /// <summary>Saves the current UI settings to the store.</summary>
        public void Save()
        {
            var st = AppSettingsStore.Get();
            st.WinLeft = Left;
            st.WinTop = Top;
            st.WinWidth = Width;
            st.WinHeight = Height;
            st.Maximized = Maximized;
            st.ShowLog = ShowLog;
            st.ActualSize = ActualSize;
            st.SmoothScaling = SmoothScaling;
            st.AutoReconnect = AutoReconnect;
            st.EnableLogging = EnableLogging;
            st.MouseMode = MouseMode;
            st.Save();
        }
    }
}
