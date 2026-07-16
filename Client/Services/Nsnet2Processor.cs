using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Voiceover.Client.Services;

// NSNet2, an alternative noise-suppression engine to RNNoise - Microsoft's
// ICASSP 2021 DNS Challenge baseline, 48kHz variant. The model file itself
// is "other content" under microsoft/DNS-Challenge's repo license (code is
// separately MIT), licensed under CC-BY 4.0 - this comment is that
// attribution. Vendored from
// https://github.com/microsoft/DNS-Challenge/tree/nsnset_48khz
// /NSNet2-baseline/nsnet2-20ms-48k-baseline.onnx
//
// Unlike RNNoise, the published model has no externally threaded recurrent
// state (verified via the actual ONNX graph metadata, not just the
// reference script - see Nsnet2ProcessorTests) - it was exported for
// offline whole-file enhancement, where the GRU layers build up their own
// context across a single big inference call. Feeding it one frame at a
// time with no memory between calls would defeat the model entirely (a
// recurrent network with no cross-call memory is just guessing per-frame).
// The compromise here: keep a sliding window of the last WindowFrames
// analysis frames and re-run the *whole window* through the model on every
// new hop, only using the newest frame's output - this gives the GRU
// layers real (if bounded, re-computed-each-time) temporal context, at the
// cost of real added latency and repeated inference that RNNoise doesn't
// have. That cost is deliberately isolated entirely inside this class (its
// own buffers, its own history, nothing shared with the other backend) so
// selecting NSNet2 can't affect RNNoise when that's selected instead - see
// NoiseSuppressionProcessor's dispatch, which only ever touches one
// backend's state per frame.
//
// Two CPU-cost traps specific to this "reprocess the window" design, both
// fixed here rather than accepted as the price of the workaround:
//
// 1. A frame's STFT (windowing + FFT + log-power) never changes once
//    computed - only the model re-run is inherently repeated, not the
//    analysis feeding it. _spectrumWindow/_featureWindow cache the last
//    WindowFrames frames so each hop computes exactly one new frame's STFT
//    (shifting the rest down) instead of redoing all WindowFrames of them -
//    a ~16x cut to the FFT/feature cost per hop.
// 2. InferenceSession defaults to one thread per physical core with busy-
//    spin waiting between calls (ONNX Runtime's default threading policy,
//    tuned for large/batched inference) - for a model this small invoked
//    every ~10.7ms, that mostly manifests as idle worker threads spinning,
//    not real work: on an 8-core/16-thread thread CPU that's up to 8
//    threads spinning almost continuously, i.e. up to ~50% total CPU, which
//    is exactly the symptom this was fixed for. Forcing single-threaded
//    execution (no separate thread pool spun up at all for a workload this
//    small) removes that overhead entirely without slowing inference down.
internal class Nsnet2Processor : IDisposable
{
    private const int WinLen = 960; // 20ms @ 48kHz - matches this app's own frame size
    private const int Nfft = 1024;
    private const int Hop = 512; // Nfft * hopfrac(0.5), per the reference implementation
    private const int FftBins = Nfft / 2 + 1; // 513
    private const double MinGainDb = -80;
    private const int WindowFrames = 16; // ~171ms of context (16 * Hop / 48000)

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly float[] _analysisWindow;
    private readonly float[] _synthesisWindow; // the pseudo-inverse "dual" window, not the same as the analysis window

    // Raw samples needed to reconstruct the last WindowFrames analysis
    // frames: (WindowFrames - 1) hops plus one full window. A plain List
    // rather than a Queue since hop windows need indexed slicing, not
    // FIFO dequeuing - _historyStart tracks the absolute stream position
    // of _rawHistory[0] so hop boundaries (computed in absolute terms)
    // can be mapped back to indices into this trimmed buffer.
    private readonly List<float> _rawHistory = new();
    private long _historyStart;
    private readonly int _rawHistoryTarget = (WindowFrames - 1) * Hop + WinLen;

    private long _totalReceived;
    private long _hopsProcessed;

    private readonly float[] _outBuffer = new float[WinLen];
    private readonly Queue<float> _pendingOutput = new();

