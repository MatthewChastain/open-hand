using Vintagestory.API.Client;

namespace OpenHand.Client;

// Own only the X offsets we add. Both absX (hit testing) and renderX inherit
// absOffsetX; fixedOffsetX makes the same translation survive recomposition.
internal sealed class HotbarCenteringLayout
{
    private readonly OwnedOffset root;
    private readonly OwnedOffset[] screenAnchors;

    internal ElementBounds Bounds => root.Bounds;
    internal int Shift { get; private set; }
    internal double CurrentShift(double scale) => root.Bounds.absOffsetX - root.OriginalFixed * scale;

    internal HotbarCenteringLayout(ElementBounds bounds, params ElementBounds[] anchors)
    {
        root = new OwnedOffset(bounds);
        screenAnchors = anchors.Select(b => new OwnedOffset(b)).ToArray();
    }

    internal bool IsIntact(double scale)
    {
        if (!root.IsIntact(scale)) return false;
        foreach (OwnedOffset anchor in screenAnchors)
            if (!anchor.IsIntact(scale)) return false;
        return true;
    }

    internal bool HasAnchor(ElementBounds bounds)
    {
        foreach (OwnedOffset anchor in screenAnchors)
            if (ReferenceEquals(anchor.Bounds, bounds)) return true;
        return false;
    }

    internal bool Apply(int pixels, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0 || !IsIntact(scale)) return false;
        root.Apply(pixels, scale);
        foreach (OwnedOffset anchor in screenAnchors) anchor.Apply(-pixels, scale);
        Shift = pixels;
        return true;
    }

    internal void Restore(double scale)
    {
        root.Restore(scale);
        foreach (OwnedOffset anchor in screenAnchors) anchor.Restore(scale);
        Shift = 0;
    }

    private sealed class OwnedOffset(ElementBounds bounds)
    {
        internal ElementBounds Bounds { get; } = bounds;
        internal double OriginalFixed { get; } = bounds.fixedOffsetX;
        private double lastFixed = bounds.fixedOffsetX;
        private double lastAbsolute = bounds.absOffsetX;

        private static bool Same(double a, double b) => Math.Abs(a - b) < 0.000001;

        internal bool IsIntact(double scale) =>
            Same(Bounds.fixedOffsetX, lastFixed) &&
            (Same(Bounds.absOffsetX, lastAbsolute) || Same(Bounds.absOffsetX, lastFixed * scale));

        internal void Apply(int pixels, double scale)
        {
            lastFixed = OriginalFixed + pixels / scale;
            lastAbsolute = OriginalFixed * scale + pixels;
            if (Bounds.fixedOffsetX != lastFixed) Bounds.fixedOffsetX = lastFixed;
            if (Bounds.absOffsetX != lastAbsolute) Bounds.absOffsetX = lastAbsolute;
        }

        internal void Restore(double scale)
        {
            // Check each field independently: never restore a stale snapshot
            // over a value another mod has since written.
            bool restoreAbsolute = Same(Bounds.absOffsetX, lastAbsolute) ||
                                   Same(Bounds.absOffsetX, lastFixed * scale);
            if (Same(Bounds.fixedOffsetX, lastFixed)) Bounds.fixedOffsetX = OriginalFixed;
            if (restoreAbsolute) Bounds.absOffsetX = Bounds.fixedOffsetX * scale;
        }
    }
}
