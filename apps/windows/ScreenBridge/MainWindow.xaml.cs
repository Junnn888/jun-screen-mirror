using System;

using Microsoft.UI.Xaml;
using ScreenBridge.Interop;

namespace ScreenBridge;

/// <summary>
/// WinUI 3 shell window. On launch it performs the C# → Rust core FFI round-trip
/// and spawns the Phase-1 Risk-R1 capture probe: a dedicated Win32 + flip-model
/// D3D11 top-level window (PLAN.md §3, §10 R1) whose capturability in Discord/OBS
/// is the make-or-break gate. Capture the separate "ScreenBridge — Viewer (R1)"
/// window, not this shell.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly R1ProbeWindowHost _probe = new();

    public MainWindow()
    {
        InitializeComponent();

        // Prove the C# -> Rust core C ABI round-trip at launch.
        int pong = NativeMethods.sb_ping(41);
        string core =
            $"ScreenBridge core ping: sb_ping(41) = {pong}; " +
            $"protocol v{NativeMethods.sb_protocol_version()}; {NativeMethods.Version()}";

        try
        {
            _probe.Start();
            StatusText.Text = core +
                "\n\nR1 probe running: a separate window titled \"ScreenBridge — Viewer (R1)\" " +
                "is rendering a live, colour-cycling flip-model D3D11 surface. Capture THAT " +
                "window in Discord, and in OBS via Window Capture → Windows 10 (1903+)/WGC.";
        }
        catch (Exception ex)
        {
            StatusText.Text = core + "\n\nR1 probe FAILED to start:\n" + ex;
        }

        Closed += (_, _) => _probe.Stop();
    }
}
