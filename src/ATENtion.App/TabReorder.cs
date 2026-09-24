using System.Collections.Generic;

namespace ATENtion.App
{
    /// <summary>Tab strip positions: where a dragged tab lands, and which tab follows a closed one.</summary>
    internal static class TabReorder
    {
        /// <summary>
        /// The index for the dragged tab: the number of other tabs whose midpoint lies left of the
        /// pointer.
        /// </summary>
        /// <remarks>
        /// Measuring against the other tabs rather than the dragged one keeps it stable when widths
        /// differ. A move shifts the tabs it passes by the dragged tab's width, away from the
        /// pointer, so the same position gives the same answer instead of swapping back.
        /// </remarks>
        /// <param name="otherMidpoints">Horizontal midpoints of every tab except the dragged one.</param>
        /// <param name="pointerX">The pointer's horizontal position, in the same coordinates.</param>
        internal static int TargetIndex(IEnumerable<double> otherMidpoints, double pointerX)
        {
            int index = 0;
            foreach (double midpoint in otherMidpoints)
                if (pointerX > midpoint) index++;
            return index;
        }

        /// <summary>The tab to show when the visible one closes: the one to its right, or the last.</summary>
        /// <param name="closingIndex">Index of the closing tab, before it is removed.</param>
        /// <param name="count">Number of tabs, including the closing one. At least two.</param>
        /// <returns>An index into the list as it is before the removal.</returns>
        internal static int NeighbourAfterClose(int closingIndex, int count) =>
            closingIndex < count - 1 ? closingIndex + 1 : closingIndex - 1;
    }
}
