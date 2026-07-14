using System.Diagnostics;
using SoundFlow.Extensions.WebRtc.Apm;
using RNNoise.NET;

namespace Voiceover.Client.Services;

// Owns the three noise-suppression native engines (WebRTC APM, RNNoise,
// DeepFilterNet) and the per-frame processing pipeline (suppression -> wet/
// dry mix -> gain). Extracted out of MicCaptureSource so there's exactly one
// implementation of "what happens to a captured frame" shared between the
// live capture path and the Settings "Test Mic" preview (see
// VoiceSettingsPanel), instead of two that could silently drift apart.
internal class NoiseSuppressionProcessor : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;

    public bool Enabled { get; set; } = true;
    public NoiseSuppressionBackend Backend { get; set; } = NoiseSuppressionBackend.WebRtcApm;

    // Boosts quiet mics ("only picks up from really close") - applied after
    // suppression/mix, same tanh soft-knee limiter as before this existed.
    public float MicGain { get; set; } = 4.0f;

    // 0-1 wet/dry blend applied after whichever backend ran: 1 = fully
    // processed (matches every backend's behavior before this setting
    // existed), lower values back off an over-suppressing backend (eating
    // quiet speech, "musical noise" artifacts) without switching backends
    // entirely - the one setting that applies uniformly to all three engines.
    public float SuppressionMix { get; set; } = 1f;

    public float DeepFilterAttenuationLimit
    {
        get => _deepFilter?.AttenuationLimit ?? LadspaHost.AttenuationLimitMax;
        set { if (_deepFilter is not null) _deepFilter.AttenuationLimit = value; }
    }

    public float DeepFilterPostFilterBeta
    {
        get => _deepFilter?.PostFilterBeta ?? LadspaHost.PostFilterBetaMin;
        set { if (_deepFilter is not null) _deepFilter.PostFilterBeta = value; }
    }

    private NoiseSuppressionLevel _webRtcNoiseSuppressionLevel = NoiseSuppressionLevel.High;
    public NoiseSuppressionLevel WebRtcNoiseSuppressionLevel
    {
        get => _webRtcNoiseSuppressionLevel;
        set
        {
            _webRtcNoiseSuppressionLevel = value;
            if (_apm is null || _apmConfig is null) return;
            _apmConfig.SetNoiseSuppression(true, value);
            _apm.ApplyConfig(_apmConfig);
        }
    }

    // Rolling (exponential moving average) ms/frame per backend - the
    // Test Mic preview surfaces these so users see a real, per-machine
    // number instead of the old qualitative-only "heavier on CPU" copy.
    // Only the currently-selected backend's number moves during live
    // capture; all three can move during a Test Mic run if the user
    // switches backends and re-tests.
    public double LastWebRtcApmMs { get; private set; }
    public double LastRNNoiseMs { get; private set; }
    public double LastDeepFilterMs { get; private set; }

    // --- WebRTC noise suppression (Google's real Audio Processing Module,
    // via SoundFlow's standalone wrapper - no SoundFlow capture/playback
    // engine involved) ---
    private readonly AudioProcessingModule? _apm;
    private readonly ApmConfig? _apmConfig;
    private const int ApmFrameSamples = 480;
    private readonly float[] _apmInput = new float[ApmFrameSamples];
    private readonly float[] _apmOutput = new float[ApmFrameSamples];
    private readonly float[][] _apmInputChannels;
    private readonly float[][] _apmOutputChannels;
    private static readonly StreamConfig ApmStreamConfig = new(SampleRate, Channels);

    // --- RNNoise (lightweight RNN denoiser, selectable alternative to the
    // APM above) ---
    private readonly Denoiser? _rnnoise;

    // --- DeepFilterNet3 (deep-learning denoiser, selectable alternative to
    // the two above, driven through LadspaHost - see that class) ---
    private readonly LadspaHost? _deepFilter;
    private readonly float[] _deepFilterBuffer = new float[LadspaHost.FrameSamples];

    // Scratch buffers reused across calls (grown on demand) rather than
    // allocated per frame - this runs ~50 times/sec on the live capture
    // path, so a per-frame allocation here would add real GC pressure.
    private short[] _mixOriginalScratch = Array.Empty<short>();
    private float[] _rnnoiseScratch = Array.Empty<float>();

    private readonly Stopwatch _stopwatch = new();

    public NoiseSuppressionProcessor()
    {
        _apmInputChannels = new[] { _apmInput };
        _apmOutputChannels = new[] { _apmOutput };

        _apm = new AudioProcessingModule();
        _apm.Initialize();
        _apmConfig = new ApmConfig();
        _apmConfig.SetNoiseSuppression(true, _webRtcNoiseSuppressionLevel);
        _apmConfig.SetHighPassFilter(true); // cuts low-frequency rumble (desk thumps, mic handling) below speech range
        _apm.ApplyConfig(_apmConfig);

        // Wrapped in try/catch unlike the APM above - both are newer, less-
        // proven native dependencies (a NuGet package still on 0.x, and a
        // hand-rolled LADSPA host against a third-party plugin DLL), so a
        // failure to load either (missing file, wrong arch, AV quarantine)
        // must not break voice capture entirely for people who never even
        // select them. The corresponding Apply* method no-ops if its engine
        // stayed null.
        try { _rnnoise = new Denoiser(); }
        catch { _rnnoise = null; }

        try { _deepFilter = new LadspaHost(); }
        catch { _deepFilter = null; }
    }

    // Processes exactly one 20ms/960-sample frame in place - noise
    // suppression (on the raw captured signal, before gain distorts the
    // noise floor it's calibrated against), then the wet/dry mix blend,
    // then gain. Callers (MicCaptureSource's live capture loop, and the
    // Test Mic preview) both slice their input into this same frame size so
    // preview and live behavior stay identical.
    public void ProcessFrame(short[] pcm)
    {
        if (Enabled)
        {
            // DeepFilterNet's "deep filtering" stage computes filter
            // coefficients from surrounding context and applies them with
            // real algorithmic delay - unlike RNNoise/WebRTC APM, which are
            // built for near-zero added latency in live calls. Blending its
            // output sample-for-sample against the (undelayed) raw signal
            // sums a signal with a delayed copy of itself - comb filtering,
            // which is exactly what "echo"/"doubling" is. Without knowing
            // the plugin's exact delay in samples to compensate for it,
            // DeepFilterNet always runs at full strength when enabled - the
            // UI (VoiceSettingsPanel) disables the Suppression Mix slider
            // for this backend to match.
            bool blending = SuppressionMix < 1f && Backend != NoiseSuppressionBackend.DeepFilterNet;
            if (blending)
            {
                if (_mixOriginalScratch.Length < pcm.Length) _mixOriginalScratch = new short[pcm.Length];
                Array.Copy(pcm, _mixOriginalScratch, pcm.Length);
            }

            switch (Backend)
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

            if (blending)
            {
                for (int i = 0; i < pcm.Length; i++)
                {
                    float blended = _mixOriginalScratch[i] * (1 - SuppressionMix) + pcm[i] * SuppressionMix;
                    pcm[i] = (short)Math.Clamp(blended, short.MinValue, short.MaxValue);
                }
            }
        }

        ApplyGain(pcm, MicGain);
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

    private void ApplyWebRtcApm(short[] pcm)
    {
        if (_apm is null) return;

        _stopwatch.Restart();
        for (int offset = 0; offset < pcm.Length; offset += ApmFrameSamples)
        {
            for (int i = 0; i < ApmFrameSamples; i++)
                _apmInput[i] = pcm[offset + i] / (float)short.MaxValue;

            _apm.ProcessStream(_apmInputChannels, ApmStreamConfig, ApmStreamConfig, _apmOutputChannels);

            for (int i = 0; i < ApmFrameSamples; i++)
                pcm[offset + i] = (short)Math.Clamp(_apmOutput[i] * short.MaxValue, short.MinValue, short.MaxValue);
        }
        _stopwatch.Stop();
        LastWebRtcApmMs = Ema(LastWebRtcApmMs, _stopwatch.Elapsed.TotalMilliseconds);
    }

    private void ApplyRNNoise(short[] pcm)
    {
        if (_rnnoise is null) return;

        if (_rnnoiseScratch.Length < pcm.Length) _rnnoiseScratch = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            _rnnoiseScratch[i] = pcm[i] / (float)short.MaxValue;

        _stopwatch.Restart();
        _rnnoise.Denoise(_rnnoiseScratch.AsSpan(0, pcm.Length));
        _stopwatch.Stop();
        LastRNNoiseMs = Ema(LastRNNoiseMs, _stopwatch.Elapsed.TotalMilliseconds);

        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(_rnnoiseScratch[i] * short.MaxValue, short.MinValue, short.MaxValue);
    }

    private void ApplyDeepFilterNet(short[] pcm)
    {
        if (_deepFilter is null) return;

        _stopwatch.Restart();
        for (int offset = 0; offset < pcm.Length; offset += LadspaHost.FrameSamples)
        {
            for (int i = 0; i < LadspaHost.FrameSamples; i++)
                _deepFilterBuffer[i] = pcm[offset + i] / (float)short.MaxValue;

            _deepFilter.Denoise(_deepFilterBuffer);

            for (int i = 0; i < LadspaHost.FrameSamples; i++)
                pcm[offset + i] = (short)Math.Clamp(_deepFilterBuffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
        }
        _stopwatch.Stop();
        LastDeepFilterMs = Ema(LastDeepFilterMs, _stopwatch.Elapsed.TotalMilliseconds);
    }

    private static double Ema(double previous, double sample) =>
        previous <= 0 ? sample : previous * 0.9 + sample * 0.1;

    public void Dispose()
    {
        _apmConfig?.Dispose();
        _apm?.Dispose();
        _rnnoise?.Dispose();
        _deepFilter?.Dispose();
    }
}
