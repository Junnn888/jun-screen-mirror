using System;
using System.IO;

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
        // Route Console diagnostics to the launching terminal (if any) so the
        // capture/encode logs are visible like the macOS `swift run` output.
        if (Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
        }

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
