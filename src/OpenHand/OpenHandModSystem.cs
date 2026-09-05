using HarmonyLib;
using OpenHand.Client;
using OpenHand.Common;
using OpenHand.Patches;
using OpenHand.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace OpenHand;

public sealed class OpenHandModSystem : ModSystem
{
    private const string HarmonyId = "openhand.vs1227";
    private Harmony? harmony;
    private OpenHandClientController? clientController;
    private OpenHandServerController? serverController;

    internal static ICoreClientAPI? ClientApi { get; private set; }

    internal static HashSet<string> AppliedPatches { get; } = new();

    internal static HashSet<string> FailedPatches { get; } = new();

    public override void StartClientSide(ICoreClientAPI api)
    {
        ClientApi = api;
        ApplyPatches(api);
        clientController = new OpenHandClientController(api);
        ApplyClientConfig(api);
        ReportClientConflicts();

        // GL texture IDs change across world transitions and texture reloads;
        // drop the cached indicator texture so it is re-uploaded next render.
        api.Event.LeftWorld += Patches.HudHotbarPatch.ResetIconTexture;
        api.Event.ReloadTextures += Patches.HudHotbarPatch.ResetIconTexture;

        RegisterStatusCommand(api);

        if (api.ModLoader.IsModEnabled("foreverempty"))
        {
            Mod.Logger.Warning("Forever Empty is enabled. Remove it before using Open Hand; the two selected-hand implementations are incompatible.");
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        ApplyPatches(api);
        serverController = new OpenHandServerController(api);
        RegisterServerStatusCommand(api);

        if (api.ModLoader.IsModEnabled("foreverempty"))
        {
            Mod.Logger.Warning("Forever Empty is enabled. Remove it before using Open Hand; the two selected-hand implementations are incompatible.");
        }
    }

    // Applied one patch class at a time so a single mismatched game-version
    // target can never disable the remaining verified substitution patches.
    private static readonly Type[] PatchTypes =
    [
        typeof(ActiveHandPatch),
        typeof(HudHotbarPatch)
    ];

    private void ApplyPatches(ICoreAPI api)
    {
        harmony ??= new Harmony(HarmonyId);
        foreach (Type patchType in PatchTypes)
        {
            try
            {
                harmony.CreateClassProcessor(patchType).Patch();
                AppliedPatches.Add(patchType.Name);
            }
            catch (Exception exception)
            {
                FailedPatches.Add(patchType.Name);
                api.Logger.Error(
                    "Open Hand skipped the {0} patch because its Vintage Story target could not be applied: {1}",
                    patchType.Name,
                    exception);
            }
        }
    }

    // Client-only config for the HUD indicator; never affects selection sync.
    private void ApplyClientConfig(ICoreClientAPI api)
    {
        OpenHandClientConfig? clientConfig;
        try
        {
            clientConfig = api.LoadModConfig<OpenHandClientConfig>(OpenHandClientConfig.ConfigFileName);
        }
        catch (Exception exception)
        {
            Mod.Logger.Error("Open Hand client config could not be parsed; using defaults: {0}", exception.Message);
            clientConfig = null;
        }

        clientConfig ??= new OpenHandClientConfig();
        IconAnchorMode anchorMode = OpenHandClientConfig.ParseIconAnchor(clientConfig.IconAnchor);
        if (!OpenHandClientConfig.IsKnownIconAnchor(clientConfig.IconAnchor))
        {
            Mod.Logger.Warning(
                "Open Hand client config has unknown iconAnchor '{0}'; using 'auto'. Expected auto, offhandGap, left, or right.",
                clientConfig.IconAnchor ?? "");
        }

        HudHotbarPatch.ApplyConfig(clientConfig, anchorMode);

        try
        {
            // Persists defaults on first run; existing files are re-written
            // unchanged (configs here carry no comments to preserve).
            api.StoreModConfig(clientConfig, OpenHandClientConfig.ConfigFileName);
        }
        catch (Exception exception)
        {
            Mod.Logger.Error("Open Hand client config could not be saved: {0}", exception.Message);
        }
    }

    // Generic conflict detection: WHO patches the methods Open Hand relies on
    // (via Harmony patch ownership), never WHAT mod it is. Behavior never
    // branches on these names; they only shape warning text.
    private static OpenHandConflictScanner.ConflictReport ScanConflicts()
    {
        return OpenHandConflictScanner.Scan(
            Patches.ActiveHandPatch.TargetMethod(),
            Patches.HudHotbarPatch.TargetMethod(),
            HarmonyId);
    }

    private void ReportClientConflicts()
    {
        OpenHandConflictScanner.ConflictReport report = ScanConflicts();
        foreach (string owner in report.SelectionPatchOwners)
        {
            Mod.Logger.Warning(
                "Another mod ({0}) patches the hotbar slot resolution Open Hand substitutes: {1}",
                owner,
                OpenHandConflictScanner.HintFor(owner, selectionPatch: true));
        }

        foreach (string owner in report.HudPatchOwners)
        {
            Mod.Logger.Notification(
                "Another mod ({0}) customizes the hotbar HUD. If the Open Hand indicator overlaps other cells, set IconAnchor or IconOffsetX/IconOffsetY in openhand.json. ({1})",
                owner,
                OpenHandConflictScanner.HintFor(owner, selectionPatch: false));
        }
    }

    // The chat command is registered on BOTH sides: client commands live under
    // the '.' prefix while '/' reaches the server's registry, so the
    // documented `/openhand status` form only exists if the server knows the
    // command too. In single-player the client runs in-process, letting the
    // server handler surface the client-side HUD diagnostics as well.
    private void RegisterServerStatusCommand(ICoreServerAPI api)
    {
        api.ChatCommands.Create("openhand")
            .WithDescription("Open Hand diagnostics")
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("status")
            .WithDescription("Reports the current Open Hand selection and integration status")
            .HandleWith(args =>
            {
                var lines = new List<string>();
                IPlayer? player = args.Caller.Player;
                if (player is null)
                {
                    lines.Add("No player is loaded.");
                }
                else
                {
                    OpenHandSelectionState state = OpenHandRuntime.Get(player);
                    lines.Add($"Selected: {(state.IsSelected ? "yes" : "no")}");
                    lines.Add($"Remembered hotbar slot: {state.RememberedHotbarSlot}");
                    lines.Add($"Server revision: {state.Revision}");
                }

                lines.Add($"Applied patches: {(AppliedPatches.Count > 0 ? string.Join(", ", AppliedPatches) : "none")}");
                lines.Add($"Failed patches: {(FailedPatches.Count > 0 ? string.Join(", ", FailedPatches) : "none")}");
                lines.Add($"Forever Empty conflict: {(api.ModLoader.IsModEnabled("foreverempty") ? "DETECTED - remove it" : "none")}");

                // Single-player only: the client HUD runs in this process.
                if (ClientApi is not null)
                {
                    lines.Add($"Icon placement: {Patches.HudHotbarPatch.DescribeIconPlacement()}");
                    OpenHandConflictScanner.ConflictReport clientReport = ScanConflicts();
                    lines.Add($"HUD patch owners: {(clientReport.HudPatchOwners.Count > 0 ? string.Join(", ", clientReport.HudPatchOwners) : "none")}");
                }

                return TextCommandResult.Success(string.Join("\n", lines), "openhand-status");
            })
            .EndSubCommand();
    }

    private void RegisterStatusCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("openhand")
            .WithDescription("Open Hand diagnostics")
            .BeginSubCommand("status")
            .WithDescription("Reports the current Open Hand selection and integration status")
            .HandleWith(_ =>
            {
                var lines = new List<string>();
                IClientPlayer? player = api.World?.Player;
                if (player is null)
                {
                    lines.Add("No world or player is loaded.");
                }
                else
                {
                    OpenHandSelectionState state = OpenHandRuntime.Get(player);
                    lines.Add($"Selected: {(state.IsSelected ? "yes" : "no")}");
                    lines.Add($"Remembered hotbar slot: {state.RememberedHotbarSlot}");
                    lines.Add($"Server revision: {state.Revision}");
                }

                lines.Add($"Applied patches: {(AppliedPatches.Count > 0 ? string.Join(", ", AppliedPatches) : "none")}");
                lines.Add($"Failed patches: {(FailedPatches.Count > 0 ? string.Join(", ", FailedPatches) : "none")}");
                lines.Add($"Forever Empty conflict: {(api.ModLoader.IsModEnabled("foreverempty") ? "DETECTED - remove it" : "none")}");
                lines.Add($"Icon placement: {Patches.HudHotbarPatch.DescribeIconPlacement()}");
                OpenHandConflictScanner.ConflictReport report = ScanConflicts();
                lines.Add($"Selection patch owners: {(report.SelectionPatchOwners.Count > 0 ? string.Join(", ", report.SelectionPatchOwners) : "none")}");
                lines.Add($"HUD patch owners: {(report.HudPatchOwners.Count > 0 ? string.Join(", ", report.HudPatchOwners) : "none")}");
                api.ShowChatMessage(string.Join("\n", lines));
                return TextCommandResult.Success("", "openhand-status");
            })
            .EndSubCommand();
    }

    public override void Dispose()
    {
        clientController?.Dispose();
        serverController?.Dispose();
        harmony?.UnpatchAll(HarmonyId);

        ICoreClientAPI? clientApi = ClientApi;
        if (clientApi is not null)
        {
            clientApi.Event.LeftWorld -= Patches.HudHotbarPatch.ResetIconTexture;
            clientApi.Event.ReloadTextures -= Patches.HudHotbarPatch.ResetIconTexture;
        }

        ClientApi = null;
    }
}
