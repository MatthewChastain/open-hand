using System.Reflection;
using Cairo;
using HarmonyLib;
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
    private static readonly AssetLocation SoilTextureLocation =
        new AssetLocation("game", "gui/backgrounds/soil.png");

    // The frame is composed on a fresh Cairo surface at the CURRENT scaled
    // slot size, precisely as vanilla does. The hand glyph alone is resampled,
    // so interpolation can never soften the final crisp frame stroke.
    private static LoadedTexture? iconFrameTexture;
    private static LoadedTexture? iconGlyphTexture;
    private static LoadedTexture? hotbarExtensionTexture;

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

    internal static void ApplyConfig(OpenHandClientConfig value, IconAnchorMode mode)
    {
        config = value;
        anchorMode = mode;
        loggedProbeFailure = false;
    }

    internal static string DescribeIconPlacement()
    {
        return $"indicator={(config.ShowIndicator ? "on" : "off")} " +
            $"{anchorMode.ToString().ToLowerInvariant()} offset=({config.IconOffsetX},{config.IconOffsetY}) | " +
            $"last render: {lastPlacementDescription}";
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
        hotbarExtensionTexture?.Dispose();
        hotbarExtensionTexture = null;
    }

    private static void Prefix(object __instance)
    {
        ICoreClientAPI? capi = OpenHandModSystem.ClientApi;
        if (capi is null ||
            HotbarGridField?.GetValue(__instance) is not GuiElementItemSlotGridBase grid ||
            grid.SlotBounds is not { Length: > 0 } slotBounds ||
            slotBounds[0] is null)
        {
            return;
        }

        ElementBounds slotZero = slotBounds[0];
        int size = slotZero.OuterWidthInt;
        (int x, int y, bool drawHotbarExtension, string placementDescription) =
            ResolvePlacement(__instance, slotZero, size);
        x += config.IconOffsetX;
        y += config.IconOffsetY;
        lastPlacementDescription = placementDescription;

        // This must render before HudHotbar.OnRenderGUI. The panel deliberately
        // overlaps its left edge; rendering in the postfix puts that overlap
        // above vanilla cells and their stack icons. The prefix lets vanilla
        // draw every existing hotbar element over the extension instead.
        if (config.ShowIndicator && drawHotbarExtension)
        {
            DrawHotbarExtension(capi, __instance, x, y, size);
        }

        if (OpenHandRuntime.IsSelected(capi.World?.Player))
        {
            grid.RemoveSlotHighlight();
        }
    }

    private static void DrawHotbarExtension(ICoreClientAPI capi, object instance, int x, int y, int size)
    {
        int sidePadding = Math.Max(1, (int)Math.Round(GuiElement.scaled(8.0)));
        // BlurFull(scaled(9)) leaves its left-edge rim visible for roughly 24
        // scaled pixels into the hotbar. Cover that whole tail.
        int joinOverlap = Math.Max(1, (int)Math.Round(GuiElement.scaled(24.0)));
        int hotbarTopInset = Math.Max(1, (int)Math.Round(GuiElement.scaled(10.0)));
        int hotbarHeight = Math.Max(1, (int)Math.Round(GuiElement.scaled(80.0)));
        int backgroundX = x - sidePadding;
        int backgroundY = y - hotbarTopInset;
        int backgroundRight = x + size + sidePadding;

        if (TryGetHotbarBounds(instance, out ElementBounds hotbarBounds))
        {
            // The source panel begins around Open Hand, but it continues below
            // the vanilla backdrop's left-rim tail. Because this executes in
            // the prefix, the bar will paint its own cells and stack icons on
            // top of every overlapped panel pixel.
            backgroundRight = Math.Max(
                backgroundRight,
                (int)hotbarBounds.renderX + joinOverlap);
        }
        if (instance is GuiDialog dialog &&
            TryGetOffhandBounds(dialog, out ElementBounds offhandBounds))
        {
            // The extension may fill the normal gap before offhand, but never
            // lies underneath offhand's own background or item stack.
            backgroundRight = Math.Min(backgroundRight, (int)offhandBounds.renderX);
        }

        int backgroundWidth = Math.Max(1, backgroundRight - backgroundX);
        if (hotbarExtensionTexture is null ||
            hotbarExtensionTexture.Width != backgroundWidth ||
            hotbarExtensionTexture.Height != hotbarHeight)
        {
            BakeHotbarExtensionTexture(capi, backgroundWidth, hotbarHeight);
        }

        if (hotbarExtensionTexture is not null && hotbarExtensionTexture.TextureId != 0)
        {
            capi.Render.Render2DTexture(
                hotbarExtensionTexture.TextureId,
                backgroundX,
                backgroundY,
                backgroundWidth,
                hotbarHeight,
                49f);
        }
    }
    private static void Postfix(object __instance)
    {
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

    private static bool TryGetHotbarBounds(object instance, out ElementBounds bounds)
    {
        bounds = null!;
        if (instance is not GuiDialog dialog ||
            dialog.Composers["hotbar"]?.Bounds is not ElementBounds hotbarBounds ||
            hotbarBounds.OuterWidthInt <= 0 ||
            hotbarBounds.OuterHeightInt <= 0)
        {
            return false;
        }

        bounds = hotbarBounds;
        return true;
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

    private static void BakeHotbarExtensionTexture(ICoreClientAPI capi, int targetWidth, int targetHeight)
    {
        LoadedTexture texture = ComposeHotbarExtensionTexture(capi, targetWidth, targetHeight);

        hotbarExtensionTexture?.Dispose();
        hotbarExtensionTexture = texture;
    }

    // Direct transcription of GuiElementDialogBackground.ComposeElements.
    // Compose beyond the requested right edge and crop, leaving that edge
    // borderless so it merges into the existing bar without a double seam.
    private static LoadedTexture ComposeHotbarExtensionTexture(
        ICoreClientAPI capi,
        int targetWidth,
        int targetHeight)
    {
        int cropMargin = Math.Max(1, (int)Math.Ceiling(GuiElement.scaled(24.0)));
        using ImageSurface source = new(
            Format.Argb32,
            targetWidth + cropMargin,
            targetHeight);
        using Context sourceContext = new(source);

        double sourceWidth = source.Width;
        double sourceHeight = source.Height;
        double strokeWidth = GuiElement.scaled(5.0);
        GuiElement.RoundRectangle(
            sourceContext,
            0,
            0,
            sourceWidth,
            sourceHeight - 1,
            GuiStyle.DialogBGRadius);
        sourceContext.SetSourceRGBA(GuiStyle.DialogStrongBgColor);
        sourceContext.FillPreserve();

        sourceContext.SetSourceRGBA(
            GuiStyle.DialogLightBgColor[0] * 2.1,
            GuiStyle.DialogStrongBgColor[1] * 2.1,
            GuiStyle.DialogStrongBgColor[2] * 2.1,
            1);
        sourceContext.LineWidth = strokeWidth * 2;
        sourceContext.StrokePreserve();
        source.BlurFull(GuiElement.scaled(9.0));

        SurfacePattern soilPattern = GuiElement.getPattern(
            capi,
            SoilTextureLocation,
            true,
            64,
            0.125f);
        sourceContext.SetSource(soilPattern);
        sourceContext.FillPreserve();
        sourceContext.Operator = Operator.Over;

        sourceContext.SetSourceRGBA(45 / 255.0, 35 / 255.0, 33 / 255.0, 0.75 * 0.75);
        sourceContext.LineWidth = strokeWidth;
        sourceContext.Stroke();

        using ImageSurface cropped = new(Format.Argb32, targetWidth, targetHeight);
        using Context croppedContext = new(cropped);
        croppedContext.SetSourceSurface(source, 0, 0);
        croppedContext.Paint();

        int textureId = capi.Gui.LoadCairoTexture(cropped, true);
        return new LoadedTexture(capi)
        {
            TextureId = textureId,
            Width = targetWidth,
            Height = targetHeight,
        };
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
