using System.Reflection;
using HarmonyLib;

namespace OpenHand.Common;

/// <summary>
/// Reports other Harmony owners patching the game methods Open Hand relies on,
/// so conflicts surface as warnings and diagnostics instead of silent breakage.
/// Owners drive warnings and status output only; behavior never branches on a
/// specific mod id (the hint tables below only customize warning text).
/// Not compiled into the state tests: this file references 0Harmony.
/// </summary>
public static class OpenHandConflictScanner
{
    public sealed record ConflictReport(
        IReadOnlyList<string> SelectionPatchOwners,
        IReadOnlyList<string> HudPatchOwners);

    // Cosmetic-only hints (HUD rendering). Behavioral conflicts (selection
    // substitution) always warn regardless of hints.
    private static readonly Dictionary<string, string> HudHintByOwner = new(StringComparer.OrdinalIgnoreCase)
    {
        ["immersivebackpacks"] =
            "Its hotbar layout rework moves HUD cells. If the Open Hand indicator overlaps other cells, set IconAnchor or IconOffsetX/IconOffsetY in openhand.json."
    };

    private static readonly Dictionary<string, string> SelectionHintByOwner = new(StringComparer.OrdinalIgnoreCase)
    {
        ["foreverempty"] = "Remove it; the two selected-hand implementations are incompatible."
    };

    /// <param name="selectionGetter">The PlayerInventoryManager.ActiveHotbarSlot getter.</param>
    /// <param name="hudRenderMethod">The HudHotbar.OnRenderGUI method.</param>
    /// <param name="ownHarmonyId">Open Hand's own Harmony id, excluded from reports.</param>
    public static ConflictReport Scan(MethodBase? selectionGetter, MethodBase? hudRenderMethod, string ownHarmonyId)
    {
        return new ConflictReport(
            CollectOwners(selectionGetter, ownHarmonyId),
            CollectOwners(hudRenderMethod, ownHarmonyId));
    }

    public static string HintFor(string owner, bool selectionPatch)
    {
        Dictionary<string, string> hints = selectionPatch ? SelectionHintByOwner : HudHintByOwner;
        return hints.TryGetValue(owner, out string? hint)
            ? hint
            : "Unknown mod; verify compatibility before reporting an Open Hand bug.";
    }

    private static IReadOnlyList<string> CollectOwners(MethodBase? method, string ownHarmonyId)
    {
        if (method is null)
        {
            return Array.Empty<string>();
        }

        // The game ships a Harmony whose GetPatchInfo returns the 1.x-style
        // HarmonyLib.Patches class (not the 2.x PatchInfo record).
        HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
        if (info is null)
        {
            return Array.Empty<string>();
        }

        SortedSet<string> owners = new(StringComparer.OrdinalIgnoreCase);
        Collect(owners, info.Prefixes, ownHarmonyId);
        Collect(owners, info.Postfixes, ownHarmonyId);
        Collect(owners, info.Transpilers, ownHarmonyId);
        Collect(owners, info.Finalizers, ownHarmonyId);
        return owners.ToArray();
    }

    private static void Collect(SortedSet<string> owners, IEnumerable<Patch> patches, string ownHarmonyId)
    {
        foreach (Patch patch in patches)
        {
            if (!string.IsNullOrEmpty(patch.owner) &&
                !patch.owner.Equals(ownHarmonyId, StringComparison.OrdinalIgnoreCase))
            {
                owners.Add(patch.owner);
            }
        }
    }
}
