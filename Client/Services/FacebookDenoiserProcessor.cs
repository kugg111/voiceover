using System.Runtime.InteropServices;

namespace Voiceover.Client.Services;

// Wraps native/denoiser_wrapper.dll - a thin C++ host (LibTorch) that
// runs the TorchScript-exported streaming Facebook Denoiser model (see the
// dev-only export tooling this was produced with; not part of this repo).
// Real incremental streaming inference, not a "reprocess the window from
// scratch" approach the way Nsnet2Processor works - this model is heavy
// enough that reprocessing would not keep up with real-time capture (see
// the export tooling's own notes on why). The model runs natively at
// 16kHz, so this class owns the 48kHz<->16kHz resampling and the sliding
// "pending" buffer its per-hop contract needs.
internal class FacebookDenoiserProcessor : IDisposable
{
    private const string DllName = "denoiser_wrapper.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Denoiser_Create([MarshalAs(UnmanagedType.LPUTF8Str)] string modelPathUtf8);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Denoiser_Reset(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Denoiser_ProcessHop(IntPtr handle, float[] frame, int frameLength,
                                                    float[] output, int outputCapacity);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Denoiser_Destroy(IntPtr handle);

    // Must match the exported model's own constants exactly (see the
    // export script: total_length=661, stride=256 for dns48 with the
    // default resample_lookahead=64/resample_buffer=256 - a different
    // pretrained checkpoint or streamer config would need these
    // recomputed, not just the .pt file swapped).
    private const int TotalLength = 661;
    private const int Stride = 256;

    private readonly IntPtr _handle;
    private readonly Resampler48kTo16k _downsampler = new();
    private readonly Resampler16kTo48k _upsampler = new();

    // Cuts wind rumble before it ever reaches the model - see HighPassFilter's
    // own header for why. 100Hz sits comfortably below typical speech
    // fundamentals (even a deep male voice's F0 rarely dips under ~85Hz)
    // while still meaningfully attenuating wind noise, which concentrates
    // well below that.
    private readonly HighPassFilter _windRumbleFilter = new(sampleRate: 48000, cutoffHz: 100f);

    // Raw 16kHz samples waiting for enough context to fill one hop -
    // mirrors DemucsStreamer.feed()'s own "pending" accumulator, just
    // owned here instead of inside the scripted model (see the export
    // tooling's per-hop contract: exactly TotalLength samples in, Stride
    // samples out, sliding forward by Stride not TotalLength).
    private readonly List<float> _pending16k = new();

    // Denoised 16kHz samples produced faster than they're needed per
    // 20ms/960-sample call (Stride=256 doesn't evenly divide the
    // 320-sample-per-call cadence at 16kHz) - queued here and drained a
    // fixed amount per call so output stays smooth instead of chunky.
    private readonly Queue<float> _output16k = new();

    private readonly float[] _hopScratch = new float[TotalLength];
    private readonly float[] _hopOutputScratch = new float[Stride];

    public FacebookDenoiserProcessor(string modelPath)
    {
        _handle = Denoiser_Create(modelPath);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to load Facebook Denoiser model");
    }

    public void Reset()
    {
        Denoiser_Reset(_handle);
        _pending16k.Clear();
        _output16k.Clear();
        _downsampler.Reset();
        _upsampler.Reset();
        _windRumbleFilter.Reset();
    }

    // Denoises one 20ms/960-sample @ 48kHz frame in place. Produces
    // silence for the first ~41ms (TotalLength/16000) while the sliding
    // window fills - matching how every other backend's own startup
    // transient is handled (NoiseSuppressionProcessor's delay-compensated
    // dry buffer, sized to this backend's own measured algorithmic delay,
    // covers exactly this the same way it already does for NSNet2).
    public void Process(short[] pcm)
    {
        // In place, before anything else touches pcm - safe because pcm gets
        // fully overwritten by the model's own output at the end of this
        // method regardless (see the final loop below), and because
        // NoiseSuppressionProcessor already captured its own untouched copy
        // of the true raw mic signal for the dry/wet blend before calling
        // into this backend at all, so filtering the model's input here
        // doesn't change what the user hears when SuppressionMix blends back
        // toward "dry".
        _windRumbleFilter.Process(pcm);

        var down = _downsampler.Process(pcm); // pcm.Length/3 samples @ 16kHz
        _pending16k.AddRange(down);

        while (_pending16k.Count >= TotalLength)
        {
            _pending16k.CopyTo(0, _hopScratch, 0, TotalLength);
            var written = Denoiser_ProcessHop(_handle, _hopScratch, TotalLength, _hopOutputScratch, Stride);
            if (written > 0)
                for (var i = 0; i < written; i++) _output16k.Enqueue(_hopOutputScratch[i]);
            _pending16k.RemoveRange(0, Stride);
        }

        var outCount = pcm.Length / 3;
        var drained = new float[outCount];
        for (var i = 0; i < outCount; i++)
            drained[i] = _output16k.Count > 0 ? _output16k.Dequeue() : 0f;

        var up = _upsampler.Process(drained); // back to pcm.Length samples @ 48kHz
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(up[i] * short.MaxValue, short.MinValue, short.MaxValue);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) Denoiser_Destroy(_handle);
    }
}
