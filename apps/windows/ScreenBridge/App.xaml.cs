using Microsoft.UI.Xaml;

namespace ScreenBridge;

/// <summary>
/// ScreenBridge Windows app shell (PLAN.md §8 Phase 0). The XAML compiler
/// auto-generates the entry point (Main); do not define one or set
/// DISABLE_XAML_GENERATED_MAIN.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
