using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ScreenBridge;

/// <summary>
/// Phase-1 Risk-R1 capture probe (PLAN.md §10 R1, §8 Phase 1). Creates a
/// dedicated top-level Win32 window backed by a <b>flip-model</b> D3D11
/// swapchain (<see cref="SwapEffect.FlipDiscard"/>) rendering live animated
/// content, on its OWN STA thread. This is the real viewer render surface the
/// live H.264 pipeline will present to — proving it is window-capturable by
/// Discord/OBS (WGC) BEFORE building capture→encode→decode on top of it.
/// </summary>
/// <remarks>
/// Flip-model + an ordinary <c>WS_OVERLAPPEDWINDOW</c> (never fullscreen-
/// exclusive) is exactly what Windows.Graphics.Capture composites and reads —
/// the default capture path for Discord (Win11) and OBS "Windows 10 (1903+)".
/// The animated content is synthetic (a smoothly colour-cycling full-window
/// clear); it is swapped for decoded H.264 frames once R1 passes.
/// </remarks>
public sealed class R1ProbeWindowHost
{
    private const string WindowClassName = "ScreenBridgeR1ProbeWindowClass";
    private const string WindowTitle = "ScreenBridge — Viewer (R1)";
    private const int InitialWidth = 1280;
    private const int InitialHeight = 720;

    private Thread? _thread;
    private volatile bool _running;
    private bool _pendingResize;

    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>Non-null if the probe failed to initialise (surfaced by <see cref="Start"/>).</summary>
    public Exception? StartupError { get; private set; }

    // Keeps the WndProc delegate rooted for the window's lifetime (LibraryImport
    // cannot marshal a delegate field, and a collected delegate would crash the
    // next message dispatch).
    private Win32.WndProc? _wndProc;
    private nint _hwnd;

    // D3D11 / DXGI objects — created and destroyed on the probe thread.
    private IDXGIFactory2? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;

    private readonly Stopwatch _clock = new();

    /// <summary>Starts the probe thread and blocks briefly until init succeeds or fails.</summary>
    public void Start()
    {
        _running = true;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "R1ProbeWindow" };
        _thread.SetApartmentState(ApartmentState.STA); // owns the HWND + message pump
        _thread.Start();

