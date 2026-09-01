namespace OpenHand.Common;

public readonly record struct OpenHandSelectionState(bool IsSelected, int RememberedHotbarSlot, int Revision)
{
    public const int PhysicalHotbarSlots = 10;

    public static OpenHandSelectionState Unselected(int activeHotbarSlot, int revision = 0) =>
        new(false, NormalizePhysicalSlot(activeHotbarSlot), revision);

    public OpenHandSelectionState Select(int activeHotbarSlot, int revision) =>
        new(true, NormalizePhysicalSlot(activeHotbarSlot), revision);

    public OpenHandSelectionState Deselect(int activeHotbarSlot, int revision) =>
        new(false, NormalizePhysicalSlot(activeHotbarSlot), revision);

    public static int NormalizePhysicalSlot(int slot) =>
        slot is >= 0 and < PhysicalHotbarSlots ? slot : PhysicalHotbarSlots - 1;
}
