using OpenHand.Common;

internal static class DoubleTapTests
{
    internal static void Run()
    {
        // Double-tap: re-tapping the active slot's number key selects Open Hand.
        DoubleTap(OpenHandDoubleTap.DoubleTapAction.Enter, 4,
            isSelected: false, activeSlot: 4, requestedSlot: 4,
            name: "re-tap active slot enters");
        DoubleTap(OpenHandDoubleTap.DoubleTapAction.ExitToSlot, 4,
            isSelected: true, activeSlot: 4, requestedSlot: 4,
            name: "re-tap remembered slot exits");

        // Double-tap: other slot keys keep vanilla semantics.
        DoubleTap(OpenHandDoubleTap.DoubleTapAction.None, 6,
            isSelected: false, activeSlot: 4, requestedSlot: 6,
            name: "different slot defers to vanilla");
        DoubleTap(OpenHandDoubleTap.DoubleTapAction.None, 6,
            isSelected: true, activeSlot: 4, requestedSlot: 6,
            name: "different slot defers while selected");

        // Double-tap: out-of-range requests are rejected outright.
        DoubleTap(OpenHandDoubleTap.DoubleTapAction.None, 4,
            isSelected: false, activeSlot: 4, requestedSlot: 10,
            name: "skill slot never double-taps");
        DoubleTap(OpenHandDoubleTap.DoubleTapAction.None, 4,
            isSelected: false, activeSlot: 4, requestedSlot: -1,
            name: "negative slot never double-taps");

        // Double-tap: disabled restores the vanilla entry behavior. The exit half
        // is not a preference: while selected, the physical slot number never moved,
        // so the remembered slot's key press is a vanilla same-value no-op
        // (ClientPlayerInventoryManager setter raises no events) and only Open Hand
        // can act on it.
        DoubleTap(OpenHandDoubleTap.DoubleTapAction.None, 4,
            isSelected: false, activeSlot: 4, requestedSlot: 4, enabled: false,
            name: "disabled passes re-tap through");
        DoubleTap(OpenHandDoubleTap.DoubleTapAction.ExitToSlot, 4,
            isSelected: true, activeSlot: 4, requestedSlot: 4, enabled: false,
            name: "remembered slot key exits even when double-tap entry is disabled");
    }

    private static void DoubleTap(
        OpenHandDoubleTap.DoubleTapAction expectedAction,
        int expectedDestination,
        bool isSelected,
        int activeSlot,
        int requestedSlot,
        string name,
        bool enabled = true)
    {
        OpenHandDoubleTap.DoubleTapDecision decision =
            OpenHandDoubleTap.Resolve(isSelected, activeSlot, requestedSlot, enabled);
        TestHarness.Equal(expectedAction, decision.Action, $"{name} action");
        TestHarness.Equal(expectedDestination, decision.Destination, $"{name} destination");
    }
}
