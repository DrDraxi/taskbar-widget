using TaskbarWidget.Rendering;

namespace TaskbarWidget.Interaction;

/// <summary>
/// Finds which panel a given pixel coordinate hits.
/// </summary>
internal static class HitTester
{
    /// <summary>
    /// Walk tree depth-first, return the deepest Panel containing (x, y).
    /// Coordinates are in the widget's local pixel space.
    /// </summary>
    public static LayoutNode? FindPanelAt(LayoutNode root, int x, int y, double dpiScale)
    {
        LayoutNode? hit = null;
        FindPanelRecursive(root, x, y, dpiScale, ref hit);
        return hit;
    }

    private static void FindPanelRecursive(LayoutNode node, int x, int y, double dpiScale, ref LayoutNode? hit)
    {
        if (node.Type == LayoutNodeType.Panel)
        {
            int cr = (int)(node.CornerRadius * dpiScale);
            if (cr > 0)
            {
                if (GdiRenderer.IsInsideRoundedRect(
                    x - node.AbsX, y - node.AbsY,
                    0, 0, node.Width, node.Height, cr))
                    hit = node;
            }
            else if (x >= node.AbsX && x < node.AbsX + node.Width &&
                     y >= node.AbsY && y < node.AbsY + node.Height)
            {
                hit = node;
            }
        }

        foreach (var child in node.Children)
            FindPanelRecursive(child, x, y, dpiScale, ref hit);
    }
}
