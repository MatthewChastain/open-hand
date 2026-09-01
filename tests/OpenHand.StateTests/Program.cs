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

Console.WriteLine("OpenHandSelectionState and wheel ring tests passed.");
