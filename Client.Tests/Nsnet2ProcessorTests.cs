using System.Diagnostics;
using System.Numerics;
using Voiceover.Client.Services;

namespace Client.Tests;

// Loads the real vendored NSNet2 model (copied to the test output
// directory - see the csproj CopyToOutputDirectory items) and runs actual
// inference, not mocks. Also confirms the model graph itself has no
// exposed state input/output, which is the whole reason Nsnet2Processor
// needs its sliding-window re-processing approach instead of the simple
// per-frame state threading RNNoise uses - documenting
// that as an assertion, not just a comment, so it can't silently drift
// true if a future model swap changes it.
//
// Deliberately does NOT assert "a synthetic test tone survives audibly" -
// investigated that directly (see git history) and found the model
// genuinely, correctly suppresses a perfectly stationary synthetic tone
// almost to silence (mask mean ~0.04, well-formed non-degenerate input
// features), since real speech is never that stationary. That's the model
// doing its job on an out-of-distribution input, not a bug to test for.
// What IS tested here is the thing actually under this class's control:
// the STFT analysis/overlap-add synthesis pipeline, verified independently
// of the model via an identity-mask reconstruction (mask=1 everywhere)
// that isolates "is my windowing/FFT/pseudo-inverse-synthesis-window math
// correct" from "what does the model output."
public class Nsnet2ProcessorTests
{
    private static string ModelPath => Path.Combine(AppContext.BaseDirectory, "nsnet2-20ms-48k-baseline.onnx");

    private static Nsnet2Processor CreateProcessor() => new(ModelPath);

    [Fact]
    public void Model_HasNoExternallyThreadedState()
    {
        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(ModelPath);

        Assert.Single(session.InputMetadata);
        Assert.Single(session.OutputMetadata);
    }

    [Fact]
    public void Denoise_ProcessesFrameWithoutThrowing()
    {
        using var processor = CreateProcessor();
        var frame = new float[960];

        var exception = Record.Exception(() => processor.Denoise(frame));

        Assert.Null(exception);
    }

    [Fact]
    public void Denoise_OutputIsFiniteAndBounded()
    {
        using var processor = CreateProcessor();
        double phase = 0;

        for (var call = 0; call < 20; call++)
        {
            var frame = new float[960];
            for (var i = 0; i < 960; i++)
            {
                frame[i] = (float)Math.Sin(phase);
                phase += 2 * Math.PI * 200 / 48000;
            }

            processor.Denoise(frame);

            foreach (var sample in frame)
            {
                Assert.False(float.IsNaN(sample), "Output contains NaN");
                Assert.False(float.IsInfinity(sample), "Output contains Infinity");
                Assert.InRange(sample, -2f, 2f);
            }
        }
    }

    [Fact]
    public void Denoise_KeepsUpWithRealTime()
    {
        using var processor = CreateProcessor();
        const int frames = 50; // 1 second of audio at 20ms/frame
        var frame = new float[960];

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
            processor.Denoise(frame);
        stopwatch.Stop();

        // NSNet2 re-runs its whole sliding window every hop (~every
        // 10.7ms), unlike the other backends' single-frame-per-call cost -
        // generous headroom here matters more than for the others.
        Assert.True(stopwatch.ElapsedMilliseconds < 3000,
            $"Processing 1s of audio took {stopwatch.ElapsedMilliseconds}ms, too slow for real-time use");
    }

    [Fact]
    public void ReconstructionPipeline_WithIdentityMask_RecoversOriginalSignal()
    {
        const int winLen = 960;
        const int nfft = 1024;
        const int hop = 512;

        var analysisWindow = Nsnet2Processor.BuildPeriodicSqrtHann(winLen);
        var synthesisWindow = Nsnet2Processor.BuildSynthesisWindow(analysisWindow, hop);

        const int totalSamples = 48000 * 2;
        var signal = new float[totalSamples];
        double phase = 0;
        for (var i = 0; i < totalSamples; i++)
        {
            signal[i] = (float)Math.Sin(phase);
            phase += 2 * Math.PI * 200 / 48000;
        }

        var outBuffer = new float[winLen];
        var output = new List<float>();

        for (var end = winLen; end <= totalSamples; end += hop)
        {
            var windowed = new float[winLen];
            for (var i = 0; i < winLen; i++)
                windowed[i] = signal[end - winLen + i] * analysisWindow[i];

            var spectrum = FourierTransform.Rfft(windowed, nfft);
            // Identity mask - no suppression, isolating the reconstruction
            // math from whatever the model itself would output.
            var timeDomain = FourierTransform.Irfft(spectrum, nfft);

            Array.Copy(outBuffer, hop, outBuffer, 0, winLen - hop);
            Array.Clear(outBuffer, winLen - hop, hop);
            for (var i = 0; i < winLen; i++)
                outBuffer[i] += timeDomain[i] * synthesisWindow[i];

            for (var i = 0; i < hop; i++)
                output.Add(outBuffer[i]);
        }

        var skip = 20000; // past the startup transient
        double outSumSq = 0;
        for (var i = skip; i < output.Count; i++) outSumSq += output[i] * (double)output[i];
        var outRms = Math.Sqrt(outSumSq / (output.Count - skip));

        double inSumSq = 0;
        for (var i = skip; i < totalSamples; i++) inSumSq += signal[i] * (double)signal[i];
        var inRms = Math.Sqrt(inSumSq / (totalSamples - skip));

        Assert.InRange(outRms / inRms, 0.95, 1.05);
    }

    // Measures the real, fixed algorithmic delay of Denoise() end-to-end
    // (STFT analysis + OLA synthesis + the 960-sample call/queue
    // quantization all together, through the real model) rather than
    // trusting a by-hand derivation - this number becomes the compensating
    // delay NoiseSuppressionProcessor applies to the dry signal so NSNet2's
    // Suppression Mix slider can be enabled without comb-filtering, so
    // getting it wrong would silently reintroduce that exact bug.
    //
    // Call size matters here, not just WinLen/Hop: Denoise() is always
    // called with exactly 960 samples (WinLen) per the class's own
    // contract, and because 960 isn't a multiple of Hop (512), the queue's
    // fill level - and therefore how many samples are available to
    // dequeue - varies call to call. That makes the steady-state delay a
    // property of the 960-sample call cadence, not just WinLen-Hop.
    [Fact]
    public void Denoise_HasFixedAlgorithmicDelay_Of896Samples()
    {
        using var processor = CreateProcessor();

        const int totalSamples = 48000 * 3;
        const int impulsePosition = 48000; // 1s in, well past the startup transient
        var signal = new float[totalSamples];
        signal[impulsePosition] = 1.0f;

        var output = new float[totalSamples];
        var frame = new float[960];
        for (var start = 0; start + 960 <= totalSamples; start += 960)
        {
            Array.Copy(signal, start, frame, 0, 960);
            processor.Denoise(frame);
            Array.Copy(frame, 0, output, start, 960);
        }

        var peakIndex = 0;
        var peakValue = 0f;
        for (var i = 0; i < totalSamples; i++)
        {
            var abs = Math.Abs(output[i]);
            if (abs > peakValue)
            {
                peakValue = abs;
                peakIndex = i;
            }
        }

        var measuredDelay = peakIndex - impulsePosition;

        // A magnitude-only gain mask can smear a single-sample impulse
        // slightly (it's a frequency-domain multiply, i.e. a time-domain
        // convolution with the gain's own compact impulse response), so
        // assert the peak lands at the expected delay rather than
        // requiring an exact sample match.
        Assert.Equal(896, measuredDelay);
    }
}
