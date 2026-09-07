using System.Reflection;
using Cairo;
using HarmonyLib;
using OpenHand.Client;
using OpenHand.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OpenHand.Patches;

// Patches the hotbar dialog's GUI render pass. The prefix hides the vanilla
// slot highlight while Open Hand is selected; the postfix draws the Open Hand
// icon cell and, while selected, vanilla's own active slot highlight border.
// Drawing inside the dialog pass places the icon in the same painter's-order
// pipeline as every vanilla HUD element, so it can never be occluded by the
// toolbar background or by nearby world geometry.
[HarmonyPatch]
internal static class HudHotbarPatch
{
    private const string HotbarDialogName = "HudHotbar";

    private static readonly FieldInfo? HotbarGridField =
        AccessTools.Field("Vintagestory.Client.NoObf.HudHotbar:hotbarSlotGrid");

    // Reflection reads (never patches) enumerating a composer's named elements.
    // Decompile evidence (VS 1.22.7): GuiComposer.staticElements and
    // GuiComposer.interactiveElements are internal Dictionary<string, GuiElement>
    // (VintagestoryApi/Client/UI/GuiComposer.cs:38-39). This is what makes
    // placement generic: every slot grid a mod adds to the hotbar dialog is
    // visible here regardless of which mod added it or what it is named.
    private static readonly FieldInfo? ComposerStaticElementsField =
        AccessTools.Field("Vintagestory.API.Client.GuiComposer:staticElements");

    private static readonly FieldInfo? ComposerInteractiveElementsField =
        AccessTools.Field("Vintagestory.API.Client.GuiComposer:interactiveElements");

    private static readonly AssetLocation IconGlyphLocation =
        new AssetLocation("openhand", "textures/hud/openhand-glyph.png");

    // The frame is composed on a fresh Cairo surface at the CURRENT scaled
    // slot size, precisely as vanilla does. The hand glyph alone is resampled,
    // so interpolation can never soften the final crisp frame stroke.
    private static LoadedTexture? iconFrameTexture;
    private static LoadedTexture? iconGlyphTexture;
    private static GuiComposer? extendedComposer;
    private static ContinuousHotbarBackground? continuousBackground;
    private static GuiComposer? failedBackgroundComposer;
    private static bool loggedBackgroundFailure;
    private static bool centeringHooksAvailable;
    private static HotbarCenteringLayout? centeredLayout;
    private static GuiComposer? centeringBlockedComposer;
    private static string centeringStatus = "off";

    internal static int CenteringOffsetX => centeredLayout?.Shift ?? 0;

    // Client config (openhand.json); defaults until StartClientSide loads the
    // real file. Client-only by definition, mirroring the ClientApi singleton.
    private static OpenHandClientConfig config = new();

    private static IconAnchorMode anchorMode = IconAnchorMode.Auto;

    // Reused across frames to keep the per-frame probe allocation-free.
    private static readonly List<(int Start, int End)> RowIntervals = new();

    private static int probeRowStart;

    private static int probeRowEnd;

    private static bool loggedProbeFailure;

    private static string lastPlacementDescription = "not rendered yet";

    // The cell rect as actually rendered in the most recent frame. Published
    // for click interception (OpenHandClientController.OnMouseDown); invalidated
    // on every path that does not draw the icon.
    private static bool indicatorRectValid;
    private static int indicatorX;
    private static int indicatorY;
    private static int indicatorSize;

    // The rendered extent of the hotbar grid's cells on the indicator's row,
    // in final screen coordinates (centering included). Published alongside
    // the cell rect for CarryOn's anchor correction.
    private static bool rowExtentValid;
    private static int rowLeft;
    private static int rowRight;

    internal static void ApplyConfig(OpenHandClientConfig value, IconAnchorMode mode)
    {
        config = value;
        anchorMode = mode;
        loggedProbeFailure = false;
        loggedBackgroundFailure = false;
        failedBackgroundComposer = null;
        ResetCentering();
        centeringBlockedComposer = null;
        if (!config.ShowIndicator || anchorMode != IconAnchorMode.Auto)
        {
            DetachContinuousBackground();
        }
    }

