using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Client;

namespace OpenHand.Patches;

// Built-in compatibility with CarryOn's carried-item HUD (decompiled CarryOn
// 1.14.3: HudCarried+HudCarriedRenderer; decompiled CarryOn 2.0.0-pre.8:
// top-level CarryOn.Client.Logic.HudCarriedRenderer). CarryOn hardcodes its
// anchor geometry to an assumed vanilla hotbar (screen-centered, 850 unscaled
// pixels wide, 36 above the bottom), so the default hands anchor L1 lands
// exactly where Open Hand draws its indicator cell, and no anchor follows
// centering shifts. This postfix re-derives each anchor position from the bar
// Open Hand actually rendered, keeping CarryOn's own icon rhythm
// (OpenHandCarryAnchorSolver). Everything is guarded: when CarryOn is not
// loaded, TargetMethod() returns null and Harmony skips the patch silently;
// when its internals change, the reflection lookups fail and the original
// positions pass through untouched. Every guarded path logs once per session
// (and the first successful repositioning per side logs once too), so a
// "nothing moved" report in the field is diagnosable from client-main.log
// without a debugger.
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
            // CarryOn 2.0.0 moved the renderer out of HudCarried into a
            // top-level internal class (CarryOn.Client.Logic.HudCarriedRenderer,
            // decompiled 2.0.0-pre.8); 1.14.x nests it inside
            // CarryOn.Client.HudCarried. Try both so one build supports the
            // whole line; all other member names match in either layout.
            Type? topLevel = AccessTools.TypeByName("CarryOn.Client.Logic.HudCarriedRenderer");
            if (topLevel is not null)
            {
                return topLevel;
            }

            Type? outer = AccessTools.TypeByName("CarryOn.Client.HudCarried");
            return outer?.GetNestedType("HudCarriedRenderer", BindingFlags.NonPublic | BindingFlags.Public);
        }
    }

    // Once-per-session diagnostic state. The placement flags are plain bools
    // so the per-frame success path stays allocation-free.
    private static readonly HashSet<string> LoggedPassThroughs = new();
    private static bool loggedLeftPlacement;
    private static bool loggedRightPlacement;

    internal static MethodBase? TargetMethod()
    {
        Type? rendererType = RendererType;
        MethodBase? method = AccessTools.Method(rendererType, "GetPositionForAnchor");
        if (method is null && rendererType is not null)
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
            LogPassThrough("anchor-tables-unavailable",
                $"cached anchor table lookup failed (leftField={LeftPositionsField is not null}, " +
                $"rightField={RightPositionsField is not null}); leaving CarryOn's defaults.");
            return;
        }

        int leftIndex = IndexOf(left, __result);
        int rightIndex = leftIndex < 0 ? IndexOf(right, __result) : -1;
        if (leftIndex < 0 && rightIndex < 0)
        {
            LogPassThrough("no-anchor-match",
                $"returned position ({__result.X},{__result.Y}) matched no cached anchor; leaving CarryOn's defaults.");
            return;
        }

        bool indicatorRectValid = HudHotbarPatch.TryGetIndicatorRect(out int cellX, out int _, out int cellSize);
        bool rowExtentValid = HudHotbarPatch.TryGetHotbarRowExtent(out int rowLeft, out int rowRight);
        if (!indicatorRectValid || !rowExtentValid)
        {
            // Indicator hidden: centering is off and the bar is vanilla, so
            // CarryOn's own hardcoded positions are already correct.
            LogPassThrough("geometry-unavailable",
                $"hotbar geometry unavailable (indicatorRect={indicatorRectValid}, rowExtent={rowExtentValid}); " +
                "leaving CarryOn's defaults.");
            return;
        }

        int? backgroundLeft = null;
        int? backgroundRight = null;
        if (HudHotbarPatch.TryGetHotbarBackgroundEdges(out int bgLeft, out int bgRight))
        {
            backgroundLeft = bgLeft;
            backgroundRight = bgRight;
        }

        // Decompile evidence (CarryOn 1.14.3 and 2.0.0-pre.8
        // HudCarriedRenderer.UpdateCachedPositions): scaled(32) icon,
        // scaled(16) gap and anchor pitch, centers clamped into the viewport.
        int iconSize = (int)Math.Round(GuiElement.scaled(32.0));
        int iconGap = (int)Math.Round(GuiElement.scaled(16.0));

        bool leftSide = leftIndex >= 0;
        int index = leftSide ? leftIndex : rightIndex;
        if (!OpenHandCarryAnchorSolver.TryPlace(
                index, leftSide, cellX, cellSize, rowLeft, rowRight, iconSize, iconGap, out int centerX,
                backgroundLeft, backgroundRight))
        {
            LogPassThrough("solver-rejected",
                "the anchor solver rejected the placement inputs; leaving CarryOn's defaults.");
            return;
        }

        ICoreClientAPI? capi = OpenHandModSystem.ClientApi;
        int frameWidth = capi?.Render.FrameWidth ?? 0;
        if (frameWidth > 0)
        {
            centerX = Math.Max(iconSize / 2, Math.Min(centerX, frameWidth - (iconSize / 2)));
        }

        if (leftSide ? !loggedLeftPlacement : !loggedRightPlacement)
        {
            if (leftSide) loggedLeftPlacement = true; else loggedRightPlacement = true;
            OpenHandModSystem.ClientApi?.Logger.Notification(
                "Open Hand repositioned CarryOn's carried-item anchor {0}{1}: ({2},{3}) -> ({4},{3}) " +
                "[cell={5} row={6}-{7} bg={8}-{9}]",
                leftSide ? "L" : "R", index + 1, __result.X, __result.Y, centerX,
                cellX, rowLeft, rowRight,
                backgroundLeft?.ToString() ?? "n/a", backgroundRight?.ToString() ?? "n/a");
        }

        __result = (centerX, __result.Y);
    }

    private static void LogPassThrough(string key, string message)
    {
        if (!LoggedPassThroughs.Add(key))
        {
            return;
        }

        OpenHandModSystem.ClientApi?.Logger.Notification("Open Hand CarryOn HUD: {0}", message);
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
