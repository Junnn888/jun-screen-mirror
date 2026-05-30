using System;
using System.Collections.Generic;
using System.Linq;

using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace ScreenBridge;

/// <summary>
/// Async hardware H.264 encoder via Media Foundation (PLAN.md §3: MF H.264,
/// NVENC/QSV/AMF). Wraps the asynchronous hardware encoder MFT and feeds it
/// D3D11 NV12 textures (zero-copy) through an <see cref="IMFDXGIDeviceManager"/>.
/// Hardware H.264 encoder MFTs are <b>asynchronous</b>: input is fed only in
/// response to <c>METransformNeedInput</c> and output pulled on
/// <c>METransformHaveOutput</c>. This driver runs that event model
/// <b>single-threaded</b> via non-blocking <c>GetEvent</c> polling from the
/// render loop — no worker-thread callbacks, so shutdown and resource lifetime
/// stay simple.
/// </summary>
/// <remarks>
/// All Vortice.MediaFoundation identifiers verified against the decompiled
/// 3.8.3 assemblies. The few keys Vortice does not expose as constants
/// (MFMediaType_Video, MF_LOW_LATENCY) are supplied as literal GUIDs and set via
/// the verified <c>IMFAttributes.Set(Guid, …)</c> overloads.
/// </remarks>
internal sealed class H264HardwareEncoder : IDisposable
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Fps = 60;
    private const int Bitrate = 8_000_000;
    private const long FrameDurationHns = 10_000_000L / Fps; // 100-ns units

    // MFT_ENUM_FLAG bits (mfapi.h) — passed as the uint flags arg to MFTEnumEx.
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    private const uint MFT_ENUM_FLAG_ASYNCMFT = 0x2;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    private const int MF_EVENT_FLAG_NO_WAIT = 0x1;

    // Keys Vortice has no named constant for.
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private IMFTransform _mft = null!;
    private IMFMediaEventGenerator _events = null!;

    private IMFSample? _pendingInput;
    private long _pendingCaptureTicks;
    private int _needInputCredits;          // encoder signalled NeedInput while we had no frame
    private long _frameIndex;
    private readonly Queue<long> _submitTicks = new(); // FIFO capture-QPC per fed frame (in order; no B-frames)

    private bool _loggedFirstOutput;

    /// <summary>Receives each encoded sample's byte size and capture→encoded latency (QPC ticks).</summary>
    public Action<int, long>? OnEncoded { get; set; }

    public void Start(IMFDXGIDeviceManager deviceManager)
    {
        _mft = EnumerateAsyncHardwareEncoder();

        // Async unlock + low latency must be set before configuring types.
        IMFAttributes attrs = _mft.Attributes;
        attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, true).CheckError();
        attrs.Set(MF_LOW_LATENCY, true); // disables B-frames on the MS encoder; best-effort

        // Hand the encoder the shared D3D11 device manager (D3D11 input textures).
        _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)deviceManager.NativePointer);

        // Encoders require OUTPUT type set BEFORE input type.
        SetOutputType();
        SetInputType();

        _events = _mft.QueryInterface<IMFMediaEventGenerator>();

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);

        Console.WriteLine("[ScreenBridge] H.264 hardware encoder started (async MFT, NV12 1920x1080@60, low-latency)");
    }

    private IMFTransform EnumerateAsyncHardwareEncoder()
    {
        var outputType = new RegisterTypeInfo
        {
            GuidMajorType = MFMediaType_Video,
            GuidSubtype = VideoFormatGuids.H264,
        };
        using IMFActivateCollection activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            inputType: null,
            outputType: outputType);

        IMFActivate? activate = activates.FirstOrDefault();
        if (activate is null)
        {
            throw new NotSupportedException("No async hardware H.264 encoder MFT found.");
        }
        return activate.ActivateObject<IMFTransform>();
    }

    private void SetOutputType()
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MFMediaType_Video).CheckError();
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Bitrate).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameSize, PackU32Pair(Width, Height)).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameRate, PackU32Pair(Fps, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError(); // MFVideoInterlace_Progressive
        _mft.SetOutputType(0, type, 0);
        type.Dispose();
    }

    private void SetInputType()
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MFMediaType_Video).CheckError();
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameSize, PackU32Pair(Width, Height)).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameRate, PackU32Pair(Fps, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
        _mft.SetInputType(0, type, 0);
        type.Dispose();
    }

    // MF packs FRAME_SIZE as (width<<32)|height and FRAME_RATE as (num<<32)|den.
    private static ulong PackU32Pair(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>
    /// Offer one captured NV12 texture (subresource 0) for encoding, tagged with
    /// its capture-time QueryPerformanceCounter value. Single-threaded: feeds now
    /// if the encoder is already asking for input, else holds it for the next
    /// NeedInput. An unconsumed prior frame is dropped (back-pressure).
    /// </summary>
    public void Submit(ID3D11Texture2D nv12Texture, long captureTicks)
    {
        _pendingInput?.Dispose();
        _pendingInput = WrapAsSample(nv12Texture);
        _pendingCaptureTicks = captureTicks;

        if (_needInputCredits > 0)
        {
            _needInputCredits--;
            FeedPending();
        }
    }

    private IMFSample WrapAsSample(ID3D11Texture2D nv12Texture)
    {
        IMFMediaBuffer buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
            typeof(ID3D11Texture2D).GUID, nv12Texture, 0u, false);
        buffer.CurrentLength = Width * Height * 3 / 2; // NV12 contiguous byte size

        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose(); // the sample holds its own reference
        sample.SampleTime = _frameIndex * FrameDurationHns;
        sample.SampleDuration = FrameDurationHns;
        _frameIndex++;
        return sample;
    }

    private void FeedPending()
    {
        if (_pendingInput is null) return;
        _submitTicks.Enqueue(_pendingCaptureTicks);
        _mft.ProcessInput(0, _pendingInput, 0);
        _pendingInput.Dispose();
        _pendingInput = null;
    }

    /// <summary>
    /// Drain all currently-queued MFT events (non-blocking). Feeds input on
    /// NeedInput, pulls + reports encoded samples on HaveOutput. Call once per
    /// render iteration.
    /// </summary>
    public void Pump()
    {
        while (true)
        {
            IMFMediaEvent ev;
            try
            {
                ev = _events.GetEvent(MF_EVENT_FLAG_NO_WAIT);
            }
            catch (SharpGenException)
            {
                break; // MF_E_NO_EVENTS_AVAILABLE — nothing more this tick
            }

            MediaEventTypes type = ev.EventType;
            ev.Dispose();

            if (type == MediaEventTypes.TransformNeedInput)
            {
                if (_pendingInput is not null) FeedPending();
                else _needInputCredits++;
            }
            else if (type == MediaEventTypes.TransformHaveOutput)
            {
                DrainOutput();
            }
        }
    }

    private void DrainOutput()
    {
        // Hardware encoders allocate their own output samples (MFT_OUTPUT_STREAM_
        // PROVIDES_SAMPLES), so leave Sample = null and read it back.
        var output = new OutputDataBuffer { StreamID = 0, Sample = null };
        Result r = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
        if (r.Failure || output.Sample is null)
        {
            return;
        }

        using IMFSample encoded = output.Sample;
        using IMFMediaBuffer buffer = encoded.ConvertToContiguousBuffer();
        int size = buffer.CurrentLength;

        long latencyTicks = 0;
        if (_submitTicks.Count > 0 && Win32.QueryPerformanceCounter(out long now))
        {
            latencyTicks = now - _submitTicks.Dequeue();
        }

        if (!_loggedFirstOutput)
        {
            _loggedFirstOutput = true;
            Console.WriteLine($"[ScreenBridge] first H.264 frame encoded ({size} bytes) — hardware encode path live");
        }
        OnEncoded?.Invoke(size, latencyTicks);
    }

    public void Dispose()
    {
        _pendingInput?.Dispose();
        _pendingInput = null;
        if (_mft is not null)
        {
            try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0); } catch { /* shutting down */ }
        }
        _events?.Dispose();
        _mft?.Dispose();
    }
}