        // Surface initialisation failures (no D3D device, etc.) to the caller.
        _ready.Wait(5000);
        if (StartupError is not null)
        {
            throw new InvalidOperationException("R1 probe failed to initialise.", StartupError);
        }
    }

    /// <summary>Signals the probe thread to exit and tear down its own D3D objects.</summary>
    public void Stop()
    {
        _running = false;
        _thread?.Join(2000);
    }

    private void ThreadMain()
    {
        try
        {
            CreateWindow();
            CreateDevice();
            CreateSwapChain();
            CreateRenderTargetView();
            _clock.Start();
        }
        catch (Exception ex)
        {
            StartupError = ex;
            _ready.Set();
            DisposeGraphics();
            return;
        }

        _ready.Set();
        RenderLoop();
        DisposeGraphics();
    }

    // MARK: window

    private void CreateWindow()
    {
        nint hInstance = Win32.GetModuleHandleW(null);
        _wndProc = WndProcImpl; // root the delegate
        nint wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);

        var wc = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            style = Win32.CS_HREDRAW | Win32.CS_VREDRAW,
            lpfnWndProc = wndProcPtr,
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        if (Win32.RegisterClassExW(in wc) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassExW failed.");
        }

        _hwnd = Win32.CreateWindowExW(
            0, WindowClassName, WindowTitle, Win32.WS_OVERLAPPEDWINDOW,
            Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, InitialWidth, InitialHeight,
            0, 0, hInstance, 0);
        if (_hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowExW failed.");
        }

        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
        Win32.UpdateWindow(_hwnd);
    }

    private nint WndProcImpl(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_SIZE:
                _pendingResize = true;
                return 0;
            case Win32.WM_CLOSE:
            case Win32.WM_DESTROY:
                _running = false;
                Win32.PostQuitMessage(0);
                return 0;
            default:
                return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    // MARK: D3D11 / DXGI

    private void CreateDevice()
    {
        FeatureLevel[] featureLevels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        // BgraSupport: flip-model + B8G8R8A8 back buffers / DWM interop.
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport;

        Result hr = D3D11CreateDevice(
            null, DriverType.Hardware, flags, featureLevels,
            out ID3D11Device device, out _, out ID3D11DeviceContext context);
        hr.CheckError();

        _device = device;
        _context = context;
    }

    private void CreateSwapChain()
    {
        _factory = CreateDXGIFactory2<IDXGIFactory2>(false);

        Win32.GetClientRect(_hwnd, out Win32.RECT rect);
        var description = new SwapChainDescription1
        {
            Width = (uint)Math.Max(rect.Width, 1),
            Height = (uint)Math.Max(rect.Height, 1),
            Format = Format.B8G8R8A8_UNorm, // flip-model-legal, WGC-friendly
            Stereo = false,
            SampleDescription = SampleDescription.Default, // MSAA is illegal in flip model
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,                 // flip model requires >= 2
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard, // the modern flip model
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        var fullscreen = new SwapChainFullscreenDescription { Windowed = true };
        _swapChain = _factory!.CreateSwapChainForHwnd(_device!, _hwnd, description, fullscreen);

        // Stop DXGI hijacking Alt+Enter into fullscreen-exclusive, which would
        // bypass DWM composition and black out window capture (PLAN.md §10 R1).
        _factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);
    }

    private void CreateRenderTargetView()
    {
        using ID3D11Texture2D backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device!.CreateRenderTargetView(backBuffer);
    }

    private void HandleResize()
    {
        _pendingResize = false;
        Win32.GetClientRect(_hwnd, out Win32.RECT rect);
        if (rect.Width <= 0 || rect.Height <= 0) return; // minimised

        _renderTargetView?.Dispose();
        _renderTargetView = null;
        // 0 buffers / Format.Unknown => keep the existing count and format.
        _swapChain!.ResizeBuffers(0, (uint)rect.Width, (uint)rect.Height, Format.Unknown);
        CreateRenderTargetView();
    }

    // MARK: loop

    private void RenderLoop()
    {
        while (_running)
        {
            while (Win32.PeekMessageW(out Win32.MSG msg, 0, 0, 0, Win32.PM_REMOVE))
            {
                if (msg.message == Win32.WM_QUIT)
                {
                    _running = false;
                    break;
                }
                Win32.TranslateMessage(in msg);
                Win32.DispatchMessageW(in msg);
            }
            if (!_running) break;

            if (_pendingResize) HandleResize();
            RenderFrame();
        }
    }

    private void RenderFrame()
    {
        if (_renderTargetView is null || _context is null || _swapChain is null) return;

        // Smoothly cycle the whole window through the hue wheel: unmistakably
        // "live" when captured, unmistakably black/frozen when capture fails.
        float hue = (float)((_clock.Elapsed.TotalSeconds * 0.30) % 1.0);
        (float r, float g, float b) = HsvToRgb(hue, 0.75f, 0.95f);
        var clear = new Color4(r, g, b, 1.0f);

        _context.OMSetRenderTargets(_renderTargetView); // re-bind: FlipDiscard drops the back buffer after Present
        _context.ClearRenderTargetView(_renderTargetView, clear);

        Result present = _swapChain.Present(1, PresentFlags.None); // syncInterval=1 => vsync-paced
        if (present.Failure && present.Code == ResultCode.DeviceRemoved.Code)
        {
            _running = false; // device lost; a production path would recreate it
        }
    }

    private static (float r, float g, float b) HsvToRgb(float h, float s, float v)
    {
        float i = (float)Math.Floor(h * 6f);
        float f = h * 6f - i;
        float p = v * (1f - s);
        float q = v * (1f - f * s);
        float t = v * (1f - (1f - f) * s);
        return ((int)i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    private void DisposeGraphics()
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _factory?.Dispose();
        _factory = null;

        if (_hwnd != 0)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
