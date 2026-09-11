namespace OpenHand.Common;

/// <summary>
/// Client-only config for the Open Hand HUD and wheel entry, stored as
/// <c>openhand.json</c> in the mod config folder. Server sync is unchanged.
/// </summary>
public sealed class OpenHandClientConfig
{
    public const string ConfigFileName = "openhand.json";

    /// <summary>
    /// Where the indicator cell is drawn relative to the hotbar row:
    /// auto (a compatible external panel left of the hotbar), offhandGap
    /// (the classic but reserved vanilla position), left of the row, or
    /// right of the row.
    /// </summary>
    public string IconAnchor { get; set; } = "auto";

    /// <summary>Final pixel nudge applied after the anchor resolves.</summary>
    public int IconOffsetX { get; set; }

    /// <summary>Final pixel nudge applied after the anchor resolves.</summary>
    public int IconOffsetY { get; set; }

    /// <summary>
    /// Whether the client renders the Open Hand HUD panel, hand cell, and
    /// selection outline. When disabled, entry is hotkey-only; wheel exit
    /// and server synchronization are unchanged.
    /// </summary>
    public bool ShowIndicator { get; set; } = true;

    /// <summary>
    /// Center a recognized hotbar together with its visible automatic extension.
    /// Unsupported layouts remain uncentered. Enabled by default; fallback
    /// placement is automatic whenever the layout is not compatible.
    /// </summary>
    public bool CenterHotbar { get; set; } = true;

    /// <summary>
    /// Re-tap entry: pressing the number key of the already-active hotbar
    /// slot selects Open Hand instead of doing nothing. Off by default; the
    /// selection itself still runs through the server-validated path.
    /// Exiting is not part of this preference: while Open Hand is selected,
    /// pressing the remembered slot's number key always returns to it,
    /// because vanilla applies a same-value no-op that Open Hand must
    /// handle itself.
    /// </summary>
    public bool DoubleTapHotbarKey { get; set; }

    /// <summary>Parses the anchor setting; unknown values resolve to Auto.</summary>
    public static IconAnchorMode ParseIconAnchor(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "offhandgap" => IconAnchorMode.OffhandGap,
            "left" => IconAnchorMode.Left,
            "right" => IconAnchorMode.Right,
            _ => IconAnchorMode.Auto
        };
    }

    /// <summary>Whether the anchor setting is one of the documented values.</summary>
    public static bool IsKnownIconAnchor(string? value)
    {
        return value?.Trim().ToLowerInvariant() is "auto" or "offhandgap" or "left" or "right";
    }
}

public enum IconAnchorMode
{
    Auto,
    OffhandGap,
    Left,
    Right
}
