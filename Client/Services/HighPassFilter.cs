namespace Voiceover.Client.Services;

// Single-pole (RC) high-pass filter - the standard, minimal-latency way to
// cut near-DC/rumble energy before it reaches a downstream processor.
// Wired in ahead of Facebook Denoiser specifically (see
// FacebookDenoiserProcessor.Process) to remove wind-noise rumble, which
// otherwise sometimes reads to that model as quasi-harmonic content it tries
// to reconstruct rather than suppress - producing a synthesized high-pitched
// tone instead of quieting the wind. A known failure mode of neural speech-
// enhancement models fed strong sub-100Hz energy dominating the input, which
// they were never trained to expect; a gentle pre-filter well below the
// speech fundamental range (typically 85Hz+) is the standard fix, not
// something the model's own weights can be tuned for from this side.
internal class HighPassFilter
{
    private readonly float _alpha;
    private float _prevInput;
    private float _prevOutput;

    public HighPassFilter(int sampleRate, float cutoffHz)
    {
        var rc = 1f / (2f * MathF.PI * cutoffHz);
        var dt = 1f / sampleRate;
        _alpha = rc / (rc + dt);
    }

    // In place, operating directly on 16-bit PCM - matches every other
    // per-frame processor in this pipeline (see NoiseSuppressionProcessor's
    // own ApplyGain).
    public void Process(short[] pcm)
    {
        for (var i = 0; i < pcm.Length; i++)
        {
            var x = pcm[i] / (float)short.MaxValue;
            var y = _alpha * (_prevOutput + x - _prevInput);
            _prevInput = x;
            _prevOutput = y;
            pcm[i] = (short)Math.Clamp(y * short.MaxValue, short.MinValue, short.MaxValue);
        }
    }

    public void Reset()
    {
        _prevInput = 0;
        _prevOutput = 0;
    }
}
