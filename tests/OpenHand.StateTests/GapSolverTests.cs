using OpenHand.Common;

internal static class GapSolverTests
{
    internal static void Run()
    {
        // Gap solver: tier 1 - the preferred gap wins when it fits the cell.
        Gap(OpenHandGapSolver.GapChoice.Preferred, 57,
            occupied: new List<(int, int)> { (0, 54), (114, 654) }, cellWidth: 54,
            preferred: (54, 114),
            name: "preferred gap fits");

        // Gap solver: a free but narrow preferred gap cannot fit the whole cell and
        // must use the HUD patch's external-row fallback rather than cover a neighbor.
        Gap(OpenHandGapSolver.GapChoice.None, 0,
            occupied: new List<(int, int)> { (0, 54), (60, 654) }, cellWidth: 54,
            preferred: (54, 60),
            name: "narrow preferred gap uses external fallback");

        // Gap solver: tier 2 - another mod's cell squatting in the preferred gap
        // pushes the indicator to the largest remaining free gap.
        Gap(OpenHandGapSolver.GapChoice.Largest, 50,
            occupied: new List<(int, int)> { (0, 50), (60, 100), (100, 600) }, cellWidth: 10,
            preferred: (50, 100),
            name: "occupied preferred gap falls through");
        Gap(OpenHandGapSolver.GapChoice.Largest, 135,
            occupied: new List<(int, int)> { (0, 50), (60, 100), (180, 700) }, cellWidth: 10,
            preferred: (50, 100),
            name: "largest gap chosen when preferred occupied");

        // Gap solver: a narrow preferred gap loses to a roomier free gap.
        Gap(OpenHandGapSolver.GapChoice.Largest, 245,
            occupied: new List<(int, int)> { (0, 50), (55, 200), (300, 700) }, cellWidth: 10,
            preferred: (50, 55),
            name: "narrow preferred gap loses to larger gap");

        // Gap solver: no preferred hint at all.
        Gap(OpenHandGapSolver.GapChoice.Largest, 50,
            occupied: new List<(int, int)> { (0, 50), (60, 100), (100, 600) }, cellWidth: 10,
            preferred: null,
            name: "largest gap without preferred hint");

        // Gap solver: equal gaps resolve to the leftmost.
        Gap(OpenHandGapSolver.GapChoice.Largest, 70,
            occupied: new List<(int, int)> { (0, 50), (100, 150), (200, 600) }, cellWidth: 10,
            preferred: null,
            name: "leftmost gap wins ties");

        // Gap solver: an exact-fit gap still fits.
        Gap(OpenHandGapSolver.GapChoice.Largest, 50,
            occupied: new List<(int, int)> { (0, 50), (60, 110) }, cellWidth: 10,
            preferred: null,
            name: "exact fit gap");

        // Gap solver: nothing fits reports the row extents for stack-above centering.
        OpenHandGapSolver.GapPlacement stacked = OpenHandGapSolver.Place(
            new List<(int, int)> { (30, 90) }, 54, null);
        TestHarness.Equal(OpenHandGapSolver.GapChoice.None, stacked.Choice, "stacked choice");
        TestHarness.Equal(30, stacked.RowStart, "stacked row start");
        TestHarness.Equal(90, stacked.RowEnd, "stacked row end");

        // Gap solver: degenerate, overlapping, adjacent, and empty inputs normalize.
        Gap(OpenHandGapSolver.GapChoice.None, 0,
            occupied: new List<(int, int)> { (30, 90), (0, 40), (90, 90) }, cellWidth: 54,
            preferred: null,
            name: "degenerate and overlapping intervals normalize");
        Gap(OpenHandGapSolver.GapChoice.None, 0,
            occupied: new List<(int, int)> { (0, 50), (50, 100) }, cellWidth: 10,
            preferred: null,
            name: "adjacent cells leave no gap");
        Gap(OpenHandGapSolver.GapChoice.None, 0,
            occupied: new List<(int, int)>(), cellWidth: 10,
            preferred: null,
            name: "empty row");
    }

    private static void Gap(
        OpenHandGapSolver.GapChoice expectedChoice,
        int expectedX,
        List<(int Start, int End)> occupied,
        int cellWidth,
        (int Start, int End)? preferred,
        string name)
    {
        OpenHandGapSolver.GapPlacement placement = OpenHandGapSolver.Place(occupied, cellWidth, preferred);
        TestHarness.Equal(expectedChoice, placement.Choice, $"{name} choice");
        TestHarness.Equal(expectedX, placement.X, $"{name} x");
    }
}
