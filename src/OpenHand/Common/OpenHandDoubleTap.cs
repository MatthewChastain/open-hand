namespace OpenHand.Common;

/// <summary>
/// Pure decision logic for re-tap hotbar key entry (off by default). Pressing
/// the number key of the already-active slot normally does nothing because
/// same-value slot assignment raises no events, so Open Hand treats that
/// gesture as a request for an empty hand. While Open Hand is selected, the
/// same press returns to the slot, since vanilla cannot see a change either.
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
        if (!enabled || requestedSlot is < 0 or >= OpenHandSelectionState.PhysicalHotbarSlots)
        {
            return new(DoubleTapAction.None, activeSlot);
        }

        // A different slot key keeps vanilla semantics: the real assignment
        // fires BeforeActiveSlotChanged, which exits Open Hand on its own.
        if (requestedSlot != activeSlot)
        {
            return new(DoubleTapAction.None, requestedSlot);
        }

        return isSelected
            ? new(DoubleTapAction.ExitToSlot, requestedSlot)
            : new(DoubleTapAction.Enter, requestedSlot);
    }
}
