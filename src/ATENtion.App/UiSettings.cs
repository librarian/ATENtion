namespace ATENtion.App
{
    /// <summary>The window geometry and view preferences, persisted between runs.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the non-secret UI state the application restores on the next run: the
    /// window's position and size, whether it was maximised, and the view toggles.
    /// </para>
    /// <para>
    /// OPERATION - A thin data carrier mapped onto <see cref="StableSettingsStore"/>.
    /// <see cref="Load"/> reads the stored values; <see cref="Save"/> writes them back. The
    /// public field surface matches the earlier file-backed version, so existing call sites are
    /// unchanged.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Backed by <see cref="StableSettingsStore"/>. All values are non-secret;
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
        /// <summary>Seconds between full frame requests; 0 sends incremental requests only, -1 asks every frame.</summary>
        /// <summary>ATEN chroma mode: 444 Enhanced Text (default) or 422 Normal.</summary>
        /// <summary>ATEN image quality level, 0 through 11 (default 11).</summary>
        /// <summary>True to let tabs that are not visible keep requesting video.</summary>
        public bool StreamAllTabs;
        /// <summary>Most frames a second a tab asks for; 0 for no limit.</summary>
        /// <summary>True to reopen the tabs that were open at exit.</summary>
        /// <summary>Profile ids of the tabs open at exit, in tab order.</summary>
        /// <summary>Profile id of the tab that was visible at exit.</summary>

        /// <summary>Loads the UI settings from the store.</summary>
        /// <returns>The persisted settings, or the defaults on first run.</returns>
        public static UiSettings Load()
        {
            var st = StableSettingsStore.Get();
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
                StreamAllTabs = st.StreamAllTabs,
            };
        }

        /// <summary>Saves the current UI settings to the store.</summary>
        public void Save()
        {
            var st = StableSettingsStore.Get();
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
            st.StreamAllTabs = StreamAllTabs;
            st.Save();
        }
    }
}