    [HarmonyPriority(Priority.Last)]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> original = instructions.ToList();
        try
        {
            List<CodeInstruction> rewritten = HotbarCenteringTranspiler.Rewrite(
                original,
                AccessTools.Method(typeof(HudHotbarPatch), nameof(PrepareHud)),
                AccessTools.Method(typeof(HudHotbarPatch), nameof(RenderSkill)),
                out centeringHooksAvailable);
            if (!centeringHooksAvailable)
                OpenHandModSystem.ClientApi?.Logger.Warning(
                    "Open Hand centering hooks did not match this hotbar render method; centering is disabled.");
            return rewritten;
        }
        catch (Exception exception)
        {
            centeringHooksAvailable = false;
            OpenHandModSystem.ClientApi?.Logger.Warning(
                "Open Hand skipped centering hooks; keeping uncentered rendering: {0}", exception.Message);
            return original;
        }
    }

    private static void RenderSkill(ISkillItemRenderer renderer, float dt, float x, float y, float z, object instance)
    {
        // Keep the item's own renderer and all its parameters except X.
        int shift = instance is GuiDialog dialog &&
                    ReferenceEquals(dialog.Composers["hotbar"], extendedComposer)
            ? CenteringOffsetX : 0;
        renderer.Render(dt, x + shift, y, z);
    }

    internal static string DescribeIconPlacement()
    {
        return $"indicator={(config.ShowIndicator ? "on" : "off")} " +
            $"{anchorMode.ToString().ToLowerInvariant()} offset=({config.IconOffsetX},{config.IconOffsetY}) | " +
            $"last render: {lastPlacementDescription} | " +
            $"background={(continuousBackground is null ? "vanilla" : $"continuous +{continuousBackground.ExtensionWidth}px")} | " +
            $"centering={(config.CenterHotbar ? "requested" : "off")} shift={CenteringOffsetX}px " +
            $"hooks={(centeringHooksAvailable ? "ready" : "unavailable")} ({centeringStatus})";
    }

    internal static bool TryGetIndicatorRect(out int x, out int y, out int size)
    {
        x = indicatorX;
        y = indicatorY;
        size = indicatorSize;
        return indicatorRectValid;
    }

    internal static bool TryGetHotbarRowExtent(out int left, out int right)
    {
        left = rowLeft;
        right = rowRight;
        return rowExtentValid;
    }

    internal static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName("Vintagestory.Client.NoObf.HudHotbar");
        return type is null ? null : AccessTools.Method(type, "OnRenderGUI");
    }

    // GL texture IDs are regenerated when leaving a world or reloading
    // textures; a cached ID would silently point at whichever texture the GL
    // reuses the handle for (e.g. the handbook close button).
    internal static void ResetIconTexture()
    {
        iconFrameTexture?.Dispose();
        iconFrameTexture = null;
        iconGlyphTexture?.Dispose();
        iconGlyphTexture = null;
        continuousBackground?.InvalidateTexture();
        failedBackgroundComposer = null;
    }

    internal static void OnLeftWorld()
    {
        DetachContinuousBackground(recompose: false);
        centeringBlockedComposer = null;
        indicatorRectValid = false;
        rowExtentValid = false;
        ResetIconTexture();
    }

    private static void Prefix(object __instance)
    {
        // The verified hook prepares the current composer AFTER vanilla's
        // internal rebuild. Keep the established prefix as the no-hook fallback.
        if (!centeringHooksAvailable) PrepareHud(__instance);
    }

    private static void PrepareHud(object __instance)
    {
        ICoreClientAPI? capi = OpenHandModSystem.ClientApi;
        if (capi is null ||
            HotbarGridField?.GetValue(__instance) is not GuiElementItemSlotGridBase grid ||
            grid.SlotBounds is not { Length: > 0 } slotBounds ||
            slotBounds[0] is null)
        {
            ResetCentering("hotbar unavailable");
            return;
        }

        ElementBounds slotZero = slotBounds[0];
        int size = slotZero.OuterWidthInt;
        (int x, int y, bool drawHotbarExtension, string placementDescription) =
            ResolvePlacement(__instance, slotZero, size);
        x += config.IconOffsetX;
        y += config.IconOffsetY;
        lastPlacementDescription = placementDescription;

        try
        {
            UpdateContinuousBackground(capi, __instance, x, y, size,
                config.ShowIndicator && drawHotbarExtension);
            UpdateCentering(capi, __instance, grid, y, size);
        }
        catch (Exception exception)
        {
            failedBackgroundComposer = (__instance as GuiDialog)?.Composers["hotbar"];
            DetachContinuousBackground();
            capi.Logger.Warning("Open Hand could not compose the continuous background; using vanilla: {0}", exception);
        }

        if (OpenHandRuntime.IsSelected(capi.World?.Player))
        {
            grid.RemoveSlotHighlight();
        }
    }

    internal static void ResetCentering(string reason = "off")
    {
        centeredLayout?.Restore(GuiElement.scaled(1));
        centeredLayout = null;
        centeringStatus = reason;
    }

    private static void UpdateCentering(ICoreClientAPI api, object instance,
        GuiElementItemSlotGridBase grid, int rowY, int size)
    {
        if (!config.CenterHotbar || !config.ShowIndicator || anchorMode != IconAnchorMode.Auto)
        {
            ResetCentering(!config.CenterHotbar ? "off" : "requires visible automatic indicator");
            return;
        }
        if (!centeringHooksAvailable)
        {
            ResetCentering("render hooks unavailable");
            return;
        }
        if (instance is not GuiDialog dialog || extendedComposer is not GuiComposer composer ||
            !ReferenceEquals(dialog.Composers["hotbar"], composer) || continuousBackground is null)
        {
            ResetCentering("compatible continuous background unavailable");
            return;
        }
        if (api.World.Player.WorldData.CurrentGameMode == EnumGameMode.Spectator ||
            !composer.Enabled || api.Render.FrameWidth <= 0 || api.Render.FrameHeight <= 0)
        {
            ResetCentering("HUD not visible");
            return;
        }
        if (ReferenceEquals(composer, centeringBlockedComposer))
        {
            ResetCentering("another layout writer; toggle centering to retry");
            return;
        }

        ElementBounds root = composer.Bounds;
        ElementBounds? gear = composer.GetElement("tempStabHoverText")?.Bounds;
        ElementBounds? text = composer.GetElement("iteminfoHover")?.Bounds;
        double scale = GuiElement.scaled(1);
        if (centeredLayout is not null &&
            (!ReferenceEquals(centeredLayout.Bounds, root) ||
             !centeredLayout.IsIntact(scale) ||
             (gear is not null && !centeredLayout.HasAnchor(gear)) ||
             text is null || !centeredLayout.HasAnchor(text)))
        {
            centeringBlockedComposer = composer;
            ResetCentering("layout ownership changed; toggle centering to retry");
            return;
        }

        // Respect non-centered/custom-position hotbars rather than overriding
        // another mod's explicit placement. Unknown virtual bounds may compute
        // coordinates without inheriting their parent's offsets.
        double baseX = root.renderX - (centeredLayout?.CurrentShift(scale) ?? 0);
        if (root.GetType() != typeof(ElementBounds) ||
            !IsScreenParent(root.ParentBounds, api.Gui.WindowBounds.GetType(),
                api.Render.FrameWidth, api.Render.FrameHeight) ||
            root.Alignment != EnumDialogArea.CenterBottom ||
            root.horizontalSizing != ElementSizing.Fixed ||
            root.renderOffsetX != 0 ||
            Math.Abs(baseX + root.OuterWidth / 2 - api.Render.FrameWidth / 2.0) > 1 ||
            text is null || !RecognizedChild(text, root) ||
            (gear is not null && !RecognizedChild(gear, root)) ||
            !RecognizedChild(grid.Bounds, root))
        {
            ResetCentering("unsupported or independently positioned bounds");
            return;
        }
        if (ComposerStaticElementsField?.GetValue(composer) is not Dictionary<string, GuiElement> elements)
        {
            ResetCentering("element probe unavailable");
            return;
        }
        foreach (GuiElement element in elements.Values)
        {
            if (element is not GuiElementItemSlotGridBase slotGrid) continue;
            if (!RecognizedChild(slotGrid.Bounds, root) || slotGrid.SlotBounds is null)
            {
                ResetCentering("independently positioned slot grid");
                return;
            }
            foreach (ElementBounds bounds in slotGrid.SlotBounds)
            {
                if (bounds is null || !RecognizedChild(bounds, root))
                {
                    ResetCentering("independently positioned slot bounds");
                    return;
                }
            }
        }

        int left = (int)baseX - continuousBackground.ExtensionWidth;
        int right = (int)baseX + root.OuterWidthInt;
        if ((long)right - left > api.Render.FrameWidth)
        {
            ResetCentering("combined bar is wider than the viewport");
            return;
        }
        int shift = OpenHandCenteringGeometry.Shift(api.Render.FrameWidth, left, right);
        if (OverlapsExternalHud(api, composer, rowY, size, left + shift, right + shift))
        {
            ResetCentering("independent HUD cells occupy the centered area");
            return;
        }
        centeredLayout ??= gear is null
            ? new HotbarCenteringLayout(root, text)
            : new HotbarCenteringLayout(root, gear, text);
        if (!centeredLayout.Apply(shift, scale))
        {
            centeringBlockedComposer = composer;
            ResetCentering("layout ownership changed; toggle centering to retry");
            return;
        }
        centeringStatus = "active";
    }

    private static bool IsScreenParent(ElementBounds? bounds, Type windowType, int width, int height)
    {
        // VS 1.22.7 GuiAPI.WindowBounds returns new ElementWindowBounds()
        // each time. Compare its type and viewport geometry, not identity:
        // reference equality would reject every vanilla HUD.
        return bounds is not null && bounds.GetType() == windowType &&
               bounds.absX == 0 && bounds.absY == 0 &&
               bounds.renderX == 0 && bounds.renderY == 0 &&
               bounds.InnerWidth == width && bounds.InnerHeight == height &&
               bounds.OuterWidth == width && bounds.OuterHeight == height;
    }

    private static bool RecognizedChild(ElementBounds bounds, ElementBounds root)
    {
        // Bounded walk also fails safely for malformed/cyclic mod layouts.
        for (int depth = 0; depth < 64; depth++)
        {
            if (ReferenceEquals(bounds, root)) return true;
            if (bounds.GetType() != typeof(ElementBounds) || bounds.renderOffsetX != 0 ||
                bounds.ParentBounds is null) return false;
            bounds = bounds.ParentBounds;
        }
        return false;
    }

    private static bool OverlapsExternalHud(ICoreClientAPI api, GuiComposer own,
        int rowY, int size, int left, int right)
    {
        if (ComposerStaticElementsField is null) return true;
        RowIntervals.Clear();
        foreach (GuiDialog dialog in api.Gui.LoadedGuis)
        {
            if (dialog is not HudElement || !dialog.IsOpened()) continue;
            foreach (GuiComposer composer in dialog.Composers.Values)
            {
                if (!composer.Enabled || ReferenceEquals(composer, own) ||
                    ReferenceEquals(composer.Bounds, own.Bounds)) continue;
                CollectRowIntervals(composer, ComposerStaticElementsField, rowY, size);
            }
        }
        foreach ((int start, int end) in RowIntervals)
            if (OpenHandCenteringGeometry.Overlaps(left, right, start, end)) return true;
        return false;
    }

    private static void UpdateContinuousBackground(
        ICoreClientAPI capi, object instance, int x, int y, int size, bool enabled)
    {
        GuiComposer? composer = (instance as GuiDialog)?.Composers["hotbar"];
        if (!ReferenceEquals(composer, extendedComposer) ||
            (continuousBackground is not null &&
             !ReferenceEquals(composer?.GetElement("element-2"), continuousBackground)))
        {
            DetachContinuousBackground();
        }
        if (!enabled || composer is null)
        {
            DetachContinuousBackground();
            return;
        }
        if (ReferenceEquals(composer, failedBackgroundComposer)) return;
        if (ComposerStaticElementsField?.GetValue(composer) is not Dictionary<string, GuiElement> elements ||
            composer.GetElement("element-2") is not GuiElementDialogBackground background ||
            (background.GetType() != typeof(GuiElementDialogBackground) && background != continuousBackground) ||
            !background.FullBlur || Math.Abs(background.Bounds.bgDrawX) > 0.001 ||
            composer.GetElement("element-3") is not GuiElementCustomDraw)
        {
            DetachContinuousBackground();
            if (!loggedBackgroundFailure)
            {
                loggedBackgroundFailure = true;
                capi.Logger.Notification(
                    "Open Hand could not safely extend this hotbar background; keeping the original background and standalone hand icon.");
            }
            return;
        }

        int padding = GetMatchingSidePadding(instance, y - config.IconOffsetY, size);
        int extensionWidth = OpenHandHudGeometry.ExtensionWidth((int)composer.Bounds.renderX, x, padding);
        if (extensionWidth == 0)
        {
            DetachContinuousBackground();
            return;
        }
        if (continuousBackground is null)
        {
            continuousBackground = new ContinuousHotbarBackground(capi, background);
            extendedComposer = composer;
            // Replace only the value, preserving static draw order and the
            // entire original bounds tree (including slot hitboxes).
            elements["element-2"] = continuousBackground;
        }
        continuousBackground.SetExtensionWidth(extensionWidth);
        if (continuousBackground.NeedsRecompose)
        {
            composer.ReCompose();
            RestoreHighlight(capi, capi.World.Player.InventoryManager.ActiveHotbarSlotNumber);
        }
    }

    internal static void DetachContinuousBackground(bool recompose = true)
    {
        ResetCentering();
        GuiComposer? composer = extendedComposer;
        ContinuousHotbarBackground? background = continuousBackground;
        extendedComposer = null;
        continuousBackground = null;
        if (background is null) return;

        if (composer is not null &&
            ComposerStaticElementsField?.GetValue(composer) is Dictionary<string, GuiElement> elements &&
            elements.TryGetValue("element-2", out GuiElement? current) && ReferenceEquals(current, background))
        {
            background.RestoreOriginalStyle();
            elements["element-2"] = background.Original;
            // Retry vanilla after a failed extension composition, but avoid
            // recomposing an old, disposed world/composer during cleanup.
            if (recompose && (composer.Composed || ReferenceEquals(composer, failedBackgroundComposer)))
            {
                try
                {
                    composer.ReCompose();
                }
                catch (Exception exception)
                {
                    OpenHandModSystem.ClientApi?.Logger.Warning(
                        "Open Hand restored the vanilla background element, but recomposition failed: {0}", exception);
                }
            }
        }
        background.Dispose();
    }

    private static void Postfix(object __instance)
    {
        // The rect describes what is on screen NOW; every early return below
        // leaves it invalid so clicks pass through while nothing is drawn.
        indicatorRectValid = false;
        rowExtentValid = false;
        if (!config.ShowIndicator)
        {
            return;
        }
        ICoreClientAPI? capi = OpenHandModSystem.ClientApi;
        IClientPlayer? player = capi?.World?.Player;
        if (capi is null || player is null ||
            HotbarGridField?.GetValue(__instance) is not GuiElementItemSlotGridBase grid ||
            grid.SlotBounds is not { Length: > 0 } slotBounds ||
            slotBounds[0] is null)
        {
            return;
        }

        ElementBounds slotZero = slotBounds[0];

        // Adjacent crops of one background never cover a vanilla slot. Draw
        // after vanilla so recomposition inside OnRenderGUI refreshes both.
        if (player.WorldData.CurrentGameMode == EnumGameMode.Spectator) return;
        if (__instance is GuiDialog dialog && extendedComposer is not null &&
            ReferenceEquals(dialog.Composers["hotbar"], extendedComposer) &&
            ReferenceEquals(extendedComposer.GetElement("element-2"), continuousBackground))
        {
            continuousBackground?.RenderExtension(extendedComposer);
        }

        // Pixel-snap to the truncated screen coordinates vanilla renders slot
        // textures at ((int)renderX/renderY, OuterWidthInt). Integer math in
        // final screen pixels keeps the icon aligned with neighboring slots at
        // every GUI scale and screen resolution. Automatic placement uses an
        // external left panel so it cannot obstruct the vanilla reserved
        // mission-skill gap; explicit left/right anchors probe the row.
        int size = slotZero.OuterWidthInt;
        (int x, int y, _, string placementDescription) = ResolvePlacement(__instance, slotZero, size);
        x += config.IconOffsetX;
        y += config.IconOffsetY;
        lastPlacementDescription = placementDescription;

        // Re-bake both the direct-composed frame and glyph when the slot size
        // changes. The frame's crisp final stroke is never resampled.
        if (iconFrameTexture is null || iconFrameTexture.Width != size ||
            iconGlyphTexture is null || iconGlyphTexture.Width != size)
        {
            BakeIconTextures(capi, size);
            if (iconFrameTexture is null || iconFrameTexture.TextureId == 0 ||
                iconGlyphTexture is null || iconGlyphTexture.TextureId == 0)
            {
                return;
            }
        }

        // The Open Hand frame and glyph at the anchor-resolved position.
        capi.Render.Render2DTexture(iconFrameTexture.TextureId, x, y, size, size, 50f);
        capi.Render.Render2DTexture(iconGlyphTexture.TextureId, x, y, size, size, 51f);

        indicatorX = x;
        indicatorY = y;
        indicatorSize = size;
        indicatorRectValid = true;

        rowLeft = int.MaxValue;
        rowRight = int.MinValue;
        if (grid.SlotBounds is { Length: > 0 } rowBounds)
        {
            int slotZeroY = (int)slotZero.renderY;
            foreach (ElementBounds bound in rowBounds)
            {
                // Same row filter as CollectRowIntervals: cells on other rows
                // (bag slots above the bar) do not bound the hotbar row.
                if (bound is null || Math.Abs((int)bound.renderY - slotZeroY) > size / 2)
                {
                    continue;
                }

                rowLeft = Math.Min(rowLeft, (int)bound.renderX);
                rowRight = Math.Max(rowRight, (int)bound.renderX + bound.OuterWidthInt);
            }
        }

        rowExtentValid = rowLeft < rowRight;

        // While selected, layer vanilla's own active slot highlight texture,
        // drawn exactly the way the slot grid draws it (2px overscan, z 50).
        if (OpenHandRuntime.IsSelected(player))
        {
            LoadedTexture? highlight = grid.highlightSlotTexture;
            if (highlight is not null && highlight.TextureId != 0)
            {
                capi.Render.Render2DTexturePremultipliedAlpha(
                    highlight.TextureId,
                    x - 2,
                    y - 2,
                    size + 4,
                    size + 4);
            }
        }
    }

    // Resolves the indicator cell position. Automatic placement always uses a
    // safe external left panel; explicit left/right anchors follow the actual
    // rendered row, while offhandGap remains an intentional legacy override.
    private static (int X, int Y, bool DrawHotbarExtension, string Description) ResolvePlacement(
        object __instance,
        ElementBounds slotZero,
        int size)
    {
        int slotZeroX = (int)slotZero.renderX;
        int slotZeroY = (int)slotZero.renderY;
        int padding = (int)Math.Round(GuiElement.scaled(1.0));
        int fallbackX = slotZeroX - size - padding;

        // Left of slot 0 is today's fallback whenever the layout cannot be
        // probed; explicit anchors degrade the same graceful way.
        const string FallbackDescription = "left of slot 0 (row probe unavailable)";

        switch (anchorMode)
        {
            case IconAnchorMode.Left:
            {
                bool haveRow = TryCollectRowIntervals(slotZeroY, size);
                if (!haveRow)
                {
                    return (fallbackX, slotZeroY, false, FallbackDescription);
                }

                return (probeRowStart - size - padding, slotZeroY, false, $"left of row ({RowIntervals.Count} cells)");
            }

            case IconAnchorMode.Right:
            {
                bool haveRow = TryCollectRowIntervals(slotZeroY, size);
                if (!haveRow)
                {
                    return (fallbackX, slotZeroY, false, FallbackDescription);
                }

                return (probeRowEnd + padding, slotZeroY, false, $"right of row ({RowIntervals.Count} cells)");
            }

            case IconAnchorMode.OffhandGap:
            {
                // Explicit choice: center the classic gap even if another mod's
                // cell now shares it, and keep the historical fallback.
                if (__instance is GuiDialog gapDialog &&
                    TryGetOffhandGap(gapDialog, slotZeroX, out (int Start, int End) explicitGap))
                {
                    int x = explicitGap.Start + (explicitGap.End - explicitGap.Start - size) / 2 - padding;
                    return (x, slotZeroY, false, "offhand gap");
                }

                return (fallbackX, slotZeroY, false, "left of slot 0 (offhand grid unavailable)");
            }

            default:
            {
                // The vanilla offhand gap is a reserved mission-skill
                // location, so automatic placement must never occupy it. Put
                // Open Hand one normal cell gutter left of the offhand cell;
                // the background remains an external left extension.
                if (__instance is GuiDialog dialog &&
                    TryGetOffhandBounds(dialog, out ElementBounds offhandBounds))
                {
                    int gutter = GetStandardCellGutter();
                    int iconX = (int)offhandBounds.renderX - size - gutter;
                    return (iconX, slotZeroY, true, $"left extension, offhand gutter={gutter}px");
                }

                // A missing composer must remain non-fatal.
                return (fallbackX, slotZeroY, true, "left extension (hotbar bounds unavailable)");
            }
        }
    }

    // Gathers the X intervals of every slot rendered on slot 0's row across
    // ALL composers of EVERY opened dialog, plus the merged row extents.
    // Scanning every dialog (not just HudHotbar) matters: other mods render
    // hotbar-row cells from their own dialogs or injected grids, and those
    // cells constrain the placement exactly like vanilla slots do.
    private static bool TryCollectRowIntervals(int slotZeroY, int size)
    {
        RowIntervals.Clear();
        if (ComposerStaticElementsField is null || ComposerInteractiveElementsField is null)
        {
            LogProbeFailureOnce("element dictionaries");
            return false;
        }

        ICoreClientAPI? capi = OpenHandModSystem.ClientApi;
        if (capi is null)
        {
            return false;
        }

        foreach (GuiDialog dialog in capi.Gui.LoadedGuis)
        {
            // Hidden dialogs render nothing, so their cells constrain nothing.
            if (!dialog.IsOpened())
            {
                continue;
            }

            foreach (GuiComposer composer in dialog.Composers.Values)
            {
                CollectRowIntervals(composer, ComposerStaticElementsField, slotZeroY, size);
                CollectRowIntervals(composer, ComposerInteractiveElementsField, slotZeroY, size);
            }
        }

        if (RowIntervals.Count == 0)
        {
            LogProbeFailureOnce("slot grids");
            return false;
        }

        probeRowStart = int.MaxValue;
        probeRowEnd = int.MinValue;
        foreach ((int start, int end) in RowIntervals)
        {
            probeRowStart = Math.Min(probeRowStart, start);
            probeRowEnd = Math.Max(probeRowEnd, end);
        }

        return true;
    }

    private static bool TryGetOffhandBounds(GuiDialog dialog, out ElementBounds bounds)
    {
        bounds = null!;
        if (dialog.Composers["hotbar"]?.GetSlotGrid("offhandgrid") is not GuiElementItemSlotGridBase offhandGrid ||
            offhandGrid.SlotBounds is not { Length: > 0 } offBounds ||
            offBounds[0] is null)
        {
            return false;
        }

        bounds = offBounds[0];
        return true;
    }

    private static int GetStandardCellGutter()
    {
        // Vanilla's rendered grid increments each cell by its 48px frame plus
        // GuiElementItemSlotGridBase.unscaledSlotPadding (3px). SlotBounds'
        // OuterWidth includes unrelated outer padding in this layout, so
        // deriving the gutter from it made the indicator gap too wide.
        return Math.Max(
            1,
            (int)Math.Round(
                GuiElement.scaled(GuiElementItemSlotGridBase.unscaledSlotPadding)));
    }

    private static bool TryGetHotbarBackgroundBounds(object instance, out ElementBounds bounds)
    {
        bounds = null!;
        if (instance is not GuiDialog dialog ||
            dialog.Composers["hotbar"]?.GetElement("element-2") is not GuiElementDialogBackground hotbarBackground ||
            hotbarBackground.Bounds.OuterWidthInt <= 0 ||
            hotbarBackground.Bounds.OuterHeightInt <= 0)
        {
            return false;
        }

        // HudHotbar.ComposeGuis installs AddShadedDialogBG as element-2.
        // Its bounds exclude the root composer's 20px vertical grow area.
        bounds = hotbarBackground.Bounds;
        return true;
    }

    private static int GetMatchingSidePadding(object instance, int rowY, int size)
    {
        // Grid bounds include a trailing gutter: measure actual rendered
        // cells so mods that resize or rebuild the hotbar are also supported.
        int fallback = Math.Max(1, (int)Math.Round(GuiElement.scaled(
            10.0 + GuiElementItemSlotGridBase.unscaledSlotPadding)));
        if (instance is not GuiDialog dialog ||
            dialog.Composers["hotbar"] is not GuiComposer composer ||
            ComposerStaticElementsField is null ||
            !TryGetHotbarBackgroundBounds(instance, out ElementBounds background))
        {
            return fallback;
        }

        RowIntervals.Clear();
        // AddInteractiveElement also registers in staticElements.
        CollectRowIntervals(composer, ComposerStaticElementsField, rowY, size);
        int rootX = (int)composer.Bounds.renderX;
        int left = rootX + (int)background.bgDrawX;
        int right = rootX + (int)(background.bgDrawX + background.OuterWidth);
        return OpenHandHudGeometry.MirrorRightPadding(left, right, RowIntervals, fallback);
    }

    private static void CollectRowIntervals(GuiComposer composer, FieldInfo elementsField, int slotZeroY, int size)
    {
        if (elementsField.GetValue(composer) is not Dictionary<string, GuiElement> elements)
        {
            return;
        }

        foreach (GuiElement element in elements.Values)
        {
            if (element is not GuiElementItemSlotGridBase grid ||
                grid.SlotBounds is not { Length: > 0 } bounds)
            {
                continue;
            }

            foreach (ElementBounds bound in bounds)
            {
                // Cells on other rows (e.g. bag slots above the bar) do not
                // constrain the horizontal placement.
                if (bound is null || Math.Abs((int)bound.renderY - slotZeroY) > size / 2)
                {
                    continue;
                }

                RowIntervals.Add(((int)bound.renderX, (int)bound.renderX + bound.OuterWidthInt));
            }
        }
    }

    private static bool TryGetOffhandGap(GuiDialog dialog, int slotZeroX, out (int Start, int End) gap)
    {
        gap = default;
        if (!TryGetOffhandBounds(dialog, out ElementBounds offZero))
        {
            return false;
        }

        int offhandRight = (int)offZero.renderX + offZero.OuterWidthInt;
        if (offhandRight >= slotZeroX)
        {
            return false;
        }

        gap = (offhandRight, slotZeroX);
        return true;
    }

    private static void LogProbeFailureOnce(string what)
    {
        if (loggedProbeFailure)
        {
            return;
        }

        loggedProbeFailure = true;
        OpenHandModSystem.ClientApi?.Logger.Notification(
            "Open Hand could not read the hotbar dialog's {0}; using the static left-of-slot-0 indicator position.",
            what);
    }

    // Exact transcription of GuiElementItemSlotGridBase.ComposeInteractiveElements:
    // draw at this GUI scale, blur the wood rim twice, then draw the unblurred
    // black final frame. This intentionally does not use a scaled slot PNG.
    private static void BakeIconTextures(ICoreClientAPI capi, int targetSize)
    {
        LoadedTexture? glyphTexture = BakeTexture(capi, IconGlyphLocation, targetSize, targetSize);
        if (glyphTexture is null)
        {
            return;
        }

        LoadedTexture frameTexture = capi.Gui.Icons.GenTexture(targetSize, targetSize, (ctx, surface) =>
        {
            double slotSize = GuiElement.scaled(GuiElementPassiveItemSlot.unscaledSlotSize);
            double frameWidth = GuiElement.scaled(4.5);
            double blurRange = GuiElement.scaled(4.0);

            ctx.SetSourceRGBA(GuiStyle.DialogSlotBackColor);
            GuiElement.RoundRectangle(ctx, 0, 0, slotSize, slotSize, GuiStyle.ElementBGRadius);
            ctx.Fill();

            ctx.SetSourceRGBA(GuiStyle.DialogSlotFrontColor);
            GuiElement.RoundRectangle(ctx, 0, 0, slotSize, slotSize, GuiStyle.ElementBGRadius);
            ctx.LineWidth = frameWidth;
            ctx.Stroke();
            surface.BlurFull(blurRange);
            surface.BlurFull(blurRange);

            GuiElement.RoundRectangle(ctx, 0, 0, slotSize, slotSize, 1);
            ctx.LineWidth = frameWidth;
            ctx.SetSourceRGBA(0, 0, 0, 0.8);
            ctx.Stroke();
        });

        iconFrameTexture?.Dispose();
        iconFrameTexture = frameTexture;
        iconGlyphTexture?.Dispose();
        iconGlyphTexture = glyphTexture;
    }

    private static LoadedTexture? BakeTexture(ICoreClientAPI capi, AssetLocation location, int targetWidth, int targetHeight)
    {
        IAsset? asset = capi.Assets.TryGet(location);
        if (asset is null)
        {
            return null;
        }

        BitmapRef source = asset.ToBitmap(capi);
        try
        {
            int[] pixels = UpscaleBilinear(source.Pixels, source.Width, source.Height, targetWidth, targetHeight);
            LoadedTexture texture = new LoadedTexture(capi) { Width = targetWidth, Height = targetHeight };
            capi.Render.LoadOrUpdateTextureFromRgba(pixels, linearMag: true, clampMode: 0, ref texture);
            return texture;
        }
        finally
        {
            source.Dispose();
        }
    }

    private static int[] UpscaleBilinear(int[] source, int srcW, int srcH, int dstW, int dstH)
    {
        int[] dst = new int[dstW * dstH];
        float xRatio = (float)srcW / dstW;
        float yRatio = (float)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            float sy = (dy + 0.5f) * yRatio - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, srcH - 1);
            int y1 = Math.Min(y0 + 1, srcH - 1);
            float fy = Math.Clamp(sy - y0, 0f, 1f);

            for (int dx = 0; dx < dstW; dx++)
            {
                float sx = (dx + 0.5f) * xRatio - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, srcW - 1);
                int x1 = Math.Min(x0 + 1, srcW - 1);
                float fx = Math.Clamp(sx - x0, 0f, 1f);

                int p00 = source[y0 * srcW + x0];
                int p10 = source[y0 * srcW + x1];
                int p01 = source[y1 * srcW + x0];
                int p11 = source[y1 * srcW + x1];

                int a = Bilinear((p00 >> 24) & 0xFF, (p10 >> 24) & 0xFF, (p01 >> 24) & 0xFF, (p11 >> 24) & 0xFF, fx, fy);
                int r = Bilinear((p00 >> 16) & 0xFF, (p10 >> 16) & 0xFF, (p01 >> 16) & 0xFF, (p11 >> 16) & 0xFF, fx, fy);
                int g = Bilinear((p00 >> 8) & 0xFF, (p10 >> 8) & 0xFF, (p01 >> 8) & 0xFF, (p11 >> 8) & 0xFF, fx, fy);
                int b = Bilinear(p00 & 0xFF, p10 & 0xFF, p01 & 0xFF, p11 & 0xFF, fx, fy);

                // LoadOrUpdateTextureFromRgba uploads with GL_RGBA, reading each
                // int as memory bytes [R,G,B,A] - i.e. 0xAABBGGRR, the reverse of
                // the source's 0xAARRGGBB layout.
                dst[dy * dstW + dx] = (a << 24) | (b << 16) | (g << 8) | r;
            }
        }

        return dst;
    }

    private static int Bilinear(int c00, int c10, int c01, int c11, float fx, float fy)
    {
        float top = c00 + (c10 - c00) * fx;
        float bottom = c01 + (c11 - c01) * fx;
        return (int)(top + (bottom - top) * fy);
    }

    // Re-applies the vanilla active slot highlight. Needed after exiting Open
    // Hand: assigning ActiveHotbarSlotNumber its current value is a no-op in
    // ClientPlayerInventoryManager and raises no ActiveSlotChanged event, while
    // the prefix above removed the highlight every frame while Open Hand was
    // selected.
    internal static void RestoreHighlight(ICoreClientAPI capi, int slot)
    {
        foreach (GuiDialog dialog in capi.Gui.LoadedGuis)
        {
            if (dialog.DebugName != HotbarDialogName)
            {
                continue;
            }

            if (HotbarGridField?.GetValue(dialog) is GuiElementItemSlotGridBase grid)
            {
                grid.HighlightSlot(slot);
            }

            break;
        }
    }
}
