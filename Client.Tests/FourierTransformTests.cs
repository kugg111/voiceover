using Voiceover.Client.Services;

namespace Client.Tests;

// Verifies FourierTransform matches NumPy's np.fft.rfft/irfft convention
// with hand-computed expected values, rather than trusting FourierOptions
// documentation alone - a scaling or conjugate-mirroring mistake here
// wouldn't throw, it would just quietly feed NSNet2 wrong features.
public class FourierTransformTests
{
    [Fact]
    public void Rfft_DcSignal_AllEnergyInBinZero()
    {
        var signal = new float[] { 2f, 2f, 2f, 2f, 2f, 2f, 2f, 2f };

        var bins = FourierTransform.Rfft(signal, 8);

        Assert.Equal(5, bins.Length); // n/2 + 1
        Assert.Equal(16.0, bins[0].Real, precision: 6); // N * value, unscaled forward
        Assert.Equal(0.0, bins[0].Imaginary, precision: 6);
        for (var i = 1; i < bins.Length; i++)
        {
            Assert.Equal(0.0, bins[i].Real, precision: 6);
            Assert.Equal(0.0, bins[i].Imaginary, precision: 6);
        }
    }

    [Fact]
    public void Rfft_KnownSineWave_MagnitudeSpikeAtExpectedBin()
    {
        const int n = 64;
        const int k = 4; // 4 full cycles over 64 samples - lands exactly on bin 4
        var signal = new float[n];
        for (var i = 0; i < n; i++)
            signal[i] = (float)Math.Sin(2 * Math.PI * k * i / n);

        var bins = FourierTransform.Rfft(signal, n);

        // A real sine wave's energy in an unscaled rfft concentrates at
        // bin k with magnitude n/2 (32 here) - everywhere else should be
        // near zero.
        Assert.Equal(32.0, bins[k].Magnitude, precision: 3);
        for (var i = 0; i < bins.Length; i++)
        {
            if (i == k) continue;
            Assert.True(bins[i].Magnitude < 0.5, $"Unexpected energy at bin {i}: {bins[i].Magnitude}");
        }
    }

    [Fact]
    public void RfftThenIrfft_RoundTripsOriginalSignal()
    {
        var original = new float[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f };

        var bins = FourierTransform.Rfft(original, 8);
        var reconstructed = FourierTransform.Irfft(bins, 8);

        Assert.Equal(original.Length, reconstructed.Length);
        for (var i = 0; i < original.Length; i++)
            Assert.Equal(original[i], reconstructed[i], precision: 4);
    }

    [Fact]
    public void RfftThenIrfft_RoundTripsNonTrivialFrameSize()
    {
        // 960 samples/20ms@48kHz - this app's actual frame size, as a
        // sanity check beyond the small hand-checkable cases above.
        const int n = 960;
        var rng = new Random(42);
        var original = new float[n];
        for (var i = 0; i < n; i++)
            original[i] = (float)(rng.NextDouble() * 2 - 1);

        var bins = FourierTransform.Rfft(original, n);
        var reconstructed = FourierTransform.Irfft(bins, n);

        for (var i = 0; i < n; i++)
            Assert.Equal(original[i], reconstructed[i], precision: 3);
    }
}
