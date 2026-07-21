using System.Runtime.InteropServices;
using LiveKit.Rtc;
using PortAudioSharp;

namespace Voiceover.Client.Linux;

// Linux equivalent of the WPF client's MicCaptureSource
// (Client/Services/MicCaptureSource.cs), using PortAudio instead of NAudio
// (WASAPI) - PortAudio reaches ALSA/PulseAudio/PipeWire on Linux through
// the exact same managed API this class also runs unmodified on Windows,
// which is what let this be live-tested for real audio I/O on this
// Windows dev box before ever touching a Linux machine (see the Linux
// client plan's Phase 2 section).
//
// Noise suppression here is NSNet2 only (no RNNoise/backend choice, no
// GPU) - see Nsnet2Processor.cs's own comment for why CPU-only NSNet2 is
// the right v1 scope on Linux (RNNoise's native binary is win-x64 only;
// DirectML GPU is Windows-only DX12).
public class MicCaptureSourceLinux : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameDurationMs = 20;
    private const uint SamplesPerFrame = SampleRate / 1000 * FrameDurationMs; // 960

    public bool MicMuted { get; set; }
    public AudioSource Source { get; }

    public bool NoiseSuppressionEnabled { get; set; } = true;

    // 0-1 wet/dry blend of the NSNet2-suppressed signal against the raw
    // captured signal - see the WPF client's own SuppressionMix for why
    // (an over-suppressing backend eating quiet speech can be backed off
    // without disabling suppression entirely).
    public float SuppressionMix { get; set; } = 1f;

    public float MicGain { get; set; } = 4.0f;

    // Fires with the fully processed frame (post gain, post noise
    // suppression) right before it's handed to LiveKit - used for the
    // local speaking-indicator detection in VoiceServiceLinux, so it
    // reacts to the same signal that's actually published.
    public event Action<short[]>? OnProcessedFrame;

    private PortAudioSharp.Stream? _stream;
    private readonly Nsnet2Processor? _nsnet2;

    // NSNet2's fixed algorithmic delay (measured empirically in the WPF
    // client's own test suite - see NoiseSuppressionProcessor.cs) - the dry
    // signal is run through a matching delay line before blending, so
    // wet/dry mixing doesn't comb-filter against a time-misaligned dry
    // reference.
    private const int Nsnet2DelaySamples = 896;
    private readonly Queue<short> _dryDelayLine = new();

    private readonly float[] _nsnet2Scratch = new float[SamplesPerFrame];
    private readonly short[] _delayedDryScratch = new short[SamplesPerFrame];

    public MicCaptureSourceLinux(int inputDeviceIndex)
    {
        Source = new AudioSource(SampleRate, Channels, 1000);

        // Best-effort - a missing/corrupt model file or an ONNX Runtime
        // load failure must not take down mic capture entirely, same
        // reasoning as the WPF client's own try/catch around this.
        try
        {
            _nsnet2 = new Nsnet2Processor(Path.Combine(AppContext.BaseDirectory, "nsnet2-20ms-48k-baseline.onnx"));
        }
        catch
        {
            _nsnet2 = null;
        }

        PortAudioBootstrap.EnsureInitialized();

        var deviceIndex = inputDeviceIndex >= 0 ? inputDeviceIndex : PortAudio.DefaultInputDevice;
        if (deviceIndex == PortAudio.NoDevice) return;

        var deviceInfo = PortAudio.GetDeviceInfo(deviceIndex);
        var parameters = new StreamParameters
        {
            device = deviceIndex,
            channelCount = Channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = deviceInfo.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _stream = new PortAudioSharp.Stream(parameters, null, SampleRate, SamplesPerFrame, StreamFlags.NoFlag, OnAudioCallback, IntPtr.Zero);
        _stream.Start();
    }

    // Runs on PortAudio's own real-time audio thread, not the UI thread -
    // must never block. Source.CaptureFrame does its own internal
    // buffering/locking on this same thread, mirroring exactly how the WPF
    // client's OnMicDataAvailable already calls it straight from NAudio's
    // callback thread today.
    private StreamCallbackResult OnAudioCallback(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userDataPtr)
    {
        if (MicMuted || input == IntPtr.Zero) return StreamCallbackResult.Continue;

        var frame = new short[frameCount];
        Marshal.Copy(input, frame, 0, (int)frameCount);

        ApplyGain(frame, MicGain);
        if (NoiseSuppressionEnabled && _nsnet2 is not null) ApplyNsnet2(frame);

        OnProcessedFrame?.Invoke(frame);

        var audioFrame = new AudioFrame(frame, SampleRate, Channels, (int)frameCount, null);
        Source.CaptureFrame(audioFrame);

        return StreamCallbackResult.Continue;
    }

    // Boosts quiet input, then compresses through a tanh soft-knee limiter
    // instead of hard-clipping - same as the WPF client's own ApplyGain.
    private static void ApplyGain(short[] pcm, float gain)
    {
        for (var i = 0; i < pcm.Length; i++)
        {
            var normalized = (pcm[i] * gain) / short.MaxValue;
            var limited = (float)Math.Tanh(normalized);
            pcm[i] = (short)(limited * short.MaxValue);
        }
    }

    private void ApplyNsnet2(short[] pcm)
    {
        var blending = SuppressionMix < 1f;

        // Keep the delay line fed every frame, not only while blending, so
        // toggling the mix slider mid-call never hits a cold, zero-filled
        // start - matches the WPF client's own reasoning.
        for (var i = 0; i < pcm.Length; i++)
        {
            _dryDelayLine.Enqueue(pcm[i]);
            _delayedDryScratch[i] = _dryDelayLine.Count > Nsnet2DelaySamples ? _dryDelayLine.Dequeue() : (short)0;
        }

        for (var i = 0; i < pcm.Length; i++)
            _nsnet2Scratch[i] = pcm[i] / (float)short.MaxValue;

        _nsnet2!.Denoise(_nsnet2Scratch);

        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(_nsnet2Scratch[i] * short.MaxValue, short.MinValue, short.MaxValue);

        if (!blending) return;

        for (var i = 0; i < pcm.Length; i++)
        {
            var blended = _delayedDryScratch[i] * (1 - SuppressionMix) + pcm[i] * SuppressionMix;
            pcm[i] = (short)Math.Clamp(blended, short.MinValue, short.MaxValue);
        }
    }

    public void Dispose()
    {
        if (_stream is not null)
        {
            try { _stream.Stop(); } catch { /* already stopped/closed */ }
            _stream.Close();
            _stream.Dispose();
            _stream = null;
        }
        Source.Dispose();
        _nsnet2?.Dispose();
    }
}
