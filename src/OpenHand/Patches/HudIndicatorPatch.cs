using System.Reflection;
using Cairo;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Client;

namespace OpenHand.Patches;

// Renders the Open Hand cell inside the hotbar dialog's own composer so it
// follows vanilla position, GUI scale, and composition exactly. The custom
// draw is baked during composer compose, so selection changes trigger a
// composer recompose to refresh the highlight brightness.
[HarmonyPatch]
internal static class HudIndicatorPatch
{
    private const string ComposerKey = "hotbar";
    private const string ElementKey = "openhand-indicator";
    private const string HotbarDialogName = "HudHotbar";

    // Vanilla anchors from HudHotbar.ComposeGuis: the offhand cell grid sits
    // at unscaled x 10 and the main slot grid at unscaled x 110.
    private const double HotbarGridUnscaledX = 110.0;

    private static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName("Vintagestory.Client.NoObf.HudHotbar");
        return type is null ? null : AccessTools.Method(type, "ComposeGuis");
    }

    private static void Postfix(object __instance)
    {
        if (__instance is not GuiDialog dialog)
        {
            return;
        }

        GuiComposer? composer = dialog.Composers[ComposerKey];
        if (composer is null)
        {
            return;
        }

        double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        double pitch = slotSize + GuiElementItemSlotGridBase.unscaledSlotPadding;
        var cellBounds = new ElementBounds
        {
            Alignment = EnumDialogArea.LeftFixed,
            BothSizing = ElementSizing.Fixed,
            // One cell pitch left of the main slot grid: the gap between the
            // offhand cell and the first main slot.
            fixedX = HotbarGridUnscaledX - pitch + 1.0,
            fixedY = 10.0,
            fixedWidth = slotSize,
            fixedHeight = slotSize
        };

        composer.AddDynamicCustomDraw(cellBounds, DrawPalmCell, ElementKey);
        composer.ReCompose();
        OpenHandModSystem.ClientApi?.Logger.Warning(
            "[openhand] indicator element added to hotbar composer");
    }

    private static void DrawPalmCell(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        bool selected = OpenHandRuntime.IsSelected(OpenHandModSystem.ClientApi?.World?.Player);

        // Cell body matching the vanilla slot background tone.
        ctx.SetSourceRGBA(0.16, 0.12, 0.07, 0.95);
        GuiElement.RoundRectangle(ctx, 2, 2, bounds.OuterWidth - 4, bounds.OuterHeight - 4, 4);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.55, 0.45, 0.25, 1.0);
        ctx.LineWidth = 3;
        ctx.Stroke();

        // Empty-hand glyph: a simple open palm circle; brighter while selected.
        ctx.SetSourceRGBA(1, 1, 1, selected ? 0.95 : 0.6);
        ctx.Arc(bounds.OuterWidth / 2.0, bounds.OuterHeight / 2.0, bounds.OuterWidth / 7.0, 0, Math.PI * 2);
        ctx.Fill();
    }

    // Re-bakes the hotbar composer so the indicator reflects a selection change.
    internal static void RecomposeIndicator(ICoreClientAPI capi)
    {
        foreach (GuiDialog dialog in capi.Gui.LoadedGuis)
        {
            if (dialog.DebugName != HotbarDialogName)
            {
                continue;
            }

            GuiComposer? composer = dialog.Composers[ComposerKey];
            if (composer is not null && composer.GetElement(ElementKey) is not null)
            {
                composer.ReCompose();
            }

            break;
        }
    }
}
