using OpenHand.Common;

static void Equal<T>(T expected, T actual, string name) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}

static void Wheel(
    OpenHandWheelRing.WheelAction expectedAction,
    int expectedDestination,
    bool isSelected,
    int activeSlot,
    bool skillOccupied,
    bool backpackMode,
    int wheelDelta,
    string name)
{
    OpenHandWheelRing.WheelDecision decision =
        OpenHandWheelRing.Resolve(isSelected, activeSlot, skillOccupied, backpackMode, wheelDelta);
    Equal(expectedAction, decision.Action, $"{name} action");
    Equal(expectedDestination, decision.Destination, $"{name} destination");
}

OpenHandSelectionState initial = OpenHandSelectionState.Unselected(9);
Equal(false, initial.IsSelected, "initial selection");
Equal(9, initial.RememberedHotbarSlot, "initial slot");

OpenHandSelectionState selected = initial.Select(9, 1);
Equal(true, selected.IsSelected, "direct selection");
Equal(9, selected.RememberedHotbarSlot, "selected slot");
Equal(1, selected.Revision, "selected revision");

OpenHandSelectionState exitedForward = selected.Deselect(0, 2);
Equal(false, exitedForward.IsSelected, "forward wheel exit");
Equal(0, exitedForward.RememberedHotbarSlot, "forward wheel destination");

OpenHandSelectionState exitedBackward = selected.Deselect(9, 2);
Equal(9, exitedBackward.RememberedHotbarSlot, "backward wheel destination");
Equal(9, OpenHandSelectionState.NormalizePhysicalSlot(99), "invalid upper slot");
Equal(9, OpenHandSelectionState.NormalizePhysicalSlot(-1), "invalid lower slot");

// Wheel ring: entering Open Hand.
Wheel(OpenHandWheelRing.WheelAction.Enter, 9,
    isSelected: false, activeSlot: 9, skillOccupied: false, backpackMode: false, wheelDelta: -1,
    name: "enter forward from slot 0-key");
Wheel(OpenHandWheelRing.WheelAction.Enter, 0,
    isSelected: false, activeSlot: 0, skillOccupied: false, backpackMode: false, wheelDelta: 1,
    name: "enter backward from slot 1-key");
Wheel(OpenHandWheelRing.WheelAction.Enter, 10,
    isSelected: false, activeSlot: 10, skillOccupied: true, backpackMode: false, wheelDelta: -1,
    name: "enter forward from occupied skill slot");

// Wheel ring: vanilla behavior must be preserved.
Wheel(OpenHandWheelRing.WheelAction.None, 9,
    isSelected: false, activeSlot: 9, skillOccupied: true, backpackMode: false, wheelDelta: -1,
    name: "no entry from slot 0-key while skill occupied");
Wheel(OpenHandWheelRing.WheelAction.None, 5,
    isSelected: false, activeSlot: 5, skillOccupied: false, backpackMode: false, wheelDelta: -1,
    name: "no entry mid-ring");
Wheel(OpenHandWheelRing.WheelAction.None, 9,
    isSelected: false, activeSlot: 9, skillOccupied: false, backpackMode: true, wheelDelta: -1,
    name: "no entry in backpack mode");
Wheel(OpenHandWheelRing.WheelAction.None, 7,
    isSelected: false, activeSlot: 7, skillOccupied: false, backpackMode: false, wheelDelta: 1,
    name: "no entry mid-ring upward");

// Wheel ring: leaving Open Hand.
Wheel(OpenHandWheelRing.WheelAction.ExitToSlot, 0,
    isSelected: true, activeSlot: 5, skillOccupied: false, backpackMode: false, wheelDelta: -1,
    name: "exit forward to slot 1-key");
Wheel(OpenHandWheelRing.WheelAction.ExitToSlot, 9,
    isSelected: true, activeSlot: 5, skillOccupied: false, backpackMode: false, wheelDelta: 1,
    name: "exit backward to slot 0-key");
Wheel(OpenHandWheelRing.WheelAction.ExitToSlot, 10,
    isSelected: true, activeSlot: 5, skillOccupied: true, backpackMode: false, wheelDelta: 1,
    name: "exit backward to occupied skill slot");
Wheel(OpenHandWheelRing.WheelAction.None, 5,
    isSelected: true, activeSlot: 5, skillOccupied: false, backpackMode: true, wheelDelta: -1,
    name: "no exit in backpack mode");

// Gap solver: tier 1 - the preferred gap wins when it fits the cell.
Gap(OpenHandGapSolver.GapChoice.Preferred, 57,
    occupied: new List<(int, int)> { (0, 54), (114, 654) }, cellWidth: 54,
    preferred: (54, 114),
    name: "preferred gap fits");

// Gap solver: tier 3 - a free but narrow preferred gap keeps the legacy
// centering (the historical behavior for the vanilla offhand gap).
Gap(OpenHandGapSolver.GapChoice.Preferred, 30,
    occupied: new List<(int, int)> { (0, 54), (60, 654) }, cellWidth: 54,
    preferred: (54, 60),
    name: "narrow preferred gap keeps legacy centering");

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
Equal(OpenHandGapSolver.GapChoice.None, stacked.Choice, "stacked choice");
Equal(30, stacked.RowStart, "stacked row start");
Equal(90, stacked.RowEnd, "stacked row end");

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

// Config anchor parsing: case-insensitive, trims, defaults on junk.
Equal(IconAnchorMode.Auto, OpenHandClientConfig.ParseIconAnchor("auto"), "anchor auto");
Equal(IconAnchorMode.OffhandGap, OpenHandClientConfig.ParseIconAnchor("OFFHANDGAP"), "anchor offhand gap");
Equal(IconAnchorMode.Left, OpenHandClientConfig.ParseIconAnchor(" left "), "anchor left");
Equal(IconAnchorMode.Right, OpenHandClientConfig.ParseIconAnchor("Right"), "anchor right");
Equal(IconAnchorMode.Auto, OpenHandClientConfig.ParseIconAnchor("nope"), "anchor junk defaults to auto");
Equal(IconAnchorMode.Auto, OpenHandClientConfig.ParseIconAnchor(null), "anchor null defaults to auto");
Equal(true, OpenHandClientConfig.IsKnownIconAnchor("offhandgap"), "known anchor");
Equal(false, OpenHandClientConfig.IsKnownIconAnchor("nope"), "unknown anchor");
Equal(false, OpenHandClientConfig.IsKnownIconAnchor(null), "null anchor");

static void Gap(
    OpenHandGapSolver.GapChoice expectedChoice,
    int expectedX,
    List<(int Start, int End)> occupied,
    int cellWidth,
    (int Start, int End)? preferred,
    string name)
{
    OpenHandGapSolver.GapPlacement placement = OpenHandGapSolver.Place(occupied, cellWidth, preferred);
    Equal(expectedChoice, placement.Choice, $"{name} choice");
    Equal(expectedX, placement.X, $"{name} x");
}

Console.WriteLine("OpenHandSelectionState, wheel ring, gap solver, and config tests passed.");
