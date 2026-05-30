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
/// Kept as 32-bit FLOAT end to end: the WASAPI mix format is float, Concentus
/// encodes/decodes float natively, and the playout format equals the mix format —
/// so WASAPI shared mode needs no format conversion (a 16-bit play buffer through
/// shared mode silently mis-converts on float-mix devices). The captured mix is
/// resampled to 48 kHz stereo (Opus's rate) and chopped into exact 20 ms frames.
/// Everything runs on NAudio's single capture thread, so the non-thread-safe
/// Concentus codecs need no locking.
///
/// KNOWN single-machine limitation: WASAPI loopback captures the render mix,
/// which includes our own playout, so this feeds back (escalating echo) — the
/// macOS side avoids it with excludesCurrentProcessAudio (macOS 14+); the Windows
/// equivalent is process-loopback exclusion (deferred, task #16). No feedback in
/// the Phase-2 two-device path.
/// </remarks>
internal sealed class WindowsAudioLoopback : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int FrameSamples = 960;            // 20 ms @ 48 kHz, per channel
    private const int FrameFloats = FrameSamples * Channels; // interleaved stereo

    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private ISampleProvider? _resampled;             // 48 kHz stereo float
    private WasapiOut? _output;
    private BufferedWaveProvider? _playBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;

    // Reused buffers (single capture thread → no allocation on the hot path).
    private readonly float[] _frame = new float[FrameFloats];
    private int _frameFilled;                        // floats accumulated
    private readonly byte[] _packet = new byte[4000];
    private readonly float[] _decoded = new float[FrameFloats];
    private readonly byte[] _decodedBytes = new byte[FrameFloats * sizeof(float)];
    private bool _loggedFirst;

    public void Start()
    {
        // Force the pure-managed Opus path (don't probe for a native libopus.dll).
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = 96000;
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        // Capture the system mix; resample to 48 kHz stereo float (passthrough when
        // the mix is already 48 kHz).
        _capture = new WasapiLoopbackCapture();
        _captureBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
        };
        ISampleProvider chain = new WdlResamplingSampleProvider(_captureBuffer.ToSampleProvider(), SampleRate);
        if (chain.WaveFormat.Channels == 1)
        {
            chain = new MonoToStereoSampleProvider(chain);
        }
        _resampled = chain;

        // Playout in the float mix format → WASAPI shared mode plays it directly.
        _playBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
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
            $"[ScreenBridge] audio loopback started: WASAPI loopback ({_capture.WaveFormat}) → Opus 48k stereo 20ms (float) → WASAPI out");
    }

    // Fires on NAudio's dedicated capture thread.
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _captureBuffer!.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Drain the resampled stream into exact 960-sample (1920-float) frames.
        int read;
        while ((read = _resampled!.Read(_frame, _frameFilled, FrameFloats - _frameFilled)) > 0)
        {
            _frameFilled += read;
            if (_frameFilled < FrameFloats)
            {
                continue;
            }
            _frameFilled = 0;
            RoundTripFrame();
        }
    }

    private void RoundTripFrame()
    {
        int encodedLen = _encoder!.Encode(_frame, FrameSamples, _packet, _packet.Length);
        if (encodedLen <= 0)
        {
            return;
        }

        int decodedSamples = _decoder!.Decode(new ReadOnlySpan<byte>(_packet, 0, encodedLen), _decoded, FrameSamples, false);
        if (decodedSamples <= 0)
        {
            return;
        }

        int decodedBytes = decodedSamples * Channels * sizeof(float);
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
