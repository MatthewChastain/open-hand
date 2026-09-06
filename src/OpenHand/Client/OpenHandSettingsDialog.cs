using OpenHand.Common;
using Vintagestory.API.Client;

namespace OpenHand.Client;

// Native settings popup bound to Ctrl+tilde (rebindable). Every change is
// applied and persisted immediately through the mod's config pipeline; the
// dialog never buffers state until close.
internal sealed class OpenHandSettingsDialog : GuiDialog
{
    private const int MinOffset = -100;

    private const int MaxOffset = 100;

    private static readonly string[] AnchorValues = ["auto", "offhandgap", "left", "right"];

    private static readonly string[] AnchorNames =
    [
        "Automatic (left extension)",
        "Offhand gap (classic)",
        "Left of hotbar row",
        "Right of hotbar row"
    ];

    private readonly Func<OpenHandClientConfig> config;

    // Receives a config mutator: apply it to the live config, push to the
    // runtime, and persist. Same pipeline as the chat commands.
    private readonly Action<Action<OpenHandClientConfig>> applyAndSave;

    public OpenHandSettingsDialog(
        ICoreClientAPI capi,
        Func<OpenHandClientConfig> config,
        Action<Action<OpenHandClientConfig>> applyAndSave)
        : base(capi)
    {
        this.config = config;
        this.applyAndSave = applyAndSave;
        ComposeDialog();
    }

    // Toggle is driven by the mod's own registered hotkey instead of a dialog
    // toggle code, so the same binding works before the world HUD exists.
    public override string ToggleKeyCombinationCode => null!;

    private void ComposeDialog()
    {
        ElementBounds inner = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, 430, 300);
        ElementBounds outer = inner.FlatCopy().FixedGrow(0, 34);
        CairoFont label = CairoFont.WhiteDetailText();
        CairoFont small = CairoFont.WhiteSmallText();

        Composers["settings"] = capi.Gui.CreateCompo("openhand-settings", outer)
            .AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false)
            .AddDialogTitleBar("Open Hand Settings", () => TryClose())
            .BeginChildElements(inner)
                .AddStaticText("Visual indicator", label, ElementBounds.Fixed(15, 14, 250, 22), "showIndicatorLabel")
                .AddSwitch(OnShowIndicatorToggled, ElementBounds.Fixed(385, 14, 28, 28), "showIndicator")
                .AddStaticText("Center hotbar", label, ElementBounds.Fixed(15, 49, 250, 22), "centerLabel")
                .AddSwitch(OnCenterToggled, ElementBounds.Fixed(385, 49, 28, 28), "centerHotbar")
                .AddStaticText("Indicator position", label, ElementBounds.Fixed(15, 94, 180, 22), "anchorLabel")
                .AddDropDown(AnchorValues, AnchorNames, AnchorIndex(),
                    OnAnchorSelected, ElementBounds.Fixed(200, 92, 213, 25), "anchor")
                .AddStaticText("Icon offset X", label, ElementBounds.Fixed(15, 133, 130, 22), "offsetXLabel")
                .AddSmallButton("-", () => NudgeOffset(axisX: true, -1),
                    ElementBounds.Fixed(150, 129, 24, 24), EnumButtonStyle.Small, "offsetXMinus")
                .AddDynamicText(OffsetText(c => c.IconOffsetX), small,
                    ElementBounds.Fixed(182, 133, 50, 22), "offsetX")
                .AddSmallButton("+", () => NudgeOffset(axisX: true, 1),
                    ElementBounds.Fixed(238, 129, 24, 24), EnumButtonStyle.Small, "offsetXPlus")
                .AddStaticText("Icon offset Y", label, ElementBounds.Fixed(15, 163, 130, 22), "offsetYLabel")
                .AddSmallButton("-", () => NudgeOffset(axisX: false, -1),
                    ElementBounds.Fixed(150, 159, 24, 24), EnumButtonStyle.Small, "offsetYMinus")
                .AddDynamicText(OffsetText(c => c.IconOffsetY), small,
                    ElementBounds.Fixed(182, 163, 50, 22), "offsetY")
                .AddSmallButton("+", () => NudgeOffset(axisX: false, 1),
                    ElementBounds.Fixed(238, 159, 24, 24), EnumButtonStyle.Small, "offsetYPlus")
                .AddStaticText(
                    "Centering applies only to compatible layouts and falls back automatically. " +
                    "With the indicator hidden, entering Open Hand is hotkey-only.",
                    small, ElementBounds.Fixed(15, 196, 400, 40), "help")
                .AddSmallButton("Reset defaults", ResetDefaults,
                    ElementBounds.Fixed(15, 258, 110, 26), EnumButtonStyle.Small, "reset")
                .AddSmallButton("Done", () => TryClose(),
                    ElementBounds.Fixed(328, 258, 85, 26), EnumButtonStyle.Small, "done")
            .EndChildElements()
            // Without Compose the static texture is never built: the dialog
            // opens (mouse ungrabbed) but renders nothing.
            .Compose();

        Composers["settings"].GetSwitch("showIndicator").SetValue(config().ShowIndicator);
        Composers["settings"].GetSwitch("centerHotbar").SetValue(config().CenterHotbar);
    }

    private int AnchorIndex()
    {
        string value = config().IconAnchor?.Trim().ToLowerInvariant() ?? "auto";
        int index = Array.IndexOf(AnchorValues, value);
        return index < 0 ? 0 : index;
    }

    private string OffsetText(Func<OpenHandClientConfig, int> selector) =>
        $"{selector(config())} px";

    private void OnShowIndicatorToggled(bool on) =>
        applyAndSave(c => c.ShowIndicator = on);

    private void OnCenterToggled(bool on) =>
        applyAndSave(c => c.CenterHotbar = on);

    // Single-select dropdowns invoke (selectedValue, true); the value is the
    // stored anchor string itself.
    private void OnAnchorSelected(string code, bool _) =>
        applyAndSave(c => c.IconAnchor = code);

    private bool NudgeOffset(bool axisX, int delta)
    {
        applyAndSave(c =>
        {
            if (axisX) c.IconOffsetX = Math.Clamp(c.IconOffsetX + delta, MinOffset, MaxOffset);
            else c.IconOffsetY = Math.Clamp(c.IconOffsetY + delta, MinOffset, MaxOffset);
        });
        Composers["settings"].GetDynamicText(axisX ? "offsetX" : "offsetY")
            .SetNewText(OffsetText(c => axisX ? c.IconOffsetX : c.IconOffsetY));
        return true;
    }

    // Rebuild instead of poking individual widgets so dropdown/switch state
    // always comes from the same source of truth as a fresh open.
    private bool ResetDefaults()
    {
        applyAndSave(c =>
        {
            c.IconAnchor = "auto";
            c.IconOffsetX = 0;
            c.IconOffsetY = 0;
            c.ShowIndicator = true;
            c.CenterHotbar = false;
        });
        Composers.ClearComposers();
        ComposeDialog();
        return true;
    }

    public override void Dispose()
    {
        Composers?.ClearComposers();
        base.Dispose();
    }
}
