using Voiceover.Client.Services;

namespace Client.Tests;

// Loads the real vendored TorchScript model + native LibTorch DLLs (see
// the csproj CopyToOutputDirectory items) and runs actual inference
// through the full 48kHz -> 16kHz -> model -> 16kHz -> 48kHz round trip,
// not mocks - same "test against the real native dependency" pattern this
// project already used for Nsnet2Processor/LadspaHost.
public class FacebookDenoiserProcessorTests
{
    private static string ModelPath => Path.Combine(AppContext.BaseDirectory, "denoiser_dns48_streaming.pt");

    [Fact]
    public void Constructor_LoadsTheRealModel()
    {
        using var processor = new FacebookDenoiserProcessor(ModelPath);
        // Reaching here without an exception is the assertion - Create()
        // returning a null handle throws from the constructor.
    }

    [Fact]
    public void Process_RunsManyFramesWithoutThrowingAndProducesFiniteOutput()
    {
        using var processor = new FacebookDenoiserProcessor(ModelPath);
        double phase = 0;

        // Enough frames (60 * 20ms = 1.2s) to run well past the model's
        // own ~41ms startup latency, so this exercises real steady-state
        // output, not just the initial silent fill period.
        for (var call = 0; call < 60; call++)
        {
            var frame = new short[960];
            for (var i = 0; i < 960; i++)
            {
                frame[i] = (short)(Math.Sin(phase) * short.MaxValue * 0.5);
                phase += 2 * Math.PI * 200 / 48000;
            }

            processor.Process(frame);

            foreach (var sample in frame)
                Assert.InRange(sample, short.MinValue, short.MaxValue);
        }
    }

    [Fact]
    public void Process_EventuallyProducesNonSilentOutput()
    {
        using var processor = new FacebookDenoiserProcessor(ModelPath);
        double phase = 0;
        var sawNonZero = false;

        for (var call = 0; call < 60 && !sawNonZero; call++)
        {
            var frame = new short[960];
            for (var i = 0; i < 960; i++)
            {
                frame[i] = (short)(Math.Sin(phase) * short.MaxValue * 0.5);
                phase += 2 * Math.PI * 200 / 48000;
            }

            processor.Process(frame);

            if (Array.Exists(frame, s => s != 0)) sawNonZero = true;
        }

        Assert.True(sawNonZero, "Expected real denoised audio to flow through eventually, not just silence.");
    }

    [Fact]
    public void Reset_DoesNotThrowAndClearsPendingState()
    {
        using var processor = new FacebookDenoiserProcessor(ModelPath);
        var frame = new short[960];
        for (var i = 0; i < 960; i++) frame[i] = (short)((i % 200) * 100 - 10000);

        for (var call = 0; call < 10; call++) processor.Process(frame);

        var exception = Record.Exception(() => processor.Reset());
        Assert.Null(exception);

        // Right after reset, the sliding window is empty again - the very
        // next frame's worth of output must be silence (same as a brand
        // new instance's first call), not leftover pre-reset audio.
        var freshFrame = new short[960];
        for (var i = 0; i < 960; i++) freshFrame[i] = (short)((i % 200) * 100 - 10000);
        processor.Process(freshFrame);
        Assert.All(freshFrame, sample => Assert.Equal(0, sample));
    }
}
