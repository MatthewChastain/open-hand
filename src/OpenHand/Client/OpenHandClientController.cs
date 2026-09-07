using OpenHand.Common;
using OpenHand.Patches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OpenHand.Client;

internal sealed class OpenHandClientController : IDisposable
{
    private const string ChannelName = "openhand";
    private const string SelectHotKeyCode = "openhand.select";
    private const string IndicatorHotKeyCode = "openhand.indicator";

    // Verified against Vintage Story 1.22.7 (InventoryPlayerHotbar):
    // slot 10 is the skill slot, slot 11 is the offhand, and the vanilla wheel
    // ring only spans hotbar slots 0-9 plus the skill slot while it is occupied.
    private const int SkillSlotIndex = 10;

    // HudHotbar.moveToHotbarSlot reads raw key index 3 to enter backpack mode.
    private const int BackpackModeRawKey = 3;

    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private readonly Func<bool> isIndicatorVisible;
    private readonly Func<bool> isDoubleTapEnabled;
    private long sweepListenerId;
    private int nextRevision;
    private bool disposed;

    public OpenHandClientController(
        ICoreClientAPI capi,
        Action openSettings,
        Func<bool> isIndicatorVisible,
        Func<bool> isDoubleTapEnabled)
    {
        this.capi = capi;
        this.isIndicatorVisible = isIndicatorVisible;
        this.isDoubleTapEnabled = isDoubleTapEnabled;
        channel = capi.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<OpenHandSelectionRequest>()
            .RegisterMessageType<OpenHandSelectionUpdate>()
            .SetMessageHandler<OpenHandSelectionUpdate>(OnSelectionUpdate);

        capi.Input.RegisterHotKey(
            SelectHotKeyCode,
            "Select Open Hand",
            GlKeys.Tilde,
            HotkeyType.CharacterControls);
        capi.Input.SetHotKeyHandler(SelectHotKeyCode, _ =>
        {
            IClientPlayer? player = capi.World?.Player;
            if (player is not null)
            {
                SelectOpenHand(player);
            }

            return true;
        });

        capi.Input.RegisterHotKey(
            IndicatorHotKeyCode,
            "Open Open Hand settings",
            GlKeys.Tilde,
            HotkeyType.CharacterControls,
            ctrlPressed: true);
        capi.Input.SetHotKeyHandler(IndicatorHotKeyCode, _ =>
        {
            openSettings();
            return true;
        });

        sweepListenerId = capi.Event.RegisterGameTickListener(
            OnGameTick, 0, 0);

        capi.Event.MouseWheelMove += OnMouseWheelMove;
        capi.Event.MouseDown += OnMouseDown;
        capi.Event.BeforeActiveSlotChanged += OnBeforeActiveSlotChanged;
        capi.Event.KeyDown += OnKeyDown;
        capi.Event.LeftWorld += OnLeftWorld;
    }

    // Enforces the substituted-slot invariants every tick: CarryOn's
    // place-down leaves its temporary block stack in the active hand slot and
    // its pick-up strands a LockedItemSlot wrapper in the mod-owned inventory
    // (see OpenHandRuntime.SweepSubstitutedSlot).
    private void OnGameTick(float deltaTime) => OpenHandRuntime.SweepSubstitutedSlot();

    // Fires from api.eventapi.TriggerMouseDown before any client system or
    // dialog sees the click (verified against 1.22.7 ClientMain
    // .UpdateMouseButtonState), so handling here protects a cursor-held stack
    // from HudDropItem, which drops stacks clicked outside every opened
    // composer's root bounds — exactly where the indicator cell sits. With an
    // empty cursor the click toggles Open Hand like the hotkey does.
    private void OnMouseDown(MouseEvent args)
    {
        if (args.Handled || !isIndicatorVisible() || capi.Input.MouseGrabbed)
        {
            return;
        }

        IClientPlayer? player = capi.World?.Player;
        if (player is null || DialogsCaptureInputs() ||
            !HudHotbarPatch.TryGetIndicatorRect(out int x, out int y, out int size) ||
            args.X < x || args.X >= x + size || args.Y < y || args.Y >= y + size)
        {
            return;
        }

        // The same guard HudDropItem applies: never steal clicks that belong
        // to an open dialog's interactive area.
        foreach (GuiDialog openedDialog in capi.Gui.OpenedGuis)
        {
            foreach (GuiComposer composer in openedDialog.Composers.Values)
            {
                if (composer.Bounds.PointInside(args.X, args.Y))
                {
                    return;
                }
            }
        }

        args.Handled = true;
        if (player.InventoryManager.MouseItemSlot is not { Empty: false })
        {
            SelectOpenHand(player);
        }
    }

