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

    private static readonly object InventoryLock = new();
    private static DummyInventory? containingInventory;

    // The substituted slot must satisfy vanilla's contract for
    // ActiveHotbarSlot: its Inventory is never null, and the slot is a real
    // member of that inventory. Mods legitimately dereference slot.Inventory
    // on every tick (Overhaul lib legacy compat crashed on the null this used
    // to return), and CarryOn's LockedItemSlot constructor searches
    // slot.Inventory by reference equality and throws when it does not find
    // the slot in it. So the slot lives at index 0 of a mod-owned one-slot
    // DummyInventory: Inventory stays non-null, membership searches succeed
    // (GetSlotId returns 0, correct for a stored slot), and the player's own
    // hotbar is never touched. The inventory is created lazily per API — a
    // single process can host several worlds over time, and ClearAll drops
    // the instance when the world changes.
    public static ItemSlot EmptySlotFor(IPlayer player)
    {
        EnsureContainingInventory(player.Entity.Api);
        return EmptyHandSlot;
    }

    private static void EnsureContainingInventory(ICoreAPI api)
    {
        lock (InventoryLock)
        {
            if (containingInventory is not null && ReferenceEquals(containingInventory.Api, api))
            {
                return;
            }

            DummyInventory inventory = new(api);
            inventory[0] = EmptyHandSlot;
            EmptyHandSlot.AttachInventory(inventory);
            containingInventory = inventory;
        }
    }

    // Foreign mods can leave the substituted slot in a state the engine must
    // never see. CarryOn's place-down leaves the placed block's stack in the
    // active hand slot (its failure branch clears it, its success branch does
    // not, and vanilla TryPlaceBlock does not consume it), which duplicates
    // the block on the next interaction; its pick-up replaces the inventory's
    // index-0 occupant with a LockedItemSlot wrapper, which crashes the next
    // pick-up's membership search. Neither artifact is reachable through the
    // substituted getter afterward, so both are reclaimed here. Runs every
    // game tick on the client and the server; CarryOn's injection and
    // placement are synchronous within one tick, so the sweep can never race
    // that window and block behaviors still see the injected stack.
    public static void SweepSubstitutedSlot()
    {
        lock (InventoryLock)
        {
            if (containingInventory is null)
            {
                return;
            }

            if (!ReferenceEquals(containingInventory[0], EmptyHandSlot))
            {
                containingInventory[0] = EmptyHandSlot;
            }

            if (!EmptyHandSlot.Empty)
            {
                EmptyHandSlot.Itemstack = null;
            }
        }
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

    public static void ClearAll()
    {
        States.Clear();
        lock (InventoryLock)
        {
            // Deliberately leave slot.Inventory attached: it must stay non-null
            // even in the window before the next world's first EmptySlotFor
            // call. The stale instance is replaced once the new API is known.
            containingInventory = null;
        }
    }

    public static IReadOnlyDictionary<string, OpenHandSelectionState> Snapshot() =>
        new Dictionary<string, OpenHandSelectionState>(States);
}
