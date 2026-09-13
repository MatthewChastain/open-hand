using OpenHand.Common;

internal static class CenteringGeometryTests
{
    internal static void Run()
    {
        TestHarness.Equal(27, OpenHandCenteringGeometry.Shift(1920, 481, 1385), "center 54-pixel extension");
        TestHarness.Equal(26, OpenHandCenteringGeometry.Shift(1920, 482, 1385), "odd extension chooses left pixel");
        TestHarness.Equal(27, OpenHandCenteringGeometry.Shift(1921, 481, 1385), "odd viewport");
        TestHarness.Equal(-23, OpenHandCenteringGeometry.Shift(1920, 531, 1435), "measured position, not hardcoded offset");
        TestHarness.Equal(0, OpenHandCenteringGeometry.Shift(800, -100, 900), "oversized bar not centered");
        TestHarness.Equal(0, OpenHandCenteringGeometry.Shift(0, 10, 50), "minimized viewport");
        TestHarness.Equal(0, OpenHandCenteringGeometry.Shift(1920, 10, 10), "empty geometry");

        TestHarness.Equal(false, OpenHandCenteringGeometry.Overlaps(100, 200, 200, 250), "abutting HUD cells allowed");
        TestHarness.Equal(true, OpenHandCenteringGeometry.Overlaps(100, 200, 199, 250), "overlapping HUD cells rejected");
        TestHarness.Equal(false, OpenHandCenteringGeometry.Overlaps(100, 200, 120, 120), "empty external bounds");
    }
}
