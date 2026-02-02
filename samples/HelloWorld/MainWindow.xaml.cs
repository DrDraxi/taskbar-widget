using Microsoft.UI.Xaml;

namespace HelloWorld;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
        // Keep window hidden - we only need it for WinUI lifecycle
    }
}
