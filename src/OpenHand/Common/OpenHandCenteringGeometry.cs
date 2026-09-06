namespace OpenHand.Common;

public static class OpenHandCenteringGeometry
{
    // Work in rendered pixels, not a hardcoded vanilla hotbar width. For odd
    // widths choose the left of the two equally close pixel-aligned positions.
    public static int Shift(int viewportWidth, int left, int right)
    {
        long width = (long)right - left;
        if (viewportWidth <= 0 || width <= 0 || width > viewportWidth) return 0;
        return (int)((viewportWidth - width) / 2 - left);
    }

    public static bool Overlaps(int left, int right, int otherLeft, int otherRight) =>
        left < right && otherLeft < otherRight && left < otherRight && otherLeft < right;
}
