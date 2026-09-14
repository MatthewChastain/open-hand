using System.Reflection;
using System.Runtime.CompilerServices;
using OpenHand.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

internal static class OpenHandRuntimeTests
{
    internal static void Run()
    {
        // The delivery pipeline runs the engine's own give logic, which reads
        // the acting player's api (the onitemgrabbed event push, the receiving
        // inventory's Api.World in DidModifyItemSlot), so the inventory-backed
        // player gets a proxy api whose Event/World/ClassRegistry are inert
        // stand-ins. The slot-contract-only players keep a null api.
        ICoreClientAPI api = TestFakes.Proxy<ICoreClientAPI>(HandleApiCall);
        try
        {
            ClientPlayer player = TestFakes.MakeClientPlayer(
                "uid-runtime-tests", api, new TestFakes.TestInventory(12));
            ClientPlayer playerWithInventory = TestFakes.MakeClientPlayer(
                "uid-runtime-tests-inventory", api,
                new TestPlayerInventory(12, "uid-runtime-tests-inventory", api));
            ClientPlayer freshPlayer = TestFakes.MakeClientPlayer(
                "uid-runtime-tests-fresh", api, new TestFakes.TestInventory(12));

            // Slot contract (main hand): Inventory never null, the substituted
            // slot is a real member (index 0) of a mod-owned one-slot
            // DummyInventory, and the player's own hotbar is never handed out.
            // Third-party mods dereference slot.Inventory every tick (Overhaul
            // lib legacy compat) and CarryOn's LockedItemSlot searches it by
            // reference identity.
            ItemSlot slot = OpenHandRuntime.EmptySlotFor(playerWithInventory);
            TestFakes.Require(slot.Inventory is DummyInventory { Count: 1 }, "the slot lives in a mod-owned one-slot inventory");
            TestFakes.Require(ReferenceEquals(slot.Inventory![0], slot), "the slot is a real member at index 0");
            TestFakes.Require(slot.Inventory!.GetSlotId(slot) == 0, "GetSlotId finds the substituted slot");
            TestFakes.Require(!ReferenceEquals(slot.Inventory, playerWithInventory.InventoryManager!.GetHotbarInventory()), "the player's own hotbar is never handed out");
            TestFakes.Require(ReferenceEquals(OpenHandRuntime.EmptySlotFor(playerWithInventory), slot), "substitution identity is stable per player");

            // Slots are per player (a foreign deposit must be delivered to its
            // owner — a shared slot could not tell players apart on a server).
            ItemSlot otherSlot = OpenHandRuntime.EmptySlotFor(player);
            TestFakes.Require(!ReferenceEquals(otherSlot, slot), "each player gets their own substituted slot");
            TestFakes.Require(!ReferenceEquals(otherSlot.Inventory, slot.Inventory), "each player's slot lives in its own inventory");

            // Slot contract (offhand): the same contract on its own slot and
            // inventory — sharing either with the main hand would let one
            // state corrupt the other's membership.
            ItemSlot offhand = OpenHandRuntime.EmptyOffhandSlotFor(playerWithInventory);
            TestFakes.Require(!ReferenceEquals(offhand, slot), "the offhand substitution is its own slot");
            TestFakes.Require(offhand.Inventory is DummyInventory { Count: 1 }, "the offhand slot lives in its own one-slot inventory");
            TestFakes.Require(!ReferenceEquals(offhand.Inventory, slot.Inventory), "the offhand inventory is not the main hand's");
            TestFakes.Require(ReferenceEquals(offhand.Inventory![0], offhand), "the offhand slot is a real member");
            TestFakes.Require(offhand.Inventory!.GetSlotId(offhand) == 0, "GetSlotId finds the offhand slot");

            // THE FIX (issue #20): a foreign mod's deposit into the
            // substituted slot — Simple Immersive Beehive hands a taken frame
            // out through ActiveHotbarSlot — is delivered to the owner's real
            // inventory by the sweep instead of being deleted with it.
            OpenHandRuntime.CarryDetector = _ => false;
            TestPlayerInventory hotbar = (TestPlayerInventory)playerWithInventory.InventoryManager!.GetHotbarInventory()!;
            ItemStack deposit = MakeStack(3);
            slot.Itemstack = deposit;
            OpenHandRuntime.SweepClientSubstitutedSlots(playerWithInventory);
            TestFakes.Require(slot.Empty, "the sweep clears a foreign main-hand deposit");
            TestFakes.Require(TotalStored(hotbar) == 3,
                "the sweep delivers the deposit to the player's inventory");

            // The offhand substitution delivers the same way (into the same
            // inventory; whether the engine merges or stacks beside it is
            // vanilla's own merge semantics, not this contract).
            ItemStack offhandDeposit = MakeStack(1);
            offhand.Itemstack = offhandDeposit;
            OpenHandRuntime.SweepClientSubstitutedSlots(playerWithInventory);
            TestFakes.Require(offhand.Empty, "the sweep clears a foreign offhand deposit");
            TestFakes.Require(TotalStored(hotbar) == 4,
                "the offhand deposit reaches the player's inventory");

            // CarryOn's place-down artifact is still discarded, not delivered:
            // the carry was active at the previous sweep, so the deposit is
            // the injected stack whose delivery would duplicate the placed
            // block. The artifact must also stay visible for the tick it is
            // injected in (block behaviors read it through the getter).
            OpenHandRuntime.CarryDetector = _ => true;
            OpenHandRuntime.SweepClientSubstitutedSlots(playerWithInventory);
            TestFakes.Require(slot.Empty, "the carry-state sweep leaves the slot empty");
            OpenHandRuntime.CarryDetector = _ => false;
            ItemStack artifact = MakeStack(1);
            slot.Itemstack = artifact;
            TestFakes.Require(!slot.Empty, "an injected stack stays visible within its tick");
            OpenHandRuntime.SweepClientSubstitutedSlots(playerWithInventory);
            TestFakes.Require(slot.Empty, "the sweep clears the carry place-down artifact");
            TestFakes.Require(TotalStored(hotbar) == 4,
                "the carry place-down artifact is not delivered");

            // The sweep reclaims CarryOn's stranded wrapper occupant for both
            // hands regardless of deposits.
            slot.Inventory[0] = new ItemSlot(null);
            OpenHandRuntime.SweepClientSubstitutedSlots(playerWithInventory);
            TestFakes.Require(ReferenceEquals(slot.Inventory[0], slot), "sweep reclaims the main-hand occupant");
            offhand.Inventory[0] = new ItemSlot(null);
            OpenHandRuntime.SweepClientSubstitutedSlots(playerWithInventory);
            TestFakes.Require(ReferenceEquals(offhand.Inventory[0], offhand), "sweep reclaims the offhand occupant");

            // A deposit pending when a fresh selection starts is reclaimed
            // through the same rules (delivered, not dropped).
            ItemStack pending = MakeStack(2);
            slot.Itemstack = pending;
            OpenHandRuntime.Set(playerWithInventory, selected: true, rememberedHotbarSlot: 5, revision: 5);
            TestFakes.Require(slot.Empty, "a fresh selection reclaims a pending deposit");
            TestFakes.Require(TotalStored(hotbar) == 6,
                "the pending deposit is delivered into the fresh selection");

            // Revision merge: a stale write never regresses settled state.
            OpenHandSelectionState stale = OpenHandRuntime.Set(playerWithInventory, selected: false, rememberedHotbarSlot: 2, revision: 3);
            TestFakes.Require(stale.IsSelected && stale.RememberedHotbarSlot == 5, "a stale selection revision never regresses state");
            OpenHandRuntime.SetOffhandEmpty(playerWithInventory, empty: true, revision: 9);
            OpenHandOffhandState staleOffhand = OpenHandRuntime.SetOffhandEmpty(playerWithInventory, empty: false, revision: 8);
            TestFakes.Require(staleOffhand.IsEmpty, "a stale offhand revision never regresses state");

            // A fresh player resolves to unselected, remembering the physical slot
            // the manager reports (whatever this game build's default is).
            int physicalSlot = freshPlayer.InventoryManager!.ActiveHotbarSlotNumber;
            OpenHandSelectionState fresh = OpenHandRuntime.Get(freshPlayer);
            TestFakes.Require(!fresh.IsSelected && fresh.RememberedHotbarSlot == physicalSlot,
                "a fresh player starts unselected on the physical slot");

            OpenHandRuntime.Clear(player);
            TestFakes.Require(!OpenHandRuntime.IsSelected(player) && !OpenHandRuntime.IsOffhandEmpty(player), "clear drops the player's state");

            // B0YAR's crash (NullReferenceException in ActiveHandPatch.Postfix,
            // reached from EntityPlayer.LightHsv during the render loop): the
            // hand-substitution patches run for every nearby player's inventory
            // manager on every tick, including a remote player whose Entity
            // link is transiently null while despawning, respawning, or
            // disconnecting. IsSelected/IsOffhandEmpty must tolerate that
            // instead of dereferencing player.Entity unconditionally.
            ClientPlayer entitylessPlayer = TestFakes.MakeClientPlayer(
                "uid-runtime-tests-no-entity", api, withEntity: false);
            TestFakes.Require(!OpenHandRuntime.IsSelected(entitylessPlayer), "a player with no entity is never selected");
            TestFakes.Require(!OpenHandRuntime.IsOffhandEmpty(entitylessPlayer), "a player with no entity never reports an empty offhand");

            // Deliberate invariant: ClearAll drops the groups but leaves each
            // slot's Inventory attached, so the slot contract holds even for a
            // stale reference in the window before the next world's first
            // EmptySlotFor call.
            OpenHandRuntime.ClearAll();
            TestFakes.Require(slot.Inventory is not null, "ClearAll keeps the substituted slot's inventory attached");
        }
        finally
        {
            OpenHandRuntime.CarryDetector = null;
            OpenHandRuntime.ClearAll();
        }
        Console.WriteLine("Passed the per-player substituted-slot contracts, deposit delivery, sweep reclamation, and revision-merge checks.");
    }

