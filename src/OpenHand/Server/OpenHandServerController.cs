using OpenHand.Client;
using OpenHand.Common;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace OpenHand.Server;

internal sealed class OpenHandServerController : IDisposable
{
    private const string ChannelName = "openhand";
    private const string PersistKey = "openhand";
    private const string SelectionKey = "Selection";
    private const string OffhandKey = "Offhand";
    private readonly IServerNetworkChannel channel;
    private readonly ICoreServerAPI sapi;
    private readonly HashSet<string> loggedCarryRejections = [];
    private readonly HashSet<string> loggedRequests = [];
    private long sweepListenerId;
    private bool disposed;

    public OpenHandServerController(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        channel = sapi.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<OpenHandSelectionRequest>()
            .RegisterMessageType<OpenHandSelectionUpdate>()
            .RegisterMessageType<OpenHandOffhandRequest>()
            .RegisterMessageType<OpenHandOffhandUpdate>()
            .SetMessageHandler<OpenHandSelectionRequest>(OnSelectionRequest)
            .SetMessageHandler<OpenHandOffhandRequest>(OnOffhandRequest);

        sapi.Event.PlayerJoin += OnPlayerJoin;
        sapi.Event.PlayerLeave += OnPlayerLeave;

        // Server-side half of the substituted-slot sweep: CarryOn's server
        // place-down leaves the placed block's stack in the active hand slot
        // and its pick-up strands a LockedItemSlot wrapper in the mod-owned
        // inventory (see OpenHandRuntime.Reclaim); any other foreign deposit
        // — a mod handing an item out through the substituted slot — is
        // delivered to that player's real inventory instead of being deleted.
        OpenHandRuntime.CarryDetector = static player => CarryOnInterop.IsCarryingHands(player.Entity);
        sweepListenerId = sapi.Event.RegisterGameTickListener(
            OnGameTick, 0, 0);
    }

    private void OnGameTick(float deltaTime)
    {
        OpenHandRuntime.SweepServerSubstitutedSlots();
    }

    private void OnSelectionRequest(IServerPlayer player, OpenHandSelectionRequest request)
    {
        OpenHandSelectionState current = OpenHandRuntime.Get(player);
        if (request.Revision <= current.Revision)
        {
            LogRequest(player,
                $"received a selection request at revision {request.Revision} while holding revision {current.Revision} — stale, re-sending the current state.");
            Send(player, current);
            return;
        }

        OpenHandSelectionState state = OpenHandRuntime.Set(
            player,
            request.Selected,
            request.RememberedHotbarSlot,
            request.Revision);
        PersistSelection(player, state);
        LogRequest(player,
            $"applied a selection request at revision {request.Revision} (was {current.Revision}) and persisted it.");
        Send(player, state);
        channel.BroadcastPacket(ToUpdate(player.PlayerUID, state), player);
        BroadcastHeldItems(player);
    }

    private void OnOffhandRequest(IServerPlayer player, OpenHandOffhandRequest request)
    {
        OpenHandOffhandState current = OpenHandRuntime.GetOffhandState(player);
        if (request.Revision <= current.Revision)
        {
            LogRequest(player,
                $"received an empty-offhand request at revision {request.Revision} while holding revision {current.Revision} — stale, re-sending the current state.");
            SendOffhand(player, current);
            return;
        }

        // Mirror the client's carry lock: the hands-carry slot is the offhand,
        // so ACTIVATING the substitution while that player is carrying is
        // rejected — re-sending the current state settles the optimistic
        // client back to the truth. Dropping the substitution stays allowed
        // for both players and the feature switch: DisableEmptyOffhand rides
        // this same request and must always win. Rejections log once per
        // player so a locked toggle is diagnosable from server-main.log.
        if (request.IsEmpty)
        {
            if (CarryOnInterop.IsCarryingHands(player.Entity))
            {
                if (loggedCarryRejections.Add(player.PlayerUID))
                {
                    sapi.Logger.Notification(
                        $"Open Hand: rejected an empty-offhand activation from {player.PlayerName} while they are carrying a block in the hands (CarryOn).");
                }

                SendOffhand(player, current);
                return;
            }

            loggedCarryRejections.Remove(player.PlayerUID);
        }

        OpenHandOffhandState state = OpenHandRuntime.SetOffhandEmpty(player, request.IsEmpty, request.Revision);
        PersistOffhand(player, state);
        LogRequest(player,
            $"applied an empty-offhand request (empty={request.IsEmpty}) at revision {request.Revision} (was {current.Revision}) and persisted it.");
        SendOffhand(player, state);
        channel.BroadcastPacket(new OpenHandOffhandUpdate
        {
            PlayerUid = player.PlayerUID,
            IsEmpty = state.IsEmpty,
            Revision = state.Revision
        }, player);
        BroadcastHeldItems(player);
    }

