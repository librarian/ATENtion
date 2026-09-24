using System;
using ATENtion.Core.Net;
using Xunit;

namespace ATENtion.App.Tests
{
    /// <summary>What a tab's connection attempts, close and power glyph are allowed to do.</summary>
    public class TabLifetimeTests
    {
        [Fact]
        public void A_New_Attempt_Cancels_The_One_Still_Running()
        {
            var attempts = new ConnectAttempts();
            var first = attempts.Begin();
            var second = attempts.Begin();

            Assert.True(first.IsCancellationRequested);
            Assert.False(second.IsCancellationRequested);
            Assert.True(attempts.IsRunning);
        }

        [Fact]
        public void Closing_Cancels_The_Running_Attempt_And_Any_Later_One()
        {
            var attempts = new ConnectAttempts();
            var running = attempts.Begin();
            attempts.Close();

            Assert.True(running.IsCancellationRequested);
            Assert.True(attempts.IsClosed);
            Assert.True(attempts.Begin().IsCancellationRequested);
            Assert.False(attempts.IsRunning);
        }

        [Fact]
        public void Only_The_Current_Attempt_Can_Mark_Itself_Finished()
        {
            var attempts = new ConnectAttempts();
            var stale = attempts.Begin();
            var current = attempts.Begin();

            attempts.Complete(stale);
            Assert.True(attempts.IsRunning);

            attempts.Complete(current);
            Assert.False(attempts.IsRunning);
            Assert.False(current.IsCancellationRequested);
        }

        [Fact]
        public void Cancelling_With_Nothing_Running_Is_Harmless()
        {
            var attempts = new ConnectAttempts();
            attempts.Cancel();
            attempts.Cancel();
            Assert.False(attempts.IsRunning);
            Assert.False(attempts.Begin().IsCancellationRequested);
        }

        [Theory]
        [InlineData(0, 3, 1)]   // first of three: the one to its right
        [InlineData(1, 3, 2)]
        [InlineData(2, 3, 1)]   // last: the one to its left
        [InlineData(0, 2, 1)]
        [InlineData(1, 2, 0)]
        public void Closing_The_Visible_Tab_Shows_A_Neighbour(int closing, int count, int expected)
        {
            int next = TabReorder.NeighbourAfterClose(closing, count);
            Assert.Equal(expected, next);
            Assert.NotEqual(closing, next);
        }

        [Theory]
        [InlineData(true, true, VideoSignalState.Present, VideoSignalState.Present)]
        [InlineData(true, true, VideoSignalState.Absent, VideoSignalState.Absent)]
        // After a drop the last reading says nothing about the host now.
        [InlineData(true, false, VideoSignalState.Present, VideoSignalState.Unknown)]
        [InlineData(false, false, VideoSignalState.Present, VideoSignalState.Unknown)]
        public void The_Glyph_Only_Trusts_A_Live_Link(bool hasSession, bool live, VideoSignalState signal,
                                                     VideoSignalState expected)
        {
            Assert.Equal(expected, PowerStatus.Displayed(hasSession, live, signal));
        }

        [Fact]
        public void A_Checked_Tab_Says_How_Old_Its_Reading_Is()
        {
            Assert.Equal("Host on - video signal present",
                PowerStatus.Tip(VideoSignalState.Present, null));
            Assert.Equal("Host on - video signal present (checked 7s ago)",
                PowerStatus.Tip(VideoSignalState.Present, TimeSpan.FromSeconds(7.8)));
            Assert.EndsWith("(checked 0s ago)",
                PowerStatus.Tip(VideoSignalState.Absent, TimeSpan.FromSeconds(-1)));
            Assert.DoesNotContain("checked",
                PowerStatus.Tip(VideoSignalState.Unknown, TimeSpan.FromSeconds(3)));
        }
    }
}

namespace ATENtion.App.Tests
{
    /// <summary>The power glyph prefers the BMC's power report to the video signal.</summary>
    public class HostPowerGlyphTests
    {
        [Theory]
        [InlineData(HostPowerState.On, VideoSignalState.Absent, true)]      // on with no picture: now green
        [InlineData(HostPowerState.On, VideoSignalState.Present, true)]
        [InlineData(HostPowerState.Off, VideoSignalState.Present, false)]   // the report wins
        [InlineData(HostPowerState.Unknown, VideoSignalState.Present, true)] // no report: a picture proves on
        [InlineData(HostPowerState.Unknown, VideoSignalState.Absent, false)]
        [InlineData(HostPowerState.Unknown, VideoSignalState.Unknown, false)]
        public void The_Glyph_Is_Green_Only_When_The_Host_Is_Known_To_Be_On(
            HostPowerState power, VideoSignalState signal, bool on)
        {
            Assert.Equal(on, PowerStatus.IsOn(power, signal));
        }

        [Fact]
        public void The_Tooltip_Says_What_The_BMC_Reported()
        {
            Assert.Equal("Host on", PowerStatus.Tip(HostPowerState.On, VideoSignalState.Present, null));
            Assert.StartsWith("Host on - no video signal", PowerStatus.Tip(HostPowerState.On, VideoSignalState.Absent, null));
            Assert.Equal("Host off (checked 4s ago)",
                PowerStatus.Tip(HostPowerState.Off, VideoSignalState.Absent, TimeSpan.FromSeconds(4.2)));
            // Without a report, the signal-based wording as before.
            Assert.Equal(PowerStatus.Tip(VideoSignalState.Absent, null),
                PowerStatus.Tip(HostPowerState.Unknown, VideoSignalState.Absent, null));
        }

        [Fact]
        public void A_Dropped_Link_Shows_Nothing_Known()
        {
            Assert.Equal(HostPowerState.Unknown, PowerStatus.Displayed(true, false, HostPowerState.On));
            Assert.Equal(HostPowerState.On, PowerStatus.Displayed(true, true, HostPowerState.On));
        }
    }
}
