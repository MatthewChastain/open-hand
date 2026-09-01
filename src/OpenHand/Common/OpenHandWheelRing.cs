namespace OpenHand.Common;

/// <summary>
/// Pure decision logic for the Open Hand wheel ring. The vanilla ring (verified
/// against Vintage Story 1.22.7 HudHotbar.moveToHotbarSlot) spans hotbar slots
/// 0-9 plus the skill slot at index 10 while it is occupied; the offhand at
/// index 11 is never part of the wheel. Open Hand sits immediately before slot
/// 0, mirroring its HUD position between the offhand box and slot 0.
/// </summary>
public static class OpenHandWheelRing
{
    public const int SkillSlotIndex = 10;

    public enum WheelAction
    {
        /// <summary>Let the event pass through to vanilla handling.</summary>
        None,

        /// <summary>Select Open Hand.</summary>
        Enter,

        /// <summary>Leave Open Hand and select <see cref="WheelDecision.Destination" />.</summary>
        ExitToSlot
    }

    public readonly record struct WheelDecision(WheelAction Action, int Destination);

    /// <param name="isSelected">Whether Open Hand is currently selected.</param>
    /// <param name="activeSlot">The current physical active hotbar slot number.</param>
    /// <param name="skillOccupied">Whether the skill slot (hotbar index 10) holds an item.</param>
    /// <param name="backpackMode">Whether the vanilla backpack-mode key is held.</param>
    /// <param name="wheelDelta">Raw wheel delta; negative scrolls forward down the ring.</param>
    public static WheelDecision Resolve(
        bool isSelected,
        int activeSlot,
        bool skillOccupied,
        bool backpackMode,
        int wheelDelta)
    {
        if (backpackMode || wheelDelta == 0)
        {
            return new(WheelAction.None, activeSlot);
        }

        if (isSelected)
        {
            int destination = wheelDelta < 0
                ? 0
                : skillOccupied
                    ? SkillSlotIndex
                    : OpenHandSelectionState.PhysicalHotbarSlots - 1;
            return new(WheelAction.ExitToSlot, destination);
        }

        bool entersFromLastSlot = activeSlot == OpenHandSelectionState.PhysicalHotbarSlots - 1 &&
                                  wheelDelta < 0 &&
                                  !skillOccupied;
        bool entersFromSkill = skillOccupied && activeSlot == SkillSlotIndex && wheelDelta < 0;
        bool entersFromFirstSlot = activeSlot == 0 && wheelDelta > 0;
        if (entersFromLastSlot || entersFromSkill || entersFromFirstSlot)
        {
            return new(WheelAction.Enter, activeSlot);
        }

        return new(WheelAction.None, activeSlot);
    }
}