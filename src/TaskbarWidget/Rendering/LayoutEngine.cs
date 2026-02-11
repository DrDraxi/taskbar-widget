using System.Runtime.InteropServices;

namespace TaskbarWidget.Rendering;

/// <summary>
/// Performs measure and arrange passes on the layout tree.
/// </summary>
internal static class LayoutEngine
{
    /// <summary>
    /// Bottom-up pass: compute Width/Height for each node in physical pixels.
    /// </summary>
    public static void Measure(LayoutNode node, double dpiScale, IntPtr hdc)
    {
        // Measure children first
        foreach (var child in node.Children)
            Measure(child, dpiScale, hdc);

        switch (node.Type)
        {
            case LayoutNodeType.Text:
                MeasureText(node, dpiScale, hdc);
                break;

            case LayoutNodeType.Image:
                MeasureImage(node, dpiScale);
                break;

            case LayoutNodeType.Canvas:
                node.Width = Scale(node.RequestedWidthDip ?? 0, dpiScale);
                node.Height = Scale(node.RequestedHeightDip ?? 0, dpiScale);
                break;

            case LayoutNodeType.Horizontal:
                MeasureHorizontal(node, dpiScale);
                break;

            case LayoutNodeType.Vertical:
                MeasureVertical(node, dpiScale);
                break;

            case LayoutNodeType.Panel:
                MeasureContainer(node, dpiScale);
                break;

            case LayoutNodeType.Root:
                MeasureContainer(node, dpiScale);
                break;
        }
    }

    /// <summary>
    /// Top-down pass: set X/Y positions and compute AbsX/AbsY.
    /// </summary>
    public static void Arrange(LayoutNode node, int parentAbsX = 0, int parentAbsY = 0)
    {
        node.AbsX = parentAbsX + node.X;
        node.AbsY = parentAbsY + node.Y;

        switch (node.Type)
        {
            case LayoutNodeType.Horizontal:
                ArrangeHorizontal(node);
                break;

            case LayoutNodeType.Vertical:
                ArrangeVertical(node);
                break;

            case LayoutNodeType.Panel:
            case LayoutNodeType.Root:
                ArrangeContainer(node);
                break;
        }

        foreach (var child in node.Children)
            Arrange(child, node.AbsX, node.AbsY);
    }

    private static void MeasureText(LayoutNode node, double dpiScale, IntPtr hdc)
    {
        var style = node.TextStyle ?? TextStyle.Default;
        var text = node.Text ?? "";
        if (string.IsNullOrEmpty(text))
        {
            node.Width = 0;
            node.Height = 0;
            return;
        }

        int fontSize = -(int)(style.FontSizeDip * dpiScale);
        var hFont = Native.CreateFontW(
            fontSize, 0, 0, 0, style.FontWeight,
            0, 0, 0, Native.DEFAULT_CHARSET,
            Native.OUT_DEFAULT_PRECIS, Native.CLIP_DEFAULT_PRECIS,
            4 /* ANTIALIASED_QUALITY */, Native.DEFAULT_PITCH,
            style.FontFamily);

        var oldFont = Native.SelectObject(hdc, hFont);
        var rect = new Native.RECT { Left = 0, Top = 0, Right = 10000, Bottom = 10000 };
        Native.DrawTextW(hdc, text, -1, ref rect,
            (uint)(Native.DT_CALCRECT | Native.DT_NOPREFIX | Native.DT_SINGLELINE));
        Native.SelectObject(hdc, oldFont);
        Native.DeleteObject(hFont);

        node.Width = rect.Right;
        node.Height = rect.Bottom;
    }

    private static void MeasureImage(LayoutNode node, double dpiScale)
    {
        if (node.RequestedWidthDip.HasValue && node.RequestedHeightDip.HasValue)
        {
            node.Width = Scale(node.RequestedWidthDip.Value, dpiScale);
            node.Height = Scale(node.RequestedHeightDip.Value, dpiScale);
        }
        else if (node.Image != null)
        {
            node.Width = node.Image.Width;
            node.Height = node.Image.Height;
        }
    }

    private static void MeasureHorizontal(LayoutNode node, double dpiScale)
    {
        int spacingPx = Scale(node.Spacing, dpiScale);
        int totalW = 0;
        int maxH = 0;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            totalW += child.Width;
            if (i > 0) totalW += spacingPx;
            maxH = Math.Max(maxH, child.Height);
        }
        node.Width = totalW;
        node.Height = maxH;
    }

    private static void MeasureVertical(LayoutNode node, double dpiScale)
    {
        int spacingPx = Scale(node.Spacing, dpiScale);
        int maxW = 0;
        int totalH = 0;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            maxW = Math.Max(maxW, child.Width);
            totalH += child.Height;
            if (i > 0) totalH += spacingPx;
        }
        node.Width = maxW;
        node.Height = totalH;
    }

    private static void MeasureContainer(LayoutNode node, double dpiScale)
    {
        if (node.RequestedWidthDip.HasValue && node.RequestedHeightDip.HasValue)
        {
            node.Width = Scale(node.RequestedWidthDip.Value, dpiScale);
            node.Height = Scale(node.RequestedHeightDip.Value, dpiScale);
            return;
        }

        // Wrap content
        int maxW = 0, maxH = 0;
        foreach (var child in node.Children)
        {
            maxW = Math.Max(maxW, child.Width);
            maxH = Math.Max(maxH, child.Height);
        }
        node.Width = node.RequestedWidthDip.HasValue ? Scale(node.RequestedWidthDip.Value, dpiScale) : maxW;
        node.Height = node.RequestedHeightDip.HasValue ? Scale(node.RequestedHeightDip.Value, dpiScale) : maxH;
    }

    private static void ArrangeHorizontal(LayoutNode node)
    {
        int x = 0;
        // Spacing is already measured in MeasureHorizontal; re-derive from first measurement
        // We need pixel spacing. It was stored as DIP in node.Spacing but we need the actual px.
        // For simplicity, we compute total spacing from measured vs sum of children widths.
        int childWidthSum = 0;
        foreach (var c in node.Children) childWidthSum += c.Width;
        int totalSpacing = node.Width - childWidthSum;
        int spacingPx = node.Children.Count > 1 ? totalSpacing / (node.Children.Count - 1) : 0;

        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            child.X = x;
            // Center on cross-axis
            child.Y = (node.Height - child.Height) / 2;
            x += child.Width;
            if (i < node.Children.Count - 1) x += spacingPx;
        }
    }

    private static void ArrangeVertical(LayoutNode node)
    {
        int y = 0;
        int childHeightSum = 0;
        foreach (var c in node.Children) childHeightSum += c.Height;
        int totalSpacing = node.Height - childHeightSum;
        int spacingPx = node.Children.Count > 1 ? totalSpacing / (node.Children.Count - 1) : 0;

        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            child.Y = y;
            // Center on cross-axis
            child.X = (node.Width - child.Width) / 2;
            y += child.Height;
            if (i < node.Children.Count - 1) y += spacingPx;
        }
    }

    private static void ArrangeContainer(LayoutNode node)
    {
        // Center each child
        foreach (var child in node.Children)
        {
            child.X = (node.Width - child.Width) / 2;
            child.Y = (node.Height - child.Height) / 2;
        }
    }

    private static int Scale(int dip, double dpiScale) => (int)Math.Ceiling(dip * dpiScale);
}
