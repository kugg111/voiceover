using System.Diagnostics;
using System.IO;
using RNNoise.NET;

namespace Voiceover.Client.Services;

// Owns the two noise-suppression engines (RNNoise, NSNet2) and the
// per-frame processing pipeline (gain -> suppression -> wet/dry mix).
// Extracted out of MicCaptureSource so there's exactly one implementation
// of "what happens to a captured frame" shared between the live capture
// path and the Settings "Test Mic" preview (see VoiceSettingsPanel),
// instead of two that could silently drift apart.
internal class NoiseSuppressionProcessor : IDisposable
{
    public bool Enabled { get; set; } = true;

    // ProcessFrame runs on the mic capture thread (NAudio's WaveInEvent
    // callback), while Backend/VadGateEnabled are set from the UI thread
    // whenever Settings is open during a live call - this lock protects the
    // shared mutable state those setters touch (delay lines, VAD internal
    // buffers) from racing the capture thread's own read/write of the same
    // state.
    private readonly object _lock = new();

    private NoiseSuppressionBackend _backend = NoiseSuppressionBackend.RNNoise;
    public NoiseSuppressionBackend Backend
    {
        get => _backend;
        set
        {
            lock (_lock)
            {
                if (_backend == value) return;
                _backend = value;
                // Drop whatever's mid-flight in the delay lines - they hold
                // raw samples captured while a since-deselected backend was
                // active, which would otherwise get blended against fresh
                // audio after a backend switch (a stale, wrong-content blip
                // instead of a clean one).
                _nsnet2DryDelayLine.Clear();
                _facebookDenoiserDryDelayLine.Clear();

                // Reset only when entering this backend, not on every
                // switch regardless of direction (including switches that
                // never touch it at all) - both because it's pointless work
                // otherwise, and because this is the one call in this whole
                // setter that crosses into LibTorch's native runtime via
                // denoiser_wrapper.dll, so it's wrapped: a failure here
                // must not take the setter (and whatever UI code called it)
                // down, and previously calling it unconditionally on every
                // switch - including switching AWAY from this backend -
                // could leave the native side in a bad state that then
                // broke whichever backend ran next, regardless of which one
                // that was.
                if (value == NoiseSuppressionBackend.FacebookDenoiser)
                {
                    try { _facebookDenoiser?.Reset(); }
                    catch { /* best-effort - see ApplyFacebookDenoiser's own null-engine fallback */ }
                }
            }
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
    public double LastFacebookDenoiserMs { get; private set; }

    // --- RNNoise (lightweight RNN denoiser) ---
    private readonly Denoiser? _rnnoise;

    // --- NSNet2 (ONNX-based denoiser - see its own header for why it's
    // meaningfully different in cost/architecture from RNNoise). CPU-only -
    // see Nsnet2Processor.cs for why the GPU/DirectML path was removed. ---
    private readonly Nsnet2Processor? _nsnet2;

    // --- Facebook Denoiser (real incremental streaming inference via
    // LibTorch - see FacebookDenoiserProcessor.cs's own header). The
    // heaviest backend by a wide margin (a genuine deep model, not a
    // lightweight RNN or a magnitude-masking STFT approach), but
    // meaningfully more aggressive on real background noise in testing. ---
    private readonly FacebookDenoiserProcessor? _facebookDenoiser;

    // --- Silero VAD pre-gate: mutes confirmed-silence stretches after
    // suppression runs, catching residual noise (fan hum, breathing) a
    // suppressor alone lets through. Off by default - newest, least-proven
    // piece of this pipeline. See ApplyVadPreRollGate for the pre-roll
    // buffering that keeps this from clipping word onsets. ---
    private readonly SileroVadProcessor? _vad;

    private bool _vadGateEnabled;
    public bool VadGateEnabled
    {
        get => _vadGateEnabled;
        set
        {
            lock (_lock)
            {
                if (_vadGateEnabled == value) return;
                _vadGateEnabled = value;
                // Stale state/context from before a gap (gate was off, or
                // this is the first time it's ever been on) would
                // otherwise bias the next few decisions - same reasoning
                // as Backend's own delay-line clear above.
                _vad?.Reset();
                _preRollBuffer.Clear();
            }
        }
    }

    // ~140ms at 20ms/frame - long enough to cover Silero's own onset-
    // detection lag (it needs a few 32ms chunks to become confident speech
    // started) without adding much noticeable delay; well under the
    // several-hundred-ms budget already established for NSNet2 itself.
    private const int VadPreRollFrames = 7;
    private readonly Queue<(short[] Pcm, bool HasSpeech)> _preRollBuffer = new();

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

    // Facebook Denoiser's own fixed algorithmic delay, in 48kHz samples -
    // derived from known, verified constants rather than empirically
    // measured through the model itself (an impulse-response test - the
    // approach Nsnet2DelaySamples above used - isn't reliable for this
    // backend specifically: it's a full waveform-domain model whose whole
    // job is suppressing anything that doesn't look like speech, and a
    // single-sample impulse embedded in silence is about as "not speech"
    // as a test signal gets, so its peak isn't trustworthy to locate).
    // Composed of: the exported model's own TotalLength=661 hop-buffering
    // delay (see FacebookDenoiserProcessor - this number came from the
    // reference DemucsStreamer's own documented streaming latency, and
    // was confirmed bit-exact against that reference implementation
    // during export, not guessed), expressed in 48kHz samples (661 * 3 =
    // 1983, exact since 48000/16000 is an integer ratio), plus each
    // resampler's own linear-phase FIR group delay ((taps-1)/2 = 31
    // samples at their default 63 taps, one on the way down to 16kHz, one
    // on the way back up to 48kHz - both operate at the 48kHz sample
    // rate, so no additional unit conversion needed for those two terms).
    private const int FacebookDenoiserDelaySamples = 1983 + 31 + 31;
    private readonly Queue<short> _facebookDenoiserDryDelayLine = new();
    private short[] _facebookDenoiserDelayedDryScratch = Array.Empty<short>();

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

        try { _vad = new SileroVadProcessor(Path.Combine(AppContext.BaseDirectory, "silero_vad.onnx")); }
        catch { _vad = null; }

        try { _nsnet2 = new Nsnet2Processor(Path.Combine(AppContext.BaseDirectory, "nsnet2-20ms-48k-baseline.onnx")); }
        catch { _nsnet2 = null; }

        try { _facebookDenoiser = new FacebookDenoiserProcessor(Path.Combine(AppContext.BaseDirectory, "denoiser_dns48_streaming.pt")); }
        catch { _facebookDenoiser = null; }
    }

    // Processes exactly one 20ms/960-sample frame in place - gain first,
    // then noise suppression, then the wet/dry mix blend. Gain runs before
    // suppression rather than after: no denoiser fully eliminates its
    // target noise, and boosting after suppression would amplify whatever
    // residual (fan hum, mic wind) each backend leaves behind right along
    // with the voice - worse the higher Mic Gain is set. Gain-before-
    // denoise is also the more standard signal-chain order (level the
    // input, then process it). Callers (MicCaptureSource's live capture
    // loop, and the Test Mic preview) both slice their input into this same
    // frame size so preview and live behavior stay identical.
    public void ProcessFrame(short[] pcm)
    {
        lock (_lock)
        {
            ApplyGain(pcm, MicGain);

            // Fed the raw (post-gain, pre-suppression) signal, before anything
            // below mutates pcm - matches what Silero was trained on, and
            // avoids any risk of a suppressor's own artifacts (musical noise,
            // over-smoothing) confusing the speech/silence decision.
            bool vadActive = Enabled && VadGateEnabled && _vad is not null;
            if (vadActive) _vad!.ProcessFrame(pcm);

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
                else if (Backend == NoiseSuppressionBackend.FacebookDenoiser)
                {
                    // Same reasoning as the NSNet2 delay line above - kept
                    // fed unconditionally so toggling the mix slider mid-call
                    // never hits a cold start.
                    if (_facebookDenoiserDelayedDryScratch.Length < pcm.Length) _facebookDenoiserDelayedDryScratch = new short[pcm.Length];
                    for (int i = 0; i < pcm.Length; i++)
                    {
                        _facebookDenoiserDryDelayLine.Enqueue(pcm[i]);
                        _facebookDenoiserDelayedDryScratch[i] = _facebookDenoiserDryDelayLine.Count > FacebookDenoiserDelaySamples
                            ? _facebookDenoiserDryDelayLine.Dequeue()
                            : (short)0;
                    }
                    if (blending) dryReference = _facebookDenoiserDelayedDryScratch;
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
                    case NoiseSuppressionBackend.FacebookDenoiser:
                        ApplyFacebookDenoiser(pcm);
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

            if (vadActive) ApplyVadPreRollGate(pcm);
        }
    }

    // Delays the (already suppressed/blended) output by VadPreRollFrames,
    // muting a delayed frame only if Silero never confirmed speech
    // anywhere between when it was captured and now. A late speech
    // confirmation retroactively un-mutes everything still sitting in the
    // buffer, not just frames from that point forward - that's what
    // actually protects a word's first syllable, since without it the
    // frames spanning VAD's own detection lag would already have been
    // committed to silence before VAD caught up.
    private void ApplyVadPreRollGate(short[] pcm)
    {
        var hasSpeechNow = _vad!.IsSpeechConfident;

        if (hasSpeechNow && _preRollBuffer.Count > 0)
        {
            var buffered = _preRollBuffer.ToArray();
            _preRollBuffer.Clear();
            foreach (var (bufferedPcm, _) in buffered)
                _preRollBuffer.Enqueue((bufferedPcm, true));
        }

        // Copied, not referenced - pcm is caller-owned and may be reused/
        // mutated again before this buffered copy's turn to be released.
        var stored = new short[pcm.Length];
        Array.Copy(pcm, stored, pcm.Length);
        _preRollBuffer.Enqueue((stored, hasSpeechNow));

        if (_preRollBuffer.Count > VadPreRollFrames)
        {
            var (oldestPcm, oldestHasSpeech) = _preRollBuffer.Dequeue();
            if (oldestHasSpeech)
                Array.Copy(oldestPcm, pcm, pcm.Length);
            else
                Array.Clear(pcm);
        }
        else
        {
            // Still filling the pre-roll window on startup - nothing old
            // enough to have a verdict yet.
            Array.Clear(pcm);
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
            pcm[i] = (short)(SoftClip(_nsnet2Scratch[i]) * short.MaxValue);
    }

    private void ApplyFacebookDenoiser(short[] pcm)
    {
        if (_facebookDenoiser is null) return;

        _stopwatch.Restart();
        _facebookDenoiser.Process(pcm);
        _stopwatch.Stop();
        LastFacebookDenoiserMs = Ema(LastFacebookDenoiserMs, _stopwatch.Elapsed.TotalMilliseconds);
    }

    private static double Ema(double previous, double sample) =>
        previous <= 0 ? sample : previous * 0.9 + sample * 0.1;

    // NSNet2's mask-and-reconstruct output isn't guaranteed to stay within
    // [-1,1] - its own STFT/overlap-add synthesis can occasionally overshoot
    // on transients (see Nsnet2ProcessorTests' own +-2 tolerance on Denoise's
    // output). A bare hard clamp on those rare over-unity samples is exactly
    // what produced audible clipping/crackle on loud speech. This only
    // engages above SoftClipKnee - anything under it (the vast majority of
    // real audio) passes through completely unchanged, unlike ApplyGain's
    // limiter, which is deliberately compressing across its whole range.
    private const float SoftClipKnee = 0.9f;
    private static float SoftClip(float x)
    {
        var abs = Math.Abs(x);
        if (abs <= SoftClipKnee) return x;

        var over = (abs - SoftClipKnee) / (1f - SoftClipKnee);
        var compressed = SoftClipKnee + (1f - SoftClipKnee) * (float)Math.Tanh(over);
        return Math.Sign(x) * compressed;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _rnnoise?.Dispose();
            _nsnet2?.Dispose();
            _facebookDenoiser?.Dispose();
            _vad?.Dispose();
        }
    }
}
