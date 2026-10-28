using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ATENtion.App.Tests
{
    public sealed class TabReorderTests
    {
        [Theory]
        [InlineData(10, 0)]    // before the first midpoint
        [InlineData(151, 1)]   // just past the first
        [InlineData(400, 2)]
        [InlineData(999, 3)]   // past every tab
        public void Index_Counts_The_Midpoints_Left_Of_The_Pointer(double x, int expected) =>
            Assert.Equal(expected, TabReorder.TargetIndex(new[] { 150.0, 285.0, 450.0 }, x));

        [Fact]
        public void A_Single_Move_Settles_Whatever_The_Tab_Widths()
        {
            double[] widths = { 60, 180, 90, 240, 35 };
            double total = widths.Sum();

            for (int dragged = 0; dragged < widths.Length; dragged++)
            {
                for (double x = 0; x <= total; x += 0.5)
                {
                    var order = Enumerable.Range(0, widths.Length).ToList();
                    int moves = 0;
                    while (true)
                    {
                        int from = order.IndexOf(dragged);
                        int to = TabReorder.TargetIndex(Midpoints(order, widths, dragged), x);
                        if (to == from) break;
                        order.RemoveAt(from);
                        order.Insert(to, dragged);
                        Assert.True(++moves == 1, $"tab {dragged} moved again at x={x}");
                    }
                }
            }
        }

        [Fact]
        public void Dragging_Back_Returns_The_Tab_To_Its_Place()
        {
            double[] widths = { 60, 180, 90 };
            var order = new List<int> { 0, 1, 2 };

            MoveTo(order, widths, dragged: 0, x: 300);      // past the other two
            Assert.Equal(new[] { 1, 2, 0 }, order);

            MoveTo(order, widths, dragged: 0, x: 5);        // back to the start
            Assert.Equal(new[] { 0, 1, 2 }, order);
        }

        private static void MoveTo(List<int> order, double[] widths, int dragged, double x)
        {
            int from = order.IndexOf(dragged);
            int to = TabReorder.TargetIndex(Midpoints(order, widths, dragged), x);
            order.RemoveAt(from);
            order.Insert(to, dragged);
        }

        // Lays the tabs out left to right in their current order, as the strip would.
        private static IEnumerable<double> Midpoints(List<int> order, double[] widths, int dragged)
        {
            double left = 0;
            foreach (int tab in order)
            {
                if (tab != dragged) yield return left + widths[tab] / 2;
                left += widths[tab];
            }
        }
    }
}
