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



    }
}

namespace ATENtion.App.Tests
{
    /// <summary>The power glyph prefers the BMC's power report to the video signal.</summary>

}
