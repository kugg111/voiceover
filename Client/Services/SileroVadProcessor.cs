using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Voiceover.Client.Services;

// Silero VAD (voice activity detection) - MIT licensed, vendored verbatim
// from https://github.com/snakers4/silero-vad (see Client.csproj). Used to
// pre-gate the mic in NoiseSuppressionProcessor: confidently-silent
// stretches get muted before they ever reach RNNoise/NSNet2, catching
// residual noise a suppressor alone lets through.
//
// The model only accepts 16kHz (or 8kHz) audio in fixed 512-sample chunks
// (32ms at 16kHz), each preceded by a 64-sample "context" tail carried
// over from the end of the previous chunk - not a hop-buffer scheme like
// Nsnet2Processor's, but a simpler explicit recurrent state (2x1x128
// floats) threaded between calls, verified against the real model's
// input/output names in SileroVadProcessorTests rather than trusted from
// the reference Python source alone. This app's own pipeline calls with
// 960 samples/20ms @ 48kHz, so a Resampler48kTo16k bridges the two: every
// call adds exactly 320 new 16kHz samples (960/3), accumulated here until
// there's enough for another 512-sample inference - the same "accumulate
// until you have enough, then infer, keep the leftover" shape
// Nsnet2Processor already uses for its own hop processing.
internal class SileroVadProcessor : IDisposable
{
    private const int NumSamples = 512;
    private const int ContextSize = 64;
    private const int SampleRate = 16000;
    private const int StateSize = 2 * 1 * 128;

    private readonly InferenceSession _session;
    private readonly Resampler48kTo16k _resampler = new();
    private readonly List<float> _pending16k = new();
    private readonly float[] _context = new float[ContextSize];
    private readonly float[] _state = new float[StateSize];
    private readonly DenseTensor<long> _srTensor = new(new[] { (long)SampleRate }, Array.Empty<int>());

    public float Threshold { get; set; } = 0.5f; // Silero's own documented default

    // Last known decision - naturally lags up to ~32ms behind the newest
    // audio (chunked VAD), updated whenever enough 16kHz audio has
    // accumulated for another inference, which isn't every call.
    public bool IsSpeechConfident { get; private set; }
    public float LastSpeechProbability { get; private set; }

    public SileroVadProcessor(string modelPath)
    {
        var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(modelPath, options);
    }

    public void ProcessFrame(short[] pcm48k)
    {
        _pending16k.AddRange(_resampler.Process(pcm48k));

        while (_pending16k.Count >= NumSamples)
        {
            var chunk = new float[ContextSize + NumSamples];
            Array.Copy(_context, chunk, ContextSize);
            _pending16k.CopyTo(0, chunk, ContextSize, NumSamples);

            RunInference(chunk);

            Array.Copy(chunk, chunk.Length - ContextSize, _context, 0, ContextSize);
            _pending16k.RemoveRange(0, NumSamples);
        }
    }

    private void RunInference(float[] chunk)
    {
        var inputTensor = new DenseTensor<float>(chunk, new[] { 1, chunk.Length });
        var stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", _srTensor)
        });

        var output = results.First(r => r.Name == "output").AsEnumerable<float>().ToArray();
        var newState = results.First(r => r.Name == "stateN").AsEnumerable<float>().ToArray();

        LastSpeechProbability = output[0];
        IsSpeechConfident = output[0] >= Threshold;
        Array.Copy(newState, _state, _state.Length);
    }

    // Called when the VAD gate is toggled back on, or the backend/device
    // otherwise changes underneath it - stale context/state from before
    // the gap would otherwise bias the very next few decisions.
    public void Reset()
    {
        Array.Clear(_context);
        Array.Clear(_state);
        _pending16k.Clear();
        IsSpeechConfident = false;
        LastSpeechProbability = 0f;
    }

    public void Dispose() => _session.Dispose();
}