    // What makes the substitution visible to OTHER players. Their clients
    // never hold this player's Open Hand state — the client update handlers
    // deliberately apply only the local player's — so a remote observer
    // renders whatever hand stacks the server last replicated to them
    // (ServerMain.BroadcastHotbarSlot -> Packet_SelectedHotbarSlot ->
    // GeneralPacketHandler.HandleSelectedHotbarSlot, decompiled 1.22.7).
    // That replication reads ActiveHotbarSlot / Entity.LeftHandItemSlot —
    // both substituted getters on this side — so it already sends the empty
    // hand correctly; the problem was that nothing triggered it. Vanilla
    // re-broadcasts only on a real slot change, an item flip, join, or a
    // dirty slot that IS the active slot, and an Open Hand toggle is none of
    // those: it deliberately leaves ActiveHotbarSlotNumber untouched, and
    // while substituted the active slot is the mod-owned dummy, which lives
    // in no tracked inventory and is never dirty. Other players therefore
    // kept seeing the real item for the whole selection. Pushing the
    // replication here is the fix, and it is the engine's own documented
    // call for exactly this ("Resends the hotbar slot contents to all other
    // clients to make sure they render the correct held item"); it skips the
    // owner, whose own client is already correct through its own patches.
    private static void BroadcastHeldItems(IServerPlayer player) =>
        player.InventoryManager?.BroadcastHotbarSlot();

    private void SendOffhand(IServerPlayer player, OpenHandOffhandState state) =>
        channel.SendPacket(new OpenHandOffhandUpdate
        {
            PlayerUid = player.PlayerUID,
            IsEmpty = state.IsEmpty,
            Revision = state.Revision
        }, player);

    // Write-through persistence: every authoritative mutation is saved into
    // the player's entity attributes (the same mechanism CarryOn uses for
    // carries), so both toggles survive relogs and server restarts and are
    // restored at join. Player.Entity can be transiently null in the same
    // despawn/disconnect window Key() guards against in OpenHandRuntime (a
    // queued packet can still be dispatched while the sender's entity is
    // being torn down) — these handlers run from network message callbacks,
    // not a tick loop, so a miss here is rare, but it must degrade to
    // "this mutation is not persisted" rather than crash the handler.
    private static ITreeAttribute? PersistRoot(IServerPlayer player)
    {
        if (player.Entity is not { } entity)
        {
            return null;
        }

        ITreeAttribute? root = entity.WatchedAttributes.GetTreeAttribute(PersistKey);
        if (root is null)
        {
            root = new TreeAttribute();
            entity.WatchedAttributes[PersistKey] = root;
        }

        return root;
    }

    private void PersistSelection(IServerPlayer player, OpenHandSelectionState state)
    {
        if (PersistRoot(player) is not { } root)
        {
            LogPersistenceSkipped(player, "selection");
            return;
        }

        root[SelectionKey] = new TreeAttribute
        {
            ["Selected"] = new BoolAttribute(state.IsSelected),
            ["RememberedSlot"] = new IntAttribute(state.RememberedHotbarSlot),
            ["Revision"] = new IntAttribute(state.Revision)
        };
        player.Entity!.WatchedAttributes.MarkPathDirty(PersistKey);
    }

    private void PersistOffhand(IServerPlayer player, OpenHandOffhandState state)
    {
        if (PersistRoot(player) is not { } root)
        {
            LogPersistenceSkipped(player, "empty-offhand");
            return;
        }

        root[OffhandKey] = new TreeAttribute
        {
            ["IsEmpty"] = new BoolAttribute(state.IsEmpty),
            ["Revision"] = new IntAttribute(state.Revision)
        };
        player.Entity!.WatchedAttributes.MarkPathDirty(PersistKey);
    }

    private void LogPersistenceSkipped(IServerPlayer player, string what)
    {
        sapi.Logger.Warning(
            "Open Hand: could not persist the {0} change for {1} — their entity was unavailable (despawning or disconnecting). The change is still applied and broadcast for this session.",
            what, player.PlayerName);
    }

