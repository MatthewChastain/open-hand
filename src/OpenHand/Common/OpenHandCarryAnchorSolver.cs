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
        out int centerX)
    {
        centerX = 0;
        if (index < 0 || cellSize <= 0 || iconSize <= 0 || iconGap < 0 || rowLeft >= rowRight)
        {
            return false;
        }

        if (leftSide)
        {
            // When the cell is left of the bar, its left edge is the boundary;
            // when it sits inside the bar (e.g. the offhand-gap anchor), the
            // row edge wins and the anchors keep their vanilla offsets.
            int boundary = Math.Min(cellX, rowLeft);
            centerX = boundary - iconGap - iconSize + (iconSize / 2) - (index * (iconSize + iconGap));
        }
        else
        {
            int boundary = Math.Max(cellX + cellSize, rowRight);
            centerX = boundary + iconGap + (iconSize / 2) + (index * (iconSize + iconGap));
        }

        return true;
    }
}
