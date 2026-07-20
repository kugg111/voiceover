using Voiceover.Client.Services;

namespace Client.Tests;

public class SileroVadProcessorTests
{
    private static string ModelPath => Path.Combine(AppContext.BaseDirectory, "silero_vad.onnx");

    private static SileroVadProcessor CreateProcessor() => new(ModelPath);

    // Verified against the real model rather than trusted from docs alone -
    // see the class this backs (SileroVadProcessor) for how these get used.
    [Fact]
    public void Model_HasExpectedInputOutputNames()
    {
        using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(ModelPath);

        Assert.Equal(new[] { "input", "state", "sr" }, session.InputMetadata.Keys);
        Assert.Equal(new[] { "output", "stateN" }, session.OutputMetadata.Keys);
        Assert.Equal(new[] { 2, -1, 128 }, session.InputMetadata["state"].Dimensions);
    }

    [Fact]
    public void ProcessFrame_ProcessesFramesWithoutThrowing()
    {
        using var processor = CreateProcessor();
        var frame = new short[960];

        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 50; i++) processor.ProcessFrame(frame);
        });

        Assert.Null(exception);
    }

    // Digital silence is the one input this can be tested against without
    // needing a real speech corpus - a real speech detector should
    // confidently NOT call it speech. Doesn't (and can't, without actual
    // recorded speech) verify the positive case.
    [Fact]
    public void ProcessFrame_ReportsLowConfidenceForSilence()
    {
        using var processor = CreateProcessor();
        var frame = new short[960]; // all zeros

        // Several seconds' worth so the GRU state settles past its
        // zero-initialized start rather than judging a single cold call.
        for (var i = 0; i < 250; i++) processor.ProcessFrame(frame);

        Assert.False(processor.IsSpeechConfident);
        Assert.True(processor.LastSpeechProbability < 0.2f,
            $"Expected low speech probability for silence, got {processor.LastSpeechProbability:F3}");
    }

    [Fact]
    public void Reset_ClearsStateAndPendingDecision()
    {
        using var processor = CreateProcessor();
        var frame = new short[960];
        for (var i = 0; i < 10; i++) processor.ProcessFrame(frame);

        processor.Reset();

        Assert.False(processor.IsSpeechConfident);
        Assert.Equal(0f, processor.LastSpeechProbability);
    }
}
