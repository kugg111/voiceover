using Voiceover.Client.Services;

namespace Client.Tests;

// Focused on the VAD pre-roll gate's structural/timing behavior - the
// "does it correctly recognize speech" question needs a real human talking
// into a real mic (see the manual live-test pass this backs), not
// something a unit test can meaningfully assert without a speech corpus.
public class NoiseSuppressionProcessorTests
{
    [Fact]
    public void VadGate_OutputsSilenceDuringStartupPreRollWindow()
    {
        using var processor = new NoiseSuppressionProcessor
        {
            Enabled = true,
            Backend = NoiseSuppressionBackend.RNNoise,
            VadGateEnabled = true
        };

        // The very first frames must come out silent regardless of input -
        // the pre-roll buffer hasn't accumulated enough history yet to
        // have rendered a mute/unmute verdict on anything.
        var frame = new short[960];
        for (var i = 0; i < 960; i++) frame[i] = short.MaxValue; // loud, unambiguous non-zero input
        processor.ProcessFrame(frame);

        Assert.All(frame, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void VadGateEnabled_Off_LeavesOutputUngated()
    {
        using var processor = new NoiseSuppressionProcessor
        {
            Enabled = true,
            Backend = NoiseSuppressionBackend.RNNoise,
            VadGateEnabled = false
        };

        var frame = new short[960];
        for (var i = 0; i < 960; i++) frame[i] = short.MaxValue;

        var exception = Record.Exception(() => processor.ProcessFrame(frame));

        Assert.Null(exception);
        // Not asserting exact sample values (RNNoise's own processing of a
        // full-scale DC-like signal isn't the point here) - just that
        // nothing forced the whole frame to zero the way the gate does.
        Assert.Contains(frame, sample => sample != 0);
    }
}
