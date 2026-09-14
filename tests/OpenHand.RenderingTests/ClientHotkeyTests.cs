using System.Reflection;
using OpenHand;
using OpenHand.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

internal static class ClientHotkeyTests
{
    internal static void Run()
    {
        IInputAPI input = DispatchProxy.Create<IInputAPI, RecordingProxy>();
        var hotkeyDict = new Vintagestory.API.Datastructures.OrderedDictionary<string, HotKey>();
        ((RecordingProxy)input).Handler = (method, args) =>
        {
            // The controller constructor re-applies saved-binding priority at
            // startup; serve the live dictionary it edits.
            if (method.Name == "get_HotKeys") return hotkeyDict;
            string code = (string)args[0]!;
            if (method.Name == "RegisterHotKey")
            {
                hotkeyDict.Add(code, new HotKey
                {
                    Code = code,
                    KeyCombinationType = (HotkeyType)args[3]!,
                    CurrentMapping = new KeyCombination
                    {
                        KeyCode = (int)(GlKeys)args[2]!,
                        Alt = (bool)args[4]!,
                        Ctrl = (bool)args[5]!,
                        Shift = (bool)args[6]!
                    }
                });
                return null;
            }
            if (method.Name == "SetHotKeyHandler")
            {
                hotkeyDict[code].Handler = (ActionConsumable<KeyCombination>)args[1]!;
                return null;
            }
            throw new InvalidOperationException($"Unexpected input call: {method.Name}");
        };
        List<object> sent = [];
        Dictionary<string, Delegate> messageHandlers = [];
        IClientNetworkChannel channel = DispatchProxy.Create<IClientNetworkChannel, RecordingProxy>();
        ((RecordingProxy)channel).Handler = (method, args) =>
        {
            if (method.Name is "RegisterMessageType" or "SetMessageHandler")
            {
                if (method.Name == "SetMessageHandler")
                {
                    Delegate handler = (Delegate)args[0]!;
                    messageHandlers[handler.GetType().GetMethod("Invoke")!.GetParameters()[0].ParameterType.Name] = handler;
                }
                return channel;
            }
            if (method.Name == "SendPacket")
            {
                sent.Add(args[0]!);
                return null;
            }
            if (method.Name == "get_Connected") return true; // the send telemetry reads the handshake state
            throw new InvalidOperationException($"Unexpected network call: {method.Name}");
        };
        IClientNetworkAPI network = DispatchProxy.Create<IClientNetworkAPI, RecordingProxy>();
        ((RecordingProxy)network).Handler = (method, _) => method.Name == "RegisterChannel"
            ? channel : throw new InvalidOperationException($"Unexpected network API call: {method.Name}");
        int subscriptions = 0;
        int tickListeners = 0;
        Action<float>? gameTick = null;
        IClientEventAPI events = DispatchProxy.Create<IClientEventAPI, RecordingProxy>();
        ((RecordingProxy)events).Handler = (method, args) =>
        {
            if (method.Name.StartsWith("add_", StringComparison.Ordinal)) subscriptions++;
            else if (method.Name.StartsWith("remove_", StringComparison.Ordinal)) subscriptions--;
            else if (method.Name == "RegisterGameTickListener")
            {
                tickListeners++; // the substituted-slot sweep
                gameTick = (Action<float>)args[0]!;
            }
            else if (method.Name == "UnregisterGameTickListener") tickListeners--;
            else throw new InvalidOperationException($"Unexpected event call: {method.Name}");
            return (long)tickListeners; // the tick registration's long; ignored elsewhere
        };
        ICoreClientAPI api = DispatchProxy.Create<ICoreClientAPI, RecordingProxy>();
        IClientPlayer player = TestFakes.MakeClientPlayer("uid-client-hotkey", api, new TestFakes.TestInventory(12));
        TestFakes.SeedControls(player.Entity);
        // The local player comes into existence only after the world finishes
        // loading; the world proxy models that so tests can deliver join-time
        // updates while it is still null.
        IClientPlayer? localPlayer = player;
        IClientWorldAccessor world = TestFakes.Proxy<IClientWorldAccessor>((method, _) =>
            method.Name == "get_Player" ? localPlayer : TestFakes.Default(method));
        ILogger logger = TestFakes.MakeRecordingLogger(out List<string> log);
        ((RecordingProxy)api).Handler = (method, _) => method.Name switch
        {
            "get_Input" => input,
            "get_Network" => network,
            "get_Event" => events,
            "get_World" => world,
            "get_Logger" => logger,
            // OpenHandRuntime partitions state by the entity API's side.
            "get_Side" => EnumAppSide.Client,
            // Visibility must not read or change selection or send packets.
            _ => throw new InvalidOperationException($"Unexpected client call: {method.Name}")
        };
        int opens = 0;
        Action openSettings = () => opens++;
        Type controllerType = typeof(OpenHandModSystem).Assembly.GetType(
            "OpenHand.Client.OpenHandClientController", throwOnError: true)!;
        bool mainHandEnabled = true;
        bool offhandEnabled = true;
        using IDisposable controller = (IDisposable)Activator.CreateInstance(
            controllerType, api, openSettings, (Func<bool>)(() => true), (Func<bool>)(() => false),
            (Func<bool>)(() => mainHandEnabled), (Func<bool>)(() => offhandEnabled))!;
        HotKey indicator = hotkeyDict["openhand.indicator"];
        HotKey select = hotkeyDict["openhand.select"];
        KeyEvent ctrlTilde = new() { KeyCode = (int)GlKeys.Tilde, CtrlPressed = true };
        KeyEvent tilde = new() { KeyCode = (int)GlKeys.Tilde };
        if (!indicator.DidPress(ctrlTilde, null!, null!, true) ||
            indicator.DidPress(tilde, null!, null!, true) ||
            indicator.DidPress(ctrlTilde, null!, null!, false) ||
            select.DidPress(ctrlTilde, null!, null!, true) ||
            !select.DidPress(tilde, null!, null!, true))
            throw new InvalidOperationException("Hotkey modifiers or character-control gating differ");
        if (!indicator.Handler(indicator.CurrentMapping) || opens != 1)
            throw new InvalidOperationException("Settings hotkey did not consume and open");
        indicator.Handler(indicator.CurrentMapping);
        if (opens != 2) throw new InvalidOperationException("Second press did not reach the dialog toggle");

        // Select entry: consumes, optimistically applies, and sends exactly one
        // request; the fire is logged once with the live binding.
        HotKey offhand = hotkeyDict["openhand.offhand"];
        TestFakes.Require(select.Handler(select.CurrentMapping), "the select hotkey consumes its press");
        TestFakes.Require(sent.Count == 1 && sent[0] is OpenHandSelectionRequest { Selected: true, Revision: 1 },
            "select entry sends one selection request");
        TestFakes.Require(OpenHandRuntime.IsSelected(player), "select entry applies the selection optimistically");
        TestFakes.Require(log.Count(message => message.Contains("openhand.select handler fired")) == 1,
            "the first select fire is logged exactly once");

        // Main-hand feature switch gates every entry path at SelectOpenHand.
        // A settled physical-slot state refuses a new selection without
        // sending a request or mutating the optimistic state.
        OpenHandRuntime.Set(player, selected: false, rememberedHotbarSlot: 0, revision: 2);
        mainHandEnabled = false;
        TestFakes.Require(!select.Handler(select.CurrentMapping), "main-hand feature switch declines entry");
        TestFakes.Require(sent.Count == 1 && !OpenHandRuntime.IsSelected(player),
            "a disabled main-hand feature sends no entry request");
        mainHandEnabled = true;

        // While carrying, entry is declined (both directions), mutates nothing,
        // and the decline is logged once per carry episode — never silently.
        TestFakes.InjectCarry(carrying: true);
        try
        {
            TestFakes.Require(!select.Handler(select.CurrentMapping), "entry while carrying declines");
            TestFakes.Require(!select.Handler(select.CurrentMapping), "a second carry-locked press still declines");
            TestFakes.Require(sent.Count == 1, "carry-locked presses send nothing");
            TestFakes.Require(!OpenHandRuntime.IsSelected(player) &&
                OpenHandRuntime.Get(player).Revision == 2,
                "carry-locked presses leave the selection state untouched");
            TestFakes.Require(log.Count(message => message.Contains("declined while carrying")) == 1,
                "the carry decline is logged once per episode");
        }
        finally
        {
            TestFakes.ResetCarryInterop();
        }

        // Released: the offhand toggle acts again (its own request packet).
        TestFakes.Require(offhand.Handler(offhand.CurrentMapping), "the offhand toggle acts when not carrying");
        TestFakes.Require(sent.Count == 2 && sent[1] is OpenHandOffhandRequest { IsEmpty: true, Revision: 1 },
            "the offhand toggle sends its request after the carry clears");

        // A persisted substitution restored at join must not outlive the
        // feature switch: receiving the restored update while the switch is
        // off drops it at once with a fresh (persisted) request.
        offhandEnabled = false;
        messageHandlers[nameof(OpenHandOffhandUpdate)].DynamicInvoke(
            new OpenHandOffhandUpdate { PlayerUid = "uid-client-hotkey", IsEmpty = true, Revision = 5 });
        TestFakes.Require(sent.Count == 3 &&
            sent[2] is OpenHandOffhandRequest { IsEmpty: false, Revision: 6 },
            "a restored offhand state is dropped when the feature switch is off");

        // The server's join replay arrives while the world is still loading —
        // before the local player exists (decompiled: PlayerJoin fires between
        // LevelInitialize and LevelFinalize). It must be buffered and applied
        // on the first tick after the player exists, never dropped: a dropped
        // replay left a restored selection invisible and the next toggle
        // bounced off the server's restored revision as stale.
        offhandEnabled = true;
        localPlayer = null;
        messageHandlers[nameof(OpenHandSelectionUpdate)].DynamicInvoke(
            new OpenHandSelectionUpdate { PlayerUid = "uid-client-hotkey", Selected = true, RememberedHotbarSlot = 5, Revision = 3 });
        messageHandlers[nameof(OpenHandOffhandUpdate)].DynamicInvoke(
            new OpenHandOffhandUpdate { PlayerUid = "uid-client-hotkey", IsEmpty = true, Revision = 7 });
        TestFakes.Require(!OpenHandRuntime.IsSelected(player) &&
            OpenHandRuntime.Get(player).Revision == 2 &&
            !OpenHandRuntime.IsOffhandEmpty(player) &&
            OpenHandRuntime.GetOffhandState(player).Revision == 6 && sent.Count == 3,
            "join-time updates arriving before the player exists change nothing yet");
        localPlayer = player;
        gameTick!.Invoke(0.5f);
        TestFakes.Require(OpenHandRuntime.IsSelected(player) &&
            OpenHandRuntime.Get(player).RememberedHotbarSlot == 5 &&
            OpenHandRuntime.Get(player).Revision == 3,
            "a buffered selection replay is applied once the player exists");
        TestFakes.Require(OpenHandRuntime.IsOffhandEmpty(player) &&
            OpenHandRuntime.GetOffhandState(player).Revision == 7,
            "a buffered offhand replay is applied once the player exists");
        TestFakes.Require(sent.Count == 5 &&
            sent[3] is OpenHandSelectionRequest { Revision: 0 } &&
            sent[4] is OpenHandOffhandRequest { Revision: 0 },
            "the ready refresh asks the server for the current state with revision 0");

        // With the restored revisions learned (3 / 7), the next toggles must
        // not be stale: the counters were bumped by the replay.
        TestFakes.Require(offhand.Handler(offhand.CurrentMapping) &&
            sent[5] is OpenHandOffhandRequest { IsEmpty: false, Revision: 8 },
            "a toggle after the replay is not stale");

        controller.Dispose();
        indicator.Handler(indicator.CurrentMapping);
        if (opens != 2 || subscriptions != 0 || tickListeners != 0)
            throw new InvalidOperationException("Controller cleanup left an active callback, subscription, or tick listener");
        Console.WriteLine("Passed settings hotkey registration, modifiers, callback isolation, and disposal checks.");
    }
}