    // Rolling cache of the last (up to) WindowFrames frames' spectra/log-
    // power features, oldest-first - see the class header's cost trap #1.
    // Always filled contiguously from index 0, so the first
    // _framesFilled*FftBins entries of _featureWindow are exactly the
    // model's input for the current hop, with no copy needed.
    private readonly Complex[][] _spectrumWindow = new Complex[WindowFrames][];
    private readonly float[] _featureWindow = new float[WindowFrames * FftBins];
    private int _framesFilled;

    // Reused across hops instead of allocated fresh each time - this runs
    // roughly every 10.7ms for the lifetime of a voice call.
    private readonly float[] _windowedScratch = new float[WinLen];
    private readonly Complex[] _estimatedSpectrumScratch = new Complex[FftBins];

    public Nsnet2Processor(string modelPath)
    {
        // See the class header's cost trap #2 - this model's per-call
        // workload is far too small to benefit from ONNX Runtime's default
        // one-thread-per-physical-core pool, so force single-threaded,
        // no-separate-thread-pool execution instead.
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1
        };
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _analysisWindow = BuildPeriodicSqrtHann(WinLen);
        _synthesisWindow = BuildSynthesisWindow(_analysisWindow, Hop);
    }

    // Denoises exactly 960 samples (20ms @ 48kHz) in place, matching every
    // other backend's ProcessFrame contract.
    public void Denoise(float[] pcm)
    {
        _rawHistory.AddRange(pcm);
        _totalReceived += pcm.Length;

        // Hop h's analysis window ends at absolute sample position
        // WinLen + h*Hop - process every hop that's now fully available,
        // in order, each using a distinct (progressively newer) window
        // position rather than always re-reading "the end of the buffer"
        // (which wouldn't change across iterations within the same call).
        while (_totalReceived >= WinLen + _hopsProcessed * Hop)
        {
            var windowEndAbsolute = WinLen + _hopsProcessed * Hop;
            ProcessOneHop(windowEndAbsolute);
            _hopsProcessed++;
        }

        // Trim now that every hop ready this call has been processed -
        // keep only what future hops could still need.
        var keepFrom = _totalReceived - _rawHistoryTarget;
        if (keepFrom > _historyStart)
        {
            var trimCount = (int)(keepFrom - _historyStart);
            if (trimCount > 0 && trimCount <= _rawHistory.Count)
            {
                _rawHistory.RemoveRange(0, trimCount);
                _historyStart += trimCount;
            }
        }

        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = _pendingOutput.Count > 0 ? _pendingOutput.Dequeue() : 0f;
    }

    private void ProcessOneHop(long windowEndAbsolute)
    {
        // windowEndAbsolute always lands exactly one hop past the previous
        // call's (see the framesAvailable derivation this replaced -
        // 1 + hopsProcessed - which only ever grows by exactly 1 per hop),
        // so only the newest frame's STFT needs computing here; every
        // older frame already sits in _spectrumWindow/_featureWindow from
        // an earlier hop. See the class header's cost trap #1.
        var frameStartRelative = (int)(windowEndAbsolute - WinLen - _historyStart);
        for (var i = 0; i < WinLen; i++)
            _windowedScratch[i] = _rawHistory[frameStartRelative + i] * _analysisWindow[i];

        var spectrum = FourierTransform.Rfft(_windowedScratch, Nfft);

        if (_framesFilled == WindowFrames)
        {
            // At capacity - shift every frame down one slot to drop the
            // oldest, matching a FIFO of exactly WindowFrames frames.
            Array.Copy(_spectrumWindow, 1, _spectrumWindow, 0, WindowFrames - 1);
            Array.Copy(_featureWindow, FftBins, _featureWindow, 0, (WindowFrames - 1) * FftBins);
        }
        else
        {
            _framesFilled++;
        }

        var slot = _framesFilled - 1;
        _spectrumWindow[slot] = spectrum;
        for (var b = 0; b < FftBins; b++)
        {
            var power = spectrum[b].Real * spectrum[b].Real + spectrum[b].Imaginary * spectrum[b].Imaginary;
            _featureWindow[slot * FftBins + b] = (float)Math.Log10(Math.Max(power, 1e-12));
        }

        var framesAvailable = _framesFilled;
        var mask = RunModel(framesAvailable);

        // Only the newest frame's output is new information - the rest of
        // the window was already emitted on previous hops.
        var newestFrame = framesAvailable - 1;
        var minGain = (float)Math.Pow(10, MinGainDb / 20);
        var newestSpectrum = _spectrumWindow[newestFrame];
        for (var b = 0; b < FftBins; b++)
        {
            var gain = Math.Clamp(mask[newestFrame * FftBins + b], minGain, 1.0f);
            _estimatedSpectrumScratch[b] = newestSpectrum[b] * gain;
        }

        var timeDomain = FourierTransform.Irfft(_estimatedSpectrumScratch, Nfft);
        // The analysis window zero-padded WinLen samples up to Nfft before
        // the FFT - only the first WinLen samples of the reconstruction
        // correspond to real windowed content.

        // Overlap-add using the pseudo-inverse synthesis window, not the
        // analysis window - see BuildSynthesisWindow for why a plain
        // matching window wouldn't reconstruct correctly at this
        // window/hop ratio.
        Array.Copy(_outBuffer, Hop, _outBuffer, 0, WinLen - Hop);
        Array.Clear(_outBuffer, WinLen - Hop, Hop);
        for (var i = 0; i < WinLen; i++)
            _outBuffer[i] += timeDomain[i] * _synthesisWindow[i];

        for (var i = 0; i < Hop; i++)
            _pendingOutput.Enqueue(_outBuffer[i]);
    }

    private float[] RunModel(int frames)
    {
        // _featureWindow is always filled contiguously from index 0 (see
        // ProcessOneHop), so the first frames*FftBins entries are exactly
        // the current window - sliced via Memory<float> to avoid copying
        // it into a fresh array on every hop.
        var inputTensor = new DenseTensor<float>(new Memory<float>(_featureWindow, 0, frames * FftBins), new[] { 1, frames, FftBins });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) });
        return results.First().AsEnumerable<float>().ToArray();
    }

    // sqrt(Hann(N, periodic=True)) - matches scipy.signal.windows.hann(N,
    // sym=False) in the reference implementation. "Periodic" (sym=False)
    // computes the window as if it were one period longer, then drops the
    // last sample - different from the textbook symmetric Hann window
    // scipy/numpy give you by default.
    internal static float[] BuildPeriodicSqrtHann(int n)
    {
        var window = new float[n];
        for (var i = 0; i < n; i++)
        {
            var hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n); // sym=False: divide by N, not N-1
            window[i] = (float)Math.Sqrt(hann);
        }
        return window;
    }

    // The reference implementation doesn't reuse the analysis window for
    // synthesis - it solves for the minimum-norm dual window via
    // np.linalg.pinv per hop-offset, since WinLen (960) isn't an exact
    // multiple of Hop (512): each output sample position is covered by
    // either 1 or 2 overlapping analysis frames (ceil(960/512) = 2), and a
    // plain constant-overlap-add normalization only handles the case
    // where every position is covered the same number of times. For a
    // single contributing sample (magnitude h), the least-squares dual is
    // 1/h; for two (h1, h2), it's h_i / (h1^2 + h2^2) - which is exactly
    // what a 1x1 or 2x1 Moore-Penrose pseudo-inverse reduces to, spelled
    // out directly here instead of pulling in a general linear-algebra
    // solve for a problem this small.
    internal static float[] BuildSynthesisWindow(float[] analysisWindow, int hop)
    {
        var n = analysisWindow.Length;
        var synthesis = new float[n];
        for (var k = 0; k < hop; k++)
        {
            var indices = new List<int>();
            for (var idx = k; idx < n; idx += hop)
                indices.Add(idx);

            if (indices.Count == 1)
            {
                var h = analysisWindow[indices[0]];
                synthesis[indices[0]] = h == 0 ? 0 : 1f / h;
            }
            else
            {
                double sumSquares = 0;
                foreach (var idx in indices) sumSquares += analysisWindow[idx] * (double)analysisWindow[idx];
                foreach (var idx in indices)
                    synthesis[idx] = sumSquares == 0 ? 0 : (float)(analysisWindow[idx] / sumSquares);
            }
        }
        return synthesis;
    }

    public void Dispose() => _session.Dispose();
}
