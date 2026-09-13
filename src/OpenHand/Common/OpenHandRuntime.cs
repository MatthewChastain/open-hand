using System.Collections.Concurrent;
using Vintagestory.API.Common;

namespace OpenHand.Common;

public static class OpenHandRuntime
{
    // Single-player runs the client and server in ONE process, and the client
    // applies toggle requests optimistically before the server confirms. The
    // sides must therefore keep SEPARATE state: a shared dictionary let the
    // client's optimistic write satisfy the server's own stale-revision gate
    // — the server read the client's just-written revision as its own,
    // rejected every request as stale, and the persistence write never ran
    // (the bug that kept the toggles from surviving a relog). The
    // request/update messages keep the two views synchronized,
    // server-authoritative.
    private static readonly ConcurrentDictionary<(EnumAppSide Side, string Uid), OpenHandSelectionState> States = new();
    private static readonly EmptyHandDummySlot EmptyHandSlot = new();

    // The empty-offhand toggle's per-player state and its own substituted
    // slot. Independent of the main-hand selection: both hands can be
    // substituted at once, so the offhand has its own dummy and inventory —
    // sharing either with the main hand would let one state corrupt the
    // other's slot contract (GetSlotId / membership).
    private static readonly ConcurrentDictionary<(EnumAppSide Side, string Uid), OpenHandOffhandState> OffhandStates = new();
    private static readonly EmptyHandDummySlot EmptyOffhandSlot = new();
    private static DummyInventory? offhandContainingInventory;

    // The per-side partition for a player. The entity's API is the real
    // discriminator (the server handler holds the server player, client input
    // holds the client player), and a null API only exists in tests — those
    // resolve to the client partition.
    private static (EnumAppSide Side, string Uid) Key(IPlayer player) =>
        (player.Entity.Api?.Side ?? EnumAppSide.Client, player.PlayerUID);

    public static bool IsSelected(IPlayer? player) =>
        player is not null &&
        States.TryGetValue(Key(player), out OpenHandSelectionState state) &&
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
        States.GetOrAdd(Key(player), _ => OpenHandSelectionState.Unselected(player.InventoryManager.ActiveHotbarSlotNumber));

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

        (EnumAppSide side, string uid) = Key(player);
        States.AddOrUpdate((side, uid), next, (_, current) => revision >= current.Revision ? next : current);
        return States[(side, uid)];
    }

    // The offhand mirror of EmptySlotFor: the substituted offhand slot
    // satisfies the same vanilla slot contract — Inventory non-null, real
    // member (index 0) of a mod-owned one-slot DummyInventory.
    public static ItemSlot EmptyOffhandSlotFor(IPlayer player)
    {
        EnsureOffhandContainingInventory(player.Entity.Api);
        return EmptyOffhandSlot;
    }

    private static void EnsureOffhandContainingInventory(ICoreAPI api)
    {
        lock (InventoryLock)
        {
            if (offhandContainingInventory is not null && ReferenceEquals(offhandContainingInventory.Api, api))
            {
                return;
            }

            DummyInventory inventory = new(api);
            inventory[0] = EmptyOffhandSlot;
            EmptyOffhandSlot.AttachInventory(inventory);
            offhandContainingInventory = inventory;
        }
    }

    // The offhand half of the sweep: the same artifacts the main hand sees
    // (CarryOn locks the left-hand slot through EntityAgent.LeftHandItemSlot,
    // whose getter returns this substituted slot while the toggle is active)
    // must never reach the engine through the substituted getter.
    public static void SweepOffhandSlot()
    {
        lock (InventoryLock)
        {
            if (offhandContainingInventory is null)
            {
                return;
            }

            if (!ReferenceEquals(offhandContainingInventory[0], EmptyOffhandSlot))
            {
                offhandContainingInventory[0] = EmptyOffhandSlot;
            }

            if (!EmptyOffhandSlot.Empty)
            {
                EmptyOffhandSlot.Itemstack = null;
            }
        }
    }

    public static bool IsOffhandEmpty(IPlayer? player) =>
        player is not null &&
        OffhandStates.TryGetValue(Key(player), out OpenHandOffhandState state) &&
        state.IsEmpty;

    public static OpenHandOffhandState GetOffhandState(IPlayer player) =>
        OffhandStates.GetOrAdd(Key(player), _ => new OpenHandOffhandState(false, 0));

    public static OpenHandOffhandState SetOffhandEmpty(IPlayer player, bool empty, int revision)
    {
        OpenHandOffhandState next = new(empty, revision);
        (EnumAppSide side, string uid) = Key(player);
        OffhandStates.AddOrUpdate((side, uid), next, (_, current) => revision >= current.Revision ? next : current);
        return OffhandStates[(side, uid)];
    }

    public static void Clear(IPlayer? player)
    {
        if (player is not null)
        {
            (EnumAppSide side, string uid) = Key(player);
            States.TryRemove((side, uid), out _);
            OffhandStates.TryRemove((side, uid), out _);
        }
    }

    public static void ClearAll()
    {
        States.Clear();
        OffhandStates.Clear();
        lock (InventoryLock)
        {
            // Deliberately leave slot.Inventory attached: it must stay non-null
            // even in the window before the next world's first EmptySlotFor
            // call. The stale instance is replaced once the new API is known.
            containingInventory = null;
            offhandContainingInventory = null;
        }
    }

    // The join-replay snapshots read ONE side's partition (the server's),
    // flattened back to uid-keyed form for the wire.
    public static IReadOnlyDictionary<string, OpenHandSelectionState> Snapshot(EnumAppSide side) =>
        States.Where(pair => pair.Key.Side == side)
            .ToDictionary(pair => pair.Key.Uid, pair => pair.Value);

    public static IReadOnlyDictionary<string, OpenHandOffhandState> OffhandSnapshot(EnumAppSide side) =>
        OffhandStates.Where(pair => pair.Key.Side == side)
            .ToDictionary(pair => pair.Key.Uid, pair => pair.Value);
}
