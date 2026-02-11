using System.Text;

namespace TaskbarWidget.Interaction;

/// <summary>
/// Drop data received from a drag-and-drop operation.
/// </summary>
public sealed class DropData
{
    public string[]? Files { get; init; }
    public string? Text { get; init; }
}

/// <summary>
/// Handles WM_DROPFILES for simple file drop support.
/// </summary>
internal sealed class DropTarget
{
    private Action<string[]>? _onFileDrop;
    private Action<string>? _onTextDrop;

    public void SetFileDropHandler(Action<string[]>? handler) => _onFileDrop = handler;
    public void SetTextDropHandler(Action<string>? handler) => _onTextDrop = handler;

    public void EnableFileDrop(IntPtr hwnd)
    {
        Native.DragAcceptFiles(hwnd, true);
    }

    /// <summary>
    /// Handle WM_DROPFILES. Returns true if handled.
    /// </summary>
    public bool OnDropFiles(IntPtr wParam)
    {
        if (_onFileDrop == null) return false;

        var hDrop = wParam;
        uint count = Native.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        var files = new string[count];

        for (uint i = 0; i < count; i++)
        {
            uint size = Native.DragQueryFileW(hDrop, i, null, 0) + 1;
            var sb = new StringBuilder((int)size);
            Native.DragQueryFileW(hDrop, i, sb, size);
            files[i] = sb.ToString();
        }

        Native.DragFinish(hDrop);
        _onFileDrop(files);
        return true;
    }
}
