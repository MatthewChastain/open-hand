using System.Reflection;
using Cairo;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OpenHand.Client;

internal sealed class OpenHandHudRenderer : IRenderer
{
    private const string HotbarDialogName = "HudHotbar";

    private static readonly FieldInfo? SlotGridField =
        AccessTools.Field("Vintagestory.Client.NoObf.HudHotbar:hotbarSlotGrid");

    private readonly ICoreClientAPI capi;
    private LoadedTexture? unselectedTexture;
    private LoadedTexture? selectedTexture;
    private bool disposed;

    public OpenHandHudRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        capi.Event.BlockTexturesLoaded += GenerateTextures;
        capi.Event.ReloadTextures += GenerateTextures;
    }

    public double RenderOrder => 1.01;

    public int RenderRange => 0;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Ortho ||
            capi.HideGuis ||
            capi.World?.Player is not IClientPlayer player ||
            unselectedTexture is null ||
            selectedTexture is null)
        {
            return;
        }

        if (!TryGetSlotZeroBounds(out ElementBounds slotZero))
        {
            return;
        }

        bool selected = OpenHandRuntime.IsSelected(player);
        LoadedTexture texture = selected ? selectedTexture : unselectedTexture;
        float size = (float)slotZero.OuterWidth;
        double x = slotZero.renderX - size;
        double y = slotZero.renderY;
        capi.Render.Render2DTexture(texture.TextureId, (float)x, (float)y, size, size);
    }

    /// <summary>
    /// Reads the vanilla hotbar grid's first slot bounds so the Open Hand cell
    /// tracks the exact position, size, and GUI scale of hotbar slot 0.
    /// </summary>
    private bool TryGetSlotZeroBounds(out ElementBounds slotZero)
    {
        foreach (GuiDialog dialog in capi.Gui.LoadedGuis)
        {
            if (dialog.DebugName != HotbarDialogName)
            {
                continue;
            }

            if (SlotGridField?.GetValue(dialog) is GuiElementItemSlotGridBase grid &&
                grid.SlotBounds is { Length: > 0 } bounds &&
                bounds[0] is not null)
            {
                slotZero = bounds[0];
                return true;
            }

            break;
        }

        slotZero = null!;
        return false;
    }

    private void GenerateTextures()
    {
        unselectedTexture?.Dispose();
        selectedTexture?.Dispose();
        int size = Math.Max(1, (int)GuiElement.scaled(50));
        unselectedTexture = CreateCellTexture(size, false);
        selectedTexture = CreateCellTexture(size, true);
    }

    private LoadedTexture CreateCellTexture(int size, bool selected)
    {
        var surface = new ImageSurface(Format.Argb32, size, size);
        var context = new Context(surface);

        // Cell body matching the vanilla slot background tone.
        context.SetSourceRGBA(0.16, 0.12, 0.07, 0.95);
        GuiElement.RoundRectangle(context, 2, 2, size - 4, size - 4, 4);
        context.FillPreserve();
        context.SetSourceRGBA(0.55, 0.45, 0.25, 1.0);
        context.LineWidth = 3;
        context.Stroke();

        // Empty-hand glyph: a simple open palm circle; brighter while selected.
        context.SetSourceRGBA(1, 1, 1, selected ? 0.95 : 0.6);
        context.Arc(size / 2d, size / 2d, size / 7d, 0, Math.PI * 2);
        context.Fill();

        int textureId = capi.Gui.LoadCairoTexture(surface, true);
        surface.Dispose();
        context.Dispose();

        return new LoadedTexture(capi)
        {
            TextureId = textureId,
            Width = size,
            Height = size
        };
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        capi.Event.BlockTexturesLoaded -= GenerateTextures;
        capi.Event.ReloadTextures -= GenerateTextures;
        unselectedTexture?.Dispose();
        selectedTexture?.Dispose();
        unselectedTexture = null;
        selectedTexture = null;
    }
}
