using HarmonyLib;
using OpenHand.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

internal static class HotkeyBindingTests
{
    internal static void Run()
    {
        // Mouse-button range: vanilla encodes buttons as
        // KeyCombination.MouseStart + EnumMouseButton (Left=0 … Button8=7).
        TestFakes.Require(OpenHandHotkeyBinding.IsMouseButton(KeyCombination.MouseStart), "mouse left is a mouse button");
        TestFakes.Require(OpenHandHotkeyBinding.IsMouseButton(KeyCombination.MouseStart + 7), "button 8 is a mouse button");
        TestFakes.Require(!OpenHandHotkeyBinding.IsMouseButton(KeyCombination.MouseStart + 8), "button 9 is out of range");
        TestFakes.Require(!OpenHandHotkeyBinding.IsMouseButton(KeyCombination.MouseStart - 1), "below the mouse range is not a mouse button");
        TestFakes.Require(!OpenHandHotkeyBinding.IsMouseButton((int)GlKeys.Tilde), "keyboard keys are not mouse buttons");

        // ApplyPriority edits the live dispatch order: mouse-bound codes move
        // to the FRONT (vanilla registers its mouse consumers first and
        // TriggerHotKey stops at the first handler returning true), keyboard
        // codes return to the end (the normal mod registration position).
        // capi.Input.HotKeys IS hotkeyManager.HotKeys — an
        // OrderedDictionary<string, HotKey> (decompiled InputAPI:30).
        Vintagestory.API.Datastructures.OrderedDictionary<string, HotKey> hotkeys = [];
        hotkeys["primarymouse"] = new HotKey { Code = "primarymouse", CurrentMapping = new KeyCombination { KeyCode = KeyCombination.MouseStart } };
        hotkeys["secondarymouse"] = new HotKey { Code = "secondarymouse", CurrentMapping = new KeyCombination { KeyCode = KeyCombination.MouseStart + 1 } };
        hotkeys["pickblock"] = new HotKey { Code = "pickblock", CurrentMapping = new KeyCombination { KeyCode = KeyCombination.MouseStart + 2 } };
        hotkeys["openhand.select"] = new HotKey { Code = "openhand.select", CurrentMapping = new KeyCombination { KeyCode = KeyCombination.MouseStart + 3 } };
        hotkeys["openhand.indicator"] = new HotKey { Code = "openhand.indicator", CurrentMapping = new KeyCombination { KeyCode = (int)GlKeys.Tilde } };
        IInputAPI input = TestFakes.Proxy<IInputAPI>((method, _) =>
            method.Name == "get_HotKeys" ? hotkeys : TestFakes.Default(method));
        ICoreClientAPI api = TestFakes.Proxy<ICoreClientAPI>((method, _) =>
            method.Name == "get_Input" ? input : TestFakes.Default(method));

        OpenHandHotkeyBinding.ApplyPriority(api, "openhand.select");
        TestFakes.Require(hotkeys.IndexOfKey("openhand.select") == 0, "a mouse-bound hotkey moves to the front of the dispatch order");
        TestFakes.Require(hotkeys.IndexOfKey("primarymouse") > 0, "vanilla's mouse consumers fall behind it");

        OpenHandHotkeyBinding.ApplyPriority(api, "openhand.indicator");
        TestFakes.Require(hotkeys.IndexOfKey("openhand.indicator") == hotkeys.Count - 1,
            "a keyboard-bound hotkey moves to the end of the dispatch order");

        // Already-correct placements are no-ops and repeat calls stay stable.
        OpenHandHotkeyBinding.ApplyPriority(api, "openhand.select");
        OpenHandHotkeyBinding.ApplyPriority(api, "openhand.select");
        TestFakes.Require(hotkeys.IndexOfKey("openhand.select") == 0 &&
            hotkeys.IndexOfKey("openhand.indicator") == hotkeys.Count - 1,
            "repeat priority calls stay stable");

        // Persistence target sanity: the vanilla reflection path the menu's
        // remaps ride must exist on the installed game. The method itself is
        // deliberately never invoked — that would dirty the real
        // clientsettings.json of the machine running the tests.
        Type settings = typeof(ClientPlayer).Assembly.GetType("Vintagestory.Client.NoObf.ClientSettings", throwOnError: true)!;
        TestFakes.Require(AccessTools.Field(settings, "Inst") is not null, "ClientSettings.Inst resolves");
        TestFakes.Require(AccessTools.Method(settings, "SetKeyMapping", [typeof(string), typeof(KeyCombination)]) is not null,
            "ClientSettings.SetKeyMapping(string, KeyCombination) resolves");
        Console.WriteLine("Passed mouse-button ranges, dispatch-order priority, and persistence reflection-target checks.");
    }
}
