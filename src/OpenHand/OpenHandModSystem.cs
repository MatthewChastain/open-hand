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
    private OpenHandIndicatorRenderer? indicatorRenderer;
    private OpenHandServerController? serverController;

    internal static ICoreClientAPI? ClientApi { get; private set; }

    internal static HashSet<string> AppliedPatches { get; } = new();

    internal static HashSet<string> FailedPatches { get; } = new();

    public override void StartClientSide(ICoreClientAPI api)
    {
        ClientApi = api;
        ApplyPatches(api);
        clientController = new OpenHandClientController(api);
        indicatorRenderer = new OpenHandIndicatorRenderer(api);
        api.Event.RegisterRenderer(indicatorRenderer, EnumRenderStage.Ortho, "openhand-indicator");
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
                    "Open Hand skipped the {0} patch because its Vintage Story 1.22.7 target could not be applied: {1}",
                    patchType.Name,
                    exception);
            }
        }
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
                api.ShowChatMessage(string.Join("\n", lines));
                return TextCommandResult.Success("", "openhand-status");
            })
            .EndSubCommand();
    }

    public override void Dispose()
    {
        clientController?.Dispose();
        indicatorRenderer?.Dispose();
        serverController?.Dispose();
        harmony?.UnpatchAll(HarmonyId);
        ClientApi = null;
    }
}
