using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenHand.Common;
using OpenHand.Patches;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

internal static class ActiveHandInventoryTests
{
    internal static void Run()
    {
        TestInventory hotbar = new(12);

        // IPlayer cannot be DispatchProxy-ed (inaccessible base member), so
        // use a real uninitialized ClientPlayer and inject the state the
        // patch and runtime actually read: PlayerUID via world data and
        // InventoryManager via its backing field.
        var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        var data = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
        data.PlayerUID = "uid-active-hand-inventory";
        AccessTools.Field(typeof(ClientPlayer), "worlddata")!.SetValue(player, data);

        var inventories =
            new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>();
        inventories["hotbar-uid-active-hand-inventory"] = hotbar;
        // The real manager: its constructor only stores the arguments, and
        // the getter paths the runtime touches never dereference game.
        var manager = new ClientPlayerInventoryManager(inventories, player, null);
        AccessTools.Field(typeof(ClientPlayer), "inventoryMgr")!.SetValue(player, manager);
        MethodInfo postfix = AccessTools.Method(typeof(ActiveHandPatch), "Postfix")!;

        try
        {
            OpenHandRuntime.Set(player, selected: true, rememberedHotbarSlot: 5, revision: 1);
            object[] args = [manager, hotbar[5]];
            postfix.Invoke(null, args);
            ItemSlot result = (ItemSlot)args[1];

            Require(ReferenceEquals(result, OpenHandRuntime.EmptySlot), "selected hand substitutes the shared slot");
            Require(ReferenceEquals(result.Inventory, hotbar), "substituted slot carries the hotbar inventory");

            // The exact dereference that crashed Overhaul lib legacy compat:
            // Inventory must be non-null and GetSlotId must return the
            // documented -1 for a slot not stored in the inventory.
            Require(result.Inventory!.GetSlotId(result) == -1, "GetSlotId is safe and returns -1 for the substitute");
            Require(result.Empty, "substituted slot stays empty");

            object[] repeat = [manager, hotbar[5]];
            postfix.Invoke(null, repeat);
            Require(ReferenceEquals((ItemSlot)repeat[1], result), "substitution identity is stable");

            OpenHandRuntime.Set(player, selected: false, rememberedHotbarSlot: 5, revision: 2);
            object[] deselect = [manager, hotbar[5]];
            postfix.Invoke(null, deselect);
            Require(ReferenceEquals((ItemSlot)deselect[1], hotbar[5]), "deselected hand returns the vanilla slot");
        }
        finally
        {
            OpenHandRuntime.ClearAll();
        }
        Console.WriteLine("Passed substituted-slot inventory contract and Overhaul lib crash-path checks.");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    private sealed class TestInventory : InventoryBase
    {
        private readonly ItemSlot[] slots;

        public TestInventory(int count) : base("test", "hotbar", null)
        {
            slots = new ItemSlot[count];
            for (int i = 0; i < count; i++) slots[i] = new ItemSlot(null);
        }

        public override int Count => slots.Length;

        public override ItemSlot this[int slotId]
        {
            get => slots[slotId];
            set => slots[slotId] = value;
        }

        public override void FromTreeAttributes(ITreeAttribute tree) { }

        public override void ToTreeAttributes(ITreeAttribute tree) { }
    }
}
