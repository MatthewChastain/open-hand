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

    private static readonly AssetLocation IconLocation =
        new AssetLocation("openhand", "textures/hud/openhand.png");

    // The icon texture is baked at the CURRENT scaled slot size (high quality
    // resample from the 48x48 asset) and drawn 1:1, so the GPU never scales
    // the texture. Re-baked whenever the slot size changes (GUI scale) or the
    // GL texture is invalidated (world transitions, texture reloads).
    private static LoadedTexture? iconTexture;

    private static MethodBase? TargetMethod()
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
        // every GUI scale and screen resolution.
        int size = slotZero.OuterWidthInt;
        int hotbarLeft = (int)slotZero.renderX;
        int y = (int)slotZero.renderY;
        int x = hotbarLeft - size - (int)Math.Round(GuiElement.scaled(1.0));

        // Center the cell evenly in the gap between the offhand slot and slot 0.
        if (__instance is GuiDialog dialog &&
            dialog.Composers["hotbar"]?.GetSlotGrid("offhandgrid") is GuiElementItemSlotGridBase offhandGrid &&
            offhandGrid.SlotBounds is { Length: > 0 } offBounds &&
            offBounds[0] is not null)
        {
            int offhandRight = (int)offBounds[0].renderX + offBounds[0].OuterWidthInt;
            x = (offhandRight + hotbarLeft - size) / 2 - (int)Math.Round(GuiElement.scaled(1.0));
        }

        // Re-bake when the icon texture is missing or the slot size changed.
        if (iconTexture is null || iconTexture.Width != size)
        {
            BakeIconTexture(capi, size);
            if (iconTexture is null || iconTexture.TextureId == 0)
            {
                return;
            }
        }

        // The Open Hand cell, centered in the gap left of the first main slot.
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

    // Resamples the 48x48 asset to the target size with bilinear filtering and
    // uploads it. Drawn 1:1 afterwards, so the GPU never scales the texture.
    private static void BakeIconTexture(ICoreClientAPI capi, int targetSize)
    {
        IAsset? asset = capi.Assets.TryGet(IconLocation);
        if (asset is null)
        {
            return;
        }

        BitmapRef source = asset.ToBitmap(capi);
        try
        {
            int[] pixels = UpscaleBilinear(source.Pixels, source.Width, source.Height, targetSize, targetSize);
            LoadedTexture texture = new LoadedTexture(capi) { Width = targetSize, Height = targetSize };
            capi.Render.LoadOrUpdateTextureFromRgba(pixels, linearMag: true, clampMode: 0, ref texture);
            iconTexture?.Dispose();
            iconTexture = texture;
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

                dst[dy * dstW + dx] = (a << 24) | (r << 16) | (g << 8) | b;
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
