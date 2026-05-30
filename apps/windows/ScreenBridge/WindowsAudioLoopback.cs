using System;

using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ScreenBridge;

/// <summary>
/// Phase-1 Windows audio loopback (PLAN.md §3, §8): WASAPI loopback captures the
/// system audio mix → Opus encode → Opus decode → WASAPI playout. Mirrors the
/// macOS AudioLoopback. WASAPI is wrapped by NAudio; Opus by Concentus (pure C#).
/// </summary>
/// <remarks>
/// The captured mix format is the device's (32-bit float, 44.1k OR 48k, usually
/// stereo), so it is resampled to the 48 kHz stereo Opus requires and chopped
/// into exact 20 ms (960-sample) frames. Everything runs on NAudio's single
/// capture thread, so the non-thread-safe Concentus codecs need no locking.
///
/// KNOWN single-machine limitation: WASAPI loopback captures the render mix,
/// which INCLUDES our own playout, so this feeds back (an escalating echo) —
/// the macOS side avoids it with excludesCurrentProcessAudio (macOS 14+); the
/// Windows equivalent is process-loopback exclusion, deferred as a refinement.
/// There is no feedback in the Phase-2 two-device path.
/// </remarks>
internal sealed class WindowsAudioLoopback : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int FrameSamples = 960;                       // 20 ms @ 48 kHz, per channel
    private const int FrameBytes = FrameSamples * Channels * 2; // interleaved 16-bit stereo

    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private IWaveProvider? _resampledTo16;                      // 48 kHz / 16-bit / stereo
    private WasapiOut? _output;
    private BufferedWaveProvider? _playBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;

    // Reused buffers (single capture thread → no allocation on the hot path).
    private readonly byte[] _frame = new byte[FrameBytes];
    private int _frameFilled;
    private readonly short[] _pcm = new short[FrameSamples * Channels];
    private readonly byte[] _packet = new byte[4000];
    private readonly short[] _decoded = new short[FrameSamples * Channels];
    private readonly byte[] _decodedBytes = new byte[FrameBytes];
    private bool _loggedFirst;

    public void Start()
    {
        // Force the pure-managed path (don't probe for a native libopus.dll).
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = 96000;
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        // Capture the system mix and resample to exactly 48 kHz / 16-bit / stereo.
        _capture = new WasapiLoopbackCapture();
        _captureBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };
        ISampleProvider resampled = new WdlResamplingSampleProvider(_captureBuffer.ToSampleProvider(), SampleRate);
        if (resampled.WaveFormat.Channels == 1)
        {
            resampled = new MonoToStereoSampleProvider(resampled);
        }
        _resampledTo16 = new SampleToWaveProvider16(resampled);

        // Playout buffer + WASAPI render (shared mode does its own SRC if needed).
        _playBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };
        _output = new WasapiOut(AudioClientShareMode.Shared, 100);
        _output.Init(_playBuffer);

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
        _output.Play();

        Console.WriteLine(
            $"[ScreenBridge] audio loopback started: WASAPI loopback ({_capture.WaveFormat}) → Opus 48k stereo 20ms → WASAPI out");
    }

    // Fires on NAudio's dedicated capture thread.
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _captureBuffer!.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Drain the resampled stream into exact 960-sample frames.
        int read;
        while ((read = _resampledTo16!.Read(_frame, _frameFilled, FrameBytes - _frameFilled)) > 0)
        {
            _frameFilled += read;
            if (_frameFilled < FrameBytes)
            {
                continue;
            }
            _frameFilled = 0;
            RoundTripFrame();
        }
    }

    private void RoundTripFrame()
    {
        Buffer.BlockCopy(_frame, 0, _pcm, 0, FrameBytes);

        int encodedLen = _encoder!.Encode(_pcm, FrameSamples, _packet, _packet.Length);
        if (encodedLen <= 0)
        {
            return;
        }

        int decodedSamples = _decoder!.Decode(new ReadOnlySpan<byte>(_packet, 0, encodedLen), _decoded, FrameSamples, false);
        if (decodedSamples <= 0)
        {
            return;
        }

        int decodedBytes = decodedSamples * Channels * 2;
        Buffer.BlockCopy(_decoded, 0, _decodedBytes, 0, decodedBytes);
        _playBuffer!.AddSamples(_decodedBytes, 0, decodedBytes);

        if (!_loggedFirst)
        {
            _loggedFirst = true;
            Console.WriteLine("[ScreenBridge] first system-audio frame Opus round-tripped → playing out");
        }
    }

    public void Dispose()
    {
        try { _capture?.StopRecording(); } catch { /* best effort */ }
        try { _output?.Stop(); } catch { /* best effort */ }
        _capture?.Dispose();
        _output?.Dispose();
    }
}
