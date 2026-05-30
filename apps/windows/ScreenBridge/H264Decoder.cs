using System;
using System.Linq;

using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
// Decoder soft codes (TransformNeedMoreInput / TransformStreamChange) live in
// Vortice.MediaFoundation; alias to avoid any CS0104 collision with Direct3D11.
using ResultCode = Vortice.MediaFoundation.ResultCode;

namespace ScreenBridge;

/// <summary>
/// Synchronous Media Foundation H.264 decoder with D3D11VA (PLAN.md §3 "D3D11VA
/// decode"). The inbox Microsoft H.264 decoder is a <b>synchronous</b> MFT that
/// still decodes on the GPU when given the shared <see cref="IMFDXGIDeviceManager"/>
/// — so it honours the hardware-decode lock while being a simple feed-then-drain
/// loop (no async event model, unlike the encoder).
/// </summary>
/// <remarks>
/// Decoded frames come back as a slice of a D3D11 texture ARRAY: unwrap via
/// <see cref="IMFDXGIBuffer"/>.GetResource + SubresourceIndex. The decoder
/// raises a one-time MF_E_TRANSFORM_STREAM_CHANGE before the first frame, which
/// is handled by (re)selecting the NV12 output type. All identifiers verified
/// against the decompiled Vortice.MediaFoundation 3.8.3.
/// </remarks>
internal sealed class H264Decoder : IDisposable
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Fps = 60;
    private const long FrameDurationHns = 10_000_000L / Fps;

    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x1;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");

    private IMFTransform _mft = null!;
    private bool _loggedFirstFrame;

    public void Start(IMFDXGIDeviceManager deviceManager)
    {
        _mft = EnumerateSyncDecoder();

        // D3D11VA: hand the decoder the shared device manager BEFORE setting types.
        _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)deviceManager.NativePointer);

        // Decoder ordering: INPUT type before OUTPUT type (opposite of the encoder).
        SetInputType();
        SelectNv12OutputType();

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);

        Console.WriteLine("[ScreenBridge] H.264 decoder started (sync MFT, D3D11VA → NV12)");
    }

    private static IMFTransform EnumerateSyncDecoder()
    {
        var inputType = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = VideoFormatGuids.H264 };
        var outputType = new RegisterTypeInfo { GuidMajorType = MFMediaType_Video, GuidSubtype = VideoFormatGuids.NV12 };
        using IMFActivateCollection activates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            inputType, outputType);

        IMFActivate? activate = activates.FirstOrDefault();
        if (activate is null)
        {
            throw new NotSupportedException("No H.264 decoder MFT found.");
        }
        return activate.ActivateObject<IMFTransform>();
    }

    private void SetInputType()
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MFMediaType_Video).CheckError();
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)Width << 32) | Height).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)Fps << 32) | 1).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError(); // progressive
        _mft.SetInputType(0, type, 0);
        type.Dispose();
    }

    // Pick the decoder's NV12 output type. Called at start and on STREAM_CHANGE.
    private void SelectNv12OutputType()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = _mft.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                throw new NotSupportedException("H.264 decoder exposes no NV12 output type."); // MF_E_NO_MORE_TYPES
            }

            using (candidate)
            {
                if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) == VideoFormatGuids.NV12)
                {
                    _mft.SetOutputType(0, candidate, 0);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Decode one encoded H.264 sample. On success returns the decoded NV12 frame
    /// as a slice of a D3D11 texture array. The caller OWNS <paramref name="texture"/>
    /// and must dispose it (releasing the decoder's pool slot) when done.
    /// </summary>
    public bool Decode(IMFSample encoded, out ID3D11Texture2D? texture, out uint subresource)
    {
        texture = null;
        subresource = 0;

        _mft.ProcessInput(0, encoded, 0);

        bool produced = false;
        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0, Sample = null };
            Result r = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);

            if (r.Code == ResultCode.TransformNeedMoreInput.Code)
            {
                break; // this frame fully drained
            }
            if (r.Code == ResultCode.TransformStreamChange.Code)
            {
                SelectNv12OutputType(); // decoder learned the real format; renegotiate + retry
                continue;
            }
            if (r.Failure || output.Sample is null)
            {
                break;
            }

            using IMFSample decoded = output.Sample;
            using IMFMediaBuffer buffer = decoded.GetBufferByIndex(0);
            using IMFDXGIBuffer dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
            nint resourcePtr = dxgi.GetResource(typeof(ID3D11Texture2D).GUID);
            subresource = dxgi.SubresourceIndex;

            texture?.Dispose(); // keep only the latest frame this call
            texture = new ID3D11Texture2D(resourcePtr);
            produced = true;
        }

        if (produced && !_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            Console.WriteLine("[ScreenBridge] first H.264 frame decoded (D3D11VA) — full encode→decode→render path live");
        }
        return produced;
    }

    public void Dispose()
    {
        if (_mft is not null)
        {
            try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0); } catch { /* shutting down */ }
        }
        _mft?.Dispose();
    }
}