    private static (OpenHandSelectionState? Selection, OpenHandOffhandState? Offhand) LoadPersisted(IServerPlayer player)
    {
        if (player.Entity is not { } entity)
        {
            return (null, null);
        }

        ITreeAttribute? root = entity.WatchedAttributes.GetTreeAttribute(PersistKey);
        if (root is null)
        {
            return (null, null);
        }

        OpenHandSelectionState? selection = null;
        OpenHandOffhandState? offhand = null;
        if (root.GetTreeAttribute(SelectionKey) is ITreeAttribute savedSelection)
        {
            selection = new OpenHandSelectionState(
                savedSelection.GetBool("Selected"),
                savedSelection.GetInt("RememberedSlot"),
                savedSelection.GetInt("Revision"));
        }

        if (root.GetTreeAttribute(OffhandKey) is ITreeAttribute savedOffhand)
        {
            offhand = new OpenHandOffhandState(
                savedOffhand.GetBool("IsEmpty"),
                savedOffhand.GetInt("Revision"));
        }

        return (selection, offhand);
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        // A join re-seeds the server's partition from the persisted truth and
        // nothing else: the runtime is process-wide, so a same-process earlier
        // session (single-player server restarts) can leave stale revisions
        // behind that would reject the rejoined client's requests as stale.
        // The restored state is re-applied server-side, the snapshot replay
        // below hands the joining client its own state (bumping its revision
        // counter so its next request is not rejected as stale), and the
        // broadcasts re-apply the state on clients that dropped it at leave.
        OpenHandRuntime.Clear(player);
        (OpenHandSelectionState? selection, OpenHandOffhandState? offhand) = LoadPersisted(player);
        sapi.Logger.Notification(
            $"Open Hand: {player.PlayerName} joined — restored persisted: {DescribePersisted(selection is { Revision: > 0 } ? selection.Value : null, offhand is { Revision: > 0 } ? offhand.Value : null)}.");
        if (selection is { Revision: > 0 } restoredSelection)
        {
            OpenHandRuntime.Set(player, restoredSelection.IsSelected, restoredSelection.RememberedHotbarSlot, restoredSelection.Revision);
            channel.BroadcastPacket(ToUpdate(player.PlayerUID, restoredSelection), player);
        }

        if (offhand is { Revision: > 0 } restoredOffhand)
        {
            OpenHandRuntime.SetOffhandEmpty(player, restoredOffhand.IsEmpty, restoredOffhand.Revision);
            channel.BroadcastPacket(new OpenHandOffhandUpdate
            {
                PlayerUid = player.PlayerUID,
                IsEmpty = restoredOffhand.IsEmpty,
                Revision = restoredOffhand.Revision
            }, player);
        }

        foreach ((string uid, OpenHandSelectionState state) in OpenHandRuntime.Snapshot(EnumAppSide.Server))
        {
            channel.SendPacket(ToUpdate(uid, state), player);
        }

        foreach ((string uid, OpenHandOffhandState state) in OpenHandRuntime.OffhandSnapshot(EnumAppSide.Server))
        {
            channel.SendPacket(new OpenHandOffhandUpdate
            {
                PlayerUid = uid,
                IsEmpty = state.IsEmpty,
                Revision = state.Revision
            }, player);
        }
    }

    private static string DescribePersisted(OpenHandSelectionState? selection, OpenHandOffhandState? offhand)
    {
        var parts = new List<string>();
        if (selection is { Revision: > 0 })
        {
            parts.Add($"selection(selected={selection.Value.IsSelected} rev={selection.Value.Revision})");
        }
        if (offhand is { Revision: > 0 })
        {
            parts.Add($"offhand(empty={offhand.Value.IsEmpty} rev={offhand.Value.Revision})");
        }
        return parts.Count > 0 ? string.Join(" + ", parts) : "none";
    }

    // First-request telemetry per player per session: proves the client's
    // requests reach these handlers and records the revision outcome, so a
    // toggle that never persists is attributable from server-main.log alone
    // (send-path failure = no line at all; stale gate = the stale line).
    private void LogRequest(IServerPlayer player, string outcome)
    {
        if (loggedRequests.Add(player.PlayerUID))
        {
            sapi.Logger.Notification($"Open Hand: {player.PlayerName} {outcome}");
        }
    }

    private void OnPlayerLeave(IServerPlayer player)
    {
        OpenHandRuntime.Clear(player);
        channel.BroadcastPacket(new OpenHandSelectionUpdate
        {
            PlayerUid = player.PlayerUID,
            Selected = false,
            RememberedHotbarSlot = OpenHandSelectionState.PhysicalHotbarSlots - 1,
            Revision = int.MaxValue
        });
        channel.BroadcastPacket(new OpenHandOffhandUpdate
        {
            PlayerUid = player.PlayerUID,
            IsEmpty = false,
            Revision = int.MaxValue
        });
    }

    private void Send(IServerPlayer player, OpenHandSelectionState state) =>
        channel.SendPacket(ToUpdate(player.PlayerUID, state), player);

    private static OpenHandSelectionUpdate ToUpdate(string playerUid, OpenHandSelectionState state) =>
        new()
        {
            PlayerUid = playerUid,
            Selected = state.IsSelected,
            RememberedHotbarSlot = state.RememberedHotbarSlot,
            Revision = state.Revision
        };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        sapi.Event.UnregisterGameTickListener(sweepListenerId);
        sapi.Event.PlayerJoin -= OnPlayerJoin;
        sapi.Event.PlayerLeave -= OnPlayerLeave;
        OpenHandRuntime.ClearAll();
    }
}
