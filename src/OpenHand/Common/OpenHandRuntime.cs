using System.Collections.Concurrent;
using Vintagestory.API.Common;

namespace OpenHand.Common;

public static class OpenHandRuntime
{
    private static readonly ConcurrentDictionary<string, OpenHandSelectionState> States = new();
    private static readonly EmptyHandDummySlot EmptyHandSlot = new();

    public static bool IsSelected(IPlayer? player) =>
        player is not null &&
        States.TryGetValue(player.PlayerUID, out OpenHandSelectionState state) &&
        state.IsSelected;

    public static ItemSlot EmptySlot => EmptyHandSlot;

    // The substituted slot must satisfy vanilla's contract for
    // ActiveHotbarSlot: it always belongs to the player's hotbar inventory.
    // Mods legitimately dereference slot.Inventory on every tick (Overhaul
    // lib legacy compat crashed on the null this used to return), so attach
    // the caller's hotbar inventory before handing the slot out. GetSlotId
    // then returns -1, the documented result for a slot not stored in the
    // inventory. The reference write is atomic; with multiple players the
    // last writer wins and identity comparisons stay correct either way.
    public static ItemSlot EmptySlotFor(IPlayer player)
    {
        if (player.InventoryManager?.GetHotbarInventory() is InventoryBase inventory &&
            !ReferenceEquals(EmptyHandSlot.Inventory, inventory))
        {
            EmptyHandSlot.AttachInventory(inventory);
        }

        return EmptyHandSlot;
    }

    // ItemSlot.Inventory is a read-only property over this protected field,
    // so the attach has to live in a subclass.
    private sealed class EmptyHandDummySlot : DummySlot
    {
        public EmptyHandDummySlot() : base(null) { }

        public void AttachInventory(InventoryBase? inventory) => this.inventory = inventory;
    }

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
