namespace Voiceover.Client.Services;

// Streaming 16kHz -> 48kHz interpolator for the Facebook Denoiser backend
// (see FacebookDenoiserProcessor.cs) - that model runs natively at 16kHz,
// so its output needs to come back up to this app's 48kHz pipeline rate
// before it can be mixed/published like every other backend's output.
// Inverse of Resampler48kTo16k, same exact 1:3 ratio: zero-stuff by 3,
// then low-pass filter both to reconstruct the missing samples and to
// suppress the spectral images zero-stuffing introduces above the
// original 8kHz Nyquist.
internal class Resampler16kTo48k
{
    private const int InterpolationFactor = 3;
    private readonly float[] _kernel;

    // Last (kernel.Length - 1) zero-stuffed samples from the previous
    // call, carried forward so the FIR convolution has real history at
    // the start of each new chunk instead of an artificial zero-padded
    // edge - same reasoning as Resampler48kTo16k's own history buffer.
    private readonly float[] _history;

    public Resampler16kTo48k(int taps = 63, double cutoffHz = 7000, double sampleRateHz = 48000)
    {
        _kernel = BuildLowPassKernel(taps, cutoffHz, sampleRateHz, InterpolationFactor);
        _history = new float[_kernel.Length - 1];
    }

    public void Reset() => Array.Clear(_history);

    // Always returns exactly pcm.Length * 3 samples.
    public float[] Process(float[] pcm)
    {
        var zeroStuffedLength = pcm.Length * InterpolationFactor;
        var extended = new float[_history.Length + zeroStuffedLength];
        Array.Copy(_history, extended, _history.Length);
        for (var i = 0; i < pcm.Length; i++)
            extended[_history.Length + i * InterpolationFactor] = pcm[i];

        var output = new float[zeroStuffedLength];
        for (var outIdx = 0; outIdx < zeroStuffedLength; outIdx++)
        {
            var centerAbs = _history.Length + outIdx;
            float sum = 0;
            for (var k = 0; k < _kernel.Length; k++)
                sum += _kernel[k] * extended[centerAbs - k];
            output[outIdx] = sum;
        }

        Array.Copy(extended, extended.Length - _history.Length, _history, 0, _history.Length);
        return output;
    }

    // Windowed-sinc low-pass, Hamming window, scaled to gain=factor - zero
    // stuffing drops the signal's average power to 1/factor (only every
    // factor'th sample carries real energy before filtering), so the
    // filter's DC gain has to make that back up rather than staying at 1
    // the way a plain decimation filter's does.
    private static float[] BuildLowPassKernel(int taps, double cutoffHz, double sampleRateHz, int gain)
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

        for (var n = 0; n < taps; n++) kernel[n] = (float)(kernel[n] * gain / sum);
        return kernel;
    }
}
