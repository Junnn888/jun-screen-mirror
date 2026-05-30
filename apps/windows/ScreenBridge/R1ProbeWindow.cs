using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
// Disambiguate the two imported ResultCode types (CS0104). Present()/swapchain
// results (DeviceRemoved, Occluded, WasStillDrawing) live in Vortice.DXGI.
using ResultCode = Vortice.DXGI.ResultCode;

namespace ScreenBridge;

/// <summary>
/// Phase-1 Windows viewer window + local video loopback (PLAN.md §3, §8, §10 R1).
/// A dedicated top-level Win32 window backed by a <b>flip-model</b> D3D11
/// swapchain (<see cref="SwapEffect.FlipDiscard"/>), proven Discord/OBS-capturable
/// in R1. The synthetic colour-cycle has been replaced by the real Phase-1
/// pipeline: Desktop Duplication captures the primary display, the D3D11 Video
/// Processor scales it into the swapchain back buffer, and the window presents it.
/// (Increment W1: capture → scale → render. Media Foundation H.264 encode +
/// D3D11VA decode are inserted in W2 between capture and the final blit.)
/// </summary>
/// <remarks>
/// The D3D11 device is created with <see cref="DeviceCreationFlags.VideoSupport"/>
/// and made multithread-protected so it can drive the Video Processor (and the
/// async Media Foundation MFTs in W2). Everything runs on the window's own STA
/// thread, which owns the device + message pump.
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

    // Capture + video-processor (scale/convert) objects.
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private DesktopDuplicator? _duplicator;
    private ID3D11Texture2D? _captureTexture;               // native-res BGRA copy of the desktop
    private ID3D11VideoProcessorEnumerator? _vpEnum;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorInputView? _vpInputView;
    private bool _loggedFirstFrame;

    /// <summary>Starts the probe thread and blocks briefly until init succeeds or fails.</summary>
    public void Start()
    {
        _running = true;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "R1ProbeWindow" };
        _thread.SetApartmentState(ApartmentState.STA); // owns the HWND + message pump
        _thread.Start();

        // Surface initialisation failures (no D3D device, no display, etc.).
        _ready.Wait(5000);
        if (StartupError is not null)
        {
            throw new InvalidOperationException("Viewer pipeline failed to initialise.", StartupError);
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
            SetupCapture();
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

        // BgraSupport: flip-model B8G8R8A8 back buffers / DWM interop.
        // VideoSupport: required for the D3D11 Video Processor + Media Foundation D3D11VA.
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        Result hr = D3D11CreateDevice(
            null, DriverType.Hardware, flags, featureLevels,
            out ID3D11Device device, out _, out ID3D11DeviceContext context);
        hr.CheckError();

        _device = device;
        _context = context;

        // Hardware MFTs (W2) touch the device from their own worker threads.
        using (ID3D11Multithread mt = device.QueryInterface<ID3D11Multithread>())
        {
            mt.SetMultithreadProtected(true);
        }

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();
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

    // MARK: capture + video processor

    private void SetupCapture()
    {
        _duplicator = new DesktopDuplicator(_device!);

        // Native-resolution BGRA copy target; also the Video Processor input
        // (which requires BindFlags.RenderTarget).
        var captureDesc = new Texture2DDescription
        {
            Width = (uint)_duplicator.Width,
            Height = (uint)_duplicator.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _captureTexture = _device!.CreateTexture2D(captureDesc);

        Win32.GetClientRect(_hwnd, out Win32.RECT rect);
        BuildVideoProcessor((uint)_duplicator.Width, (uint)_duplicator.Height,
                            (uint)Math.Max(rect.Width, 1), (uint)Math.Max(rect.Height, 1));

        Console.WriteLine(
            $"[ScreenBridge] viewer pipeline up: {_duplicator.Width}x{_duplicator.Height} desktop → flip-model swapchain (DDA + Video Processor)");
    }

    // The content description bakes in the input AND output dimensions, so the
    // processor must be rebuilt whenever the window (output) size changes.
    private void BuildVideoProcessor(uint inWidth, uint inHeight, uint outWidth, uint outHeight)
    {
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = inWidth,
            InputHeight = inHeight,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = outWidth,
            OutputHeight = outHeight,
            Usage = VideoUsage.PlaybackNormal,
        };
        _vpEnum = _videoDevice!.CreateVideoProcessorEnumerator(content);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_vpEnum, 0);

        // BGRA full-range in and out (W1 is a same-format scale; W2 swaps in the
        // NV12 colour-space configuration for encode/decode).
        _videoContext!.VideoProcessorSetStreamColorSpace(_videoProcessor, 0, new VideoProcessorColorSpace());
        _videoContext.VideoProcessorSetOutputColorSpace(_videoProcessor, new VideoProcessorColorSpace());

        // Input view over the stable capture texture (recreated with the enumerator).
        _vpInputView?.Dispose();
        _vpInputView = _videoDevice.CreateVideoProcessorInputView(_captureTexture!, _vpEnum,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0, // use the resource's DXGI format
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
    }

    private void HandleResize()
    {
        _pendingResize = false;
        Win32.GetClientRect(_hwnd, out Win32.RECT rect);
        if (rect.Width <= 0 || rect.Height <= 0) return; // minimised

        // 0 buffers / Format.Unknown => keep the existing count and format.
        _swapChain!.ResizeBuffers(0, (uint)rect.Width, (uint)rect.Height, Format.Unknown);

        // Output dimensions changed → rebuild the (dimension-baked) processor.
        _videoProcessor?.Dispose();
        _videoProcessor = null;
        _vpEnum?.Dispose();
        _vpEnum = null;
        BuildVideoProcessor((uint)_duplicator!.Width, (uint)_duplicator.Height,
                            (uint)rect.Width, (uint)rect.Height);
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
        if (_videoProcessor is null || _swapChain is null || _captureTexture is null ||
            _duplicator is null || _videoContext is null || _videoDevice is null)
        {
            return;
        }

        // Capture the latest desktop frame (reuse the previous one on timeout).
        bool gotNew = _duplicator.TryCaptureInto(_context!, _captureTexture);
        if (gotNew && !_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            Console.WriteLine("[ScreenBridge] first desktop frame captured — scaling + presenting");
        }

        // Scale/convert the capture texture into the current flip-model back buffer.
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        using ID3D11VideoProcessorOutputView outputView = _videoDevice.CreateVideoProcessorOutputView(
            backBuffer, _vpEnum!, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

        var streams = new[]
        {
            new VideoProcessorStream { Enable = true, InputSurface = _vpInputView! },
        };
        _videoContext.VideoProcessorBlt(_videoProcessor, outputView, 0u, streams).CheckError();

        Result present = _swapChain.Present(1, PresentFlags.None); // syncInterval=1 => vsync-paced
        if (present.Failure && present.Code == ResultCode.DeviceRemoved.Code)
        {
            _running = false; // device lost; a production path would recreate it
        }
    }

    private void DisposeGraphics()
    {
        _vpInputView?.Dispose();
        _vpInputView = null;
        _videoProcessor?.Dispose();
        _videoProcessor = null;
        _vpEnum?.Dispose();
        _vpEnum = null;
        _captureTexture?.Dispose();
        _captureTexture = null;
        _duplicator?.Dispose();
        _duplicator = null;
        _videoContext?.Dispose();
        _videoContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;

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
