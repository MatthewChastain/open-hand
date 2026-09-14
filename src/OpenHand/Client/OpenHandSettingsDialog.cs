using OpenHand.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

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

    // The rebindable Open Hand hotkeys, shown with capture-and-press rebind
    // buttons. Capture writes event-scale keycodes into the hotkey's
    // CurrentMapping — the same field vanilla's own controls screen writes —
    // and OpenHandHotkeyBinding persists the mapping through vanilla's own
    // ClientSettings path and keeps mouse bindings reachable by the
    // dispatcher, so matching, persistence, and triggering all follow the
    // game's normal path.
    private static readonly string[] BindableHotkeyCodes =
    [
        "openhand.select",
        "openhand.offhand",
        "openhand.indicator"
    ];

    private static readonly string[] BindableHotkeyNames =
    [
        "Select Open Hand",
        "Toggle Open Offhand",
        "Open Hand Settings"
    ];

    private readonly Func<OpenHandClientConfig> config;

    // Receives a config mutator: apply it to the live config, push to the
    // runtime, and persist. Same pipeline as the chat commands.
    private readonly Action<Action<OpenHandClientConfig>> applyAndSave;

    // While capturing, the dialog reports capturing inputs, so the client
    // controller's KeyDown/MouseDown listeners yield (their guard checks
    // CaptureAllInputs) and setting Handled in the capture handler keeps the
    // captured key from also firing the very hotkey being rebound.
    //
    // Keys arrive through capi.Event.KeyDown, which fires before hotkey
    // dispatch. Mouse buttons arrive through the dialog's own OnMouseDown:
    // CaptureRawMouse makes ClientMain.OnMouseDownRaw route EVERY raw click
    // straight to the dialogs (GuiManager) and skip the hotkey manager — the
    // same mechanism vanilla's escape-menu settings use, and the only way to
    // see buttons 4-8, whose clicks never reach capi.Event.MouseDown because
    // no vanilla hotkey routes them into UpdateMouseButtonState. The click
    // that starts a capture is dispatched before capturing begins, so the
    // first press the override sees is the one the user means to bind.
    private string? capturingHotKeyCode;

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

    public override bool CaptureAllInputs() => capturingHotKeyCode is not null;

    // While capturing, every raw mouse click is routed to the dialogs instead
    // of the hotkey manager (verified against 1.22.7 ClientMain
    // .OnMouseDownRaw and GuiManager.CaptureRawMouse) — exactly what vanilla's
    // own settings screen does so a capture sees all eight buttons.
    public override bool CaptureRawMouse() => capturingHotKeyCode is not null;

    public override void OnMouseDown(MouseEvent args)
    {
        if (capturingHotKeyCode is null)
        {
            base.OnMouseDown(args);
            return;
        }

        // Swallow every click during capture so nothing reaches the world or
        // other dialogs; bind only real buttons (wheel and None are not
        // buttons). Vanilla mouse hotkeys ignore modifiers
        // (HotKey.MouseControlsIgnoreModifiers), so store none.
        args.Handled = true;
        if (args.Button is EnumMouseButton.None or EnumMouseButton.Wheel ||
            !OpenHandHotkeyBinding.IsMouseButton(KeyCombination.MouseStart + (int)args.Button))
        {
            return;
        }

        FinishCapture(
            applied: true,
            KeyCombination.MouseStart + (int)args.Button,
            ctrl: false,
            alt: false,
            shift: false);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (capturingHotKeyCode is not null)
        {
            // The release belonging to a captured press must not leak into
            // other dialogs or the world either.
            args.Handled = true;
            return;
        }

        base.OnMouseUp(args);
    }

    // Layout: 500px wide with independently padded sections. Explicit section
    // ranges and an intentionally blank footer gap keep every text and button
    // bound disjoint at normal GUI scales.
    private void ComposeDialog()
    {
        ElementBounds inner = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, 500, 670);
        ElementBounds outer = inner.FlatCopy().FixedGrow(0, 34);
        CairoFont label = CairoFont.WhiteDetailText();
        CairoFont small = CairoFont.WhiteSmallText();

        Composers["settings"] = capi.Gui.CreateCompo("openhand-settings", outer)
            .AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false)
            .AddDialogTitleBar("Open Hand Settings", () => TryClose())
            .BeginChildElements(inner)
                .AddStaticText("MAIN HAND", small, ElementBounds.Fixed(24, 24, 452, 20), "mainHandHeading")
                .AddStaticText("Enable Open Hand", label, ElementBounds.Fixed(24, 52, 380, 22), "mainHandEnabledLabel")
                .AddSwitch(OnMainHandToggled, ElementBounds.Fixed(448, 49, 28, 28), "mainHandEnabled")
                .AddStaticText("Show indicator", label, ElementBounds.Fixed(24, 84, 380, 22), "showIndicatorLabel")
                .AddSwitch(OnShowIndicatorToggled, ElementBounds.Fixed(448, 81, 28, 28), "showIndicator")
                .AddStaticText("Slot key double-tap", label, ElementBounds.Fixed(24, 116, 380, 22), "doubleTapLabel")
                .AddSwitch(OnDoubleTapToggled, ElementBounds.Fixed(448, 113, 28, 28), "doubleTap")

                .AddStaticText("OFFHAND", small, ElementBounds.Fixed(24, 162, 452, 20), "offhandHeading")
                .AddStaticText("Enable Open Offhand", label, ElementBounds.Fixed(24, 190, 380, 22), "offhandEnabledLabel")
                .AddSwitch(OnEmptyOffhandToggled, ElementBounds.Fixed(448, 187, 28, 28), "emptyOffhand")
                .AddStaticText("Show offhand indicator", label, ElementBounds.Fixed(24, 222, 380, 22), "showOffhandIndicatorLabel")
                .AddSwitch(OnShowOffhandIndicatorToggled, ElementBounds.Fixed(448, 219, 28, 28), "showOffhandIndicator")

                .AddStaticText("HOTBAR APPEARANCE", small, ElementBounds.Fixed(24, 268, 452, 20), "appearanceHeading")
                .AddStaticText("Center hotbar", label, ElementBounds.Fixed(24, 296, 380, 22), "centerLabel")
                .AddSwitch(OnCenterToggled, ElementBounds.Fixed(448, 293, 28, 28), "centerHotbar")
                .AddStaticText("Indicator position", label, ElementBounds.Fixed(24, 330, 150, 22), "anchorLabel")
                .AddDropDown(AnchorValues, AnchorNames, AnchorIndex(),
                    OnAnchorSelected, ElementBounds.Fixed(178, 327, 298, 26), "anchor")
                .AddStaticText("Icon offset X", label, ElementBounds.Fixed(24, 366, 130, 22), "offsetXLabel")
                .AddSmallButton("-", () => NudgeOffset(axisX: true, -1),
                    ElementBounds.Fixed(166, 364, 28, 26), EnumButtonStyle.Small, "offsetXMinus")
                .AddDynamicText(OffsetText(c => c.IconOffsetX), small,
                    ElementBounds.Fixed(204, 366, 64, 22), "offsetX")
                .AddSmallButton("+", () => NudgeOffset(axisX: true, 1),
                    ElementBounds.Fixed(276, 364, 28, 26), EnumButtonStyle.Small, "offsetXPlus")
                .AddStaticText("Icon offset Y", label, ElementBounds.Fixed(24, 400, 130, 22), "offsetYLabel")
                .AddSmallButton("-", () => NudgeOffset(axisX: false, -1),
                    ElementBounds.Fixed(166, 398, 28, 26), EnumButtonStyle.Small, "offsetYMinus")
                .AddDynamicText(OffsetText(c => c.IconOffsetY), small,
                    ElementBounds.Fixed(204, 400, 64, 22), "offsetY")
                .AddSmallButton("+", () => NudgeOffset(axisX: false, 1),
                    ElementBounds.Fixed(276, 398, 28, 26), EnumButtonStyle.Small, "offsetYPlus")

                .AddStaticText("KEYBINDS", small, ElementBounds.Fixed(24, 450, 452, 20), "bindsHeading")
                .AddStaticText("Click a keybind, then press a key or mouse button. Escape cancels.",
                    small, ElementBounds.Fixed(24, 476, 452, 44), "bindsLabel")
                .AddStaticText(BindableHotkeyNames[0], label, ElementBounds.Fixed(24, 530, 238, 24), "bindSelectLabel")
                .AddSmallButton(BindButtonText(BindableHotkeyCodes[0]), () => BeginKeyCapture(BindableHotkeyCodes[0]),
                    ElementBounds.Fixed(274, 528, 202, 26), EnumButtonStyle.Small, "bindSelect")
                .AddStaticText(BindableHotkeyNames[1], label, ElementBounds.Fixed(24, 564, 238, 24), "bindOffhandLabel")
                .AddSmallButton(BindButtonText(BindableHotkeyCodes[1]), () => BeginKeyCapture(BindableHotkeyCodes[1]),
                    ElementBounds.Fixed(274, 562, 202, 26), EnumButtonStyle.Small, "bindOffhand")
                .AddStaticText(BindableHotkeyNames[2], label, ElementBounds.Fixed(24, 598, 238, 24), "bindIndicatorLabel")
                .AddSmallButton(BindButtonText(BindableHotkeyCodes[2]), () => BeginKeyCapture(BindableHotkeyCodes[2]),
                    ElementBounds.Fixed(274, 596, 202, 26), EnumButtonStyle.Small, "bindIndicator")
                .AddSmallButton("Reset defaults", ResetDefaults,
                    ElementBounds.Fixed(24, 632, 142, 30), EnumButtonStyle.Small, "reset")
                .AddSmallButton("Done", () => TryClose(),
                    ElementBounds.Fixed(386, 632, 90, 30), EnumButtonStyle.Small, "done")
            .EndChildElements()
            // Without Compose the static texture is never built: the dialog
            // opens (mouse ungrabbed) but renders nothing.
            .Compose();

        Composers["settings"].GetSwitch("showIndicator").SetValue(config().ShowIndicator);
        Composers["settings"].GetSwitch("mainHandEnabled").SetValue(config().MainHandEnabled);
        Composers["settings"].GetSwitch("showOffhandIndicator").SetValue(config().ShowOffhandIndicator);
        Composers["settings"].GetSwitch("centerHotbar").SetValue(config().CenterHotbar);
        Composers["settings"].GetSwitch("doubleTap").SetValue(config().DoubleTapHotbarKey);
        Composers["settings"].GetSwitch("emptyOffhand").SetValue(config().EmptyOffhandEnabled);
    }

    private string BindButtonText(string hotkeyCode)
    {
        if (capturingHotKeyCode == hotkeyCode)
        {
            return "Press a key…";
        }

        return capi.Input.HotKeys.TryGetValue(hotkeyCode, out HotKey? hotkey)
            ? hotkey.CurrentMapping.ToString()
            : "unbound";
    }
    private bool BeginKeyCapture(string hotKeyCode)
    {
        if (capturingHotKeyCode == hotKeyCode)
        {
            return true;
        }

        if (capturingHotKeyCode is not null)
        {
            // Switching buttons mid-capture: drop the previous capture.
            StopCaptureListeners();
        }

        capturingHotKeyCode = hotKeyCode;
        capi.Event.KeyDown += OnCaptureKeyDown;

        // Rebuild so the capturing button shows "Press a key…".
        Composers.ClearComposers();
        ComposeDialog();
        return true;
    }

    private void OnCaptureKeyDown(KeyEvent args)
    {
        args.Handled = true;

        if (args.KeyCode == (int)GlKeys.Escape)
        {
            FinishCapture(applied: false, 0, ctrl: false, alt: false, shift: false);
            return;
        }

        // Bare modifier presses build no binding; wait for the modified key.
        switch ((GlKeys)args.KeyCode)
        {
            case GlKeys.LShift or GlKeys.RShift or GlKeys.LControl or GlKeys.RControl or
                GlKeys.LAlt or GlKeys.RAlt or GlKeys.Menu:
                return;
        }

        FinishCapture(applied: true, args.KeyCode, args.CtrlPressed, args.AltPressed, args.ShiftPressed);
    }

    private void FinishCapture(bool applied, int keyCode, bool ctrl, bool alt, bool shift)
    {
        StopCaptureListeners();
        string? hotkeyCode = capturingHotKeyCode;
        capturingHotKeyCode = null;

        if (applied && hotkeyCode is not null &&
            capi.Input.HotKeys.TryGetValue(hotkeyCode, out HotKey? hotkey))
        {
            hotkey.CurrentMapping = new KeyCombination
            {
                KeyCode = keyCode,
                SecondKeyCode = null,
                Ctrl = ctrl,
                Alt = alt,
                Shift = shift,
                OnKeyUp = false
            };
            // Vanilla's own remap path: persist to the client settings (read
            // back at hotkey registration) and keep the dispatcher order so a
            // mouse-bound hotkey is reached before vanilla's mouse consumers.
            OpenHandHotkeyBinding.Persist(capi, hotkeyCode, hotkey.CurrentMapping);
            OpenHandHotkeyBinding.ApplyPriority(capi, hotkeyCode);
        }

        // Rebuild so every bind button reflects the current mapping.
        Composers.ClearComposers();
        ComposeDialog();
    }

    private void StopCaptureListeners()
    {
        capi.Event.KeyDown -= OnCaptureKeyDown;
    }

    private int AnchorIndex()
    {
        string value = config().IconAnchor?.Trim().ToLowerInvariant() ?? "auto";
        int index = Array.IndexOf(AnchorValues, value);
        return index < 0 ? 0 : index;
    }

    private string OffsetText(System.Func<OpenHandClientConfig, int> selector) =>
        $"{selector(config())} px";

    private void OnShowIndicatorToggled(bool on) =>
        applyAndSave(c => c.ShowIndicator = on);
    private void OnMainHandToggled(bool on) =>
        applyAndSave(c => c.MainHandEnabled = on);

    private void OnShowOffhandIndicatorToggled(bool on) =>
        applyAndSave(c => c.ShowOffhandIndicator = on);

    private void OnCenterToggled(bool on) =>
        applyAndSave(c => c.CenterHotbar = on);

    private void OnDoubleTapToggled(bool on) =>
        applyAndSave(c => c.DoubleTapHotbarKey = on);

    private void OnEmptyOffhandToggled(bool on) =>
        applyAndSave(c => c.EmptyOffhandEnabled = on);

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
            c.MainHandEnabled = true;
            c.ShowOffhandIndicator = true;
            c.CenterHotbar = true;
            c.DoubleTapHotbarKey = false;
            c.EmptyOffhandEnabled = false;
        });
        Composers.ClearComposers();
        ComposeDialog();
        return true;
    }

    public override void Dispose()
    {
        if (capturingHotKeyCode is not null)
        {
            StopCaptureListeners();
            capturingHotKeyCode = null;
        }

        Composers?.ClearComposers();
        base.Dispose();
    }
}