    // The engine merges same-item stacks by its own semantics (the fake
    // collectible does not implement merging, so deposits may land beside
    // each other); the delivery contract counts the whole hotbar.
    private static int TotalStored(TestPlayerInventory hotbar) =>
        Enumerable.Range(0, hotbar.Count).Sum(i => hotbar[i]!.Itemstack?.StackSize ?? 0);

    // A real ItemStack over a test collectible: the delivery path runs the
    // engine's own merge pipeline, which dereferences the stack's collectible
    // (storage flags, inventory-modified callbacks), so an uninitialized
    // stack cannot exercise it.
    private static ItemStack MakeStack(int stackSize) =>
        new((Item)RuntimeHelpers.GetUninitializedObject(typeof(TestItem)), stackSize);

    private sealed class TestItem : Item
    {
        public override EnumItemStorageFlags GetStorageFlags(ItemStack itemstack) => EnumItemStorageFlags.General;

        // The engine's merge path iterates the collectible's behavior list,
        // which a hand-built collectible does not carry; reporting zero keeps
        // every deposit on the empty-slot path (next free slot), so
        // TryMergeStacks is never reached.
        public override int GetMergableQuantity(ItemStack sinkStack, ItemStack sourceStack, EnumMergePriority priority) => 0;

        public override TransitionState[] UpdateAndGetTransitionStates(IWorldAccessor world, ItemSlot inslot) => [];

