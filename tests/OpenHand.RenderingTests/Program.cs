using System.Reflection;
using System.Runtime.InteropServices;
using Cairo;
using OpenHand.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

if (OperatingSystem.IsLinux())
{
    NativeLibrary.SetDllImportResolver(typeof(ImageSurface).Assembly,
        (name, _, _) => name == "libcairo-2" ? NativeLibrary.Load("libcairo.so.2") : IntPtr.Zero);
}

static byte[] Bytes(ImageSurface surface)
{
    surface.Flush();
    byte[] result = new byte[surface.Stride * surface.Height];
    Marshal.Copy(surface.DataPtr, result, 0, result.Length);
    return result;
}

static void Same(byte[] expected, byte[] actual, string name)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{name}: pixels differ");
}

static void ClearTop(ImageSurface surface)
{
    using Context ctx = new(surface);
    ctx.Antialias = Antialias.Best;
    ctx.Operator = Operator.Clear;
    ctx.Rectangle(0, 0, surface.Width, GuiElement.scaled(25));
    ctx.Fill();
}

// No game window or GPU: only capture uploads; use a synthetic nearest-repeat
// soil pattern so the tests do not distribute any proprietary game assets.
byte[] uploaded = [];
int deleted = 0;
IGuiAPI gui = DispatchProxy.Create<IGuiAPI, RecordingProxy>();
((RecordingProxy)gui).Handler = (method, args) =>
{
    if (method.Name == "LoadOrUpdateCairoTexture")
    {
        ImageSurface surface = (ImageSurface)args[0]!;
        LoadedTexture texture = (LoadedTexture)args[2]!;
        uploaded = Bytes(surface);
        texture.TextureId = 1;
        texture.Width = surface.Width;
        texture.Height = surface.Height;
        return null;
    }
    if (method.Name == "DeleteTexture")
    {
        deleted++;
        return null;
    }
    throw new InvalidOperationException($"Unexpected GUI call: {method.Name}");
};
ICoreClientAPI api = DispatchProxy.Create<ICoreClientAPI, RecordingProxy>();
((RecordingProxy)api).Handler = (method, _) => method.Name == "get_Gui"
    ? gui : throw new InvalidOperationException($"Unexpected client call: {method.Name}");

