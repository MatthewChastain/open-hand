using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenHand;
using OpenHand.Client;
using OpenHand.Patches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

internal static class CenteringTests
{
    internal static void Run()
    {
        float previousScale = RuntimeEnv.GUIScale;
        try
        {
            foreach (float scale in new[] { 0.75f, 1f, 1.375f, 1.5f, 2f })
                BoundsChecks(scale);
            TranspilerChecks();
            CenteringIntegrationTests.Run();
        }
        finally
        {
            RuntimeEnv.GUIScale = previousScale;
        }
        Console.WriteLine("Passed centering bounds, hitbox, scale/resize, ownership, restore, and installed-game transpiler checks.");
    }

    private static void Near(double expected, double actual, string name)
    {
        if (Math.Abs(expected - actual) > 0.000001)
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    private static void BoundsChecks(float scale)
    {
        RuntimeEnv.GUIScale = scale;
        ElementBounds window = new ScreenBounds(2560, 1440);
        window.CalcWorldBounds();
        ElementBounds root = ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 0, 850, 100);
        root.ParentBounds = window;
        root.IsDrawingSurface = true;
        ElementBounds body = ElementBounds.Fixed(0, 20, 850, 80);
        ElementBounds slot = ElementBounds.Fixed(110, 10, 48, 48);
        ElementBounds gear = ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 0, 100, 80);
        ElementBounds text = ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 0, 400, 0);
        gear.fixedOffsetX = 3;
        text.fixedOffsetX = -2;
        root.WithChild(body);
        body.WithChild(slot).WithChild(gear).WithChild(text);
        root.CalcWorldBounds();
        double rootX = root.renderX;
        double slotX = slot.renderX;
        double slotAbs = slot.absX;
        double gearX = gear.absX;
        double textX = text.renderX;
        double drawX = body.bgDrawX;
        HotbarCenteringLayout layout = new(root, gear, text);
        int shift = (int)Math.Round(27 * scale);
        Require(layout.Apply(shift, scale), "initial centering");
        Near(rootX + shift, root.renderX, "root moves");
        Near(slotX + shift, slot.renderX, "slot rendering moves");
        Near(slotAbs + shift, slot.absX, "slot hitbox moves");
        Require(slot.PointInside(slot.absX + 1, slot.absY + 1), "moved slot receives pointer");
        Near(gearX, gear.absX, "gear hover stays screen anchored");
        Near(textX, text.renderX, "item text stays screen anchored");
        Near(drawX, body.bgDrawX, "static background coordinates unchanged");
        for (int i = 0; i < 100; i++) Require(layout.Apply(shift, scale), "repeat centering");
        Near(rootX + shift, root.renderX, "no accumulated drift");
        root.MarkDirtyRecursive();
        root.CalcWorldBounds();
        Require(layout.IsIntact(scale), "recomposition preserves ownership");
        Near(slotX + shift, slot.renderX, "recomposition preserves slot offset");

        RuntimeEnv.GUIScale = scale * 1.25f;
        root.MarkDirtyRecursive();
        root.CalcWorldBounds();
        Require(layout.IsIntact(RuntimeEnv.GUIScale), "GUI scale recalculation recognized");
        Near(root.renderX - layout.CurrentShift(RuntimeEnv.GUIScale),
            window.InnerWidth / 2 - root.OuterWidth / 2, "recover unshifted root after scale change");
        Require(layout.Apply(shift + 5, RuntimeEnv.GUIScale), "update scaled translation");
        layout.Restore(RuntimeEnv.GUIScale);
        Near(0, root.fixedOffsetX, "restore root offset");
        Near(3, gear.fixedOffsetX, "restore existing gear offset");
        Near(-2, text.fixedOffsetX, "restore existing text offset");
        Near(0, layout.Shift, "restore published shift");

        // Restore must not overwrite a conflicting mod's fields.
        HotbarCenteringLayout conflicting = new(root, gear, text);
        Require(conflicting.Apply(20, RuntimeEnv.GUIScale), "apply before foreign write");
        root.fixedOffsetX = 77;
        root.absOffsetX = 91;
        Require(!conflicting.Apply(21, RuntimeEnv.GUIScale), "yield to foreign layout writer");
        conflicting.Restore(RuntimeEnv.GUIScale);
        Near(77, root.fixedOffsetX, "preserve foreign fixed offset");
        Near(91, root.absOffsetX, "preserve foreign absolute offset");
        Near(3, gear.fixedOffsetX, "restore own child compensation after conflict");
    }

    private static void TranspilerChecks()
    {
        // References remain local to this optional test project.
        Type hotbar = typeof(Vintagestory.Client.NoObf.HudHotbar);
        MethodInfo target = hotbar.GetMethod("OnRenderGUI")!;
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
        MethodInfo prepare = AccessTools.Method(typeof(HudHotbarPatch), "PrepareHud");
        MethodInfo render = AccessTools.Method(typeof(HudHotbarPatch), "RenderSkill");
        var labelsBefore = original.Select(c => c.labels.ToArray()).ToArray();
        List<CodeInstruction> rewritten = HotbarCenteringTranspiler.Rewrite(original, prepare, render, out bool supported);
        Require(supported, "installed 1.22.7 centering pattern must match");
        Require(rewritten.Count == original.Count + 3, "only three instructions added");
        Require(rewritten.Count(c => Equals(c.operand, prepare)) == 1, "one post-rebuild prepare call");
        Require(rewritten.Count(c => Equals(c.operand, render)) == 1, "one skill-render wrapper");
        for (int i = 0; i < original.Count; i++)
            Require(original[i].labels.SequenceEqual(labelsBefore[i]), "original labels not mutated");
        int prep = rewritten.FindIndex(c => Equals(c.operand, prepare));
        Require(rewritten[prep - 1].opcode == OpCodes.Ldarg_0 &&
                rewritten[prep - 1].labels.Count > 0, "rebuild branch enters prepare hook");
        Require(rewritten[prep + 1].labels.Count == 0, "branch cannot bypass preparation");
        int wrap = rewritten.FindIndex(c => Equals(c.operand, render));
        Require(rewritten[wrap - 1].opcode == OpCodes.Ldarg_0 &&
                rewritten[wrap].opcode == OpCodes.Call, "skill wrapper receives instance");

        List<CodeInstruction> missingSkill = original.Where(c =>
            !Equals(c.operand, typeof(ISkillItemRenderer).GetMethod("Render"))).ToList();
        Require(ReferenceEquals(missingSkill, HotbarCenteringTranspiler.Rewrite(
            missingSkill, prepare, render, out supported)) && !supported, "missing skill call leaves all IL untouched");
        List<CodeInstruction> changedRebuild = original.Select(c => new CodeInstruction(c)).ToList();
        changedRebuild[1].opcode = OpCodes.Nop;
        Require(ReferenceEquals(changedRebuild, HotbarCenteringTranspiler.Rewrite(
            changedRebuild, prepare, render, out supported)) && !supported, "changed rebuild leaves all IL untouched");
        List<CodeInstruction> duplicateSkill = new(original);
        duplicateSkill.Add(original.Single(c => Equals(c.operand, typeof(ISkillItemRenderer).GetMethod("Render"))));
        Require(ReferenceEquals(duplicateSkill, HotbarCenteringTranspiler.Rewrite(
            duplicateSkill, prepare, render, out supported)) && !supported, "ambiguous skill calls disable centering");

        // Compile the complete real patch, not just the matching algorithm.
        Harmony harmony = new("openhand.tests.centering");
        try
        {
            harmony.CreateClassProcessor(typeof(HudHotbarPatch)).Patch();
            Require(HudHotbarPatch.DescribeIconPlacement().Contains("hooks=ready"), "actual patch keeps hooks active");
            Require(Harmony.GetPatchInfo(target).Transpilers.Count == 1, "one existing-target transpiler");
            harmony.CreateClassProcessor(typeof(HudHotbarPatch)).Patch();
            Require(HudHotbarPatch.DescribeIconPlacement().Contains("hooks=ready"),
                "client/server reapplication keeps hooks active");
        }
        finally
        {
            harmony.UnpatchAll("openhand.tests.centering");
        }

        // Exercise the actual ModSystem registration path twice, as happens
        // when client and integrated server start in the same process.
        ICoreClientAPI api = DispatchProxy.Create<ICoreClientAPI, RecordingProxy>();
        ((RecordingProxy)api).Handler = (method, _) => throw new InvalidOperationException(method.Name);
        OpenHandModSystem system = new();
        try
        {
            MethodInfo apply = AccessTools.Method(typeof(OpenHandModSystem), "ApplyPatches");
            apply.Invoke(system, [api]);
            apply.Invoke(system, [api]);
            Patches patches = Harmony.GetPatchInfo(target);
            Require(patches.Prefixes.Count(p => p.owner == "openhand.vs1227") == 1 &&
                    patches.Postfixes.Count(p => p.owner == "openhand.vs1227") == 1 &&
                    patches.Transpilers.Count(p => p.owner == "openhand.vs1227") == 1,
                "mod startup registers each HUD callback exactly once");
        }
        finally
        {
            system.Dispose();
        }
    }

    private sealed class ScreenBounds(int width, int height) : ElementBounds
    {
        public override double absX => 0;
        public override double absY => 0;
        public override double renderX => 0;
        public override double renderY => 0;
        public override double InnerWidth => width;
        public override double InnerHeight => height;
        public override double OuterWidth => width;
        public override double OuterHeight => height;
        public override void CalcWorldBounds() { Initialized = true; }
    }
}
