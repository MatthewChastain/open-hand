using System.Reflection;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Client;

namespace OpenHand.Patches;

// Built-in compatibility with CarryOn's carried-item HUD (decompiled CarryOn
// 1.14.3, CarryOn.Client.HudCarried+HudCarriedRenderer). CarryOn hardcodes its
// anchor geometry to an assumed vanilla hotbar (screen-centered, 850 unscaled
// pixels wide, 36 above the bottom), so the default hands anchor L1 lands
// exactly where Open Hand draws its indicator cell, and no anchor follows
// centering shifts. This postfix re-derives each anchor position from the bar
// Open Hand actually rendered, keeping CarryOn's own icon rhythm
// (OpenHandCarryAnchorSolver). Everything is guarded: when CarryOn is not
// loaded, TargetMethod() returns null and Harmony skips the patch silently;
// when its internals change, the reflection lookups fail and the original
// positions pass through untouched.
[HarmonyPatch]
internal static class CarryOnHudPatch
{
    private static readonly FieldInfo? LeftPositionsField =
        AccessTools.Field(RendererType, "cachedLeftPositions");

    private static readonly FieldInfo? RightPositionsField =
        AccessTools.Field(RendererType, "cachedRightPositions");

    private static Type? RendererType
    {
        get
        {
            Type? outer = AccessTools.TypeByName("CarryOn.Client.HudCarried");
            return outer?.GetNestedType("HudCarriedRenderer", BindingFlags.NonPublic | BindingFlags.Public);
        }
    }

    internal static MethodBase? TargetMethod()
    {
        MethodBase? method = AccessTools.Method(RendererType, "GetPositionForAnchor");
        if (method is null && LeftPositionsField is not null)
        {
            // CarryOn is loaded but its renderer shape changed; say so once per
            // game start rather than failing silently in a confusing way.
            OpenHandModSystem.ClientApi?.Logger.Notification(
                "Open Hand found CarryOn but could not match its carried-item HUD anchors; leaving them at CarryOn's defaults.");
        }

        return method;
    }

    // Runs once per rendered anchor (icons and anchor backgrounds alike).
    // The anchor itself is not passed through Harmony (private enum), so the
    // anchor slot is identified by matching the returned position against
    // CarryOn's cached anchor tables; anything unmatched passes through.
    private static void Postfix(object __instance, ref (int X, int Y) __result)
    {
        if (LeftPositionsField?.GetValue(__instance) is not (int, int)[] left ||
            RightPositionsField?.GetValue(__instance) is not (int, int)[] right)
        {
            return;
        }

        int leftIndex = IndexOf(left, __result);
        int rightIndex = leftIndex < 0 ? IndexOf(right, __result) : -1;
        if (leftIndex < 0 && rightIndex < 0)
        {
            return;
        }

        if (!HudHotbarPatch.TryGetIndicatorRect(out int cellX, out int _, out int cellSize) ||
            !HudHotbarPatch.TryGetHotbarRowExtent(out int rowLeft, out int rowRight))
        {
            // Indicator hidden: centering is off and the bar is vanilla, so
            // CarryOn's own hardcoded positions are already correct.
            return;
        }

        int? backgroundLeft = null;
        int? backgroundRight = null;
        if (HudHotbarPatch.TryGetHotbarBackgroundEdges(out int bgLeft, out int bgRight))
        {
            backgroundLeft = bgLeft;
            backgroundRight = bgRight;
        }

        // Decompile evidence (CarryOn 1.14.3 HudCarriedRenderer.
        // UpdateCachedPositions): scaled(32) icon, scaled(16) gap and anchor
        // pitch, centers clamped into the viewport.
        int iconSize = (int)Math.Round(GuiElement.scaled(32.0));
        int iconGap = (int)Math.Round(GuiElement.scaled(16.0));

        bool leftSide = leftIndex >= 0;
        int index = leftSide ? leftIndex : rightIndex;
        if (!OpenHandCarryAnchorSolver.TryPlace(
                index, leftSide, cellX, cellSize, rowLeft, rowRight, iconSize, iconGap, out int centerX,
                backgroundLeft, backgroundRight))
        {
            return;
        }

        ICoreClientAPI? capi = OpenHandModSystem.ClientApi;
        int frameWidth = capi?.Render.FrameWidth ?? 0;
        if (frameWidth > 0)
        {
            centerX = Math.Max(iconSize / 2, Math.Min(centerX, frameWidth - (iconSize / 2)));
        }

        __result = (centerX, __result.Y);
    }

    private static int IndexOf((int, int)[] table, (int X, int Y) value)
    {
        for (int i = 0; i < table.Length; i++)
        {
            if (table[i].Item1 == value.X && table[i].Item2 == value.Y)
            {
                return i;
            }
        }

        return -1;
    }
}