    // Vanilla's hotbarslot1-10 handlers return true and the hotkey dispatcher
    // stops at the first handler that does, so Open Hand hotkeys registered on
    // the same keys would never fire. The KeyDown event reaches mod listeners
    // before hotkey dispatch (verified against Vintage Story 1.22.7
    // ClientMain.OnKeyDown), and leaving args.Handled untouched keeps vanilla's
    // own handling of the press intact.
    private void OnKeyDown(KeyEvent args)
    {
        if (args.Handled)
        {
            return;
        }

        IClientPlayer? player = capi.World?.Player;
        if (player is null || DialogsCaptureInputs())
        {
            return;
        }

        if (!isDoubleTapEnabled())
        {
            return;
        }

        // With a slot hovered (inventory open), vanilla turns a number press
        // into a swap with that slot; never select Open Hand on top of it.
        if (player.InventoryManager.CurrentHoveredSlot is not null)
        {
            return;
        }

        int? requestedSlot = SlotForKeyEvent(args);
        if (requestedSlot is null)
        {
            return;
        }

        OpenHandDoubleTap.DoubleTapDecision decision = OpenHandDoubleTap.Resolve(
            OpenHandRuntime.IsSelected(player),
            player.InventoryManager.ActiveHotbarSlotNumber,
            requestedSlot.Value,
            isDoubleTapEnabled());
        switch (decision.Action)
        {
            case OpenHandDoubleTap.DoubleTapAction.Enter:
                SelectOpenHand(player);
                break;
            case OpenHandDoubleTap.DoubleTapAction.ExitToSlot:
                // Selection locked while carrying: CarryOn cancels the slot
                // change anyway, and exiting would strand the carried block.
                if (!CarryOnInterop.IsCarryingHands(player.Entity))
                {
                    DeselectToSlot(player, decision.Destination);
                }

                break;
        }
    }

    // Resolve the press against vanilla's own hotbarslot bindings so user
    // rebinds are honored; modifier variants such as the Ctrl backpack-slot
    // keys never match their unmodified mapping.
    private int? SlotForKeyEvent(KeyEvent args)
    {
        for (int slot = 0; slot < OpenHandSelectionState.PhysicalHotbarSlots; slot++)
        {
            if (capi.Input.HotKeys.TryGetValue($"hotbarslot{slot + 1}", out HotKey? hotkey) &&
                hotkey.CurrentMapping.KeyCode == args.KeyCode &&
                hotkey.CurrentMapping.Alt == args.AltPressed &&
                hotkey.CurrentMapping.Ctrl == args.CtrlPressed &&
                hotkey.CurrentMapping.Shift == args.ShiftPressed)
            {
                return slot;
            }
        }

        return null;
    }

    private void SelectOpenHand(IClientPlayer player)
    {
        OpenHandSelectionState current = OpenHandRuntime.Get(player);
        if (current.IsSelected)
        {
            // Toggle: pressing the hotkey again returns to the slot held before
            // entering Open Hand — blocked while carrying, the same lock as
            // scrolling and digit keys.
            if (!CarryOnInterop.IsCarryingHands(player.Entity))
            {
                DeselectToSlot(player, current.RememberedHotbarSlot);
            }

            return;
        }

        // Mirrors vanilla HudHotbar.OnKeySlot: cancel any held-item use first so
        // the substitution never interrupts an active item interaction.
        if (player.Entity.Controls.HandUse != EnumHandInteract.None &&
            !CancelHeldUse(player))
        {
            return;
        }

        RequestSelection(true, player.InventoryManager.ActiveHotbarSlotNumber, player);
    }

    private void DeselectToSlot(IClientPlayer player, int destination)
    {
        RequestSelection(false, destination, player);
        int previousSlot = player.InventoryManager.ActiveHotbarSlotNumber;
        player.InventoryManager.ActiveHotbarSlotNumber = destination;
        // Same-value assignment raises no ActiveSlotChanged event, so restore
        // the highlight the HUD patch removed while Open Hand was selected.
        if (player.InventoryManager.ActiveHotbarSlotNumber == previousSlot)
        {
            HudHotbarPatch.RestoreHighlight(capi, destination);
        }
    }

    private bool CancelHeldUse(IClientPlayer player)
    {
        EnumHandInteract handUse = player.Entity.Controls.HandUse;
        if (!player.Entity.TryStopHandAction(false, EnumItemUseCancelReason.ChangeSlot))
        {
            return false;
        }

        capi.Network.SendHandInteraction(
            2,
            player.CurrentBlockSelection,
            player.CurrentEntitySelection,
            handUse,
            1,
            firstEvent: false,
            EnumItemUseCancelReason.ChangeSlot);
        return true;
    }

