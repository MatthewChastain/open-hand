using System.Reflection;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Client;

namespace OpenHand.Patches;

[HarmonyPatch]
internal static class HudHotbarPatch
{
    private static readonly FieldInfo? HotbarGridField =
        AccessTools.Field("Vintagestory.Client.NoObf.HudHotbar:hotbarSlotGrid");

    private static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName("Vintagestory.Client.NoObf.HudHotbar");
        return type is null ? null : AccessTools.Method(type, "OnRenderGUI");
    }

    private static void Prefix(object __instance)
    {
        if (OpenHandRuntime.IsSelected(OpenHandModSystem.ClientApi?.World?.Player) &&
            HotbarGridField?.GetValue(__instance) is GuiElementItemSlotGridBase grid)
        {
            grid.RemoveSlotHighlight();
        }
    }
}
