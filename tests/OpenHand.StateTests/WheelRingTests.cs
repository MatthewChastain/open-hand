using OpenHand.Common;

internal static class WheelRingTests
{
    internal static void Run()
    {
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
            TestHarness.Equal(visibleExit, hiddenExit, "hidden indicator preserves wheel exit");
        }

        OpenHandClientConfig wheelConfig = new() { ShowIndicator = false };
        TestHarness.Equal(OpenHandWheelRing.WheelAction.None,
            OpenHandWheelRing.Resolve(false, 9, false, false, -1, wheelConfig.ShowIndicator).Action,
            "hidden indicator skips wheel entry");
        wheelConfig.ShowIndicator = true;
        TestHarness.Equal(OpenHandWheelRing.WheelAction.Enter,
            OpenHandWheelRing.Resolve(false, 9, false, false, -1, wheelConfig.ShowIndicator).Action,
            "showing indicator restores wheel entry");
    }

    private static void Wheel(
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
        TestHarness.Equal(expectedAction, decision.Action, $"{name} action");
        TestHarness.Equal(expectedDestination, decision.Destination, $"{name} destination");
    }
}
