namespace Voiceover.Client.Services;

// Streaming 48kHz -> 16kHz decimator feeding Silero VAD (see
// SileroVadProcessor.cs) - VAD only needs a cheap speech/silence signal,
// not broadcast-quality audio, so a modest windowed-sinc FIR low-pass
// (avoiding aliasing above the 8kHz Nyquist of the 16kHz output) followed
// by keeping every 3rd sample is plenty. 48000/16000 is an exact 3:1
// ratio, so there's no fractional-sample-position tracking to get wrong
// the way an arbitrary rate pair would need - this class assumes it's
// always called with a sample count divisible by 3 (the live pipeline
// always calls with exactly 960, matching every other backend's frame
// contract).
internal class Resampler48kTo16k
{
    private const int DecimationFactor = 3;
    private readonly float[] _kernel;

    // Last (kernel.Length - 1) input samples from the previous call,
    // carried forward so the FIR convolution has real history at the start
    // of each new chunk instead of an artificial zero-padded edge.
    private readonly float[] _history;

    public Resampler48kTo16k(int taps = 63, double cutoffHz = 7000, double sampleRateHz = 48000)
    {
        _kernel = BuildLowPassKernel(taps, cutoffHz, sampleRateHz);
        _history = new float[_kernel.Length - 1];
    }

    public void Reset() => Array.Clear(_history);

    // Always returns exactly pcm.Length / 3 samples.
    public float[] Process(short[] pcm)
    {
        var extended = new float[_history.Length + pcm.Length];
        Array.Copy(_history, extended, _history.Length);
        for (var i = 0; i < pcm.Length; i++)
            extended[_history.Length + i] = pcm[i] / (float)short.MaxValue;

        var outputCount = pcm.Length / DecimationFactor;
        var output = new float[outputCount];
        for (var outIdx = 0; outIdx < outputCount; outIdx++)
        {
            var centerAbs = _history.Length + outIdx * DecimationFactor;
            float sum = 0;
            for (var k = 0; k < _kernel.Length; k++)
                sum += _kernel[k] * extended[centerAbs - k];
            output[outIdx] = sum;
        }

        Array.Copy(extended, extended.Length - _history.Length, _history, 0, _history.Length);
        return output;
    }

    // Windowed-sinc low-pass, Hamming window (~53dB stopband attenuation -
    // plenty for a VAD-only feed, no need for a sharper/costlier design).
    private static float[] BuildLowPassKernel(int taps, double cutoffHz, double sampleRateHz)
    {
        if (taps % 2 == 0) taps++; // odd length - one exact center tap, symmetric/linear-phase
        var kernel = new float[taps];
        var center = taps / 2;
        var fc = cutoffHz / sampleRateHz; // normalized cutoff, 0..0.5

        double sum = 0;
        for (var n = 0; n < taps; n++)
        {
            var m = n - center;
            var sinc = m == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * n / (taps - 1));
            kernel[n] = (float)(sinc * window);
            sum += kernel[n];
        }

        // Normalize so DC gain is exactly 1 (window truncation otherwise
        // leaves it slightly off).
        for (var n = 0; n < taps; n++) kernel[n] = (float)(kernel[n] / sum);
        return kernel;
    }
}
