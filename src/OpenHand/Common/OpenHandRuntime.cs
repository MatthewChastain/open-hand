using System.Collections.Concurrent;
using Vintagestory.API.Common;

namespace OpenHand.Common;

public static class OpenHandRuntime
{
    private static readonly ConcurrentDictionary<string, OpenHandSelectionState> States = new();
    private static readonly DummySlot EmptyHandSlot = new(null);

    public static bool IsSelected(IPlayer? player) =>
        player is not null &&
        States.TryGetValue(player.PlayerUID, out OpenHandSelectionState state) &&
        state.IsSelected;

    public static ItemSlot EmptySlot => EmptyHandSlot;

    public static OpenHandSelectionState Get(IPlayer player) =>
        States.GetOrAdd(player.PlayerUID, _ => OpenHandSelectionState.Unselected(player.InventoryManager.ActiveHotbarSlotNumber));

    public static OpenHandSelectionState Set(IPlayer player, bool selected, int rememberedHotbarSlot, int revision)
    {
        OpenHandSelectionState next = selected
            ? Get(player).Select(rememberedHotbarSlot, revision)
            : Get(player).Deselect(rememberedHotbarSlot, revision);

        // The shared empty-hand slot must never carry an item into the next
        // selection: if any engine code wrote to ActiveHotbarSlot while Open
        // Hand was selected, drop it here.
        if (next.IsSelected)
        {
            EmptyHandSlot.Itemstack = null;
        }

        States.AddOrUpdate(player.PlayerUID, next, (_, current) => revision >= current.Revision ? next : current);
        return States[player.PlayerUID];
    }

    public static void Clear(IPlayer? player)
    {
        if (player is not null)
        {
            States.TryRemove(player.PlayerUID, out _);
        }
    }

    public static void ClearAll() => States.Clear();

    public static IReadOnlyDictionary<string, OpenHandSelectionState> Snapshot() =>
        new Dictionary<string, OpenHandSelectionState>(States);
}
