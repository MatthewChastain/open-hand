using System.Reflection;
using OpenHand;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

internal static class ClientHotkeyTests
{
    internal static void Run()
    {
        Dictionary<string, HotKey> hotkeys = new();
        IInputAPI input = DispatchProxy.Create<IInputAPI, RecordingProxy>();
        ((RecordingProxy)input).Handler = (method, args) =>
        {
            string code = (string)args[0]!;
            if (method.Name == "RegisterHotKey")
            {
                hotkeys.Add(code, new HotKey
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
                hotkeys[code].Handler = (ActionConsumable<KeyCombination>)args[1]!;
                return null;
            }
            throw new InvalidOperationException($"Unexpected input call: {method.Name}");
        };
        IClientNetworkChannel channel = DispatchProxy.Create<IClientNetworkChannel, RecordingProxy>();
        ((RecordingProxy)channel).Handler = (method, _) =>
            method.Name is "RegisterMessageType" or "SetMessageHandler"
                ? channel : throw new InvalidOperationException($"Unexpected network call: {method.Name}");
        IClientNetworkAPI network = DispatchProxy.Create<IClientNetworkAPI, RecordingProxy>();
        ((RecordingProxy)network).Handler = (method, _) => method.Name == "RegisterChannel"
            ? channel : throw new InvalidOperationException($"Unexpected network API call: {method.Name}");
        int subscriptions = 0;
        IClientEventAPI events = DispatchProxy.Create<IClientEventAPI, RecordingProxy>();
        ((RecordingProxy)events).Handler = (method, _) =>
        {
            if (method.Name.StartsWith("add_", StringComparison.Ordinal)) subscriptions++;
            else if (method.Name.StartsWith("remove_", StringComparison.Ordinal)) subscriptions--;
            else throw new InvalidOperationException($"Unexpected event call: {method.Name}");
            return null;
        };
        ICoreClientAPI api = DispatchProxy.Create<ICoreClientAPI, RecordingProxy>();
        ((RecordingProxy)api).Handler = (method, _) => method.Name switch
        {
            "get_Input" => input,
            "get_Network" => network,
            "get_Event" => events,
            // Visibility must not read or change selection or send packets.
            _ => throw new InvalidOperationException($"Unexpected client call: {method.Name}")
        };
        int opens = 0;
        Action openSettings = () => opens++;
        Type controllerType = typeof(OpenHandModSystem).Assembly.GetType(
            "OpenHand.Client.OpenHandClientController", throwOnError: true)!;
        using IDisposable controller = (IDisposable)Activator.CreateInstance(
            controllerType, api, openSettings, (Func<bool>)(() => true), (Func<bool>)(() => false))!;
        HotKey indicator = hotkeys["openhand.indicator"];
        HotKey select = hotkeys["openhand.select"];
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
        controller.Dispose();
        indicator.Handler(indicator.CurrentMapping);
        if (opens != 2 || subscriptions != 0)
            throw new InvalidOperationException("Controller cleanup left an active callback or subscription");
        Console.WriteLine("Passed settings hotkey registration, modifiers, callback isolation, and disposal checks.");
    }
}
