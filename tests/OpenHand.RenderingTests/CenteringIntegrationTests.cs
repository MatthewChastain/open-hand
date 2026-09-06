using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OpenHand.Client;
using OpenHand.Common;
using OpenHand.Patches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

internal static class CenteringIntegrationTests
{
    internal static void Run()
    {
        float previousScale = RuntimeEnv.GUIScale;
        RuntimeEnv.GUIScale = 1;
        ElementBounds window = new TestWindow();
        List<GuiDialog> dialogs = new();
        IGuiAPI gui = Proxy<IGuiAPI>((method, _) => method.Name switch
        {
            // GuiAPI.WindowBounds constructs a fresh ElementWindowBounds
            // on every call; it is not the composer's parent by identity.
            "get_WindowBounds" => new TestWindow(),
            "get_LoadedGuis" => dialogs,
            _ => throw new InvalidOperationException(method.Name)
        });
        int frameWidth = 1920;
        IRenderAPI render = Proxy<IRenderAPI>((method, _) => method.Name switch
        {
            "get_FrameWidth" => frameWidth,
            "get_FrameHeight" => 1080,
            _ => throw new InvalidOperationException(method.Name)
        });
        var data = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
        data.CurrentGameMode = EnumGameMode.Survival;
        var player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(player, data);
        IClientWorldAccessor world = Proxy<IClientWorldAccessor>((_, _) => player);
        ICoreClientAPI api = Proxy<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Gui" => gui,
            "get_Render" => render,
            "get_World" => world,
            _ => throw new InvalidOperationException(method.Name)
        });

        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 0, 850, 100);
        root.ParentBounds = window;
        root.IsDrawingSurface = true;
        ElementBounds gridBounds = ElementBounds.Fixed(110, 35, 510, 48);
        ElementBounds slot = ElementBounds.Fixed(0, 0, 48, 48);
        ElementBounds gear = ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 0, 100, 80);
        ElementBounds text = ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 0, 400, 0);
        root.WithChild(gridBounds).WithChild(gear).WithChild(text);
        gridBounds.WithChild(slot);
        root.CalcWorldBounds();
        var grid = (GuiElementItemSlotGrid)RuntimeHelpers.GetUninitializedObject(typeof(GuiElementItemSlotGrid));
        grid.Bounds = gridBounds;
        grid.SlotBounds = [slot];
        GuiComposer composer = Composer(api, root);
        var elements = (Dictionary<string, GuiElement>)AccessTools.Field(typeof(GuiComposer), "staticElements").GetValue(composer)!;
        using GuiElementDialogBackground original = new(api, ElementBounds.Fill.WithParent(root), false, 5, 0.75f);
        using ContinuousHotbarBackground background = new(api, original);
        background.SetExtensionWidth(54);
        elements["element-2"] = background;
        elements["hotbargrid"] = grid;
        elements["tempStabHoverText"] = new GuiElementCustomDraw(api, gear, (_, _, _) => { });
        elements["iteminfoHover"] = new GuiElementCustomDraw(api, text, (_, _, _) => { });
        HudHotbar dialog = Dialog(composer);
        dialogs.Add(dialog);
        OpenHandClientConfig config = new() { CenterHotbar = true };
        HudHotbarPatch.ApplyConfig(config, IconAnchorMode.Auto);
        Set("extendedComposer", composer);
        Set("continuousBackground", background);
        Set("centeringHooksAvailable", true);
        MethodInfo update = AccessTools.Method(typeof(HudHotbarPatch), "UpdateCentering");
        void Update() => update.Invoke(null, [api, dialog, grid, (int)slot.renderY, 48]);
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"{name}: {HudHotbarPatch.DescribeIconPlacement()}");
        }

        try
        {
            double gearX = gear.absX;
            double slotX = slot.absX;
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 27, "compatible layout centers");
            Check(slot.absX == slotX + 27 && gear.absX == gearX, "slot and gear hitboxes");
            RecordingSkill skill = new();
            AccessTools.Method(typeof(HudHotbarPatch), "RenderSkill").Invoke(null, [skill, 0.5f, 599f, 1000f, 200f, dialog]);
            Check(skill.Last == (0.5f, 626f, 1000f, 200f), "skill renderer only shifts X");
            for (int i = 0; i < 5; i++) Update();
            Check(HudHotbarPatch.CenteringOffsetX == 27, "stable repeated frame");

            config.ShowIndicator = false;
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 0 && slot.absX == slotX, "hidden indicator restores bounds");
            config.ShowIndicator = true;
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 27, "visible indicator resumes centering");
            config.CenterHotbar = false;
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 0, "preference off restores bounds");
            config.CenterHotbar = true;
            root.ParentBounds = ElementBounds.Fixed(0, 0, 1920, 1080).WithParent(window);
            root.ParentBounds.CalcWorldBounds();
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 0, "non-window parent rejected even with matching geometry");
            root.ParentBounds = window;
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 27, "real screen parent resumes centering");

            frameWidth = 800;
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 0, "unsupported viewport falls back");
            frameWidth = 1920;
            Set("centeringHooksAvailable", false);
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 0, "missing hooks disable centering");
            Set("centeringHooksAvailable", true);

            // Another HUD's independent grid in the destination area blocks
            // centering without moving that HUD or requiring a mod-ID rule.
            ElementBounds otherRoot = ElementBounds.Fixed(0, 0, 1920, 1080).WithParent(window);
            ElementBounds otherBounds = ElementBounds.Fixed(1386, slot.renderY, 48, 48).WithParent(otherRoot);
            otherRoot.CalcWorldBounds();
            otherBounds.CalcWorldBounds();
            var otherGrid = (GuiElementItemSlotGrid)RuntimeHelpers.GetUninitializedObject(typeof(GuiElementItemSlotGrid));
            otherGrid.Bounds = otherBounds;
            otherGrid.SlotBounds = [otherBounds];
            GuiComposer other = Composer(api, otherRoot);
            ((Dictionary<string, GuiElement>)AccessTools.Field(typeof(GuiComposer), "staticElements").GetValue(other)!)["grid"] = otherGrid;
            dialogs.Add(Dialog(other));
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 0 &&
                  HudHotbarPatch.DescribeIconPlacement().Contains("independent HUD cells"), "external HUD collision fallback");
            dialogs.RemoveAt(1);
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 27, "removed collision resumes centering");

            root.fixedOffsetX = 80;
            root.absOffsetX = 80;
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 0 && root.fixedOffsetX == 80, "foreign root write preserved");
            root.fixedOffsetX = 0;
            root.absOffsetX = 0;
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 0, "foreign ownership blocks automatic retries");
            HudHotbarPatch.ApplyConfig(config, IconAnchorMode.Auto);
            Update();
            Check(HudHotbarPatch.CenteringOffsetX == 27, "explicit toggle permits retry");
            HudHotbarPatch.OnLeftWorld();
            Check(HudHotbarPatch.CenteringOffsetX == 0 && root.fixedOffsetX == 0, "world exit restores layout");
        }
        finally
        {
            HudHotbarPatch.ResetCentering();
            Set("extendedComposer", null);
            Set("continuousBackground", null);
            Set("centeringBlockedComposer", null);
            HudHotbarPatch.ApplyConfig(new OpenHandClientConfig(), IconAnchorMode.Auto);
            RuntimeEnv.GUIScale = previousScale;
        }
    }

    private static T Proxy<T>(System.Func<MethodInfo, object?[], object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, RecordingProxy>();
        ((RecordingProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static void Set(string name, object? value) => AccessTools.Field(typeof(HudHotbarPatch), name).SetValue(null, value);
    private static GuiComposer Composer(ICoreClientAPI api, ElementBounds root) =>
        (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [api, root, "centering-test"], null)!;

    private static HudHotbar Dialog(GuiComposer composer)
    {
        var dialog = (HudHotbar)RuntimeHelpers.GetUninitializedObject(typeof(HudHotbar));
        dialog.Composers = new GuiDialog.DlgComposers(dialog);
        dialog.Composers["hotbar"] = composer;
        AccessTools.Field(typeof(GuiDialog), "opened").SetValue(dialog, true);
        return dialog;
    }

    private sealed class RecordingSkill : ISkillItemRenderer
    {
        internal (float, float, float, float) Last;
        public void Render(float deltaTime, float posX, float posY, float posZ) => Last = (deltaTime, posX, posY, posZ);
    }

    private sealed class TestWindow : ElementBounds
    {
        internal TestWindow() { Initialized = true; IsWindowBounds = true; }
        public override double absX => 0;
        public override double absY => 0;
        public override double renderX => 0;
        public override double renderY => 0;
        public override double InnerWidth => 1920;
        public override double InnerHeight => 1080;
        public override double OuterWidth => 1920;
        public override double OuterHeight => 1080;
    }
}
