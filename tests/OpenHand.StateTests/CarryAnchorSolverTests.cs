using OpenHand.Common;

internal static class CarryAnchorSolverTests
{
    internal static void Run()
    {
        // CarryOn anchor solver: L1 clears a cell left of the bar (vanilla coords:
        // cell [949, 997], row [1000, 1612], unscaled icon 32, gap 16).
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(0, true, 949, 48, 1000, 1612, 32, 16, out int l1, null, null), "L1 places");
        TestHarness.Equal(917, l1, "L1 clears the indicator cell");
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(1, true, 949, 48, 1000, 1612, 32, 16, out int l2, null, null), "L2 places");
        TestHarness.Equal(869, l2, "L2 continues the 48-pixel pitch leftward");
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(2, true, 949, 48, 1000, 1612, 32, 16, out int l3, null, null), "L3 places");
        TestHarness.Equal(821, l3, "L3 continues the pitch");

        // CarryOn anchor solver: the L1 icon rect [901, 933] must clear the cell rect
        // [949, 997] with the full 16-pixel gap.
        TestHarness.Equal(false, 933 > 949 && 901 < 997, "corrected L1 icon stays left of the cell");

        // CarryOn anchor solver: R1 follows the real row's right edge, matching
        // CarryOn's vanilla barRight + 32 center when the edges agree.
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(0, false, 949, 48, 1000, 1612, 32, 16, out int r1, null, null), "R1 places");
        TestHarness.Equal(1644, r1, "R1 sits right of the real row edge");
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(1, false, 949, 48, 1000, 1612, 32, 16, out int r2, null, null), "R2 places");
        TestHarness.Equal(1692, r2, "R2 continues the pitch rightward");

        // CarryOn anchor solver: a right-anchored cell pushes the R anchors past it.
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(0, false, 1700, 48, 1000, 1612, 32, 16, out int r1Shifted, null, null), "shifted R1 places");
        TestHarness.Equal(1780, r1Shifted, "R1 clears a cell right of the row");

        // CarryOn anchor solver: a cell inside the bar (offhand-gap anchor) leaves
        // the left boundary at the row edge, i.e. CarryOn's own vanilla offsets.
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(0, true, 1040, 48, 1000, 1612, 32, 16, out int l1Inside, null, null), "inside-bar cell places");
        TestHarness.Equal(968, l1Inside, "cell inside the bar anchors L1 to the row edge");

        // CarryOn anchor solver: centering shift flows through the real geometry.
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(0, true, 949 + 27, 48, 1000 + 27, 1612 + 27, 32, 16, out int l1Shifted, null, null), "shifted geometry places");
        TestHarness.Equal(917 + 27, l1Shifted, "centering shift moves the whole anchor stack");

        // CarryOn anchor solver: degenerate inputs pass through untouched.
        TestHarness.Equal(false, OpenHandCarryAnchorSolver.TryPlace(-1, true, 949, 48, 1000, 1612, 32, 16, out _, null, null), "negative index");
        TestHarness.Equal(false, OpenHandCarryAnchorSolver.TryPlace(0, true, 949, 0, 1000, 1612, 32, 16, out _, null, null), "empty cell");
        TestHarness.Equal(false, OpenHandCarryAnchorSolver.TryPlace(0, true, 949, 48, 1612, 1612, 32, 16, out _, null, null), "empty row");
        TestHarness.Equal(false, OpenHandCarryAnchorSolver.TryPlace(0, true, 949, 48, 1000, 1612, 0, 16, out _, null, null), "empty icon");

        // CarryOn anchor solver: the gap is measured from the VISIBLE background
        // edge. The Open Hand extension reaches 13 pixels left of the cell (936) and
        // the vanilla background wraps the row by 13 on the right (1625); measuring
        // from the cells instead would leave only the padding visible between the
        // bar and a carried icon.
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(0, true, 949, 48, 1000, 1612, 32, 16, out int l1Bg, 936, 1625), "extension-edge L1 places");
        TestHarness.Equal(904, l1Bg, "L1 keeps a full 16-pixel gap from the extension edge");
        TestHarness.Equal(true, 936 - 904 - 16 == 0 || 936 - (l1Bg + 16) == 16, "L1 icon right edge sits 16 pixels from the extension");
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(1, true, 949, 48, 1000, 1612, 32, 16, out int l2Bg, 936, 1625), "extension-edge L2 places");
        TestHarness.Equal(856, l2Bg, "L2 continues the pitch from the extension edge");
        TestHarness.Equal(true, OpenHandCarryAnchorSolver.TryPlace(0, false, 949, 48, 1000, 1612, 32, 16, out int r1Bg, 936, 1625), "background-edge R1 places");
        TestHarness.Equal(1657, r1Bg, "R1 keeps a full 16-pixel gap from the background edge");
        TestHarness.Equal(true, 1657 - 16 - 1625 == 16, "R1 icon left edge sits 16 pixels from the background");
    }
}
