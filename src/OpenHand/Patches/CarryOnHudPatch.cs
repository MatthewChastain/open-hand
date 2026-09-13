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
//
// Z-ORDER (CarryOnRenderOrderPatch): HudCarriedRenderer registers at
// RenderOrder 1.0 — the same order as GuiManager's Ortho GUI pass — and
// ClientEventManager.RegisterRenderer inserts a new renderer BEFORE the
// first entry whose order is not strictly smaller, so carried icons render
// BEFORE (under) every dialog. Vanilla bars never expose this because the
// icons sit outside the bar; Open Hand's left extension grows the bar INTO
// the icon zone, and then the extension paints over the icons on any frame
// the position correction does not apply — reported as "the hotbar is
// overlapping the CarryOn icons". The patch raises the order to at least
// 1.01 (above the GUI pass, below the 1.02 crosshair/cursor documented in
// IRenderer), so the icons are HUD overlays that draw on top of the bar, as
// CarryOn's own anchor backgrounds already assume.
[HarmonyPatch]
internal static class CarryOnHudPatch
{
    private static readonly FieldInfo? LeftPositionsField =
        AccessTools.Field(RendererType, "cachedLeftPositions");

    private static readonly FieldInfo? RightPositionsField =
        AccessTools.Field(RendererType, "cachedRightPositions");

    // Shared with CarryOnRenderOrderPatch (same assembly): both patches
    // target the same renderer class, resolved across both CarryOn layouts.
    internal static Type? RendererType
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
    // Repositionings log on first placement and again whenever the correction
    // inputs change (a carried-item pickup recomposes the hotbar and can move
    // the bar); a steady HUD stays silent.
    private static string? lastLoggedLeftInputs;
    private static string? lastLoggedRightInputs;

    // The last geometry the hotbar dialog actually rendered. The correction
    // runs per GetPositionForAnchor call, which can happen on a frame the
    // hotbar dialog has not rendered (or not published) yet — world-join
    // ordering, HUD toggles, dialog transitions. Passing through then would
    // snap the icons back onto the (extended) bar while it overpaints them;
    // the last good geometry keeps the correction stable instead. It is
    // cleared when the HUD is gone for good (world exit), not per frame.
    private static bool lastGeometryValid;
    private static int lastCellX;
    private static int lastCellSize;
    private static int lastRowLeft;
    private static int lastRowRight;
    private static bool lastBackgroundValid;
    private static int lastBackgroundLeft;
    private static int lastBackgroundRight;

    internal static void ResetLastGeometry()
    {
        lastGeometryValid = false;
    }

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
        bool backgroundValid = HudHotbarPatch.TryGetHotbarBackgroundEdges(out int bgLeft, out int bgRight);
        int? backgroundLeft = backgroundValid ? bgLeft : null;
        int? backgroundRight = backgroundValid ? bgRight : null;
        if (indicatorRectValid && rowExtentValid)
        {
            lastGeometryValid = true;
            lastCellX = cellX;
            lastCellSize = cellSize;
            lastRowLeft = rowLeft;
            lastRowRight = rowRight;
            if (backgroundValid)
            {
                // The background edges only move when the extension attaches
                // or the bar layout changes, and both publish valid edges
                // again; a single recompose frame with missing bounds must
                // not retract them — the icons would then fall back to the
                // row edges, which sit INSIDE the visually wider bar.
                lastBackgroundValid = true;
                lastBackgroundLeft = bgLeft;
                lastBackgroundRight = bgRight;
            }
        }
        else if (lastGeometryValid)
        {
            // Transient gap (the hotbar dialog has not rendered/published this
            // frame): correct against the last rendered geometry instead of
            // snapping back to CarryOn's defaults, which overlap the extended
            // bar and get overpainted by it.
            cellX = lastCellX;
            cellSize = lastCellSize;
            rowLeft = lastRowLeft;
            rowRight = lastRowRight;
            if (lastBackgroundValid)
            {
                backgroundLeft = lastBackgroundLeft;
                backgroundRight = lastBackgroundRight;
            }
            else
            {
                backgroundLeft = null;
                backgroundRight = null;
            }

            LogPassThrough("stale-geometry",
                "hotbar geometry unavailable this frame; correcting against the last rendered geometry.");
        }
        else
        {
            // Nothing rendered yet this session: centering is off and the bar
            // is vanilla, so CarryOn's own hardcoded positions are already
            // correct.
            LogPassThrough("geometry-unavailable",
                $"hotbar geometry unavailable (indicatorRect={indicatorRectValid}, rowExtent={rowExtentValid}); " +
                "leaving CarryOn's defaults.");
            return;
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

        string inputs = $"cell={cellX}/{cellSize} row={rowLeft}-{rowRight} " +
            $"bg={backgroundLeft?.ToString() ?? "n/a"}-{backgroundRight?.ToString() ?? "n/a"}";
        string? lastLoggedInputs = leftSide ? lastLoggedLeftInputs : lastLoggedRightInputs;
        if (lastLoggedInputs != inputs)
        {
            if (leftSide) lastLoggedLeftInputs = inputs; else lastLoggedRightInputs = inputs;
            OpenHandModSystem.ClientApi?.Logger.Notification(
                "Open Hand repositioned CarryOn's carried-item anchor {0}{1}: ({2},{3}) -> ({4},{3}) [{5}]",
                leftSide ? "L" : "R", index + 1, __result.X, __result.Y, centerX, inputs);
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

// Raises CarryOn's carried-item renderer above the GUI pass — see the
// z-order note on CarryOnHudPatch. The getter is read once per renderer
// registration (world start, after our patches apply), so the raised order
// takes effect from the first join. Everything is guarded: a missing or
// renamed type/property leaves CarryOn's own order in place and logs once.
[HarmonyPatch]
internal static class CarryOnRenderOrderPatch
{
    // Above GuiManager's Ortho GUI pass (1.0, registered at startup, so
    // insertion puts the equal-ordered CarryOn renderer BEFORE it), below the
    // crosshair and mouse cursor (1.02) documented in IRenderer.
    private const double MinimumOrder = 1.01;

    internal static MethodBase? TargetMethod()
    {
        MethodBase? getter = AccessTools.PropertyGetter(CarryOnHudPatch.RendererType, "RenderOrder");
        if (getter is null && CarryOnHudPatch.RendererType is not null)
        {
            OpenHandModSystem.ClientApi?.Logger.Notification(
                "Open Hand could not raise CarryOn's carried-item HUD above the GUI layer; the hotbar may paint over its icons.");
        }

        return getter;
    }

    private static void Postfix(ref double __result)
    {
        __result = Math.Max(__result, MinimumOrder);
    }
}
