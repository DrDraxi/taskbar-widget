using System.Runtime.InteropServices;
using TaskbarWidget.Rendering;

namespace TaskbarWidget.Interaction;

/// <summary>
/// Tracks mouse hover state and dispatches click events to panels.
/// </summary>
internal sealed class MouseTracker
{
    private bool _trackingMouse;
    private LayoutNode? _hoveredPanel;
    private LayoutNode? _rootNode;
    private double _dpiScale = 1.0;

    public LayoutNode? HoveredPanel => _hoveredPanel;
    public bool IsHovering => _trackingMouse;

    public event Action? HoverChanged;

    public void SetRoot(LayoutNode root, double dpiScale)
    {
        _rootNode = root;
        _dpiScale = dpiScale;
    }

    /// <summary>
    /// Returns true if hover state changed (requires re-render).
    /// </summary>
    public bool OnMouseMove(IntPtr hwnd, int x, int y)
    {
        if (!_trackingMouse)
        {
            var tme = new Native.TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
                dwFlags = Native.TME_LEAVE,
                hwndTrack = hwnd
            };
            Native.TrackMouseEvent(ref tme);
            _trackingMouse = true;
        }

        if (_rootNode == null) return false;

        var newHovered = HitTester.FindPanelAt(_rootNode, x, y, _dpiScale);
        return UpdateHoveredPanel(newHovered);
    }

    /// <summary>
    /// Returns true if hover state changed.
    /// </summary>
    public bool OnMouseLeave()
    {
        _trackingMouse = false;
        return UpdateHoveredPanel(null);
    }

    public void OnLeftButtonDown(int x, int y)
    {
        if (_rootNode == null) return;
        var panel = HitTester.FindPanelAt(_rootNode, x, y, _dpiScale);
        panel?.OnClick?.Invoke();
    }

    public void OnRightButtonDown(int x, int y)
    {
        if (_rootNode == null) return;
        var panel = HitTester.FindPanelAt(_rootNode, x, y, _dpiScale);
        panel?.OnRightClick?.Invoke();
    }

    public void OnDoubleClick(int x, int y)
    {
        if (_rootNode == null) return;
        var panel = HitTester.FindPanelAt(_rootNode, x, y, _dpiScale);
        panel?.OnDoubleClick?.Invoke();
    }

    private bool UpdateHoveredPanel(LayoutNode? newPanel)
    {
        if (newPanel == _hoveredPanel) return false;

        if (_hoveredPanel != null)
            _hoveredPanel.IsHovered = false;

        _hoveredPanel = newPanel;

        if (_hoveredPanel != null)
            _hoveredPanel.IsHovered = true;

        HoverChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Get the tooltip info for the currently hovered panel, or null.
    /// </summary>
    public (string? Title, string? Body) GetHoveredTooltip()
    {
        if (_hoveredPanel != null && (_hoveredPanel.TooltipTitle != null || _hoveredPanel.TooltipBody != null))
            return (_hoveredPanel.TooltipTitle, _hoveredPanel.TooltipBody);
        return (null, null);
    }
}
