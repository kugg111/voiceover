using System.Diagnostics;
using System.IO;
using RNNoise.NET;

namespace Voiceover.Client.Services;

// Owns the two noise-suppression engines (RNNoise, NSNet2) and the
// per-frame processing pipeline (suppression -> wet/dry mix -> gain).
// Extracted out of MicCaptureSource so there's exactly one implementation
// of "what happens to a captured frame" shared between the live capture
// path and the Settings "Test Mic" preview (see VoiceSettingsPanel),
// instead of two that could silently drift apart.
internal class NoiseSuppressionProcessor : IDisposable
{
    public bool Enabled { get; set; } = true;

    private NoiseSuppressionBackend _backend = NoiseSuppressionBackend.RNNoise;
    public NoiseSuppressionBackend Backend
    {
        get => _backend;
        set
        {
            if (_backend == value) return;
            _backend = value;
            // Drop whatever's mid-flight in the NSNet2 delay line - it
            // holds raw samples captured while NSNet2 was last selected,
            // which would otherwise get blended against fresh audio after
            // a backend switch (a stale, wrong-content ~18ms blip instead
            // of a clean one).
            _nsnet2DryDelayLine.Clear();
        }
    }

    // Boosts quiet mics ("only picks up from really close") - applied after
    // suppression/mix, same tanh soft-knee limiter as before this existed.
    public float MicGain { get; set; } = 4.0f;

    // 0-1 wet/dry blend applied after whichever backend ran: 1 = fully
    // processed (matches every backend's behavior before this setting
    // existed), lower values back off an over-suppressing backend (eating
    // quiet speech, "musical noise" artifacts) without switching backends
    // entirely.
    public float SuppressionMix { get; set; } = 1f;

    // Rolling (exponential moving average) ms/frame per backend - the
    // Test Mic preview surfaces these so users see a real, per-machine
    // number instead of a qualitative-only "heavier on CPU" copy. Only the
    // currently-selected backend's number moves during live capture; both
    // can move during a Test Mic run if the user switches backends and
    // re-tests.
    public double LastRNNoiseMs { get; private set; }
    public double LastNsnet2Ms { get; private set; }

    // --- RNNoise (lightweight RNN denoiser) ---
    private readonly Denoiser? _rnnoise;

    // --- NSNet2 (ONNX-based denoiser - see its own header for why it's
    // meaningfully different in cost/architecture from RNNoise) ---
    private readonly Nsnet2Processor? _nsnet2;

    // Scratch buffers reused across calls (grown on demand) rather than
    // allocated per frame - this runs ~50 times/sec on the live capture
    // path, so a per-frame allocation here would add real GC pressure.
    private short[] _mixOriginalScratch = Array.Empty<short>();
    private float[] _rnnoiseScratch = Array.Empty<float>();
    private float[] _nsnet2Scratch = Array.Empty<float>();

    // NSNet2's fixed algorithmic delay, measured empirically (not derived
    // by hand alone) in Nsnet2ProcessorTests.
    // Denoise_HasFixedAlgorithmicDelay_Of896Samples: the real 960-sample
    // call cadence against its 512-sample hop produces an exact, constant
    // 896-sample lag between a raw sample arriving and its processed
    // counterpart coming out. Running the dry signal through a matching
    // fixed-delay FIFO before blending keeps dry/wet time-aligned, so
    // mixing them doesn't comb-filter the way blending against the
    // undelayed raw signal would.
    private const int Nsnet2DelaySamples = 896;
    private readonly Queue<short> _nsnet2DryDelayLine = new();
    private short[] _nsnet2DelayedDryScratch = Array.Empty<short>();

    private readonly Stopwatch _stopwatch = new();

