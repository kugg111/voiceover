using LiveKit.Rtc;
using NAudio.Wave;
using SoundFlow.Extensions.WebRtc.Apm;
using RNNoise.NET;

namespace Voiceover.Client.Services;

// Captures the mic, boosts it, runs it through real noise suppression, and
// hands processed frames to a LiveKit AudioSource for publishing. Used to be
// half of OpusAudioEndPoint - LiveKit's own engine now owns Opus encoding
// and jitter buffering, so this class only does what's still actually this
// app's job: NAudio device capture, gain, and noise suppression (WebRTC's
// real Audio Processing Module, not a hand-rolled RMS gate - see the PR that
// introduced it for why that mattered).
public class MicCaptureSource : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameDurationMs = 20;
    private const int SamplesPerFrame = SampleRate / 1000 * FrameDurationMs; // 960

    // Boosts quiet mics ("only picks up from really close") - applied to the
    // raw captured PCM after noise suppression. A soft-knee limiter (see
    // ApplyGain) keeps loud passages from clipping/crackling once boosted.
    public float MicGain { get; set; } = 4.0f;
    public bool MicMuted { get; set; }
    public bool NoiseSuppressionEnabled { get; set; } = true;
    public NoiseSuppressionBackend NoiseSuppressionBackend { get; set; } = NoiseSuppressionBackend.WebRtcApm;

    // Only meaningful for the DeepFilterNet backend (see LadspaHost) -
    // stored here too (not just forwarded) so the value survives even if
    // _deepFilter failed to load, and is applied to a newly-created
    // LadspaHost by the constructor below.
    private float _deepFilterAttenuationLimit = LadspaHost.AttenuationLimitMax;
    public float DeepFilterAttenuationLimit
    {
        get => _deepFilterAttenuationLimit;
        set
        {
            _deepFilterAttenuationLimit = value;
            if (_deepFilter is not null) _deepFilter.AttenuationLimit = value;
        }
    }

    // Fires with the fully processed frame (post noise-suppression, post-
    // gain) right before it's handed to LiveKit - used for the local
    // speaking-indicator detection in VoiceService, so it reacts to the same
    // signal that's actually published rather than the raw mic input.
    public event Action<short[]>? OnProcessedFrame;

    public AudioSource Source { get; }

    private readonly int _inputDeviceIndex;
    private WaveInEvent? _waveIn;
    private readonly List<short> _captureAccumulator = new();

    // --- WebRTC noise suppression (Google's real Audio Processing Module,
    // via SoundFlow's standalone wrapper - no SoundFlow capture/playback
    // engine involved, NAudio still owns that) ---
    private readonly AudioProcessingModule? _apm;
    private readonly ApmConfig? _apmConfig;

    // The APM processes fixed 10ms/480-sample chunks regardless of this
    // class's own 20ms/960-sample framing, so each frame is processed as
    // two consecutive halves. Buffers are reused across calls rather than
    // allocated per frame.
    private const int ApmFrameSamples = 480;
    private readonly float[] _apmInput = new float[ApmFrameSamples];
    private readonly float[] _apmOutput = new float[ApmFrameSamples];
    private readonly float[][] _apmInputChannels;
    private readonly float[][] _apmOutputChannels;
    private static readonly StreamConfig ApmStreamConfig = new(SampleRate, Channels);

    // --- RNNoise (lightweight RNN denoiser, selectable alternative to the
    // APM above - see NoiseSuppressionBackend). Denoiser.Denoise() handles
    // its own internal 480-sample framing and accepts any buffer length, so
    // unlike the APM this doesn't need a manual sub-chunking loop - the
    // full 960-sample frame goes in as one call. Samples are normalized to
    // -1..1 float same as the APM path; the wrapper does its own internal
    // scaling to what the native model expects. ---
    private readonly Denoiser? _rnnoise;
    private readonly float[] _rnnoiseBuffer = new float[SamplesPerFrame];

    // --- DeepFilterNet3 (deep-learning denoiser, selectable alternative
    // to the two above - see NoiseSuppressionBackend). Unlike RNNoise's
    // simple function-call library, this is a LADSPA plugin, so it's
    // driven through LadspaHost (a small P/Invoke LADSPA host, see that
    // class for why). It only accepts its own fixed hop_size per call, so
    // this needs the same manual sub-chunking loop as the APM path above,
    // not RNNoise's any-length convenience. ---
    private readonly LadspaHost? _deepFilter;
    private readonly float[] _deepFilterBuffer = new float[LadspaHost.FrameSamples];

    public MicCaptureSource(int inputDeviceIndex)
    {
        _inputDeviceIndex = inputDeviceIndex;
        _apmInputChannels = new[] { _apmInput };
        _apmOutputChannels = new[] { _apmOutput };

        Source = new AudioSource(SampleRate, Channels, 1000);

        if (WaveInEvent.DeviceCount == 0) return;
        if (_inputDeviceIndex >= 0 && _inputDeviceIndex >= WaveInEvent.DeviceCount) return;

        _apm = new AudioProcessingModule();
        _apm.Initialize();
        _apmConfig = new ApmConfig();
        _apmConfig.SetNoiseSuppression(true, NoiseSuppressionLevel.High);
        _apmConfig.SetHighPassFilter(true); // cuts low-frequency rumble (desk thumps, mic handling) below speech range
        _apm.ApplyConfig(_apmConfig);

        // Constructed unconditionally alongside the APM (cheap to init)
        // rather than lazily on first selection, so switching
        // NoiseSuppressionBackend mid-session never needs to spin up native
        // resources on the audio callback thread. Wrapped in try/catch
        // unlike the APM above - RNNoise is a newer, less-proven native
        // dependency, and a failure to load its native library (missing
        // file, wrong arch, AV quarantine) must not break voice joining
        // entirely for people who never even select it. ApplyRNNoise
        // no-ops if this stays null; the WebRTC APM path is unaffected.
        try
        {
            _rnnoise = new Denoiser();
        }
        catch
        {
            _rnnoise = null;
        }

        // Same defensive construction as RNNoise above - an even newer,
        // less-proven native dependency (a hand-rolled LADSPA host against
        // a third-party plugin DLL, not a published/tested NuGet package),
        // so a failure to load it must never break voice joining for
        // people who never select it.
        try
        {
            _deepFilter = new LadspaHost();
        }
        catch
        {
            _deepFilter = null;
        }

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _inputDeviceIndex,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = FrameDurationMs,
            NumberOfBuffers = 2
        };
        _waveIn.DataAvailable += OnMicDataAvailable;
        _waveIn.StartRecording();
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (MicMuted)
        {
            // Drop whatever was mid-accumulation rather than let it sit and
            // get sent as a stale burst the moment the mic is unmuted.
            _captureAccumulator.Clear();
            return;
        }

        int sampleCount = args.BytesRecorded / 2;
        var incoming = new short[sampleCount];
        Buffer.BlockCopy(args.Buffer, 0, incoming, 0, args.BytesRecorded);
        _captureAccumulator.AddRange(incoming);

        // NAudio's actual callback buffer size isn't guaranteed to line up
        // exactly with a clean frame boundary - accumulate and slice off
        // exact 20ms/960-sample frames rather than assuming each callback
        // already is one.
        while (_captureAccumulator.Count >= SamplesPerFrame)
        {
            var frame = _captureAccumulator.GetRange(0, SamplesPerFrame).ToArray();
            _captureAccumulator.RemoveRange(0, SamplesPerFrame);

            // Noise suppression runs on the raw captured signal, before
            // gain - the APM's noise modelling is calibrated for normal mic
            // input levels, and boosting first would distort the noise
            // floor it's trying to characterize.
            ApplyNoiseSuppression(frame);
            ApplyGain(frame, MicGain);

            OnProcessedFrame?.Invoke(frame);

            var audioFrame = new AudioFrame(frame, SampleRate, Channels, SamplesPerFrame, null);
            Source.CaptureFrame(audioFrame);
        }
    }

    // Boosts quiet input, then compresses through a tanh soft-knee limiter
    // instead of hard-clipping - a boosted loud passage compresses smoothly
    // rather than crackling the way naive multiply-and-truncate would.
    private static void ApplyGain(short[] pcm, float gain)
    {
        for (int i = 0; i < pcm.Length; i++)
        {
            float normalized = (pcm[i] * gain) / short.MaxValue;
            float limited = (float)Math.Tanh(normalized);
            pcm[i] = (short)(limited * short.MaxValue);
        }
    }

    private void ApplyNoiseSuppression(short[] pcm)
    {
        if (!NoiseSuppressionEnabled) return;

        switch (NoiseSuppressionBackend)
        {
            case NoiseSuppressionBackend.RNNoise:
                ApplyRNNoise(pcm);
                break;
            case NoiseSuppressionBackend.DeepFilterNet:
                ApplyDeepFilterNet(pcm);
                break;
            default:
                ApplyWebRtcApm(pcm);
                break;
        }
    }

    private void ApplyWebRtcApm(short[] pcm)
    {
        if (_apm is null) return;

        for (int offset = 0; offset < pcm.Length; offset += ApmFrameSamples)
        {
            for (int i = 0; i < ApmFrameSamples; i++)
                _apmInput[i] = pcm[offset + i] / (float)short.MaxValue;

            _apm.ProcessStream(_apmInputChannels, ApmStreamConfig, ApmStreamConfig, _apmOutputChannels);

            for (int i = 0; i < ApmFrameSamples; i++)
                pcm[offset + i] = (short)Math.Clamp(_apmOutput[i] * short.MaxValue, short.MinValue, short.MaxValue);
        }
    }

    private void ApplyRNNoise(short[] pcm)
    {
        if (_rnnoise is null) return;

        for (int i = 0; i < pcm.Length; i++)
            _rnnoiseBuffer[i] = pcm[i] / (float)short.MaxValue;

        _rnnoise.Denoise(_rnnoiseBuffer.AsSpan(0, pcm.Length));

        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(_rnnoiseBuffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
    }

    private void ApplyDeepFilterNet(short[] pcm)
    {
        if (_deepFilter is null) return;

        for (int offset = 0; offset < pcm.Length; offset += LadspaHost.FrameSamples)
        {
            for (int i = 0; i < LadspaHost.FrameSamples; i++)
                _deepFilterBuffer[i] = pcm[offset + i] / (float)short.MaxValue;

            _deepFilter.Denoise(_deepFilterBuffer);

            for (int i = 0; i < LadspaHost.FrameSamples; i++)
                pcm[offset + i] = (short)Math.Clamp(_deepFilterBuffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
        }
    }

    public void Dispose()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnMicDataAvailable;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }
        _apmConfig?.Dispose();
        _apm?.Dispose();
        _rnnoise?.Dispose();
        _deepFilter?.Dispose();
        Source.Dispose();
    }
}
