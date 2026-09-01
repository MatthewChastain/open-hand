using OpenHand.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OpenHand.Client;

internal sealed class OpenHandClientController : IDisposable
{
    private const string ChannelName = "openhand";
    private const string SelectHotKeyCode = "openhand.select";

    // Verified against Vintage Story 1.22.7 (InventoryPlayerHotbar):
    // slot 10 is the skill slot, slot 11 is the offhand, and the vanilla wheel
    // ring only spans hotbar slots 0-9 plus the skill slot while it is occupied.
    private const int SkillSlotIndex = 10;

    // HudHotbar.moveToHotbarSlot reads raw key index 3 to enter backpack mode.
    private const int BackpackModeRawKey = 3;

    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private int nextRevision;
    private bool disposed;

    public OpenHandClientController(ICoreClientAPI capi)
    {
        this.capi = capi;
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

        capi.Event.MouseWheelMove += OnMouseWheelMove;
        capi.Event.BeforeActiveSlotChanged += OnBeforeActiveSlotChanged;
        capi.Event.LeftWorld += OnLeftWorld;
    }

    private void SelectOpenHand(IClientPlayer player)
    {
        if (OpenHandRuntime.IsSelected(player))
        {
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
        if (player is null || !WheelWouldReachHotbar())
        {
            return;
        }

        bool skillOccupied = !IsHotbarSlotEmpty(player, SkillSlotIndex);
        OpenHandWheelRing.WheelDecision decision = OpenHandWheelRing.Resolve(
            OpenHandRuntime.IsSelected(player),
            player.InventoryManager.ActiveHotbarSlotNumber,
            skillOccupied,
            capi.Input.KeyboardKeyStateRaw[BackpackModeRawKey],
            args.delta);

        switch (decision.Action)
        {
            case OpenHandWheelRing.WheelAction.Enter:
                args.SetHandled();
                SelectOpenHand(player);
                break;
            case OpenHandWheelRing.WheelAction.ExitToSlot:
                args.SetHandled();
                RequestSelection(false, decision.Destination, player);
                player.InventoryManager.ActiveHotbarSlotNumber = decision.Destination;
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
        foreach (GuiDialog openedDialog in capi.Gui.OpenedGuis)
        {
            if (openedDialog.CaptureAllInputs())
            {
                return false;
            }
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

    private static bool IsHotbarSlotEmpty(IClientPlayer player, int slotIndex)
    {
        return player.InventoryManager.GetHotbarInventory()?[slotIndex] is not { Empty: false };
    }

    private EnumHandling OnBeforeActiveSlotChanged(ActiveSlotChangeEventArgs change)
    {
        IClientPlayer? player = capi.World?.Player;
        if (player is not null && OpenHandRuntime.IsSelected(player))
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
        capi.Event.MouseWheelMove -= OnMouseWheelMove;
        capi.Event.BeforeActiveSlotChanged -= OnBeforeActiveSlotChanged;
        capi.Event.LeftWorld -= OnLeftWorld;
        capi.Input.SetHotKeyHandler(SelectHotKeyCode, _ => true);
        OpenHandRuntime.ClearAll();
    }
}
