using ATENtion.Core.Net;
using Xunit;

namespace ATENtion.Tests
{
    /// <summary>Locks down conservative defaults for baseline-dependent ASPEED delta video.</summary>
    public class VideoSessionDefaultsTests
    {
        [Fact]
        public void Defaults_To_Ordered_Requests_And_Periodic_Full_Refresh()
        {
            using (var session = new KvmVideoSession(new KvmConnectionOptions()))
            {
                Assert.Equal(1, session.PipelineDepth);
                Assert.Equal(5, session.FullRefreshIntervalTicks);
            }
        }
    }
}