var cache = (Dictionary<AssetLocation, KeyValuePair<SurfacePattern, ImageSurface>>)
    typeof(GuiElement).GetField("cachedPatterns", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
AssetLocation key = GuiElement.dirtTextureName.Clone().WithPathPrefix(0.125f + "-").WithPathPrefix("64@");
float previousScale = RuntimeEnv.GUIScale;
int cases = 0;
try
{
    foreach (float scale in new[] { 0.75f, 1f, 1.25f, 1.375f, 1.5f, 2f, 2.75f })
    {
        RuntimeEnv.GUIScale = scale;
        using ImageSurface soil = new(Format.Argb32, 32, 32);
        using (Context ctx = new(soil))
        {
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                ctx.SetSourceRGBA(x / 32.0, y / 32.0, ((x + y) % 11) / 11.0, 64 / 255.0);
                ctx.Rectangle(x, y, 1, 1);
                ctx.Fill();
            }
        }
        using SurfacePattern pattern = new(soil) { Extend = Extend.Repeat, Filter = Filter.Nearest };
        double factor = 0.125 / scale;
        pattern.Matrix = new Matrix(factor, 0, 0, factor, 0, 0);
        cache[key] = new(pattern, soil);

        foreach (int logicalWidth in new[] { 640, 850, 1061 })
        {
            int width = (int)(logicalWidth * scale);
            int height = (int)(100 * scale);
            int extension = (int)Math.Round(54 * scale);
            ElementBounds bounds = new DrawingBounds(logicalWidth * scale, 25 * scale, 80 * scale);
            using GuiElementDialogBackground original = new(api, bounds, false, 5, 0.75f) { FullBlur = true };
            using ContinuousHotbarBackground background = new(api, original);
            background.SetExtensionWidth(extension);
            using ImageSurface existing = new(Format.Argb32, width, height);
            // Preserve a static draw occurring before the background.
            using (Context ctx = new(existing))
            {
                ctx.SetSourceRGBA(0.2, 0.1, 0.3, 0.4);
                ctx.Rectangle(10, 2, 30, 12);
                ctx.Fill();
            }
            using ImageSurface actual = background.ComposeSharedSurface(existing);
            if (!ReferenceEquals(bounds, background.Bounds) || !ReferenceEquals(bounds, original.Bounds))
                throw new InvalidOperationException("Composition changed layout bounds");

            // Independent reference: vanilla draws one larger rectangle at
            // x=0, with a shifted pattern instead of a shifted Cairo context.
            using ImageSurface expected = new(Format.Argb32, width + extension, height);
            using (Context ctx = new(expected))
            {
                ctx.Antialias = Antialias.Best;
                ctx.SetSourceSurface(existing, extension, 0);
                ctx.Paint();
                pattern.Matrix = new Matrix(factor, 0, 0, factor, -extension * factor, 0);
                using GuiElementDialogBackground reference = new(api,
                    new DrawingBounds(logicalWidth * scale + extension, 25 * scale, 80 * scale),
                    false, 5, 0.75f) { FullBlur = true };
                reference.ComposeElements(ctx, expected);
                pattern.Matrix = new Matrix(factor, 0, 0, factor, 0, 0);
            }
            Same(Bytes(expected), Bytes(actual), $"shared background scale={scale} width={logicalWidth}");

            using ImageSurface left = ContinuousHotbarBackground.Crop(expected, 0, extension);
            using ImageSurface right = ContinuousHotbarBackground.Crop(expected, extension, width);
            ClearTop(left);
            ClearTop(right);
            using (Context ctx = new(existing))
            {
                background.ComposeElements(ctx, existing);
            }
            ClearTop(existing); // vanilla's following custom-draw element
            Same(Bytes(right), Bytes(existing), "main static texture");
            Same(Bytes(left), uploaded, "left crop upload");
            if (background.NeedsRecompose) throw new InvalidOperationException("Composition stayed dirty");

            background.InvalidateTexture();
            if (!background.NeedsRecompose) throw new InvalidOperationException("Reload did not invalidate");
            background.SetExtensionWidth(extension + 3);
            using ImageSurface resized = background.ComposeSharedSurface(existing);
            if (resized.Width != width + extension + 3) throw new InvalidOperationException("Resize failed");
            background.Alpha = 0.5f;
            background.RestoreOriginalStyle();
            if (original.Alpha != 0.5f) throw new InvalidOperationException("Style restoration failed");
            background.SetExtensionWidth(0);
            using ImageSurface disabled = new(Format.Argb32, width, height);
            using ImageSurface vanilla = new(Format.Argb32, width, height);
            using (Context ctx = new(disabled))
            {
                ctx.Antialias = Antialias.Best;
                background.ComposeElements(ctx, disabled);
            }
            using (Context ctx = new(vanilla))
            {
                ctx.Antialias = Antialias.Best;
                original.ComposeElements(ctx, vanilla);
            }
            Same(Bytes(vanilla), Bytes(disabled), "disabled extension uses vanilla rendering");
            cases++;
        }
        cache.Remove(key);
    }
}
finally
{
    cache.Remove(key);
    RuntimeEnv.GUIScale = previousScale;
}
if (deleted != cases) throw new InvalidOperationException("Texture disposal count mismatch");
Console.WriteLine($"Passed {cases} continuous-background pixel comparisons, crop/upload, bounds, resize, and reload checks.");
ClientHotkeyTests.Run();
ActiveHandInventoryTests.Run();
CenteringTests.Run();

public class RecordingProxy : DispatchProxy
{
    public System.Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        Handler(targetMethod!, args ?? []);
}

internal sealed class DrawingBounds(double width, double top, double height) : ElementBounds
{
    public override double bgDrawX => 0;
    public override double bgDrawY => top;
    public override double OuterWidth => width;
    public override double OuterHeight => height;
    public override void CalcWorldBounds() { }
}