    private void OnMouseWheelMove(MouseWheelEventArgs args)
    {
        if (args.IsHandled || args.delta == 0)
        {
            return;
        }

        IClientPlayer? player = capi.World?.Player;
        if (player is null)
        {
            return;
        }

        // Selection locked while carrying: scrolling neither exits Open Hand
        // nor cycles slots (CarryOn blocks those changes while carrying).
        if (OpenHandRuntime.IsSelected(player) && CarryOnInterop.IsCarryingHands(player.Entity))
        {
            if (WheelWouldReachHotbar())
            {
                args.SetHandled();
            }

            return;
        }

        if (!WheelWouldReachHotbar())
        {
            return;
        }

        bool skillOccupied = !IsHotbarSlotEmpty(player, SkillSlotIndex);
        OpenHandWheelRing.WheelDecision decision = OpenHandWheelRing.Resolve(
            OpenHandRuntime.IsSelected(player),
            player.InventoryManager.ActiveHotbarSlotNumber,
            skillOccupied,
            capi.Input.KeyboardKeyStateRaw[BackpackModeRawKey],
            args.delta,
            allowEntry: isIndicatorVisible());

        switch (decision.Action)
        {
            case OpenHandWheelRing.WheelAction.Enter:
                args.SetHandled();
                SelectOpenHand(player);
                break;
            case OpenHandWheelRing.WheelAction.ExitToSlot:
                args.SetHandled();
                DeselectToSlot(player, decision.Destination);
                break;
        }
    }

    /// <summary>
    /// Replicates the wheel staging of <c>ClientMain.OnMouseWheel</c> and
    /// <c>GuiManager.OnMouseWheel</c> so interception only happens when the scroll
    /// would otherwise reach the vanilla hotbar slot cycling.
    /// </summary>
    private bool WheelWouldReachHotbar()
    {
        if (DialogsCaptureInputs())
        {
            return false;
        }

        foreach (GuiDialog loadedDialog in capi.Gui.LoadedGuis)
        {
            if (!loadedDialog.IsOpened() || !loadedDialog.ShouldReceiveMouseEvents())
            {
                continue;
            }

            bool cursorInside = false;
            foreach (GuiComposer composer in loadedDialog.Composers.Values)
            {
                cursorInside |= composer.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY);
            }

            if (cursorInside)
            {
                // The hotbar HUD itself is the only dialog that cycles slots on wheel.
                return loadedDialog.DebugName == "HudHotbar";
            }
        }

        return true;
    }

    // Chat and other capture dialogs swallow the press upstream in
    // ClientMain.OnKeyDown, but the KeyDown event itself fires first, so the
    // double-tap listener has to apply the same filter itself.
    private bool DialogsCaptureInputs()
    {
        foreach (GuiDialog openedDialog in capi.Gui.OpenedGuis)
        {
            if (openedDialog.CaptureAllInputs())
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHotbarSlotEmpty(IClientPlayer player, int slotIndex)
    {
        return player.InventoryManager.GetHotbarInventory()?[slotIndex] is not { Empty: false };
    }

    private EnumHandling OnBeforeActiveSlotChanged(ActiveSlotChangeEventArgs change)
    {
        IClientPlayer? player = capi.World?.Player;
        if (player is not null && OpenHandRuntime.IsSelected(player) &&
            !CarryOnInterop.IsCarryingHands(player.Entity))
        {
            RequestSelection(false, change.ToSlot, player);
        }

        return EnumHandling.PassThrough;
    }

    private void RequestSelection(bool selected, int rememberedHotbarSlot, IClientPlayer player)
    {
        OpenHandSelectionState current = OpenHandRuntime.Get(player);
        if (current.Revision > 0 && current.IsSelected == selected)
        {
            return;
        }

        int revision = ++nextRevision;
        OpenHandRuntime.Set(player, selected, rememberedHotbarSlot, revision);
        channel.SendPacket(new OpenHandSelectionRequest
        {
            Selected = selected,
            RememberedHotbarSlot = rememberedHotbarSlot,
            Revision = revision
        });
    }

    private void OnSelectionUpdate(OpenHandSelectionUpdate update)
    {
        IClientPlayer? localPlayer = capi.World?.Player;
        if (localPlayer is not null && update.PlayerUid == localPlayer.PlayerUID)
        {
            nextRevision = Math.Max(nextRevision, update.Revision);
            OpenHandRuntime.Set(localPlayer, update.Selected, update.RememberedHotbarSlot, update.Revision);
        }
    }

    private void OnLeftWorld()
    {
        OpenHandRuntime.ClearAll();
        nextRevision = 0;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        capi.Event.UnregisterGameTickListener(sweepListenerId);
        capi.Event.MouseWheelMove -= OnMouseWheelMove;
        capi.Event.MouseDown -= OnMouseDown;
        capi.Event.BeforeActiveSlotChanged -= OnBeforeActiveSlotChanged;
        capi.Event.KeyDown -= OnKeyDown;
        capi.Event.LeftWorld -= OnLeftWorld;
        capi.Input.SetHotKeyHandler(SelectHotKeyCode, _ => true);
        capi.Input.SetHotKeyHandler(IndicatorHotKeyCode, _ => true);
        OpenHandRuntime.ClearAll();
    }
}
