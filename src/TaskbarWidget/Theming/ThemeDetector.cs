namespace TaskbarWidget.Theming;

/// <summary>
/// Detects the current system theme (dark/light) using uxtheme.dll.
/// Listens for WM_SETTINGCHANGE to update.
/// </summary>
public static class ThemeDetector
{
    private static bool? _cachedIsDark;

    public static bool IsDarkMode
    {
        get
        {
            _cachedIsDark ??= DetectDarkMode();
            return _cachedIsDark.Value;
        }
    }

    public static Theme CurrentTheme => IsDarkMode ? Theme.Dark : Theme.Light;

    /// <summary>
    /// Call from WndProc on WM_SETTINGCHANGE to refresh the cached value.
    /// </summary>
    public static void OnSettingChange()
    {
        _cachedIsDark = DetectDarkMode();
    }

    private static bool DetectDarkMode()
    {
        try
        {
            return Native.ShouldSystemUseDarkMode();
        }
        catch
        {
            return true; // default to dark
        }
    }
}
