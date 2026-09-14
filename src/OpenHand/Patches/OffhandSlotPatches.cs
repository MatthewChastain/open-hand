using System.Reflection;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Common;

namespace OpenHand.Patches;

// The empty-offhand toggle substitutes the offhand's slot resolution the same
// way ActiveHandPatch substitutes the main hand: postfix-only, item stacks
// untouched, and the substituted slot is a real member of a mod-owned
// inventory (the slot-contract invariant). Two read paths reach the offhand
// (decompiled VS 1.22.7), and both must be substituted or the "empty" lie
// leaks into whichever system takes the other path:
// - Vintagestory.Common.PlayerInventoryManager.OffhandHotbarSlot — the base
//   getter returns Inventories["hotbar-<uid>"][11]; neither
//   ClientPlayerInventoryManager nor ServerPlayerInventoryManager overrides
//   it, so patching the base getter covers both sides.
// - Vintagestory.API.Common.EntityPlayer.LeftHandItemSlot — an override of
//   EntityAgent's virtual get/set that re-fetches GetHotbarInventory()[11] on
//   every call, so the override itself (never the base property) is the patch
//   target: virtual dispatch on player entities lands there.
// Both patches are registered on client and server and stay inert while the
// toggle is off: the postfix reads per-player runtime state and passes the
// real slot through untouched otherwise.
[HarmonyPatch]
internal static class OffhandInventoryPatch
{
    private static readonly FieldInfo? PlayerField =
        AccessTools.Field("Vintagestory.Common.PlayerInventoryManager:player");

    internal static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName("Vintagestory.Common.PlayerInventoryManager");
        return type is null ? null : AccessTools.PropertyGetter(type, "OffhandHotbarSlot");
    }

    private static void Postfix(object __instance, ref ItemSlot __result)
    {
        if (PlayerField?.GetValue(__instance) is IPlayer player && OpenHandRuntime.IsOffhandEmpty(player))
        {
            __result = OpenHandRuntime.EmptyOffhandSlotFor(player);
        }
    }
}

[HarmonyPatch]
internal static class OffhandEntityPatch
{
    internal static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName("Vintagestory.API.Common.EntityPlayer");
        return type is null ? null : AccessTools.PropertyGetter(type, "LeftHandItemSlot");
    }

    private static void Postfix(EntityPlayer __instance, ref ItemSlot __result)
    {
        if (__instance.Player is { } player && OpenHandRuntime.IsOffhandEmpty(player))
        {
            __result = OpenHandRuntime.EmptyOffhandSlotFor(player);
        }
    }
}