        public override void OnModifiedInInventorySlot(IWorldAccessor world, ItemSlot slot, ItemStack? extractedStack = null) { }
    }

    // TryGiveItemstack only considers InventoryBasePlayer inventories that
    // belong to the acting player (InventoryBasePlayer.HasOpened/
    // CanPlayerAccess compare uids), so the receiving hotbar must derive from
    // it — the plain TestInventory elsewhere in the suite is skipped.
    private sealed class TestPlayerInventory(int slotCount, string uid, ICoreAPI? api)
        : InventoryBasePlayer("hotbar", uid, api)
    {
        private readonly ItemSlot[] slots =
            [.. Enumerable.Range(0, slotCount).Select(_ => new ItemSlot(null))];

        public override int Count => slots.Length;

        public override ItemSlot? this[int slotId]
        {
            get => slots[slotId];
            set => slots[slotId] = value!;
        }

        public override void FromTreeAttributes(ITreeAttribute tree) { }

        public override void ToTreeAttributes(ITreeAttribute tree) { }
    }

    // Inert stand-ins for exactly the api members the engine's give pipeline
    // touches: the onitemgrabbed event push, the receiving inventory's
    // Api.World, the inventory ctor's network-util wiring, and the side.
    private static object? HandleApiCall(MethodInfo method, object?[] args) => method.Name switch
    {
        "get_Event" => TestFakes.Proxy<IEventAPI>((m, a) => TestFakes.Default(m)),
        "get_World" => TestFakes.Proxy<IWorldAccessor>((m, a) => TestFakes.Default(m)),
        "get_ClassRegistry" => TestFakes.Proxy<IClassRegistryAPI>((m, a) => TestFakes.Default(m)),
        "get_Side" => EnumAppSide.Client,
        _ => TestFakes.Default(method)
    };
}
