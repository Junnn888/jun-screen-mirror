using Microsoft.UI.Xaml;
using ScreenBridge.Interop;

namespace ScreenBridge;

/// <summary>
/// Empty viewer-shell window for Phase 0. On launch it performs the C# → Rust
/// core FFI round-trip and shows the result (PLAN.md §8 Phase 0). The dedicated
/// Win32 + D3D11 viewer window (PLAN.md §3, §10 R1) arrives in Phase 1.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Prove the C# -> Rust core C ABI round-trip at launch.
        int pong = NativeMethods.sb_ping(41);
        StatusText.Text =
            $"ScreenBridge core ping: sb_ping(41) = {pong}; " +
            $"protocol v{NativeMethods.sb_protocol_version()}; {NativeMethods.Version()}";
    }
}
