using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Voiceover.Client.Linux;

// Real-valued FFT/IFFT matching NumPy's np.fft.rfft/irfft convention - the
// reference implementation NSNet2 ships (and the model itself, since that's
// what it was trained against) assumes this exact normalization.
// MathNet.Numerics only exposes the general complex
// Fourier.Forward/Inverse, not a dedicated real-FFT, so this wraps it: pad
// real input into a Complex[], transform, then either take the first
// n/2+1 bins (rfft) or mirror them back out to the full spectrum before
// inverse-transforming (irfft).
//
// FourierOptions.AsymmetricScaling (MATLAB-compatible: unscaled forward,
// 1/n inverse) is the option that matches NumPy's convention - verified
// against known values in FourierTransformTests rather than assumed,
// since a scaling mismatch here wouldn't crash anything, it would just
// quietly feed the neural nets features they weren't trained on.
internal static class FourierTransform
{
    private const FourierOptions NumpyCompatible = FourierOptions.AsymmetricScaling;

    // Forward real FFT: n real samples -> n/2+1 complex bins.
    public static Complex[] Rfft(ReadOnlySpan<float> real, int n)
    {
        var buffer = new Complex[n];
        for (var i = 0; i < n; i++)
            buffer[i] = i < real.Length ? real[i] : 0.0;

        Fourier.Forward(buffer, NumpyCompatible);

        var bins = n / 2 + 1;
        var result = new Complex[bins];
        Array.Copy(buffer, result, bins);
        return result;
    }

    // Inverse real FFT: n/2+1 complex bins (conjugate-symmetric spectrum of
    // a real signal) -> n real samples.
    public static float[] Irfft(ReadOnlySpan<Complex> bins, int n)
    {
        var buffer = new Complex[n];
        var half = n / 2 + 1;
        for (var i = 0; i < half && i < bins.Length; i++)
            buffer[i] = bins[i];
        for (var i = half; i < n; i++)
            buffer[i] = Complex.Conjugate(buffer[n - i]);

        Fourier.Inverse(buffer, NumpyCompatible);

        var result = new float[n];
        for (var i = 0; i < n; i++)
            result[i] = (float)buffer[i].Real;
        return result;
    }
}
