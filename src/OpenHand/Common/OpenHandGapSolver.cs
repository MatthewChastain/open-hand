namespace OpenHand.Common;

/// <summary>
/// Pure placement logic for the Open Hand HUD cell. Given the X intervals
/// occupied by every slot rendered on the hotbar row (final screen pixels),
/// decides where an extra cell fits without covering anything. Layout-driven
/// on purpose: other mods add or move row cells (e.g. Immersive Backpacks'
/// bag slots), so no mod name ever participates in the decision.
/// All intervals are half-open [start, end).
/// </summary>
public static class OpenHandGapSolver
{
    public enum GapChoice
    {
        /// <summary>Centered in the preferred interval while that interval is free.</summary>
        Preferred,

        /// <summary>Centered in the longest free gap of the row (leftmost on ties).</summary>
        Largest,

        /// <summary>No bounded gap fits; the caller stacks the cell above the row.</summary>
        None
    }

    public readonly record struct GapPlacement(GapChoice Choice, int X, int RowStart, int RowEnd);

    /// <summary>
    /// Chooses an X position for a cell of <paramref name="cellWidth" /> on a
    /// row occupied by <paramref name="occupied" />. The preferred interval
    /// wins when it is itself a free gap of at least the cell width; otherwise
    /// the longest fitting bounded gap is used; otherwise the placement fails
    /// and the caller stacks the cell above the row (centered on RowStart/
    /// RowEnd). Degenerate and overlapping input intervals are normalized.
    /// </summary>
    public static GapPlacement Place(
        IReadOnlyList<(int Start, int End)> occupied,
        int cellWidth,
        (int Start, int End)? preferred)
    {
        if (cellWidth <= 0 || occupied.Count == 0)
        {
            return new(GapChoice.None, 0, 0, 0);
        }

        List<(int Start, int End)> merged = Merge(occupied);
        int rowStart = merged[0].Start;
        int rowEnd = merged[^1].End;

        // The preferred interval (the vanilla offhand↔slot-0 gap) wins while it
        // is free: first when it actually fits the cell, otherwise - as the
        // last resort - with the legacy centering that tolerates a narrow gap.
        // This keeps pure-vanilla placement pixel-identical to the historical
        // unconditional centering while modified rows prefer a roomier gap.
        bool preferredFree = preferred is (int prefStart, int prefEnd) &&
                             !OverlapsAny(merged, prefStart, prefEnd);
        if (preferredFree && preferred is (int fitStart, int fitEnd) && fitEnd - fitStart >= cellWidth)
        {
            return new(GapChoice.Preferred, fitStart + (fitEnd - fitStart - cellWidth) / 2, rowStart, rowEnd);
        }

        int bestStart = 0;
        int bestLength = 0;
        for (int i = 0; i < merged.Count - 1; i++)
        {
            int gapStart = merged[i].End;
            int gapLength = merged[i + 1].Start - gapStart;
            // Strictly greater keeps the leftmost gap on ties.
            if (gapLength >= cellWidth && gapLength > bestLength)
            {
                bestStart = gapStart;
                bestLength = gapLength;
            }
        }

        if (bestLength >= cellWidth)
        {
            return new(GapChoice.Largest, bestStart + (bestLength - cellWidth) / 2, rowStart, rowEnd);
        }

        if (preferredFree && preferred is (int narrowStart, int narrowEnd))
        {
            return new(GapChoice.Preferred, narrowStart + (narrowEnd - narrowStart - cellWidth) / 2, rowStart, rowEnd);
        }

        return new(GapChoice.None, 0, rowStart, rowEnd);
    }

    private static List<(int Start, int End)> Merge(IReadOnlyList<(int Start, int End)> occupied)
    {
        List<(int Start, int End)> intervals = new(occupied.Count);
        foreach ((int start, int end) in occupied)
        {
            if (end > start)
            {
                intervals.Add((start, end));
            }
        }

        intervals.Sort(static (a, b) => a.Start != b.Start ? a.Start - b.Start : a.End - b.End);

        List<(int Start, int End)> merged = new(intervals.Count);
        foreach ((int start, int end) in intervals)
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                (int _, int lastEnd) = merged[^1];
                merged[^1] = (merged[^1].Start, Math.Max(lastEnd, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        return merged;
    }

    private static bool OverlapsAny(List<(int Start, int End)> merged, int start, int end)
    {
        foreach ((int mStart, int mEnd) in merged)
        {
            if (start < mEnd && mStart < end)
            {
                return true;
            }
        }

        return false;
    }
}
