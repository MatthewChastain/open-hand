using OpenHand.Common;
using OpenHand.Patches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace OpenHand.Client;

internal sealed class OpenHandClientController : IDisposable
{
    private const string ChannelName = "openhand";
    private const string SelectHotKeyCode = "openhand.select";
    private const string IndicatorHotKeyCode = "openhand.indicator";
    private const string OffhandHotKeyCode = "openhand.offhand";

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
    private readonly Func<bool> isEmptyOffhandEnabled;
    private long sweepListenerId;
    private int nextRevision;
    private int nextOffhandRevision;
    private bool disposed;

    // The server's join replay (and its snapshot of other players) is sent
    // mid-join — decompiled 1.22.7: HandleRequestJoin fires PlayerJoin between
    // LevelInitialize and LevelFinalize — so those packets arrive while the
    // client world is still loading and World.Player is still null. Dropping
    // them there made a restored selection invisible until the next toggle
    // (whose request then bounced off the server's restored revision as
    // stale). Buffer them and apply on the first tick after the player
    // exists.
    private readonly List<OpenHandSelectionUpdate> pendingSelectionUpdates = [];
    private readonly List<OpenHandOffhandUpdate> pendingOffhandUpdates = [];
    private bool refreshSent;

    public OpenHandClientController(
        ICoreClientAPI capi,
        Action openSettings,
        Func<bool> isIndicatorVisible,
        Func<bool> isDoubleTapEnabled,
        Func<bool> isEmptyOffhandEnabled)
    {
        this.capi = capi;
        this.isIndicatorVisible = isIndicatorVisible;
        this.isDoubleTapEnabled = isDoubleTapEnabled;
        this.isEmptyOffhandEnabled = isEmptyOffhandEnabled;
        channel = capi.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<OpenHandSelectionRequest>()
            .RegisterMessageType<OpenHandSelectionUpdate>()
            .RegisterMessageType<OpenHandOffhandRequest>()
            .RegisterMessageType<OpenHandOffhandUpdate>()
            .SetMessageHandler<OpenHandSelectionUpdate>(OnSelectionUpdate)
            .SetMessageHandler<OpenHandOffhandUpdate>(OnOffhandUpdate);

        capi.Input.RegisterHotKey(
            SelectHotKeyCode,
            "Select Open Hand",
            GlKeys.Tilde,
            HotkeyType.CharacterControls);
        capi.Input.SetHotKeyHandler(SelectHotKeyCode, _ =>
        {
            LogFireOnce(SelectHotKeyCode);
            IClientPlayer? player = capi.World?.Player;
            if (player is null)
            {
                return false;
            }

            if (DeclinesMouseContext(SelectHotKeyCode))
            {
                LogOnce($"{SelectHotKeyCode}:grab", GrabDeclineMessage(SelectHotKeyCode));
                return false;
            }

            return SelectOpenHand(player);
        });

        capi.Input.RegisterHotKey(
            IndicatorHotKeyCode,
            "Open Open Hand settings",
            GlKeys.Tilde,
            HotkeyType.CharacterControls,
            ctrlPressed: true);
        capi.Input.SetHotKeyHandler(IndicatorHotKeyCode, _ =>
        {
            LogFireOnce(IndicatorHotKeyCode);
            if (DeclinesMouseContext(IndicatorHotKeyCode))
            {
                LogOnce($"{IndicatorHotKeyCode}:grab", GrabDeclineMessage(IndicatorHotKeyCode));
                return false;
            }

            openSettings();
            return true;
        });

        capi.Input.RegisterHotKey(
            OffhandHotKeyCode,
            "Toggle empty offhand",
            GlKeys.Tilde,
            HotkeyType.CharacterControls,
            shiftPressed: true);
        capi.Input.SetHotKeyHandler(OffhandHotKeyCode, _ =>
        {
            LogFireOnce(OffhandHotKeyCode);
            IClientPlayer? player = capi.World?.Player;
            if (player is null)
            {
                return false;
            }

            if (!isEmptyOffhandEnabled())
            {
                LogOnce($"{OffhandHotKeyCode}:feature",
                    $"Open Hand: {OffhandHotKeyCode} declined — the empty-offhand feature switch is off; enable it in the Open Hand settings dialog.");
                return false;
            }

            if (DeclinesMouseContext(OffhandHotKeyCode))
            {
                LogOnce($"{OffhandHotKeyCode}:grab", GrabDeclineMessage(OffhandHotKeyCode));
                return false;
            }

            return ToggleEmptyOffhand(player);
        });

        // Registration just applied any mapping saved in the game's client
        // settings; a mouse-bound hotkey must move ahead of vanilla's mouse
        // consumers or the dispatcher never reaches it (see
        // OpenHandHotkeyBinding). No-op for keyboard bindings.
        OpenHandHotkeyBinding.ApplyPriority(capi, SelectHotKeyCode);
        OpenHandHotkeyBinding.ApplyPriority(capi, OffhandHotKeyCode);
        OpenHandHotkeyBinding.ApplyPriority(capi, IndicatorHotKeyCode);

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
    // (see OpenHandRuntime.SweepSubstitutedSlot); with the empty offhand
    // active, CarryOn's own left-hand lock lands in the substituted offhand
    // slot the same way (see OpenHandRuntime.SweepOffhandSlot).
    private void OnGameTick(float deltaTime)
    {
        OpenHandRuntime.SweepSubstitutedSlot();
        OpenHandRuntime.SweepOffhandSlot();
        SuppressOffhandHeldPose();
        ApplyJoinReplay();
        RequestInitialStateRefresh();
    }

    // Applies the join-time updates buffered above, in arrival order, once
    // the local player exists.
    private void ApplyJoinReplay()
    {
        IClientPlayer? localPlayer = capi.World?.Player;
        if (localPlayer is null ||
            (pendingSelectionUpdates.Count == 0 && pendingOffhandUpdates.Count == 0))
        {
            return;
        }

        LogOnce("replay:buffered",
            $"Open Hand: applying {pendingSelectionUpdates.Count + pendingOffhandUpdates.Count} state update(s) buffered during the world load.");
        foreach (OpenHandSelectionUpdate update in pendingSelectionUpdates)
        {
            ApplySelectionUpdate(localPlayer, update);
        }
        foreach (OpenHandOffhandUpdate update in pendingOffhandUpdates)
        {
            ApplyOffhandUpdate(localPlayer, update);
        }
        pendingSelectionUpdates.Clear();
        pendingOffhandUpdates.Clear();
    }

    // Self-heal: ask the server for the current state once the player exists
    // by sending revision-0 requests — the server's stale-revision branch
    // answers them with the authoritative state. Repairs any join-time
    // broadcast that was missed for any other reason.
    private void RequestInitialStateRefresh()
    {
        if (refreshSent || capi.World?.Player is null)
        {
            return;
        }

        refreshSent = true;
        LogOnce("net:refresh", "Open Hand: requesting the current selection and offhand state from the server.");
        SendRequest("selection refresh", new OpenHandSelectionRequest { Revision = 0 });
        SendRequest("empty-offhand refresh", new OpenHandOffhandRequest { Revision = 0 });
    }

    // While the offhand reads as empty, a raised shield would keep its pose:
    // ItemShield.OnHeldIdle decides the raise animation from
    // `LeftHandItemSlot == slot` (decompiled VSSurvivalMod 1.22.7), which no
    // longer matches the real shield's slot once substituted — and its
    // "wrong hand" branch re-raises the other arm while crouched. Suppress
    // both raise animations every tick for as long as the substitution is
    // active; toggling off hands the state back to vanilla's own logic.
    private void SuppressOffhandHeldPose()
    {
        IClientPlayer? player = capi.World?.Player;
        if (player is null || !OpenHandRuntime.IsOffhandEmpty(player))
        {
            return;
        }

        StopRaiseAnimation(player.Entity, "raiseshield-left");
        StopRaiseAnimation(player.Entity, "raiseshield-right");
    }

    private static void StopRaiseAnimation(Entity entity, string animation)
    {
        if (entity.AnimManager.IsAnimationActive([animation]))
        {
            entity.StopAnimation(animation);
        }
    }

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

        // With a slot hovered (inventory open), vanilla turns a number press
        // into a swap with that slot; never select Open Hand on top of it,
        // and never exit on top of a swap either.
        if (player.InventoryManager.CurrentHoveredSlot is not null)
        {
            return;
        }

        int? requestedSlot = SlotForKeyEvent(args);
        if (requestedSlot is null)
        {
            return;
        }

        // Resolved even when the double-tap preference is off: while Open
        // Hand is selected, pressing the remembered slot's digit is a vanilla
        // same-value no-op, so exiting is this listener's job alone.
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
                if (!CarryLockDeclines(player, SelectHotKeyCode))
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

    // A mouse-bound hotkey shares the dispatcher with vanilla's own mouse
    // controls (primarymouse/secondarymouse/middlemouse route every click
    // into the world and GUI), and Open Hand's hotkey sits ahead of them
    // while mouse-bound. Act only while the mouse is grabbed — the in-world
    // control state — and report every decline as false so the click falls
    // through to vanilla's consumers unchanged. Keyboard bindings keep their
    // existing behavior; the guard never applies to them.
    private bool DeclinesMouseContext(string hotKeyCode)
    {
        return capi.Input.HotKeys.TryGetValue(hotKeyCode, out HotKey? hotkey) &&
            OpenHandHotkeyBinding.IsMouseButton(hotkey.CurrentMapping.KeyCode) &&
            !capi.Input.MouseGrabbed;
    }

    // Returns whether the selection actually changed: hotkey handlers report
    // this to the dispatcher so declined presses fall through to vanilla.
    private bool SelectOpenHand(IClientPlayer player)
    {
        // The selection is locked in BOTH directions while carrying. CarryOn
        // wraps both hand slots for the whole carry (LockedItemSlot.Lock in
        // CarryStateService.SetCarried; Restore only in RemoveCarried, which
        // re-reads the hand slots — decompiled CarryOn 2.0.0-pre.8), so
        // flipping the substitution mid-carry strands the wrapper in a real
        // hotbar slot (dead until relog), and exiting mid-carry strands the
        // player on a slot that cannot place the block.
        if (CarryLockDeclines(player, SelectHotKeyCode))
        {
            return false;
        }

        OpenHandSelectionState current = OpenHandRuntime.Get(player);
        if (current.IsSelected)
        {
            // Toggle: pressing the hotkey again returns to the slot held before
            // entering Open Hand.
            DeselectToSlot(player, current.RememberedHotbarSlot);
            return true;
        }

        // Mirrors vanilla HudHotbar.OnKeySlot: cancel any held-item use first so
        // the substitution never interrupts an active item interaction.
        if (player.Entity.Controls.HandUse != EnumHandInteract.None &&
            !CancelHeldUse(player))
        {
            LogOnce($"{SelectHotKeyCode}:handuse",
                $"Open Hand: {SelectHotKeyCode} declined — a held-item interaction is active and could not be cancelled.");
            return false;
        }

        return RequestSelection(true, player.InventoryManager.ActiveHotbarSlotNumber, player);
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

        // Selection locked while carrying, in both directions and from any
        // state: scrolling neither enters nor exits Open Hand nor cycles slots
        // (CarryOn blocks those changes while carrying, and the substitution
        // must not flip mid-carry — see SelectOpenHand).
        if (CarryOnInterop.IsCarryingHands(player.Entity))
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

    private bool RequestSelection(bool selected, int rememberedHotbarSlot, IClientPlayer player)
    {
        OpenHandSelectionState current = OpenHandRuntime.Get(player);
        if (current.Revision > 0 && current.IsSelected == selected)
        {
            return false;
        }

        int revision = ++nextRevision;
        OpenHandRuntime.Set(player, selected, rememberedHotbarSlot, revision);
        SendRequest("selection", new OpenHandSelectionRequest
        {
            Selected = selected,
            RememberedHotbarSlot = rememberedHotbarSlot,
            Revision = revision
        });
        return true;
    }

    private void OnSelectionUpdate(OpenHandSelectionUpdate update)
    {
        IClientPlayer? localPlayer = capi.World?.Player;
        if (localPlayer is null)
        {
            pendingSelectionUpdates.Add(update);
            return;
        }

        ApplySelectionUpdate(localPlayer, update);
    }

    private void ApplySelectionUpdate(IClientPlayer localPlayer, OpenHandSelectionUpdate update)
    {
        if (update.PlayerUid != localPlayer.PlayerUID)
        {
            return;
        }

        nextRevision = Math.Max(nextRevision, update.Revision);
        OpenHandRuntime.Set(localPlayer, update.Selected, update.RememberedHotbarSlot, update.Revision);
    }

    // The empty-offhand toggle: requests the substitution through the
    // server-validated path exactly like the main-hand selection. Nothing is
    // ever written to the real offhand slot — the engine's offhand reads land
    // in the substituted empty slot for as long as the state says so.
    // Locked while carrying: the hands-carry slot IS the offhand — the
    // same lock as the selection paths (the handler's false result lets a
    // mouse-bound press fall through to vanilla's world interaction).
    private bool ToggleEmptyOffhand(IClientPlayer player)
    {
        if (CarryLockDeclines(player, OffhandHotKeyCode))
        {
            return false;
        }

        OpenHandOffhandState current = OpenHandRuntime.GetOffhandState(player);
        return RequestOffhandEmpty(player, !current.IsEmpty);
    }

    // Dropping the feature switch in the settings menu must also clear any
    // live substitution — the switch gates the feature, not just the hotkey.
    internal void DisableEmptyOffhand()
    {
        IClientPlayer? player = capi.World?.Player;
        if (player is not null && OpenHandRuntime.IsOffhandEmpty(player))
        {
            RequestOffhandEmpty(player, empty: false);
        }
    }

    private bool RequestOffhandEmpty(IClientPlayer player, bool empty)
    {
        OpenHandOffhandState current = OpenHandRuntime.GetOffhandState(player);
        if (current.Revision > 0 && current.IsEmpty == empty)
        {
            return false;
        }

        // Mirror the main-hand selection: apply optimistically so the
        // substitution is visible this frame, then let the server's broadcast
        // settle the authoritative revision.
        int revision = ++nextOffhandRevision;
        OpenHandRuntime.SetOffhandEmpty(player, empty, revision);
        SendRequest("empty-offhand", new OpenHandOffhandRequest { IsEmpty = empty, Revision = revision });
        return true;
    }

    // First-send telemetry per request kind: proves the request left the
    // client and records the channel's handshake state at that moment, so a
    // request that never arrives server-side is diagnosable from
    // client-main.log alone. Send failures are logged (and swallowed) instead
    // of propagating through the hotkey dispatcher — the optimistic state
    // stays until the server's next update settles it.
    private void SendRequest<T>(string kind, T message)
    {
        LogOnce($"net:{kind}",
            $"Open Hand: sending {kind} request (channel connected: {channel.Connected}).");
        try
        {
            channel.SendPacket(message);
        }
        catch (Exception exception)
        {
            capi.Logger.Error(
                $"Open Hand: {kind} request could not be sent: {exception.Message}");
        }
    }

    private void OnOffhandUpdate(OpenHandOffhandUpdate update)
    {
        IClientPlayer? localPlayer = capi.World?.Player;
        if (localPlayer is null)
        {
            pendingOffhandUpdates.Add(update);
            return;
        }

        ApplyOffhandUpdate(localPlayer, update);
    }

    private void ApplyOffhandUpdate(IClientPlayer localPlayer, OpenHandOffhandUpdate update)
    {
        if (update.PlayerUid != localPlayer.PlayerUID)
        {
            return;
        }

        nextOffhandRevision = Math.Max(nextOffhandRevision, update.Revision);
        OpenHandRuntime.SetOffhandEmpty(localPlayer, update.IsEmpty, update.Revision);

        // A persisted substitution restored at join must not outlive the
        // feature switch: if the switch is off, drop the restored state
        // at once — the request re-persists the cleared state server-side.
        if (update.IsEmpty && !isEmptyOffhandEnabled())
        {
            RequestOffhandEmpty(localPlayer, empty: false);
        }
    }

    private void OnLeftWorld()
    {
        OpenHandRuntime.ClearAll();
        nextRevision = 0;
        nextOffhandRevision = 0;
        refreshSent = false;
        pendingSelectionUpdates.Clear();
        pendingOffhandUpdates.Clear();
        loggedNotes.Clear();
    }

    // Per-world-session notes so a "the hotkey did nothing" report is
    // diagnosable from client-main.log without a debugger: the first fire of
    // each hotkey (proves the dispatcher reaches the handler and names the
    // live binding) and the first decline per reason. Carry declines re-log
    // per carry episode.
    private readonly HashSet<string> loggedNotes = [];

    private void LogOnce(string noteKey, string message)
    {
        if (loggedNotes.Add(noteKey))
        {
            capi.Logger.Notification(message);
        }
    }

    private void LogFireOnce(string hotKeyCode)
    {
        capi.Input.HotKeys.TryGetValue(hotKeyCode, out HotKey? hotkey);
        LogOnce($"{hotKeyCode}:fired", $"Open Hand: {hotKeyCode} handler fired (binding: {DescribeBinding(hotkey)}).");
    }

    // Deliberately avoids KeyCombination.ToString(): that routes through the
    // game's GlKeyNames/OpenTK lookup, which a diagnostic inside a hotkey
    // handler must never depend on. Vanilla names mouse bindings by
    // EnumMouseButton (Left=0 … Button8=7), so MouseStart+4 is Button5.
    private static string DescribeBinding(HotKey? hotkey)
    {
        if (hotkey is null)
        {
            return "unbound";
        }

        KeyCombination mapping = hotkey.CurrentMapping;
        string key = OpenHandHotkeyBinding.IsMouseButton(mapping.KeyCode)
            ? $"mouse button {mapping.KeyCode - KeyCombination.MouseStart + 1}"
            : $"keycode {mapping.KeyCode}";
        string modifiers =
            (mapping.Ctrl ? "Ctrl+" : "") +
            (mapping.Alt ? "Alt+" : "") +
            (mapping.Shift ? "Shift+" : "");
        return modifiers + key;
    }

    private static string GrabDeclineMessage(string hotKeyCode) =>
        $"Open Hand: {hotKeyCode} is bound to a mouse button and only acts while the mouse is grabbed in-world; the click passed through to the game.";

    // The carry lock's shared gate: declines while CarryOn reports a block
    // carried in the hands, logged once per carry episode so the lock is
    // visible in the log instead of silently eating hotkeys.
    private bool CarryLockDeclines(IClientPlayer player, string hotKeyCode)
    {
        if (!CarryOnInterop.IsCarryingHands(player.Entity))
        {
            // The next carry episode gets its own decline note.
            loggedNotes.Remove($"{hotKeyCode}:carry");
            return false;
        }

        LogOnce($"{hotKeyCode}:carry",
            $"Open Hand: {hotKeyCode} declined while carrying a block in the hands (CarryOn) — the selection and the offhand toggle stay locked until the block is placed or dropped; /openhand status reports the carry state.");
        return true;
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
        // Detach the stale closures; report false so a still-registered
        // hotkey (mouse bindings survive in the game's client settings)
        // never consumes input on the way out.
        capi.Input.SetHotKeyHandler(SelectHotKeyCode, _ => false);
        capi.Input.SetHotKeyHandler(IndicatorHotKeyCode, _ => false);
        capi.Input.SetHotKeyHandler(OffhandHotKeyCode, _ => false);
        OpenHandRuntime.ClearAll();
    }
}
