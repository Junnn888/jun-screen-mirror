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
    // Constructed on the UI thread so NAudio's stop callbacks marshal back here.
    private readonly WindowsAudioLoopback _audio = new();

    public MainWindow()
    {
        InitializeComponent();

        // Prove the C# -> Rust core C ABI round-trip at launch.
        int pong = NativeMethods.sb_ping(41);
        string core =
            $"ScreenBridge core ping: sb_ping(41) = {pong}; " +
            $"protocol v{NativeMethods.sb_protocol_version()}; {NativeMethods.Version()}";

        string status = core;
        try
        {
            _probe.Start();
            status += "\n\nViewer running: a separate \"ScreenBridge — Viewer (R1)\" window mirrors " +
                "your screen through the full DDA → H.264 encode → D3D11VA decode → render pipeline. " +
                "Capture THAT window in Discord, or OBS Window Capture → Windows 10 (1903+)/WGC.";
        }
        catch (Exception ex)
        {
            status += "\n\nViewer FAILED to start:\n" + ex;
        }

        try
        {
            _audio.Start();
            status += "\n\nAudio loopback running (WASAPI → Opus → WASAPI). NOTE: on one machine " +
                "this feeds back — play audio briefly to verify, expect an escalating echo.";
        }
        catch (Exception ex)
        {
            status += "\n\nAudio loopback FAILED to start:\n" + ex;
        }

        StatusText.Text = status;

        Closed += (_, _) =>
        {
            _probe.Stop();
            _audio.Dispose();
        };
    }
}
