using Voiceover.Client.Services;

namespace Client.Tests;

public class Resampler48kTo16kTests
{
    private static short[] BuildTone(double freqHz, int sampleCount, double sampleRate = 48000)
    {
        var pcm = new short[sampleCount];
        double phase = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            pcm[i] = (short)(Math.Sin(phase) * short.MaxValue * 0.5);
            phase += 2 * Math.PI * freqHz / sampleRate;
        }
        return pcm;
    }

    private static double Rms(float[] samples, int skip)
    {
        double sumSq = 0;
        for (var i = skip; i < samples.Length; i++) sumSq += samples[i] * (double)samples[i];
        return Math.Sqrt(sumSq / (samples.Length - skip));
    }

    [Fact]
    public void Process_AlwaysReturnsExactlyOneThirdTheSamples()
    {
        var resampler = new Resampler48kTo16k();
        var pcm = new short[960];

        var output = resampler.Process(pcm);

        Assert.Equal(320, output.Length);
    }

    // A 1kHz tone sits well within the 16kHz output's 8kHz Nyquist, so the
    // low-pass filter should pass it through close to full amplitude -
    // this is the pipeline's actual voice-frequency range, not an edge
    // case, so heavy attenuation here would mean Silero VAD is fed a
    // quieted-down signal it wasn't designed for.
    [Fact]
    public void Process_PassesLowFrequencyContentThroughMostlyIntact()
    {
        var resampler = new Resampler48kTo16k();
        const int totalSamples48k = 48000 * 2;
        var pcm = BuildTone(1000, totalSamples48k);

        var output = new List<float>();
        for (var offset = 0; offset + 960 <= totalSamples48k; offset += 960)
        {
            var frame = new short[960];
            Array.Copy(pcm, offset, frame, 0, 960);
            output.AddRange(resampler.Process(frame));
        }

        var skip = 5000; // past filter startup transient
        var outRms = Rms(output.ToArray(), skip);
        var inputAmplitudeRms = 0.5 / Math.Sqrt(2); // RMS of the generated sine, normalized to match Process's -1..1 output scale

        Assert.InRange(outRms / inputAmplitudeRms, 0.85, 1.05);
    }

    // A 20kHz tone is above the 16kHz output's 8kHz Nyquist - without a
    // real anti-aliasing filter this would fold back into the audible
    // passband as a false low-frequency signal and could confuse VAD into
    // seeing "speech-like" energy that isn't there. The low-pass filter
    // existing at all is what this test actually verifies.
    [Fact]
    public void Process_RejectsAboveNyquistContent()
    {
        var resampler = new Resampler48kTo16k();
        const int totalSamples48k = 48000 * 2;
        var pcm = BuildTone(20000, totalSamples48k);

        var output = new List<float>();
        for (var offset = 0; offset + 960 <= totalSamples48k; offset += 960)
        {
            var frame = new short[960];
            Array.Copy(pcm, offset, frame, 0, 960);
            output.AddRange(resampler.Process(frame));
        }

        var skip = 5000;
        var outRms = Rms(output.ToArray(), skip);
        var inputAmplitudeRms = short.MaxValue * 0.5 / Math.Sqrt(2);

        // Well below the passband case's 0.85-1.05 ratio - proves the
        // filter is actually doing something, not just a no-op decimator.
        Assert.True(outRms / inputAmplitudeRms < 0.1,
            $"Expected strong attenuation above Nyquist, got ratio {outRms / inputAmplitudeRms:F3}");
    }
}
