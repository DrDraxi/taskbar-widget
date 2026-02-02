using Microsoft.UI.Xaml;

namespace HelloWorld;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private HelloWidget? _widget;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Create hidden window (required for WinUI lifecycle)
        _mainWindow = new MainWindow();

        // Create and show the widget with unique text based on PID
        var pid = Environment.ProcessId;
        _widget = new HelloWidget();
        if (_widget.Initialize())
        {
            _widget.SetText($"Hi #{pid % 1000}");
            _widget.Show();
        }
    }
}
