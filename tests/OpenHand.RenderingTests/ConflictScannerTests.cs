using System.Reflection;
using HarmonyLib;
using OpenHand.Common;

internal static class ConflictScannerTests
{
    internal static void Run()
    {
        // Unpatched methods report no owners at all.
        MethodInfo plain = typeof(ConflictScannerTests).GetMethod(nameof(Plain), BindingFlags.NonPublic | BindingFlags.Static)!;
        OpenHandConflictScanner.ConflictReport empty = OpenHandConflictScanner.Scan(plain, plain, "openhand.tests.scanner");
        TestFakes.Require(empty.SelectionPatchOwners.Count == 0 && empty.HudPatchOwners.Count == 0,
            "unpatched methods report no conflict owners");
        OpenHandConflictScanner.ConflictReport nulls = OpenHandConflictScanner.Scan(null, null, "openhand.tests.scanner");
        TestFakes.Require(nulls.SelectionPatchOwners.Count == 0 && nulls.HudPatchOwners.Count == 0,
            "missing targets report no conflict owners");

        // Hint tables: known owners get their targeted text, everything else
        // the generic fallback, matched case-insensitively.
        TestFakes.Require(OpenHandConflictScanner.HintFor("foreverempty", selectionPatch: true).Contains("incompatible", StringComparison.Ordinal),
            "Forever Empty gets the removal hint");
        TestFakes.Require(OpenHandConflictScanner.HintFor("ForeverEmpty", selectionPatch: true).Contains("incompatible", StringComparison.Ordinal),
            "hint matching is case-insensitive");
        TestFakes.Require(OpenHandConflictScanner.HintFor("immersivebackpacks", selectionPatch: false).Contains("hotbar layout", StringComparison.Ordinal),
            "Immersive Backpacks gets the layout hint");
        TestFakes.Require(OpenHandConflictScanner.HintFor("unknownmod", selectionPatch: true).Contains("Unknown mod", StringComparison.Ordinal),
            "unknown owners get the generic hint");

        // A foreign patch owner on a scanned method shows up in the report; the
        // mod's own owner id is excluded.
        Harmony harmony = new("openhand.tests.scanner.other");
        try
        {
            harmony.Patch(plain,
                prefix: new HarmonyMethod(typeof(ConflictScannerTests).GetMethod(nameof(ForeignPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
            OpenHandConflictScanner.ConflictReport patched = OpenHandConflictScanner.Scan(plain, null, "openhand.tests.scanner");
            TestFakes.Require(patched.SelectionPatchOwners.SequenceEqual(new[] { "openhand.tests.scanner.other" }),
                "a foreign patch owner is reported");
            TestFakes.Require(patched.HudPatchOwners.Count == 0, "an unpatched second target stays empty");
        }
        finally
        {
            harmony.UnpatchAll("openhand.tests.scanner.other");
        }
        Console.WriteLine("Passed conflict scanning and hint-table checks.");
    }

    private static void Plain() { }

    private static void ForeignPrefix() { }
}
