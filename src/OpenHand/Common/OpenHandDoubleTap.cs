namespace OpenHand.Common;

/// <summary>
/// Pure decision logic for the hotbar number keys around the empty hand.
/// Pressing the number key of the already-active slot normally does nothing
/// because same-value slot assignment raises no events, so Open Hand treats
/// that gesture as a request for an empty hand — opt-in via the double-tap
/// preference. While Open Hand is selected, the same press always returns to
/// the slot regardless of that preference: the physical slot number never
/// moved, so vanilla applies a same-value no-op and fires no events
/// (decompiled 1.22.7 ClientPlayerInventoryManager.ActiveHotbarSlotNumber
/// setter) — exiting is Open Hand's own responsibility there.
/// </summary>
public static class OpenHandDoubleTap
{
    public enum DoubleTapAction
    {
        /// <summary>Let the press pass through to vanilla handling.</summary>
        None,

        /// <summary>Select Open Hand.</summary>
        Enter,

        /// <summary>Leave Open Hand and select <see cref="DoubleTapDecision.Destination" />.</summary>
        ExitToSlot
    }

    public readonly record struct DoubleTapDecision(DoubleTapAction Action, int Destination);

    /// <param name="isSelected">Whether Open Hand is currently selected.</param>
    /// <param name="activeSlot">The current physical active hotbar slot number.</param>
    /// <param name="requestedSlot">The physical slot whose number key was pressed.</param>
    /// <param name="enabled">Whether the client double-tap preference is on.</param>
    public static DoubleTapDecision Resolve(bool isSelected, int activeSlot, int requestedSlot, bool enabled)
    {
        if (requestedSlot is < 0 or >= OpenHandSelectionState.PhysicalHotbarSlots)
        {
            return new(DoubleTapAction.None, activeSlot);
        }

        // A different slot key keeps vanilla semantics: the real assignment
        // fires BeforeActiveSlotChanged, which exits Open Hand on its own.
        if (requestedSlot != activeSlot)
        {
            return new(DoubleTapAction.None, requestedSlot);
        }

        // While selected, the same press must return to the slot: vanilla
        // sees a same-value assignment and raises no events, so no other
        // path can act on it. That half is unconditional; the entry gesture
        // is the only part behind the double-tap preference.
        if (isSelected)
        {
            return new(DoubleTapAction.ExitToSlot, requestedSlot);
        }

        return enabled
            ? new(DoubleTapAction.Enter, requestedSlot)
            : new(DoubleTapAction.None, activeSlot);
    }
}
