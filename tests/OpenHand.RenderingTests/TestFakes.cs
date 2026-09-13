using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenHand.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

// Shared fake builders for the game-backed tests. The client player is a real
// uninitialized ClientPlayer seeded with exactly the state the mod reads
// (decompile evidence, VS 1.22.7): worlddata.PlayerUID (public field),
// worlddata.EntityPlayer (plain setter), the inventoryMgr backing field, and
// Entity.Api — a public FIELD on Entity (Vintagestory.API.Common.Entities.
// Entity:101) — so the fake ICoreClientAPI injects directly.
internal static class TestFakes
{
    internal static T Proxy<T>(System.Func<MethodInfo, object?[], object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, RecordingProxy>();
        ((RecordingProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    internal static ClientPlayer MakeClientPlayer(string uid, ICoreClientAPI api, InventoryBase? hotbar = null)
    {
        var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        var data = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
        data.PlayerUID = uid;
        var entity = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
        entity.Api = api;
        data.EntityPlayer = entity;
        AccessTools.Field(typeof(ClientPlayer), "worlddata")!.SetValue(player, data);
        if (hotbar is not null)
        {
            var inventories = new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>();
            inventories[$"hotbar-{uid}"] = hotbar;
            AccessTools.Field(typeof(ClientPlayer), "inventoryMgr")!.SetValue(
                player, new ClientPlayerInventoryManager(inventories, player, null));
        }
        return player;
    }

    // IServerPlayer cannot be DispatchProxy-ed (its hierarchy hides a method
    // from the proxy builder), so the server fake is a real uninitialized
    // ServerPlayer seeded the same way: worlddata.PlayerUID (internal field),
    // worlddata.EntityPlayer (plain setter), and the inventoryMgr field — a
    // real PlayerInventoryManager whose ActiveHotbarSlotNumber defaults to 0
    // (decompiled VS 1.22.7).
    internal static ServerPlayer MakeServerPlayer(string uid, ICoreAPI? api = null)
    {
        var player = (ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
        var data = (ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));
        AccessTools.Field(typeof(ServerWorldPlayerData), "PlayerUID")!.SetValue(data, uid);
        var entity = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
        // OpenHandRuntime partitions state by the entity API's side (the
        // single-player client/server pollution fix), so a server player
        // without an injected API defaults to one whose Side is Server.
        entity.Api = api ?? Proxy<ICoreAPI>((method, _) =>
            method.Name == "get_Side" ? EnumAppSide.Server : Default(method));
        // WatchedAttributes is a public field on Entity (VS 1.22.7); the
        // persistence feature writes the toggles here, so seed a live tree.
        entity.WatchedAttributes = new SyncedTreeAttribute();
        data.EntityPlayer = entity;
        AccessTools.Field(typeof(ServerPlayer), "worlddata")!.SetValue(player, data);
        AccessTools.Field(typeof(ServerPlayer), "inventoryMgr")!.SetValue(
            player, new ServerPlayerInventoryManager(
                new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>(), player, null));
        return player;
    }

    // Permissive DispatchProxy fallback: value-type returns get their default
    // so an unanticipated call cannot throw InvalidCastException; reference
    // returns are null and any dereference fails loudly at use.
    internal static object? Default(MethodInfo method) =>
        method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType)
            : null;

    internal static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    // A recording ILogger: captures Notification/Warning/Error text so tests
    // can assert which diagnostics the mod emitted (and how often).
    internal static ILogger MakeRecordingLogger(out List<string> messages)
    {
        List<string> recorded = [];
        messages = recorded;
        return Proxy<ILogger>((method, args) =>
        {
            if (method.Name is "Notification" or "Warning" or "Error" or
                "Debug" or "VerboseDebug" or "Audit" or "Chat")
            {
                recorded.Add($"{method.Name}: {string.Join(" | ", args.Select(a => a?.ToString() ?? ""))}");
                return null;
            }
            return Default(method);
        });
    }

    // Deterministic carry state without CarryOn installed: inject the 2.0
    // layout's reflection state (libSystem / carryManagerProperty /
    // instanceGetCarried / handsSlot) with a test double whose GetCarried
    // reports the queued value — exactly the invocation shape IsCarryingHands
    // performs against the real CarryOnLib manager.
    internal enum TestCarrySlot
    {
        Hands
    }

    // Public members on purpose: the injection resolves them with default
    // (public-only) reflection flags, mirroring how the interop reads the real
    // CarryOnLibSystem surface.
    internal sealed class CarryHolder(bool carrying)
    {
        public object CarryManager => this;

        public object? GetCarried(Entity entity, TestCarrySlot slot) => carrying ? new object() : null;
    }

    internal static void InjectCarry(bool carrying)
    {
        AccessTools.Field(typeof(CarryOnInterop), "resolved")!.SetValue(null, true);
        AccessTools.Field(typeof(CarryOnInterop), "getCarriedExtension")!.SetValue(null, null);
        AccessTools.Field(typeof(CarryOnInterop), "libSystem")!.SetValue(null, new CarryHolder(carrying));
        AccessTools.Field(typeof(CarryOnInterop), "carryManagerProperty")!.SetValue(
            null, typeof(CarryHolder).GetProperty("CarryManager"));
        AccessTools.Field(typeof(CarryOnInterop), "instanceGetCarried")!.SetValue(
            null, typeof(CarryHolder).GetMethod("GetCarried"));
        AccessTools.Field(typeof(CarryOnInterop), "handsSlot")!.SetValue(null, TestCarrySlot.Hands);
    }

    internal static void ResetCarryInterop()
    {
        foreach (string field in new[] { "getCarriedExtension", "libSystem", "carryManagerProperty", "instanceGetCarried", "handsSlot" })
        {
            AccessTools.Field(typeof(CarryOnInterop), field)!.SetValue(null, null);
        }
        AccessTools.Field(typeof(CarryOnInterop), "resolved")!.SetValue(null, false);
    }

    // SelectOpenHand reads Entity.Controls.HandUse; an uninitialized entity's
    // backing field is null, so seed a real controls object (HandUse defaults
    // to None).
    internal static void SeedControls(EntityPlayer entity)
    {
        AccessTools.Field(typeof(Vintagestory.API.Common.EntityAgent), "controls")!
            .SetValue(entity, new EntityControls());
    }

    // A minimal live inventory the fake player's hotbar can occupy, so tests
    // can prove the mod never hands out a player-owned inventory.
    internal sealed class TestInventory : InventoryBase
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
