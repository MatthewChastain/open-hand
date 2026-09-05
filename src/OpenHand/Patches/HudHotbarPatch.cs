using System.Reflection;
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

    private static readonly AssetLocation IconLocation =
        new AssetLocation("openhand", "textures/hud/openhand.png");
    private static readonly AssetLocation HotbarExtensionLocation =
        new AssetLocation("openhand", "textures/hud/hotbar-extension.png");

    // The icon texture is baked at the CURRENT scaled slot size (high quality
    // resample from the 48x48 asset) and drawn 1:1, so the GPU never scales
    // the texture. Re-baked whenever the slot size changes (GUI scale) or the
    // GL texture is invalidated (world transitions, texture reloads).
    private static LoadedTexture? iconTexture;
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
        return $"{anchorMode.ToString().ToLowerInvariant()} offset=({config.IconOffsetX},{config.IconOffsetY}) | last render: {lastPlacementDescription}";
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
        iconTexture?.Dispose();
        iconTexture = null;
        hotbarExtensionTexture?.Dispose();
        hotbarExtensionTexture = null;
    }

    private static void Prefix(object __instance)
    {
        if (OpenHandRuntime.IsSelected(OpenHandModSystem.ClientApi?.World?.Player) &&
            HotbarGridField?.GetValue(__instance) is GuiElementItemSlotGridBase grid)
        {
            grid.RemoveSlotHighlight();
        }
    }

    private static void Postfix(object __instance)
    {
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
        // every GUI scale and screen resolution. The anchor probes the row's
        // actual layout each frame so other mods that add or move hotbar cells
        // keep the icon aligned without any per-mod special-casing.
        int size = slotZero.OuterWidthInt;
        (int x, int y, bool drawHotbarExtension, string placementDescription) = ResolvePlacement(__instance, slotZero, size);
        x += config.IconOffsetX;
        y += config.IconOffsetY;
        lastPlacementDescription = placementDescription;

        // Re-bake when the icon texture is missing or the slot size changed.
        if (iconTexture is null || iconTexture.Width != size)
        {
            BakeIconTexture(capi, size);
            if (iconTexture is null || iconTexture.TextureId == 0)
            {
                return;
            }
        }

        if (drawHotbarExtension)
        {
            int sidePadding = Math.Max(1, (int)Math.Round(GuiElement.scaled(8.0)));
            int hotbarTopInset = Math.Max(1, (int)Math.Round(GuiElement.scaled(10.0)));
            int hotbarHeight = Math.Max(1, (int)Math.Round(GuiElement.scaled(80.0)));
            int backgroundX = x - sidePadding;
            int backgroundY = y - hotbarTopInset;
            int backgroundWidth = size + sidePadding * 2;
            int backgroundHeight = hotbarHeight;
            if (TryGetHotbarBounds(__instance, out ElementBounds hotbarBounds))
            {
                // The extension ends exactly where the real hotbar backdrop
                // begins. Its known vanilla 80px unscaled height and 10px
                // row inset align it with the actual hotbar, not unrelated
                // HUD widgets included in the composer's larger bounds.
                int hotbarLeft = (int)hotbarBounds.renderX;
                backgroundX = hotbarLeft - size - sidePadding * 2;
                backgroundY = y - hotbarTopInset;
                backgroundWidth = hotbarLeft - backgroundX;
                backgroundHeight = hotbarHeight;
            }
            if (hotbarExtensionTexture is null ||
                hotbarExtensionTexture.Width != backgroundWidth ||
                hotbarExtensionTexture.Height != backgroundHeight)
            {
                BakeHotbarExtensionTexture(capi, backgroundWidth, backgroundHeight);
            }

            if (hotbarExtensionTexture is not null && hotbarExtensionTexture.TextureId != 0)
            {
                capi.Render.Render2DTexture(
                    hotbarExtensionTexture.TextureId,
                    backgroundX,
                    backgroundY,
                    backgroundWidth,
                    backgroundHeight,
                    49f);
            }
        }
        // The Open Hand cell at the anchor-resolved position.
        capi.Render.Render2DTexture(iconTexture.TextureId, x, y, size, size, 50f);

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

    // Resolves the indicator cell position from the hotbar row's ACTUAL
    // rendered layout. In vanilla this reproduces the original offhand-gap
    // centering exactly; when other mods add or move row cells, the same math
    // follows the new layout instead of assuming vanilla geometry.
    private static (int X, int Y, bool DrawHotbarExtension, string Description) ResolvePlacement(object __instance, ElementBounds slotZero, int size)
    {
        int slotZeroX = (int)slotZero.renderX;
        int slotZeroY = (int)slotZero.renderY;
        int padding = (int)Math.Round(GuiElement.scaled(1.0));
        int fallbackX = slotZeroX - size - padding;

        // Left of slot 0 is today's fallback whenever the layout cannot be
        // probed; explicit anchors degrade the same graceful way.
        const string FallbackDescription = "left of slot 0 (row probe unavailable)";
        bool haveRow = TryCollectRowIntervals(slotZeroY, size);

        switch (anchorMode)
        {
            case IconAnchorMode.Left:
            {
                if (!haveRow)
                {
                    return (fallbackX, slotZeroY, false, FallbackDescription);
                }

                return (probeRowStart - size - padding, slotZeroY, false, $"left of row ({RowIntervals.Count} cells)");
            }

            case IconAnchorMode.Right:
            {
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
                if (!haveRow)
                {
                    return (fallbackX, slotZeroY, false, FallbackDescription);
                }

                (int, int)? preferred = __instance is GuiDialog autoDialog &&
                    TryGetOffhandGap(autoDialog, slotZeroX, out (int Start, int End) autoGap)
                        ? autoGap
                        : null;
                OpenHandGapSolver.GapPlacement placement = OpenHandGapSolver.Place(RowIntervals, size, preferred);
                switch (placement.Choice)
                {
                    case OpenHandGapSolver.GapChoice.Preferred:
                    {
                        int x = placement.X - padding;
                        return (x, slotZeroY, false, $"preferred gap x={x} row=[{placement.RowStart}..{placement.RowEnd}] ({RowIntervals.Count} cells)");
                    }

                    case OpenHandGapSolver.GapChoice.Largest:
                    {
                        int x = placement.X - padding;
                        return (x, slotZeroY, false, $"largest gap x={x} row=[{placement.RowStart}..{placement.RowEnd}] ({RowIntervals.Count} cells)");
                    }

                    default:
                    {
                        // No free gap anywhere on the row: use the Open Hand
                        // cell artwork as a visual extension immediately left
                        // of the whole bar rather than covering a neighbor.
                        // Use the hotbar composer's left edge, not its first
                        // slot, to reserve a whole external panel. The icon
                        // stays top-aligned with the physical row cells.
                        int sidePadding = Math.Max(1, (int)Math.Round(GuiElement.scaled(8.0)));
                        if (TryGetHotbarBounds(__instance, out ElementBounds hotbarBounds))
                        {
                            int hotbarLeft = (int)hotbarBounds.renderX;
                            int iconX = hotbarLeft - size - sidePadding;
                            int iconY = slotZeroY;
                            return (iconX, iconY, true, $"left extension x={iconX} hotbar=[{hotbarLeft}..{hotbarLeft + hotbarBounds.OuterWidthInt}] (no free gap; {RowIntervals.Count} cells)");
                        }

                        // A missing composer must remain non-fatal; use the
                        // detected row as the conservative fallback.
                        int extensionX = placement.RowStart - size - sidePadding;
                        return (extensionX, slotZeroY, true, $"left extension x={extensionX} (no free gap; {RowIntervals.Count} cells)");
                    }
                }
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
        if (dialog.Composers["hotbar"]?.GetSlotGrid("offhandgrid") is not GuiElementItemSlotGridBase offhandGrid ||
            offhandGrid.SlotBounds is not { Length: > 0 } offBounds ||
            offBounds[0] is null)
        {
            return false;
        }

        ElementBounds offZero = offBounds[0];
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

    // Resamples the 48x48 asset to the target size with bilinear filtering and
    // uploads it. Drawn 1:1 afterwards, so the GPU never scales the texture.
    private static void BakeIconTexture(ICoreClientAPI capi, int targetSize)
    {
        LoadedTexture? texture = BakeTexture(capi, IconLocation, targetSize, targetSize);
        if (texture is null)
        {
            return;
        }

        iconTexture?.Dispose();
        iconTexture = texture;
    }

    private static void BakeHotbarExtensionTexture(ICoreClientAPI capi, int targetWidth, int targetHeight)
    {
        LoadedTexture? texture = BakeTexture(capi, HotbarExtensionLocation, targetWidth, targetHeight);
        if (texture is null)
        {
            return;
        }

        hotbarExtensionTexture?.Dispose();
        hotbarExtensionTexture = texture;
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
