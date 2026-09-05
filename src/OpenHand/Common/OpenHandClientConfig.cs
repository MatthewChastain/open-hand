namespace OpenHand.Common;

/// <summary>
/// Client-only config for the Open Hand HUD, stored as <c>openhand.json</c>
/// in the mod config folder. It never affects selection state or server sync.
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
