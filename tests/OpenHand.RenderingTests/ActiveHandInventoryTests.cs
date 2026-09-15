using System.Reflection;
using HarmonyLib;
using OpenHand.Common;
using OpenHand.Patches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

internal static class ActiveHandInventoryTests
{
    internal static void Run()
    {
        // InventoryBase skips its network-util wiring for a null api, which is
        // all the substituted-slot contract below exercises.
        ICoreClientAPI api = null!;
        TestFakes.TestInventory hotbar = new(12);
        ClientPlayer player = TestFakes.MakeClientPlayer("uid-active-hand-inventory", api, hotbar);
        MethodInfo postfix = AccessTools.Method(typeof(ActiveHandPatch), "Postfix")!;
        try
        {
            OpenHandRuntime.Set(player, selected: true, rememberedHotbarSlot: 5, revision: 1);
            ItemSlot result = Substitute(postfix, hotbar, player.InventoryManager!);
            TestFakes.Require(ReferenceEquals(result, OpenHandRuntime.EmptySlotFor(player)), "selected hand substitutes the player's slot");

            // The exact dereference that crashed Overhaul lib legacy compat:
            // Inventory must be non-null, and the slot must be a real member of
            // it. The 1.0.1 build attached the player's hotbar WITHOUT
            // membership (GetSlotId -1) and CarryOn's LockedItemSlot, which
            // searches slot.Inventory by reference identity, crashed on chest
            // pick-up — the slot must live in the mod-owned inventory instead.
            TestFakes.Require(result.Inventory is not null, "the substituted slot carries a non-null inventory");
            TestFakes.Require(ReferenceEquals(result.Inventory![0], result), "the substituted slot is an inventory member");
            TestFakes.Require(result.Inventory!.GetSlotId(result) == 0, "GetSlotId finds the substitute");
            TestFakes.Require(!ReferenceEquals(result.Inventory, hotbar), "the substituted slot never carries the player's own hotbar");
            TestFakes.Require(result.Empty, "substituted slot stays empty");
            TestFakes.Require(ReferenceEquals(Substitute(postfix, hotbar, player.InventoryManager!), result), "substitution identity is stable");

            OpenHandRuntime.Set(player, selected: false, rememberedHotbarSlot: 5, revision: 2);
            TestFakes.Require(ReferenceEquals(Substitute(postfix, hotbar, player.InventoryManager!), hotbar[5]), "deselected hand returns the vanilla slot");
        }
        finally
        {
            OpenHandRuntime.ClearAll();
        }
        Console.WriteLine("Passed the substituted-slot inventory contract and postfix passthrough checks.");
    }

    private static ItemSlot Substitute(MethodInfo postfix, TestFakes.TestInventory hotbar, object manager)
    {
        object[] args = [manager, hotbar[5]];
        postfix.Invoke(null, args);
        return (ItemSlot)args[1];
    }
}
