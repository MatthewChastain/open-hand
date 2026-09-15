using System.Reflection;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Client;
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
// real slot through untouched otherwise. A third patch below
// (OffhandFlipPatch) guards the one vanilla path that WRITES through the
// substituted getter.
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

// The vanilla "flip hand slots" hotkey (fliphandslots → HudHotbar
// .KeyFlipHandSlots, decompiled 1.22.7) flips the active hotbar slot with
// EntityPlayer.LeftHandItemSlot. While the empty-offhand substitution is
// active that pair is a real slot and the mod-owned dummy, and vanilla's
// flip WRITES the active item into the dummy: the per-tick sweep then
// deletes it (the substituted slot is not storage), while the flip packet —
// which names the dummy inventory ("dummy-N") the server cannot resolve —
// is ignored, so the item still exists server-side. The client shows the
// item gone while the server still holds it: the "pocket dimension" of the
// Mod DB report, retrievable by toggling off and flipping again (that flip
// runs on real slots, and its broadcast re-syncs the server's copy back).
// The honest behavior is declining the flip: the engine reads the hand as
// empty, and an empty offhand is not a place to put an item. The prefix
// below skips the vanilla flip for as long as the substitution is active
// (which also covers a CarryOn hands-carry that began while the toggle was
// already on — flipping would pair the active slot with the substituted
// hand there too); with the toggle off it passes through untouched.
[HarmonyPatch]
internal static class OffhandFlipPatch
{
    private static bool loggedDecline;

    internal static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName("Vintagestory.Client.NoObf.HudHotbar");
        return type is null ? null : AccessTools.Method(type, "KeyFlipHandSlots");
    }

    // Returns false to skip the vanilla flip while the offhand reads empty;
    // true otherwise, so vanilla runs unchanged. Client-only target: the
    // flip hotkey exists only in HudHotbar, and with the substitution off
    // the vanilla flip pairs two real slots and needs no guard.
    private static bool Prefix()
    {
        IClientPlayer? player = OpenHandModSystem.ClientApi?.World?.Player;
        if (player is null || !OpenHandRuntime.IsOffhandEmpty(player))
        {
            return true;
        }

        if (!loggedDecline)
        {
            loggedDecline = true;
            OpenHandModSystem.ClientApi?.Logger.Notification(
                "Open Hand: declined the vanilla flip-hand-slots hotkey while the empty offhand is active — the substituted offhand is not storage. Turn the empty-offhand toggle off to use the real offhand slot.");
        }

        return false;
    }
}
