namespace OpenHand.Common;

internal static class OpenHandHudGeometry
{
    internal static int ExtensionWidth(int composerLeft, int iconLeft, int outerPadding)
    {
        return Math.Max(0, composerLeft - iconLeft + outerPadding);
    }

    internal static int MirrorRightPadding(
        int backgroundLeft,
        int backgroundRight,
        IReadOnlyList<(int Start, int End)> cells,
        int fallback)
    {
        int rightmost = backgroundLeft;
        foreach ((int start, int end) in cells)
        {
            if (start >= backgroundLeft && end > start && end <= backgroundRight)
            {
                rightmost = Math.Max(rightmost, end);
            }
        }

        return rightmost > backgroundLeft ? backgroundRight - rightmost : fallback;
    }
}
