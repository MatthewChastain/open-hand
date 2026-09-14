using OpenHand.Common;

internal static class HudGeometryTests
{
    internal static void Run()
    {
        // Mirror final rendered pixels, including the trailing grid gutter.
        TestHarness.Equal(13, OpenHandHudGeometry.MirrorRightPadding(100, 950,
            new (int, int)[] { (110, 158), (889, 937) }, 99), "vanilla right padding");
        TestHarness.Equal(20, OpenHandHudGeometry.MirrorRightPadding(100, 1375,
            new (int, int)[] { (115, 187), (1283, 1355) }, 99), "1.5 scale right padding");
        TestHarness.Equal(24, OpenHandHudGeometry.MirrorRightPadding(77, 1199,
            new (int, int)[] { (90, 150), (1000, 1060), (1115, 1175) }, 99), "modded width padding");
        TestHarness.Equal(13, OpenHandHudGeometry.MirrorRightPadding(100, 950,
            new (int, int)[] { (889, 937), (960, 1008), (90, 150), (945, 945) }, 99), "ignore outside or empty cells");
        TestHarness.Equal(13, OpenHandHudGeometry.MirrorRightPadding(100, 950,
            Array.Empty<(int, int)>(), 13), "missing grids fallback");
        TestHarness.Equal(0, OpenHandHudGeometry.MirrorRightPadding(100, 950,
            new (int, int)[] { (902, 950) }, 13), "flush right cell padding");

        TestHarness.Equal(54, OpenHandHudGeometry.ExtensionWidth(100, 59, 13), "shared background extends 54 pixels");
        TestHarness.Equal(81, OpenHandHudGeometry.ExtensionWidth(150, 89, 20), "shared background scaled width");
        TestHarness.Equal(54, OpenHandHudGeometry.ExtensionWidth(900, 859, 13), "screen translation does not change extension width");
        TestHarness.Equal(0, OpenHandHudGeometry.ExtensionWidth(100, 120, 13), "no extension for inset icon");
        TestHarness.Equal(11, OpenHandHudGeometry.ExtensionWidth(100, 102, 13), "extend only exposed padding");
    }
}
