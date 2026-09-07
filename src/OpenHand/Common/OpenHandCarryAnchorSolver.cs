namespace OpenHand.Common;

/// <summary>
/// Pure placement logic for CarryOn's carried-item HUD anchors. CarryOn
/// hardcodes its anchor geometry to an assumed vanilla hotbar (screen-centered,
/// 850 unscaled pixels wide), which collides with the Open Hand indicator cell
/// and ignores hotbar centering. This solver re-derives each anchor from the
/// bar Open Hand actually rendered: left anchors clear the indicator cell (or
/// the row edge when the cell sits inside the bar), right anchors follow the
/// real row's right edge, and both keep CarryOn's own icon rhythm (decompiled
/// CarryOn 1.14.3 HudCarriedRenderer: unscaled 32px icon, 16px gap, 48px pitch).
/// All inputs are final screen pixels.
/// </summary>
public static class OpenHandCarryAnchorSolver
{
    /// <summary>
    /// Computes the icon center X for one anchor. <paramref name="index" /> is
    /// the anchor's slot in CarryOn's table (L1..L3 / R1..R3 map to 0..2).
    /// <paramref name="backgroundLeft" /> / <paramref name="backgroundRight" />
    /// are the visible hotbar background edges (the Open Hand extension
    /// included) when known: CarryOn's 16-pixel gap is measured against the
    /// background edge, not the outermost cell, so ignoring them leaves only
    /// the background's own padding visible between bar and icon.
    /// Returns false when the inputs are degenerate; callers must pass the
    /// original position through untouched then.
    /// </summary>
    public static bool TryPlace(
        int index,
        bool leftSide,
        int cellX,
        int cellSize,
        int rowLeft,
        int rowRight,
        int iconSize,
        int iconGap,
        out int centerX,
        int? backgroundLeft,
        int? backgroundRight)
    {
        centerX = 0;
        if (index < 0 || cellSize <= 0 || iconSize <= 0 || iconGap < 0 || rowLeft >= rowRight)
        {
            return false;
        }

        if (leftSide)
        {
            // When the cell is left of the bar, the leftmost visible element
            // (cell, extension edge, or background edge) is the boundary;
            // when it sits inside the bar (e.g. the offhand-gap anchor), the
            // background/row edge wins and the anchors keep their vanilla
            // offsets.
            int boundary = Math.Min(cellX, rowLeft);
            if (backgroundLeft is int visibleLeft)
            {
                boundary = Math.Min(boundary, visibleLeft);
            }

            centerX = boundary - iconGap - iconSize + (iconSize / 2) - (index * (iconSize + iconGap));
        }
        else
        {
            int boundary = Math.Max(cellX + cellSize, rowRight);
            if (backgroundRight is int visibleRight)
            {
                boundary = Math.Max(boundary, visibleRight);
            }

            centerX = boundary + iconGap + (iconSize / 2) + (index * (iconSize + iconGap));
        }

        return true;
    }
}
