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
    string name,
    bool allowEntry = true)
{
    OpenHandWheelRing.WheelDecision decision =
        OpenHandWheelRing.Resolve(isSelected, activeSlot, skillOccupied, backpackMode, wheelDelta, allowEntry);
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

// A hidden indicator disables wheel entry across the whole vanilla ring,
// including the occupied skill slot, but never traps hotkey-selected Open Hand.
foreach (bool skillOccupied in new[] { false, true })
foreach (bool backpackMode in new[] { false, true })
foreach (int delta in new[] { -3, -1, 0, 1, 3 })
for (int slot = 0; slot < 14; slot++)
{
    Wheel(OpenHandWheelRing.WheelAction.None, slot,
        false, slot, skillOccupied, backpackMode, delta,
        $"hidden indicator passes through slot={slot} skill={skillOccupied} backpack={backpackMode} delta={delta}",
        allowEntry: false);
    OpenHandWheelRing.WheelDecision visibleExit =
        OpenHandWheelRing.Resolve(true, slot, skillOccupied, backpackMode, delta);
    OpenHandWheelRing.WheelDecision hiddenExit =
        OpenHandWheelRing.Resolve(true, slot, skillOccupied, backpackMode, delta, allowEntry: false);
    Equal(visibleExit, hiddenExit, "hidden indicator preserves wheel exit");
}

OpenHandClientConfig wheelConfig = new() { ShowIndicator = false };
Equal(OpenHandWheelRing.WheelAction.None,
    OpenHandWheelRing.Resolve(false, 9, false, false, -1, wheelConfig.ShowIndicator).Action,
    "hidden indicator skips wheel entry");
wheelConfig.ShowIndicator = true;
Equal(OpenHandWheelRing.WheelAction.Enter,
    OpenHandWheelRing.Resolve(false, 9, false, false, -1, wheelConfig.ShowIndicator).Action,
    "showing indicator restores wheel entry");

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
Equal(true, new OpenHandClientConfig().ShowIndicator, "indicator defaults on");
Equal(false, new OpenHandClientConfig().CenterHotbar, "centering defaults off");
Equal(false, new OpenHandClientConfig { ShowIndicator = false }.ShowIndicator, "indicator can be disabled");

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

// Mirror final rendered pixels, including the trailing grid gutter.
Equal(13, OpenHandHudGeometry.MirrorRightPadding(100, 950,
    new (int, int)[] { (110, 158), (889, 937) }, 99), "vanilla right padding");
Equal(20, OpenHandHudGeometry.MirrorRightPadding(100, 1375,
    new (int, int)[] { (115, 187), (1283, 1355) }, 99), "1.5 scale right padding");
Equal(24, OpenHandHudGeometry.MirrorRightPadding(77, 1199,
    new (int, int)[] { (90, 150), (1000, 1060), (1115, 1175) }, 99), "modded width padding");
Equal(13, OpenHandHudGeometry.MirrorRightPadding(100, 950,
    new (int, int)[] { (889, 937), (960, 1008), (90, 150), (945, 945) }, 99), "ignore outside or empty cells");
Equal(13, OpenHandHudGeometry.MirrorRightPadding(100, 950,
    Array.Empty<(int, int)>(), 13), "missing grids fallback");
Equal(0, OpenHandHudGeometry.MirrorRightPadding(100, 950,
    new (int, int)[] { (902, 950) }, 13), "flush right cell padding");

Equal(54, OpenHandHudGeometry.ExtensionWidth(100, 59, 13), "shared background extends 54 pixels");
Equal(81, OpenHandHudGeometry.ExtensionWidth(150, 89, 20), "shared background scaled width");
Equal(54, OpenHandHudGeometry.ExtensionWidth(900, 859, 13), "screen translation does not change extension width");
Equal(0, OpenHandHudGeometry.ExtensionWidth(100, 120, 13), "no extension for inset icon");
Equal(11, OpenHandHudGeometry.ExtensionWidth(100, 102, 13), "extend only exposed padding");

Console.WriteLine("OpenHandSelectionState, wheel ring, gap solver, config, and HUD geometry tests passed.");

Equal(27, OpenHandCenteringGeometry.Shift(1920, 481, 1385), "center 54-pixel extension");
Equal(26, OpenHandCenteringGeometry.Shift(1920, 482, 1385), "odd extension chooses left pixel");
Equal(27, OpenHandCenteringGeometry.Shift(1921, 481, 1385), "odd viewport");
Equal(-23, OpenHandCenteringGeometry.Shift(1920, 531, 1435), "measured position, not hardcoded offset");
Equal(0, OpenHandCenteringGeometry.Shift(800, -100, 900), "oversized bar not centered");
Equal(0, OpenHandCenteringGeometry.Shift(0, 10, 50), "minimized viewport");
Equal(0, OpenHandCenteringGeometry.Shift(1920, 10, 10), "empty geometry");
Equal(false, OpenHandCenteringGeometry.Overlaps(100, 200, 200, 250), "abutting HUD cells allowed");
Equal(true, OpenHandCenteringGeometry.Overlaps(100, 200, 199, 250), "overlapping HUD cells rejected");
Equal(false, OpenHandCenteringGeometry.Overlaps(100, 200, 120, 120), "empty external bounds");
Console.WriteLine("Opt-in centering geometry and config tests passed.");