    public NoiseSuppressionProcessor()
    {
        // Wrapped in try/catch - both are newer, less-proven native
        // dependencies (a NuGet package still on 0.x, and a from-scratch
        // ONNX inference pipeline), so a failure to load either (missing
        // file, wrong arch, AV quarantine) must not break voice capture
        // entirely for people who never even select them. The
        // corresponding Apply* method no-ops if its engine stayed null.
        try { _rnnoise = new Denoiser(); }
        catch { _rnnoise = null; }

        try
        {
            _nsnet2 = new Nsnet2Processor(Path.Combine(AppContext.BaseDirectory, "nsnet2-20ms-48k-baseline.onnx"));
        }
        catch { _nsnet2 = null; }
    }

    // Processes exactly one 20ms/960-sample frame in place - gain first,
    // then noise suppression, then the wet/dry mix blend. Gain runs first
    // rather than last: no denoiser fully eliminates its target noise, and
    // boosting after suppression would amplify whatever residual (fan hum,
    // mic wind) each backend leaves behind right along with the voice -
    // worse the higher Mic Gain is set. Gain-before-denoise is also the
    // more standard signal-chain order (level the input, then process it).
    // Callers (MicCaptureSource's live capture loop, and the Test Mic
    // preview) both slice their input into this same frame size so preview
    // and live behavior stay identical.
    public void ProcessFrame(short[] pcm)
    {
        ApplyGain(pcm, MicGain);

        if (Enabled)
        {
            bool blending = SuppressionMix < 1f;
            short[]? dryReference = null;

            if (Backend == NoiseSuppressionBackend.Nsnet2)
            {
                // Keep the delay line fed every frame NSNet2 is selected,
                // not only while blending is active, so toggling the mix
                // slider mid-call never hits a cold, zero-filled start.
                if (_nsnet2DelayedDryScratch.Length < pcm.Length) _nsnet2DelayedDryScratch = new short[pcm.Length];
                for (int i = 0; i < pcm.Length; i++)
                {
                    _nsnet2DryDelayLine.Enqueue(pcm[i]);
                    _nsnet2DelayedDryScratch[i] = _nsnet2DryDelayLine.Count > Nsnet2DelaySamples
                        ? _nsnet2DryDelayLine.Dequeue()
                        : (short)0;
                }
                if (blending) dryReference = _nsnet2DelayedDryScratch;
            }
            else if (blending)
            {
                if (_mixOriginalScratch.Length < pcm.Length) _mixOriginalScratch = new short[pcm.Length];
                Array.Copy(pcm, _mixOriginalScratch, pcm.Length);
                dryReference = _mixOriginalScratch;
            }

            switch (Backend)
            {
                case NoiseSuppressionBackend.Nsnet2:
                    ApplyNsnet2(pcm);
                    break;
                default:
                    ApplyRNNoise(pcm);
                    break;
            }

            if (dryReference is not null)
            {
                for (int i = 0; i < pcm.Length; i++)
                {
                    float blended = dryReference[i] * (1 - SuppressionMix) + pcm[i] * SuppressionMix;
                    pcm[i] = (short)Math.Clamp(blended, short.MinValue, short.MaxValue);
                }
            }
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

    private void ApplyNsnet2(short[] pcm)
    {
        if (_nsnet2 is null) return;

        if (_nsnet2Scratch.Length < pcm.Length) _nsnet2Scratch = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            _nsnet2Scratch[i] = pcm[i] / (float)short.MaxValue;

        _stopwatch.Restart();
        _nsnet2.Denoise(_nsnet2Scratch);
        _stopwatch.Stop();
        LastNsnet2Ms = Ema(LastNsnet2Ms, _stopwatch.Elapsed.TotalMilliseconds);

        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(_nsnet2Scratch[i] * short.MaxValue, short.MinValue, short.MaxValue);
    }

    private static double Ema(double previous, double sample) =>
        previous <= 0 ? sample : previous * 0.9 + sample * 0.1;

    public void Dispose()
    {
        _rnnoise?.Dispose();
        _nsnet2?.Dispose();
    }
}
