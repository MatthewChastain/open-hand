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

    private static readonly ConcurrentDictionary<(EnumAppSide Side, string Uid), OpenHandOffhandState> OffhandStates = new();

    // The substituted slots are PER PLAYER: a foreign mod's deposit must be
    // delivered to its owner's inventory, and on a server every selecting
    // player deposits into their own slot — a shared slot could not tell
    // them apart. Each group carries its own one-slot DummyInventories, so
    // the slot contract (Inventory non-null, real member at index 0) holds
    // for every player independently.
    private static readonly ConcurrentDictionary<(EnumAppSide Side, string Uid), SubstitutedHandSlots> SlotGroups = new();

    // Carry-state probe for the deposit sweep: Common cannot reference the
    // client-side CarryOn interop, so both controllers register the same
    // static read (client and server each resolve carries on their own
    // side). Unset (tests, headless) resolves to not-carrying.
    public static System.Func<IPlayer, bool>? CarryDetector { get; set; }

    private static readonly object NoteLock = new();
    private static readonly HashSet<string> LoggedNotes = new();

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

    public static ItemSlot EmptySlotFor(IPlayer player) => GroupFor(player).MainSlot;

    private static SubstitutedHandSlots GroupFor(IPlayer player) =>
        SlotGroups.GetOrAdd(Key(player), static (_, owner) => new SubstitutedHandSlots(owner), player);

    public static OpenHandSelectionState Get(IPlayer player) =>
        States.GetOrAdd(Key(player), _ => OpenHandSelectionState.Unselected(player.InventoryManager.ActiveHotbarSlotNumber));

    public static OpenHandSelectionState Set(IPlayer player, bool selected, int rememberedHotbarSlot, int revision)
    {
        OpenHandSelectionState next = selected
            ? Get(player).Select(rememberedHotbarSlot, revision)
            : Get(player).Deselect(rememberedHotbarSlot, revision);

        // The substituted slots must never carry a deposit into a fresh
        // selection: reclaim through the sweep rules (deliver to the player's
        // inventory, discard CarryOn artifacts) instead of dropping the stack.
        if (next.IsSelected && SlotGroups.TryGetValue(Key(player), out SubstitutedHandSlots? group))
        {
            SweepGroup(group);
        }

        (EnumAppSide side, string uid) = Key(player);
        States.AddOrUpdate((side, uid), next, (_, current) => revision >= current.Revision ? next : current);
        return States[(side, uid)];
    }

    // The empty-offhand toggle's per-player state and its own substituted
    // slot. Independent of the main-hand selection: both hands can be
    // substituted at once, so the offhand has its own dummy and inventory —
    // sharing either with the main hand would let one state corrupt the
    // other's slot contract (GetSlotId / membership).
    public static ItemSlot EmptyOffhandSlotFor(IPlayer player) => GroupFor(player).OffhandSlot;

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
            SlotGroups.TryRemove((side, uid), out _);
        }
    }

    public static void ClearAll()
    {
        States.Clear();
        OffhandStates.Clear();
        lock (NoteLock)
        {
            LoggedNotes.Clear();
        }

        // Deliberately leave each slot's Inventory attached: it must stay
        // non-null even for a stale reference handed out before the world
        // change (the slot contract). The stale groups are replaced by fresh
        // ones when the next world hands the slots out again.
        SlotGroups.Clear();
    }

    // The join-replay snapshots read ONE side's partition (the server's),
    // flattened back to uid-keyed form for the wire.
    public static IReadOnlyDictionary<string, OpenHandSelectionState> Snapshot(EnumAppSide side) =>
        States.Where(pair => pair.Key.Side == side)
            .ToDictionary(pair => pair.Key.Uid, pair => pair.Value);

    public static IReadOnlyDictionary<string, OpenHandOffhandState> OffhandSnapshot(EnumAppSide side) =>
        OffhandStates.Where(pair => pair.Key.Side == side)
            .ToDictionary(pair => pair.Key.Uid, pair => pair.Value);

    // ---- Deposit sweep -------------------------------------------------

    // Client tick: sweep the local player's two substituted slots. Foreign
    // mods read the same substituted getters the engine does, and some write
    // back into them — Simple Immersive Beehive hands a taken frame out
    // through ActiveHotbarSlot (its TryPutInto sink), and vanilla's own
    // hotbar-sync packet handler assigns ActiveHotbarSlot.Itemstack directly
    // (decompiled 1.22.7 GeneralPacketHandler.HandleSelectedHotbarSlot). The
    // sweep used to delete every deposit, deleting the item itself.
    public static void SweepClientSubstitutedSlots(IPlayer? player)
    {
        if (player is not null && SlotGroups.TryGetValue(Key(player), out SubstitutedHandSlots? group))
        {
            SweepGroup(group);
        }
    }

    // Server tick: sweep every server-side player's slots.
    public static void SweepServerSubstitutedSlots()
    {
        foreach (SubstitutedHandSlots group in SlotGroups.Values)
        {
            if (group.Side == EnumAppSide.Server)
            {
                SweepGroup(group);
            }
        }
    }

    private static void SweepGroup(SubstitutedHandSlots group)
    {
        bool carryNow = CarryDetector?.Invoke(group.Player) ?? false;
        Reclaim(group.MainSlot, group, "main-hand");
        Reclaim(group.OffhandSlot, group, "offhand");
        group.CarryActiveAtLastSweep = carryNow;
    }

    // Foreign mods can leave the substituted slot in a state the engine must
    // never see. The wrapper case is CarryOn's pick-up (it replaces the
    // inventory's index-0 occupant with a LockedItemSlot, which crashes the
    // next pick-up's membership search); the stack case is either CarryOn's
    // place-down artifact or a foreign mod's item transfer, decided by the
    // carry state observed at the PREVIOUS sweep:
    // - a carry was active then: the deposit is CarryOn's injected stack —
    //   it had to stay visible to block behaviors for that tick, and
    //   delivering it would duplicate the placed block, so it is discarded
    //   (the carried item itself was restored by CarryOn's RemoveCarried).
    // - no carry: the deposit is a genuine item hand-out (a beehive frame, a
    //   filled bucket) whose only copy would otherwise vanish — deliver it
    //   to the player's real inventory.
    // Runs every game tick on the client and the server; CarryOn's injection
    // and placement are synchronous within one tick, so the sweep can never
    // race that window and block behaviors still see the injected stack.
    private static void Reclaim(SubstitutedSlot slot, SubstitutedHandSlots group, string hand)
    {
        if (slot.Inventory is DummyInventory inventory && !ReferenceEquals(inventory[0], slot))
        {
            inventory[0] = slot;
        }

        ItemStack? deposit = slot.Itemstack;
        if (deposit is null)
        {
            return;
        }

        slot.Itemstack = null;
        if (group.CarryActiveAtLastSweep)
        {
            LogOnce(group, $"{hand}:discard",
                $"Open Hand: discarded {Describe(deposit)} left in player {group.Player.PlayerUID}'s substituted {hand} slot — a hands carry was active a tick ago, so this is a CarryOn place-down artifact and delivering it would duplicate the placed block.");
            return;
        }

        Deliver(group, hand, deposit);
    }

    private static void Deliver(SubstitutedHandSlots group, string hand, ItemStack deposit)
    {
        try
        {
            ItemStack copy = deposit.Clone();
            if (group.Player.InventoryManager.TryGiveItemstack(copy, true))
            {
                LogOnce(group, $"{hand}:deliver",
                    $"Open Hand: delivered {Describe(deposit)} that a foreign mod deposited into player {group.Player.PlayerUID}'s substituted {hand} slot to their inventory (the sweep would otherwise have deleted it).");
                return;
            }

            if (group.Side == EnumAppSide.Server)
            {
                group.Player.Entity.World.SpawnItemEntity(copy, group.Player.Entity.Pos.XYZ.Add(0.5, 0.5, 0.5), null);
                LogOnce(group, $"{hand}:drop",
                    $"Open Hand: player {group.Player.PlayerUID}'s inventory could not take {Describe(deposit)} from the substituted {hand} slot — dropped it as an item entity at their position instead.");
                return;
            }

            LogOnce(group, $"{hand}:clientdiscard",
                $"Open Hand: discarded {Describe(deposit)} from player {group.Player.PlayerUID}'s substituted {hand} slot — this side holds only a view of the inventory; the authoritative side delivers the deposit.");
        }
        catch (Exception exception)
        {
            LogOnce(group, $"{hand}:error",
                $"Open Hand: delivering {Describe(deposit)} from the substituted {hand} slot failed: {exception.Message} — the stack was discarded rather than left where the sweep would delete it.");
        }
    }

    // Deliberately avoids ItemStack.ToString(): that pulls the stack's code
    // through the collectible, which a fake-or-unresolved stack may not
    // carry. Describe must never throw inside the sweep.
    private static string Describe(ItemStack stack) =>
        $"{stack.StackSize}x {stack.Collectible?.Code?.Path ?? "unknown item"}";

    private static void LogOnce(SubstitutedHandSlots group, string key, string message)
    {
        lock (NoteLock)
        {
            if (!LoggedNotes.Add($"{group.Side}:{group.Player.PlayerUID}:{key}"))
            {
                return;
            }
        }

        group.Player.Entity.Api?.Logger?.Notification(message);
    }

    // The substituted slot must satisfy vanilla's contract for
    // ActiveHotbarSlot / LeftHandItemSlot: its Inventory is never null, and
    // the slot is a real member of that inventory. Mods legitimately
    // dereference slot.Inventory on every tick (Overhaul lib legacy compat
    // crashed on the null this used to return), and CarryOn's LockedItemSlot
    // constructor searches slot.Inventory by reference equality and throws
    // when it does not find the slot in it. So the slot lives at index 0 of
    // its own mod-owned one-slot DummyInventory: Inventory stays non-null,
    // membership searches succeed (GetSlotId returns 0, correct for a stored
    // slot), and the player's own hotbar is never touched.
    private sealed class SubstitutedSlot : DummySlot
    {
        public SubstitutedSlot(DummyInventory containingInventory)
        {
            AttachInventory(containingInventory);
            containingInventory[0] = this;
        }

        // ItemSlot.Inventory is a read-only property over this protected field,
        // so the attach has to live in a subclass.
        private void AttachInventory(InventoryBase? attached) => inventory = attached;
    }

    private sealed class SubstitutedHandSlots
    {
        public readonly IPlayer Player;
        public readonly EnumAppSide Side;
        public readonly SubstitutedSlot MainSlot;
        public readonly SubstitutedSlot OffhandSlot;

        // Carry state as of the previous sweep — see Reclaim.
        public bool CarryActiveAtLastSweep;

        public SubstitutedHandSlots(IPlayer owner)
        {
            Player = owner;
            Side = owner.Entity.Api?.Side ?? EnumAppSide.Client;
            ICoreAPI? api = owner.Entity.Api;
            MainSlot = new SubstitutedSlot(new DummyInventory(api));
            OffhandSlot = new SubstitutedSlot(new DummyInventory(api));
        }
    }
}
