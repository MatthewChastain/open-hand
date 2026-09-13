using HarmonyLib;
using OpenHand.Client;
using OpenHand.Common;
using OpenHand.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

internal static class ServerControllerTests
{
    internal static void Run()
    {
        // Fakes for the whole server surface the controller touches. Packets
        // are recorded per channel call; event subscriptions are recorded as
        // delegates so the tests can raise join/leave by hand.
        Dictionary<string, Delegate> handlers = [];
        List<(object Message, object?[] Targets)> sent = [];
        List<(object Message, object?[] Except)> broadcasts = [];
        List<Delegate> joins = [];
        List<Delegate> leaves = [];
        List<object?> unregisteredTicks = [];
        IServerNetworkChannel channel = null!;
        channel = TestFakes.Proxy<IServerNetworkChannel>((method, args) =>
        {
            switch (method.Name)
            {
                case "RegisterMessageType":
                    return channel;
                case "SetMessageHandler":
                {
                    // Record the handler under every name the closed delegate
                    // type offers for the message: the generic argument, the
                    // delegate type name, and the Invoke parameter type.
                    Delegate handler = (Delegate)args[0]!;
                    Type delegateType = args[0]!.GetType();
                    handlers[delegateType.Name] = handler;
                    if (delegateType.IsGenericType)
                    {
                        handlers[delegateType.GetGenericArguments()[0].Name] = handler;
                    }
                    Type messageType = delegateType.GetMethod("Invoke")!.GetParameters()[1].ParameterType;
                    handlers[messageType.Name] = handler;
                    return channel;
                }
                case "SendPacket":
                    sent.Add((args[0]!, (object?[])args[1]!));
                    return null;
                case "BroadcastPacket":
                    broadcasts.Add((args[0]!, (object?[])args[1]!));
                    return null;
                default:
                    return TestFakes.Default(method);
            }
        });
        IServerEventAPI events = TestFakes.Proxy<IServerEventAPI>((method, args) =>
        {
            switch (method.Name)
            {
                case "add_PlayerJoin": joins.Add((Delegate)args[0]!); return null;
                case "remove_PlayerJoin": joins.Remove((Delegate)args[0]!); return null;
                case "add_PlayerLeave": leaves.Add((Delegate)args[0]!); return null;
                case "remove_PlayerLeave": leaves.Remove((Delegate)args[0]!); return null;
                case "RegisterGameTickListener": return 1L;
                case "UnregisterGameTickListener": unregisteredTicks.Add(args[0]); return null;
                default: return TestFakes.Default(method);
            }
        });
        IServerNetworkAPI network = TestFakes.Proxy<IServerNetworkAPI>((method, _) =>
            method.Name == "RegisterChannel" ? channel : TestFakes.Default(method));
        ICoreServerAPI sapi = TestFakes.Proxy<ICoreServerAPI>((method, _) => method.Name switch
        {
            "get_Network" => network,
            "get_Event" => events,
            _ => TestFakes.Default(method)
        });

        IServerPlayer player = TestFakes.MakeServerPlayer("uid-server-tests");
        ILogger logger = TestFakes.MakeRecordingLogger(out List<string> log);
        ((RecordingProxy)sapi).Handler = (method, _) => method.Name switch
        {
            "get_Network" => network,
            "get_Event" => events,
            "get_Logger" => logger,
            _ => TestFakes.Default(method)
        };
        OpenHandServerController controller = new(sapi);
        Delegate selectionHandler = handlers[nameof(OpenHandSelectionRequest)];
        Delegate offhandHandler = handlers[nameof(OpenHandOffhandRequest)];
        try
        {
            // A fresh revision applies and is broadcast to the others.
            selectionHandler.DynamicInvoke(player,
                new OpenHandSelectionRequest { Selected = true, RememberedHotbarSlot = 5, Revision = 1 });
            OpenHandSelectionState state = OpenHandRuntime.Get(player);
            TestFakes.Require(state.IsSelected && state.RememberedHotbarSlot == 5 && state.Revision == 1,
                "a fresh selection revision applies");
            TestFakes.Require(sent.Count == 1 &&
                sent[0].Targets.Length == 1 && ReferenceEquals(sent[0].Targets[0], player) &&
                SelectionUpdateIs(sent[0].Message, "uid-server-tests", selected: true, slot: 5, revision: 1),
                "the settled selection is sent back to the requester");
            TestFakes.Require(broadcasts.Count == 1 &&
                broadcasts[0].Except.Length == 1 && ReferenceEquals(broadcasts[0].Except[0], player) &&
                SelectionUpdateIs(broadcasts[0].Message, "uid-server-tests", selected: true, slot: 5, revision: 1),
                "the settled selection is broadcast to the others, requester excepted");

            // A stale or duplicate revision only re-sends the settled state.
            sent.Clear();
            broadcasts.Clear();
            selectionHandler.DynamicInvoke(player,
                new OpenHandSelectionRequest { Selected = false, RememberedHotbarSlot = 2, Revision = 1 });
            selectionHandler.DynamicInvoke(player,
                new OpenHandSelectionRequest { Selected = false, RememberedHotbarSlot = 2, Revision = 0 });
            state = OpenHandRuntime.Get(player);
            TestFakes.Require(state.IsSelected && state.RememberedHotbarSlot == 5,
                "stale selection revisions never regress state");
            TestFakes.Require(sent.Count == 2 && broadcasts.Count == 0,
                "stale revisions only re-send the settled state");

            // The offhand toggle rides the same revision scheme.
            offhandHandler.DynamicInvoke(player, new OpenHandOffhandRequest { IsEmpty = true, Revision = 1 });
            TestFakes.Require(OpenHandRuntime.IsOffhandEmpty(player), "a fresh offhand revision applies");
            TestFakes.Require(sent.Count == 3 && broadcasts.Count == 1 &&
                OffhandUpdateIs(broadcasts[0].Message, "uid-server-tests", isEmpty: true, revision: 1),
                "the settled offhand state is broadcast");

            // Carry lock: ACTIVATING while carrying is rejected and settles the
            // client back; dropping stays allowed for both players and the
            // feature switch, and activation resumes when not carrying.
            TestFakes.InjectCarry(carrying: true);
            try
            {
                sent.Clear();
                broadcasts.Clear();
                offhandHandler.DynamicInvoke(player, new OpenHandOffhandRequest { IsEmpty = true, Revision = 2 });
                TestFakes.Require(OpenHandRuntime.IsOffhandEmpty(player),
                    "activation while carrying leaves the substitution off");
                TestFakes.Require(sent.Count == 1 && broadcasts.Count == 0 &&
                    ReferenceEquals(sent[0].Targets[0], player) &&
                    OffhandUpdateIs(sent[0].Message, "uid-server-tests", isEmpty: true, revision: 1),
                    "the rejected activation settles back to the current state");

                TestFakes.Require(log.Count(message => message.Contains("rejected an empty-offhand activation")) == 1,
                    "the carry rejection is logged once per player");

                offhandHandler.DynamicInvoke(player, new OpenHandOffhandRequest { IsEmpty = true, Revision = 2 });
                TestFakes.Require(OpenHandRuntime.GetOffhandState(player).Revision == 1,
                    "a second carry-locked activation is still rejected (no mutation)");
                TestFakes.Require(log.Count(message => message.Contains("rejected an empty-offhand activation")) == 1,
                    "repeat rejections do not spam the log");

                offhandHandler.DynamicInvoke(player, new OpenHandOffhandRequest { IsEmpty = false, Revision = 2 });
                TestFakes.Require(!OpenHandRuntime.IsOffhandEmpty(player),
                    "dropping the substitution is allowed while carrying");
                TestFakes.Require(broadcasts.Count == 1, "the settled drop is broadcast");

                TestFakes.InjectCarry(carrying: false);
                offhandHandler.DynamicInvoke(player, new OpenHandOffhandRequest { IsEmpty = true, Revision = 3 });
                TestFakes.Require(OpenHandRuntime.IsOffhandEmpty(player), "activation resumes when not carrying");
            }
            finally
            {
                TestFakes.ResetCarryInterop();
            }

            // Player join replays both snapshots to the newcomer.
            IServerPlayer newcomer = TestFakes.MakeServerPlayer("uid-server-tests-newcomer");
            sent.Clear();
            broadcasts.Clear();
            joins.Single().DynamicInvoke(newcomer);
            TestFakes.Require(sent.Count == 2 &&
                ReferenceEquals(sent[0].Targets[0], newcomer) && ReferenceEquals(sent[1].Targets[0], newcomer) &&
                SelectionUpdateIs(sent[0].Message, "uid-server-tests", selected: true, slot: 5, revision: 1) &&
                OffhandUpdateIs(sent[1].Message, "uid-server-tests", isEmpty: true, revision: 3),
                "player join replays both state snapshots to the newcomer");

            // Player leave clears the player's state and broadcasts int.MaxValue
            // resets so every other client drops the leaver's substitutions.
            broadcasts.Clear();
            leaves.Single().DynamicInvoke(player);
            TestFakes.Require(!OpenHandRuntime.IsSelected(player) && !OpenHandRuntime.IsOffhandEmpty(player),
                "player leave clears the player's state");
            TestFakes.Require(broadcasts.Count == 2 &&
                SelectionUpdateIs(broadcasts[0].Message, "uid-server-tests", selected: false,
                    slot: OpenHandSelectionState.PhysicalHotbarSlots - 1, revision: int.MaxValue) &&
                OffhandUpdateIs(broadcasts[1].Message, "uid-server-tests", isEmpty: false, revision: int.MaxValue),
                "player leave broadcasts the reset revisions");

            // Persistence: mutations write through to the entity's watched
            // attributes, survive leave, and are restored at join.
            Vintagestory.API.Datastructures.ITreeAttribute? persistedRoot =
                player.Entity.WatchedAttributes.GetTreeAttribute("openhand");
            TestFakes.Require(persistedRoot?.GetTreeAttribute("Selection") is not null &&
                persistedRoot.GetTreeAttribute("Offhand") is not null,
                "mutations persist to the player's watched attributes");

            // A relog: same saved player data, fresh player object. The join
            // handler restores the state into the runtime, replays it to the
            // rejoining client, and re-broadcasts it to the others.
            IServerPlayer rejoining = TestFakes.MakeServerPlayer("uid-server-tests");
            rejoining.Entity.WatchedAttributes = player.Entity.WatchedAttributes;
            sent.Clear();
            broadcasts.Clear();
            joins.Single().DynamicInvoke(rejoining);
            TestFakes.Require(OpenHandRuntime.IsSelected(rejoining) &&
                OpenHandRuntime.Get(rejoining).RememberedHotbarSlot == 5 &&
                OpenHandRuntime.Get(rejoining).Revision == 1,
                "the selection persists across relogs");
            TestFakes.Require(OpenHandRuntime.IsOffhandEmpty(rejoining) &&
                OpenHandRuntime.GetOffhandState(rejoining).Revision == 3,
                "the offhand toggle persists across relogs");
            TestFakes.Require(sent.Count == 2 &&
                ReferenceEquals(sent[0].Targets[0], rejoining) &&
                ReferenceEquals(sent[1].Targets[0], rejoining) &&
                SelectionUpdateIs(sent[0].Message, "uid-server-tests", selected: true, slot: 5, revision: 1) &&
                OffhandUpdateIs(sent[1].Message, "uid-server-tests", isEmpty: true, revision: 3),
                "the join replay hands the rejoining client its persisted state");
            TestFakes.Require(broadcasts.Count == 2 &&
                ReferenceEquals(broadcasts[0].Except[0], rejoining) &&
                SelectionUpdateIs(broadcasts[0].Message, "uid-server-tests", selected: true, slot: 5, revision: 1) &&
                OffhandUpdateIs(broadcasts[1].Message, "uid-server-tests", isEmpty: true, revision: 3),
                "the restored state is re-broadcast to the other players");

            // Single-player pollution regression: the client and server share
            // one process, and the client applies toggle requests optimistically
            // BEFORE the server confirms. The client's optimistic write must
            // never satisfy the server's own stale-revision gate — it once did
            // exactly that (shared static state), the server rejected every
            // request as stale, and the persistence write never ran.
            IClientPlayer clientView = TestFakes.MakeClientPlayer("uid-sp-pollution",
                TestFakes.Proxy<ICoreClientAPI>((method, _) =>
                    method.Name == "get_Side" ? EnumAppSide.Client : TestFakes.Default(method)),
                new TestFakes.TestInventory(12));
            OpenHandRuntime.Set(clientView, selected: true, rememberedHotbarSlot: 3, revision: 1);
            IServerPlayer spServerView = TestFakes.MakeServerPlayer("uid-sp-pollution");
            selectionHandler.DynamicInvoke(spServerView,
                new OpenHandSelectionRequest { Selected = true, RememberedHotbarSlot = 3, Revision = 1 });
            TestFakes.Require(OpenHandRuntime.IsSelected(spServerView) &&
                OpenHandRuntime.Get(spServerView).Revision == 1,
                "the client's optimistic write never satisfies the server's stale gate");
            TestFakes.Require(spServerView.Entity.WatchedAttributes.GetTreeAttribute("openhand")?.GetTreeAttribute("Selection") is not null,
                "the request the client's optimism once ate is persisted");
        }
        finally
        {
            controller.Dispose();
        }
        TestFakes.Require(unregisteredTicks.Count == 1 && Equals(unregisteredTicks[0], 1L),
            "dispose unregisters the sweep tick listener");
        TestFakes.Require(joins.Count == 0 && leaves.Count == 0,
            "dispose unsubscribes the join and leave handlers");
        TestFakes.Require(!OpenHandRuntime.IsSelected(TestFakes.MakeServerPlayer("uid-server-tests-newcomer")),
            "dispose clears every player's state");
        Console.WriteLine("Passed server request validation, snapshot replay, leave resets, and carry-lock checks.");
    }

    private static bool SelectionUpdateIs(object message, string uid, bool selected, int slot, int revision) =>
        message is OpenHandSelectionUpdate update && update.PlayerUid == uid &&
        update.Selected == selected && update.RememberedHotbarSlot == slot && update.Revision == revision;

    private static bool OffhandUpdateIs(object message, string uid, bool isEmpty, int revision) =>
        message is OpenHandOffhandUpdate update && update.PlayerUid == uid &&
        update.IsEmpty == isEmpty && update.Revision == revision;
}
