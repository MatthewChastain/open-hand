using System.Reflection;
using HarmonyLib;
using OpenHand.Patches;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

internal static class PatchTargetTests
{
    internal static void Run()
    {
        // The game-update tripwire: every vanilla patch target must resolve
        // against the installed game, and the mod's own reflection lookups
        // (private fields read per frame or per postfix) must resolve too.
        // A mismatch here is exactly what "re-verify patch targets on game
        // updates" checks by hand.
        TestFakes.Require(Equals(ActiveHandPatch.TargetMethod(),
            AccessTools.PropertyGetter(typeof(PlayerInventoryManager), "ActiveHotbarSlot")),
            "the main hand patches the ActiveHotbarSlot getter");
        TestFakes.Require(Equals(OffhandInventoryPatch.TargetMethod(),
            AccessTools.PropertyGetter(typeof(PlayerInventoryManager), "OffhandHotbarSlot")),
            "the offhand inventory patch targets the base getter");
        TestFakes.Require(Equals(OffhandEntityPatch.TargetMethod(),
            AccessTools.PropertyGetter(typeof(EntityPlayer), "LeftHandItemSlot")),
            "the offhand entity patch targets the LeftHandItemSlot override, not the base property");
        TestFakes.Require(Equals(HudHotbarPatch.TargetMethod(),
            AccessTools.Method(typeof(HudHotbar), "OnRenderGUI")),
            "the HUD patch targets HudHotbar.OnRenderGUI");

        RequireResolved("ActiveHandPatch", "PlayerField");
        RequireResolved("OffhandInventoryPatch", "PlayerField");
        RequireResolved("HudHotbarPatch", "HotbarGridField");
        RequireResolved("HudHotbarPatch", "ComposerStaticElementsField");
        RequireResolved("HudHotbarPatch", "ComposerInteractiveElementsField");

        // Guarded CarryOn targets: null (no-throw) without the mod, resolved
        // with it — either way TargetMethod must not throw.
        bool carryOnLoaded = AccessTools.TypeByName("CarryOn.CarryOnLib.CarryOnLibSystem") is not null ||
                             AccessTools.TypeByName("CarryOn.API.Common.CarryableExtensions") is not null;
        MethodBase? hudTarget = CarryOnHudPatch.TargetMethod();
        MethodBase? orderTarget = CarryOnRenderOrderPatch.TargetMethod();
        TestFakes.Require(carryOnLoaded
                ? hudTarget is not null && orderTarget is not null
                : hudTarget is null && orderTarget is null,
            "the CarryOn HUD guards degrade to null without the mod and resolve with it");

        // Application smoke test: the vanilla-target patches apply for real and
        // register exactly once under the test owner.
        Harmony harmony = new("openhand.tests.patchtargets");
        try
        {
            harmony.CreateClassProcessor(typeof(ActiveHandPatch)).Patch();
            harmony.CreateClassProcessor(typeof(OffhandInventoryPatch)).Patch();
            harmony.CreateClassProcessor(typeof(OffhandEntityPatch)).Patch();
            Patches mainHand = Harmony.GetPatchInfo(AccessTools.PropertyGetter(typeof(PlayerInventoryManager), "ActiveHotbarSlot"))!;
            Patches offhandGetter = Harmony.GetPatchInfo(AccessTools.PropertyGetter(typeof(PlayerInventoryManager), "OffhandHotbarSlot"))!;
            Patches leftHand = Harmony.GetPatchInfo(AccessTools.PropertyGetter(typeof(EntityPlayer), "LeftHandItemSlot"))!;
            TestFakes.Require(mainHand.Postfixes.Count(p => p.owner == "openhand.tests.patchtargets") == 1,
                "the main-hand postfix applies");
            TestFakes.Require(offhandGetter.Postfixes.Count(p => p.owner == "openhand.tests.patchtargets") == 1,
                "the offhand getter postfix applies");
            TestFakes.Require(leftHand.Postfixes.Count(p => p.owner == "openhand.tests.patchtargets") == 1,
                "the offhand entity postfix applies");
        }
        finally
        {
            harmony.UnpatchAll("openhand.tests.patchtargets");
        }
        Console.WriteLine("Passed patch-target resolution, reflection lookups, guarded CarryOn targets, and application smoke checks.");
    }

    private static void RequireResolved(string patchType, string fieldName) =>
        TestFakes.Require(
            AccessTools.Field(AccessTools.TypeByName($"OpenHand.Patches.{patchType}"), fieldName)?.GetValue(null) is not null,
            $"{patchType}.{fieldName} resolves against the installed game");
}
