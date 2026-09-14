using System.Runtime.CompilerServices;
using OpenHand.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

internal static class OpenHandRuntimeTests
{
    internal static void Run()
    {
        // The mod-owned DummyInventory never dereferences the api in the paths
        // under test (InventoryBase skips its network-util wiring for a null
        // api), so the runtime contract tests pass null directly.
        ICoreClientAPI api = null!;
        try
        {
            ClientPlayer player = TestFakes.MakeClientPlayer(
                "uid-runtime-tests", api, new TestFakes.TestInventory(12));
            ClientPlayer playerWithInventory = TestFakes.MakeClientPlayer(
                "uid-runtime-tests-inventory", api, new TestFakes.TestInventory(12));
            ClientPlayer freshPlayer = TestFakes.MakeClientPlayer(
                "uid-runtime-tests-fresh", api, new TestFakes.TestInventory(12));

            // Slot contract (main hand): Inventory never null, the substituted
            // slot is a real member (index 0) of a mod-owned one-slot
            // DummyInventory, and the player's own hotbar is never handed out.
            // Third-party mods dereference slot.Inventory every tick (Overhaul
            // lib legacy compat) and CarryOn's LockedItemSlot searches it by
            // reference identity.
            ItemSlot slot = OpenHandRuntime.EmptySlotFor(playerWithInventory);
            TestFakes.Require(ReferenceEquals(slot, OpenHandRuntime.EmptySlot), "main hand substitutes the shared slot");
            TestFakes.Require(slot.Inventory is DummyInventory { Count: 1 }, "the slot lives in a mod-owned one-slot inventory");
            TestFakes.Require(ReferenceEquals(slot.Inventory![0], slot), "the slot is a real member at index 0");
            TestFakes.Require(slot.Inventory!.GetSlotId(slot) == 0, "GetSlotId finds the substituted slot");
            TestFakes.Require(!ReferenceEquals(slot.Inventory, playerWithInventory.InventoryManager!.GetHotbarInventory()), "the player's own hotbar is never handed out");
            TestFakes.Require(ReferenceEquals(OpenHandRuntime.EmptySlotFor(player), slot), "substitution identity is stable");

            // Slot contract (offhand): the same contract on its own slot and
            // inventory — sharing either with the main hand would let one
            // state corrupt the other's membership.
            ItemSlot offhand = OpenHandRuntime.EmptyOffhandSlotFor(playerWithInventory);
            TestFakes.Require(!ReferenceEquals(offhand, slot), "the offhand substitution is its own slot");
            TestFakes.Require(offhand.Inventory is DummyInventory { Count: 1 }, "the offhand slot lives in its own one-slot inventory");
            TestFakes.Require(!ReferenceEquals(offhand.Inventory, slot.Inventory), "the offhand inventory is not the main hand's");
            TestFakes.Require(ReferenceEquals(offhand.Inventory![0], offhand), "the offhand slot is a real member");
            TestFakes.Require(offhand.Inventory!.GetSlotId(offhand) == 0, "GetSlotId finds the offhand slot");

            // The sweep reclaims CarryOn's placement artifacts (a stranded
            // stack and a foreign occupant wrapper) for both hands.
            slot.Itemstack = (ItemStack)RuntimeHelpers.GetUninitializedObject(typeof(ItemStack));
            slot.Inventory[0] = new ItemSlot(null);
            OpenHandRuntime.SweepSubstitutedSlot();
            TestFakes.Require(ReferenceEquals(slot.Inventory[0], slot), "sweep reclaims the main-hand occupant");
            TestFakes.Require(slot.Empty, "sweep clears an injected main-hand stack");

            offhand.Inventory[0] = new ItemSlot(null);
            OpenHandRuntime.SweepOffhandSlot();
            TestFakes.Require(ReferenceEquals(offhand.Inventory[0], offhand), "sweep reclaims the offhand occupant");
            TestFakes.Require(offhand.Empty, "the offhand sweep leaves the slot empty");

            // Revision merge: a stale write never regresses settled state.
            OpenHandRuntime.Set(playerWithInventory, selected: true, rememberedHotbarSlot: 5, revision: 5);
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

            // Deliberate invariant: ClearAll drops the instance but leaves the
            // shared slot's Inventory attached, so the slot contract holds even
            // in the window before the next world's first EmptySlotFor call.
            OpenHandRuntime.ClearAll();
            TestFakes.Require(slot.Inventory is not null, "ClearAll keeps the shared slot's inventory attached");
        }
        finally
        {
            OpenHandRuntime.ClearAll();
        }
        Console.WriteLine("Passed the substituted-slot contracts, sweep reclamation, and revision-merge checks.");
    }
}
