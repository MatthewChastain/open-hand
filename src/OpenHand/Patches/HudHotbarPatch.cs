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

    private static int iconTextureId;
    private static bool iconLoadAttempted;

    // GL texture IDs are regenerated when leaving a world or reloading
    // textures; a cached ID would silently point at whichever texture the GL
    // reuses the handle for (e.g. the handbook close button).
    internal static void ResetIconTexture()
    {
        iconTextureId = 0;
        iconLoadAttempted = false;
    }

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

        if (iconTextureId == 0 && !iconLoadAttempted)
        {
            iconLoadAttempted = true;
            iconTextureId = capi.Render.GetOrLoadTexture(IconLocation);
        }

        if (iconTextureId == 0)
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
        int y = (int)slotZero.renderY - (int)Math.Round(GuiElement.scaled(0.5));
        int x = hotbarLeft - size - (int)Math.Round(GuiElement.scaled(1.5));

        // Center the cell evenly in the gap between the offhand slot and slot 0.
        if (__instance is GuiDialog dialog &&
            dialog.Composers["hotbar"]?.GetSlotGrid("offhandgrid") is GuiElementItemSlotGridBase offhandGrid &&
            offhandGrid.SlotBounds is { Length: > 0 } offBounds &&
            offBounds[0] is not null)
        {
            int offhandRight = (int)offBounds[0].renderX + offBounds[0].OuterWidthInt;
            x = (offhandRight + hotbarLeft - size) / 2 - (int)Math.Round(GuiElement.scaled(1.5));
        }

        // The Open Hand cell, centered in the gap left of the first main slot.
        capi.Render.Render2DTexture(iconTextureId, x, y, size, size, 50f);

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
