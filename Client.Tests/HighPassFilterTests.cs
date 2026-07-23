using Voiceover.Client.Services;

namespace Client.Tests;

public class HighPassFilterTests
{
    private const double SampleRate = 48000;

    private static short[] BuildTone(double freqHz, int sampleCount)
    {
        var pcm = new short[sampleCount];
        double phase = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            pcm[i] = (short)(Math.Sin(phase) * short.MaxValue * 0.5);
            phase += 2 * Math.PI * freqHz / SampleRate;
        }
        return pcm;
    }

    private static double Rms(short[] samples, int skip)
    {
        double sumSq = 0;
        for (var i = skip; i < samples.Length; i++) sumSq += samples[i] * (double)samples[i];
        return Math.Sqrt(sumSq / (samples.Length - skip));
    }

    // 30Hz sits well below the 100Hz cutoff and in the range real wind
    // rumble concentrates in - this is the whole point of the filter, so it
    // needs to be meaningfully attenuated, not just nudged.
    [Fact]
    public void Process_StronglyAttenuatesSubCutoffRumble()
    {
        var filter = new HighPassFilter(sampleRate: 48000, cutoffHz: 100f);
        var pcm = BuildTone(30, 48000 * 2);

        filter.Process(pcm);

        var skip = 10000; // past the filter's own startup transient
        var outRms = Rms(pcm, skip);
        var inputAmplitudeRms = short.MaxValue * 0.5 / Math.Sqrt(2);

        Assert.True(outRms / inputAmplitudeRms < 0.3,
            $"Expected strong attenuation well below cutoff, got ratio {outRms / inputAmplitudeRms:F3}");
    }

    // 1kHz sits well within the speech range this filter must leave alone -
    // heavy attenuation here would mean Facebook Denoiser is being fed a
    // quieted-down voice signal, not just de-rumbled wind.
    [Fact]
    public void Process_PassesSpeechRangeContentThroughMostlyIntact()
    {
        var filter = new HighPassFilter(sampleRate: 48000, cutoffHz: 100f);
        var pcm = BuildTone(1000, 48000 * 2);

        filter.Process(pcm);

        var skip = 10000;
        var outRms = Rms(pcm, skip);
        var inputAmplitudeRms = short.MaxValue * 0.5 / Math.Sqrt(2);

        Assert.InRange(outRms / inputAmplitudeRms, 0.9, 1.05);
    }

    [Fact]
    public void Reset_ClearsFilterState()
    {
        var filter = new HighPassFilter(sampleRate: 48000, cutoffHz: 100f);
        var warmup = BuildTone(30, 48000);
        filter.Process(warmup);

        filter.Reset();

        // Immediately after Reset, a single sample from silence should
        // produce (near-)zero output, not carry over the previous signal's
        // filter state (_prevInput/_prevOutput) into stale-context math.
        var probe = new short[] { 0, 0, 0 };
        filter.Process(probe);

        Assert.All(probe, s => Assert.True(Math.Abs((int)s) < 10));
    }
}
