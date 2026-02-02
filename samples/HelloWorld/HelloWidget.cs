using Microsoft.UI.Content;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using TaskbarWidget;

namespace HelloWorld;

public sealed class HelloWidget : IDisposable
{
    private const string WidgetClassName = "HelloWorldTaskbarWidget";

    private TaskbarInjectionHelper? _injectionHelper;
    private DesktopWindowXamlSource? _xamlSource;
    private TextBlock? _textBlock;
    private bool _disposed;

    public bool IsVisible => _injectionHelper?.IsVisible ?? false;

    public bool Initialize()
    {
        if (_disposed) return false;

        try
        {
            _injectionHelper = new TaskbarInjectionHelper(new TaskbarInjectionConfig
            {
                ClassName = WidgetClassName,
                WindowTitle = "HelloWidget",
                WidthDip = 80,
                DeferInjection = true
            });

            var result = _injectionHelper.Initialize();
            if (!result.Success)
            {
                _injectionHelper.Dispose();
                _injectionHelper = null;
                return false;
            }

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(result.WindowHandle);
            _xamlSource = new DesktopWindowXamlSource();
            _xamlSource.Initialize(windowId);
            _xamlSource.SiteBridge.ResizePolicy = ContentSizePolicy.ResizeContentToParentWindow;

            _textBlock = new TextBlock
            {
                Text = "Hello!",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var contentBorder = new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Child = _textBlock,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Hover effect
            contentBorder.PointerEntered += (s, e) =>
                contentBorder.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(80, 255, 255, 255));
            contentBorder.PointerExited += (s, e) =>
                contentBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            var rootGrid = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Children = { contentBorder }
            };

            _xamlSource.Content = rootGrid;

            if (!_injectionHelper.Inject())
            {
                _xamlSource.Dispose();
                _xamlSource = null;
                _injectionHelper.Dispose();
                _injectionHelper = null;
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetText(string text)
    {
        if (_textBlock != null) _textBlock.Text = text;
    }

    public void Show() => _injectionHelper?.Show();
    public void Hide() => _injectionHelper?.Hide();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _xamlSource?.Dispose();
        _xamlSource = null;
        _injectionHelper?.Dispose();
        _injectionHelper = null;
    }
}
