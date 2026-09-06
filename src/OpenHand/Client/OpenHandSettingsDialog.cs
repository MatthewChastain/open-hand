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

    // Layout: 480px wide with generous vertical spacing and clear sectioning:
    // - Behavior switches (pitch 36px, right-aligned)
    // - Indicator position (280px dropdown prevents text truncation)
    // - Pixel offset steppers
    // - Help text block with dedicated vertical clearance
    // - Footer buttons safely below the wrapped help text
    private void ComposeDialog()
    {
        ElementBounds inner = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, 480, 424);
        ElementBounds outer = inner.FlatCopy().FixedGrow(0, 34);
        CairoFont label = CairoFont.WhiteDetailText();
        CairoFont small = CairoFont.WhiteSmallText();

        Composers["settings"] = capi.Gui.CreateCompo("openhand-settings", outer)
            .AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false)
            .AddDialogTitleBar("Open Hand Settings", () => TryClose())
            .BeginChildElements(inner)
                .AddStaticText("Visual indicator", label, ElementBounds.Fixed(18, 28, 380, 22), "showIndicatorLabel")
                .AddSwitch(OnShowIndicatorToggled, ElementBounds.Fixed(434, 25, 28, 28), "showIndicator")
                .AddStaticText("Center hotbar", label, ElementBounds.Fixed(18, 64, 380, 22), "centerLabel")
                .AddSwitch(OnCenterToggled, ElementBounds.Fixed(434, 61, 28, 28), "centerHotbar")
                .AddStaticText("Slot key double-tap", label, ElementBounds.Fixed(18, 100, 380, 22), "doubleTapLabel")
                .AddSwitch(OnDoubleTapToggled, ElementBounds.Fixed(434, 97, 28, 28), "doubleTap")
                .AddStaticText("Indicator position", label, ElementBounds.Fixed(18, 144, 160, 22), "anchorLabel")
                .AddDropDown(AnchorValues, AnchorNames, AnchorIndex(),
                    OnAnchorSelected, ElementBounds.Fixed(180, 141, 282, 26), "anchor")
                .AddStaticText("Icon offset X", label, ElementBounds.Fixed(18, 182, 130, 22), "offsetXLabel")
                .AddSmallButton("-", () => NudgeOffset(axisX: true, -1),
                    ElementBounds.Fixed(160, 180, 24, 24), EnumButtonStyle.Small, "offsetXMinus")
                .AddDynamicText(OffsetText(c => c.IconOffsetX), small,
                    ElementBounds.Fixed(192, 182, 55, 22), "offsetX")
                .AddSmallButton("+", () => NudgeOffset(axisX: true, 1),
                    ElementBounds.Fixed(252, 180, 24, 24), EnumButtonStyle.Small, "offsetXPlus")
                .AddStaticText("Icon offset Y", label, ElementBounds.Fixed(18, 216, 130, 22), "offsetYLabel")
                .AddSmallButton("-", () => NudgeOffset(axisX: false, -1),
                    ElementBounds.Fixed(160, 214, 24, 24), EnumButtonStyle.Small, "offsetYMinus")
                .AddDynamicText(OffsetText(c => c.IconOffsetY), small,
                    ElementBounds.Fixed(192, 216, 55, 22), "offsetY")
                .AddSmallButton("+", () => NudgeOffset(axisX: false, 1),
                    ElementBounds.Fixed(252, 214, 24, 24), EnumButtonStyle.Small, "offsetYPlus")
                .AddStaticText(
                    "Centering applies only to compatible layouts and falls back automatically. " +
                    "With the indicator hidden, entering Open Hand is hotkey-only. " +
                    "Slot key double-tap selects Open Hand when the active slot's number key is pressed again.",
                    small, ElementBounds.Fixed(18, 258, 444, 90), "help")
                .AddSmallButton("Reset defaults", ResetDefaults,
                    ElementBounds.Fixed(18, 374, 120, 28), EnumButtonStyle.Small, "reset")
                .AddSmallButton("Done", () => TryClose(),
                    ElementBounds.Fixed(372, 374, 90, 28), EnumButtonStyle.Small, "done")
            .EndChildElements()
            // Without Compose the static texture is never built: the dialog
            // opens (mouse ungrabbed) but renders nothing.
            .Compose();

        Composers["settings"].GetSwitch("showIndicator").SetValue(config().ShowIndicator);
        Composers["settings"].GetSwitch("centerHotbar").SetValue(config().CenterHotbar);
        Composers["settings"].GetSwitch("doubleTap").SetValue(config().DoubleTapHotbarKey);
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

    private void OnDoubleTapToggled(bool on) =>
        applyAndSave(c => c.DoubleTapHotbarKey = on);

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
            c.CenterHotbar = true;
            c.DoubleTapHotbarKey = false;
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
