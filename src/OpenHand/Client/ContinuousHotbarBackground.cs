using Cairo;
using Vintagestory.API.Client;

namespace OpenHand.Client;

// VS 1.22.7: GuiComposer.Compose invokes static elements in insertion order
// before uploading the static surface. Only the vanilla background at
// element-2 is substituted; grids and later custom static draws stay in place.
internal sealed class ContinuousHotbarBackground : GuiElementDialogBackground
{
    private LoadedTexture extensionTexture;

    internal GuiElementDialogBackground Original { get; }
    internal int ExtensionWidth { get; private set; }
    internal bool NeedsRecompose { get; private set; } = true;

    internal ContinuousHotbarBackground(ICoreClientAPI api, GuiElementDialogBackground original)
        : base(api, original.Bounds, false, 5, original.Alpha)
    {
        Original = original;
        Shade = original.Shade;
        FullBlur = original.FullBlur;
        RenderAsPremultipliedAlpha = original.RenderAsPremultipliedAlpha;
        extensionTexture = new LoadedTexture(api);
    }

    internal void SetExtensionWidth(int width)
    {
        width = Math.Max(0, width);
        if (ExtensionWidth == width) return;
        ExtensionWidth = width;
        NeedsRecompose = true;
    }

    internal void InvalidateTexture()
    {
        extensionTexture.Dispose();
        extensionTexture = new LoadedTexture(api);
        NeedsRecompose = true;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        if (ExtensionWidth == 0)
        {
            base.ComposeElements(ctx, surface);
            NeedsRecompose = false;
            return;
        }

        using ImageSurface combined = ComposeSharedSurface(surface);

        // Replace pixels, not source-over them a second time. Earlier static
        // contents were copied into the shared surface before vanilla's blur.
        ctx.Save();
        try
        {
            ctx.Operator = Operator.Source;
            ctx.SetSourceSurface(combined, -ExtensionWidth, 0);
            ctx.Paint();
        }
        finally
        {
            ctx.Restore();
        }

        using ImageSurface left = Crop(combined, 0, ExtensionWidth);
        using (Context leftContext = new(left))
        {
            // HudHotbar's next static element clears composer rows 0..25.
            // It still executes normally on the main surface; apply the same
            // rule to the newly exposed pixels outside that surface.
            leftContext.Operator = Operator.Clear;
            leftContext.Rectangle(0, 0, left.Width, scaled(25));
            leftContext.Fill();
        }
        if (!RenderAsPremultipliedAlpha) left.DemulAlpha();
        api.Gui.LoadOrUpdateCairoTexture(left, true, ref extensionTexture);
        NeedsRecompose = false;
    }

    // Kept separate from GPU upload so local Cairo tests can compare all
    // pixels against one continuous vanilla background at several scales.
    internal ImageSurface ComposeSharedSurface(ImageSurface existing)
    {
        ElementBounds layoutBounds = Bounds;
        layoutBounds.CalcWorldBounds();
        ImageSurface combined = new(Format.Argb32, existing.Width + ExtensionWidth, existing.Height);
        try
        {
            using Context ctx = new(combined);
            ctx.Antialias = Antialias.Best;
            ctx.SetSourceSurface(existing, ExtensionWidth, 0);
            ctx.Paint();
            // Keep vanilla soil coordinates unchanged on the existing bar.
            // The new left portion samples negative coordinates naturally.
            ctx.Translate(ExtensionWidth, 0);
            Bounds = new ExpandedDrawingBounds(layoutBounds, ExtensionWidth);
            base.ComposeElements(ctx, combined);
            return combined;
        }
        catch
        {
            combined.Dispose();
            throw;
        }
        finally
        {
            // Never move the layout tree: slot render bounds and hitboxes,
            // the gear, and the mission-skill region are unchanged.
            Bounds = layoutBounds;
        }
    }

    internal static ImageSurface Crop(ImageSurface source, int x, int width)
    {
        ImageSurface crop = new(Format.Argb32, width, source.Height);
        using Context ctx = new(crop);
        ctx.Operator = Operator.Source;
        ctx.SetSourceSurface(source, -x, 0);
        ctx.Paint();
        return crop;
    }

    internal void RenderExtension(GuiComposer composer)
    {
        if (NeedsRecompose || extensionTexture.TextureId == 0 || ExtensionWidth == 0) return;
        api.Render.Render2DTexture(
            extensionTexture.TextureId,
            (int)composer.Bounds.renderX - ExtensionWidth,
            (int)composer.Bounds.renderY,
            extensionTexture.Width,
            extensionTexture.Height,
            composer.zDepth,
            composer.Color);
    }

    internal void RestoreOriginalStyle()
    {
        Original.Alpha = Alpha;
        Original.Shade = Shade;
        Original.FullBlur = FullBlur;
    }

    public override void Dispose()
    {
        extensionTexture.Dispose();
        NeedsRecompose = true;
        base.Dispose();
    }

    private sealed class ExpandedDrawingBounds : ElementBounds
    {
        private readonly double x;
        private readonly double y;
        private readonly double width;
        private readonly double height;

        internal ExpandedDrawingBounds(ElementBounds original, int extensionWidth)
        {
            x = original.bgDrawX - extensionWidth;
            y = original.bgDrawY;
            width = original.OuterWidth + extensionWidth;
            height = original.OuterHeight;
        }

        public override double bgDrawX => x;
        public override double bgDrawY => y;
        public override double OuterWidth => width;
        public override double OuterHeight => height;
        public override void CalcWorldBounds() { }
    }
}
