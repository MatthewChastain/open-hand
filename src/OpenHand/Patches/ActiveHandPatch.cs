using System.Reflection;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Common;

namespace OpenHand.Patches;

[HarmonyPatch]
internal static class ActiveHandPatch
{
    private static readonly FieldInfo? PlayerField = AccessTools.Field("Vintagestory.Common.PlayerInventoryManager:player");

    private static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName("Vintagestory.Common.PlayerInventoryManager");
        return type is null ? null : AccessTools.PropertyGetter(type, "ActiveHotbarSlot");
    }

    private static void Postfix(object __instance, ref ItemSlot __result)
    {
        if (PlayerField?.GetValue(__instance) is IPlayer player && OpenHandRuntime.IsSelected(player))
        {
            __result = OpenHandRuntime.EmptySlot;
        }
    }
}
