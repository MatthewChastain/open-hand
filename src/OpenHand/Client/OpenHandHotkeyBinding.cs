using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;

namespace OpenHand.Client;

// Binding support for the rebindable Open Hand hotkeys. Two vanilla mechanics
// decide whether a remap actually works, both verified against decompiled
// 1.22.7 assemblies:
//
// 1. Persistence. HotkeyManager.RegisterHotKey applies a stored mapping from
//    ClientSettings.KeyMapping at registration, and vanilla's own controls
//    screen writes remaps through ClientSettings.Inst.SetKeyMapping
//    (GuiCompositeSettings.CompletedCapture), which marks clientsettings.json
//    dirty. Setting HotKey.CurrentMapping alone is session-only, so remaps
//    made in the Open Hand menu are persisted through the same
//    SetKeyMapping call — by reflection, because ClientSettings lives in
//    VintagestoryLib (Vintagestory.Client.NoObf), which mods do not
//    compile against. A renamed or missing target degrades to session-only
//    bindings (logged once), never a crash.
//
// 2. Ordering. HotkeyManager.TriggerHotKey walks HotKeys.ValuesOrdered and
//    stops at the first handler that returns true, and mouse clicks enter
//    that walk through HotkeyManager.OnMouseButton (KeyCode = button + 240).
//    Vanilla registers primarymouse/secondarymouse/middlemouse — whose
//    handlers route the click into the world/GUI (SystemHotkeys ->
//    ClientMain.UpdateMouseButtonState) and return true — plus pickblock on
//    the middle button, all before any mod hotkey exists. A mod hotkey bound
//    to a mouse button therefore never fires unless it is moved ahead of
//    them. capi.Input.HotKeys IS hotkeyManager.HotKeys (InputAPI), so the
//    reordering below edits the live dispatch order. While mouse-bound, the
//    hotkey sits at the front and its handler runs first; handlers must
//    return false whenever they decline so the click falls through to
//    vanilla's own consumers unchanged. Keyboard-bound hotkeys return to the
//    end (the normal mod registration position), because keyboard dispatch
//    reaches mod listeners through capi.Event.KeyDown first and front
//    placement would only disturb vanilla keyboard conflicts.
internal static class OpenHandHotkeyBinding
{
    private const string ClientSettingsTypeName = "Vintagestory.Client.NoObf.ClientSettings";

    private static FieldInfo? settingsInstanceField;
    private static MethodInfo? setKeyMappingMethod;
    private static bool persistenceResolved;
    private static bool loggedPersistenceFailure;

    /// <summary>
    /// Whether the keycode is one of the eight mouse buttons vanilla encodes
    /// as KeyCombination.MouseStart + EnumMouseButton (Left=0 … Button8=7).
    /// </summary>
    internal static bool IsMouseButton(int keyCode) =>
        keyCode >= KeyCombination.MouseStart && keyCode < KeyCombination.MouseStart + 8;

    /// <summary>
    /// Persists a remap through vanilla's own settings path so it survives
    /// restarts: registration reads it back from ClientSettings.KeyMapping.
    /// </summary>
    internal static void Persist(ICoreClientAPI capi, string hotkeyCode, KeyCombination mapping)
    {
        if (!ResolvePersistence() || settingsInstanceField?.GetValue(null) is not object instance)
        {
            LogPersistenceFailureOnce(capi, "ClientSettings.Inst is unavailable");
            return;
        }

        try
        {
            setKeyMappingMethod!.Invoke(instance, [hotkeyCode, mapping]);
        }
        catch (Exception exception)
        {
            LogPersistenceFailureOnce(capi, exception.Message);
        }
    }

    /// <summary>
    /// Places the hotkey where the vanilla dispatcher can actually reach it:
    /// front of the order while mouse-bound, end of the order (the normal mod
    /// registration position) while keyboard-bound.
    /// </summary>
    internal static void ApplyPriority(ICoreClientAPI capi, string hotkeyCode)
    {
        if (!capi.Input.HotKeys.TryGetValue(hotkeyCode, out HotKey? hotkey))
        {
            return;
        }

        bool mouseBound = IsMouseButton(hotkey.CurrentMapping.KeyCode);
        int index = capi.Input.HotKeys.IndexOfKey(hotkeyCode);
        bool alreadyCorrect = mouseBound ? index == 0 : index == capi.Input.HotKeys.Count - 1;
        if (alreadyCorrect)
        {
            return;
        }

        capi.Input.HotKeys.Remove(hotkeyCode);
        if (mouseBound)
        {
            capi.Input.HotKeys.Insert(0, hotkeyCode, hotkey);
        }
        else
        {
            capi.Input.HotKeys.Add(hotkeyCode, hotkey);
        }
    }

    private static bool ResolvePersistence()
    {
        if (persistenceResolved)
        {
            return setKeyMappingMethod is not null && settingsInstanceField is not null;
        }

        persistenceResolved = true;
        Type? settingsType = AccessTools.TypeByName(ClientSettingsTypeName);
        settingsInstanceField = settingsType is null
            ? null
            : AccessTools.Field(settingsType, "Inst");
        setKeyMappingMethod = settingsType is null
            ? null
            : AccessTools.Method(
                settingsType,
                "SetKeyMapping",
                [typeof(string), typeof(KeyCombination)]);
        return setKeyMappingMethod is not null && settingsInstanceField is not null;
    }

    private static void LogPersistenceFailureOnce(ICoreClientAPI capi, string reason)
    {
        if (loggedPersistenceFailure)
        {
            return;
        }

        loggedPersistenceFailure = true;
        capi.Logger.Warning(
            "Open Hand could not persist this keybind through the game's client settings ({0}); it stays active for this session only.",
            reason);
    }
}
