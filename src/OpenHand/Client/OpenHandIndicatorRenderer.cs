using System.Reflection;
using Cairo;
using HarmonyLib;
using OpenHand.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OpenHand.Client;

// Draws the Open Hand icon cell in the gap between the offhand cell and the
// first main slot, tracking the vanilla hotbar slot 0 screen position. While
// Open Hand is selected, a gold border is layered on top, mirroring the
// vanilla active slot highlight layering (item layer, z 90).
internal sealed class OpenHandIndicatorRenderer : IRenderer
{
    private const string HotbarDialogName = "HudHotbar";

    private static readonly FieldInfo? SlotGridField =
        AccessTools.Field("Vintagestory.Client.NoObf.HudHotbar:hotbarSlotGrid");

    private static readonly AssetLocation IconLocation =
        new AssetLocation("openhand", "textures/hud/openhand.jpg");

    private readonly ICoreClientAPI capi;
    private int iconTextureId;
    private bool iconLoadAttempted;
    private LoadedTexture? borderTexture;
    private bool loggedBoundsFailure;
    private bool loggedFirstDraw;
    private bool disposed;

    public OpenHandIndicatorRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
        capi.Event.ReloadTextures += LoadTextures;
    }

    public double RenderOrder => 1.01;

    public int RenderRange => 0;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage != EnumRenderStage.Ortho ||
            capi.HideGuis ||
            capi.World?.Player is not IClientPlayer player)
        {
            return;
        }

        if (iconTextureId == 0)
        {
            LoadTextures();
            if (iconTextureId == 0)
            {
                return;
            }
        }

        if (!TryGetSlotZeroBounds(out ElementBounds slotZero))
        {
            if (!loggedBoundsFailure)
            {
                loggedBoundsFailure = true;
                string names = string.Join(",", capi.Gui.LoadedGuis.Select(static d => d.DebugName));
                capi.Logger.Warning("[openhand] hotbar bounds lookup failed; loaded dialogs: {0}", names);
            }

            return;
        }

        bool selected = OpenHandRuntime.IsSelected(player);
        float size = (float)slotZero.OuterWidth;
        float x = (float)slotZero.renderX - size;
        float y = (float)slotZero.renderY;

        // z 90: the vanilla item layer, proven visible above the hotbar background.
        capi.Render.Render2DTexture(iconTextureId, x, y, size, size, 90f);
        if (selected && borderTexture is not null)
        {
            capi.Render.Render2DTexturePremultipliedAlpha(
                borderTexture.TextureId, x - 3f, y - 3f, size + 6f, size + 6f, 91f);
        }

        if (!loggedFirstDraw)
        {
            loggedFirstDraw = true;
            capi.Logger.Warning(
                "[openhand] indicator drawn at {0},{1} size {2} icon {3} selected={4}",
                x, y, size, iconTextureId, selected);
        }
    }

    private void LoadTextures()
    {
        if (iconTextureId == 0 && !iconLoadAttempted)
        {
            iconLoadAttempted = true;
            iconTextureId = capi.Render.GetOrLoadTexture(IconLocation);
            capi.Logger.Warning("[openhand] icon texture id {0}", iconTextureId);
        }

        borderTexture?.Dispose();
        const int size = 64;
        var surface = new ImageSurface(Format.Argb32, size, size);
        var context = new Context(surface);
        context.SetSourceRGBA(1.0, 0.85, 0.35, 1.0);
        GuiElement.RoundRectangle(context, 3, 3, size - 6, size - 6, 5);
        context.LineWidth = 5;
        context.Stroke();
        int textureId = capi.Gui.LoadCairoTexture(surface, true);
        surface.Dispose();
        context.Dispose();
        borderTexture = new LoadedTexture(capi)
        {
            TextureId = textureId,
            Width = size,
            Height = size
        };
    }

    // Reads the vanilla hotbar grid's first slot bounds so the icon tracks the
    // exact position, size, and GUI scale of hotbar slot 0. The vanilla slot
    // highlight renders from these same screen-space coordinates.
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        capi.Event.ReloadTextures -= LoadTextures;
        borderTexture?.Dispose();
        borderTexture = null;
    }
}
