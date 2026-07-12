using System.Runtime.InteropServices;

namespace Voiceover.Client.Services;

// Thin P/Invoke host for exactly one plugin: DeepFilterNet's official
// "DeepFilter Mono" LADSPA plugin (deep_filter_ladspa.dll, vendored from
// https://github.com/Rikorose/DeepFilterNet/releases/tag/v0.5.6 - see
// Client/native/deep_filter_ladspa.dll). Not a general-purpose LADSPA
// host - the port layout below (2 audio + 6 control ports, in this exact
// order) is read straight from the plugin's own source
// (ladspa/src/lib.rs, get_ladspa_descriptor index 0) rather than
// discovered dynamically at runtime, since this only ever loads that one
// plugin.
//
// Struct layout note: this DLL targets x86_64-pc-windows-msvc, where C's
// `unsigned long` (and Rust's libc::c_ulong, which the plugin's own FFI
// layer - github.com/nwoeanhinnogaehr/ladspa.rs - is built on) is 32
// bits, Windows' LLP64 model, unlike Linux/macOS's LP64 where it's 64.
// Getting this wrong would silently misalign every field after
// UniqueID, including the function pointers - verified against both the
// LADSPA spec (ladspa.h) and the ladspa.rs crate's own #[repr(C)] struct
// before writing this.
internal class LadspaHost : IDisposable
{
    private const uint SampleRate = 48000; // matches MicCaptureSource's own SampleRate

    // 480 samples/10ms @ 48kHz - the plugin's own hop_size, and already
    // this app's established sub-chunk size (matches ApplyWebRtcApm/
    // ApplyRNNoise). Calling run() with any count is technically fine -
    // the plugin queues internally on its own worker thread - but
    // matching hop_size keeps data flowing through in lockstep with the
    // rest of the pipeline.
    public const int FrameSamples = 480;

    [StructLayout(LayoutKind.Sequential)]
    private struct LadspaDescriptor
    {
        public uint UniqueId;
        public IntPtr Label;
        public int Properties;
        public IntPtr Name;
        public IntPtr Maker;
        public IntPtr Copyright;
        public uint PortCount;
        public IntPtr PortDescriptors;
        public IntPtr PortNames;
        public IntPtr PortRangeHints;
        public IntPtr ImplementationData;
        public IntPtr Instantiate;
        public IntPtr ConnectPort;
        public IntPtr Activate;
        public IntPtr Run;
        public IntPtr RunAdding;
        public IntPtr SetRunAddingGain;
        public IntPtr Deactivate;
        public IntPtr Cleanup;
    }

    private delegate IntPtr InstantiateFn(IntPtr descriptor, uint sampleRate);
    private delegate void ConnectPortFn(IntPtr instance, uint port, IntPtr dataLocation);
    private delegate void ActivateFn(IntPtr instance);
    private delegate void RunFn(IntPtr instance, uint sampleCount);
    private delegate void DeactivateFn(IntPtr instance);
    private delegate void CleanupFn(IntPtr instance);

    [DllImport("deep_filter_ladspa.dll")]
    private static extern IntPtr ladspa_descriptor(uint index);

    private readonly IntPtr _instance;
    private readonly InstantiateFn _instantiate;
    private readonly ConnectPortFn _connectPort;
    private readonly ActivateFn? _activate;
    private readonly RunFn _run;
    private readonly DeactivateFn? _deactivate;
    private readonly CleanupFn _cleanup;

    // Pinned native buffers the plugin reads/writes directly - connected
    // once at construction (LADSPA allows reconnecting per-call, but a
    // fixed buffer overwritten each frame is simpler and is exactly how
    // real-time LADSPA hosts normally operate).
    private readonly IntPtr _audioInBuffer;
    private readonly IntPtr _audioOutBuffer;
    private readonly List<IntPtr> _controlBuffers = new();

    public LadspaHost()
    {
        var descPtr = ladspa_descriptor(0); // index 0 = mono, see get_ladspa_descriptor
        if (descPtr == IntPtr.Zero)
            throw new InvalidOperationException("deep_filter_ladspa.dll returned no descriptor for index 0.");

        var desc = Marshal.PtrToStructure<LadspaDescriptor>(descPtr);

        _instantiate = Marshal.GetDelegateForFunctionPointer<InstantiateFn>(desc.Instantiate);
        _connectPort = Marshal.GetDelegateForFunctionPointer<ConnectPortFn>(desc.ConnectPort);
        _activate = desc.Activate == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<ActivateFn>(desc.Activate);
        _run = Marshal.GetDelegateForFunctionPointer<RunFn>(desc.Run);
        _deactivate = desc.Deactivate == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<DeactivateFn>(desc.Deactivate);
        _cleanup = Marshal.GetDelegateForFunctionPointer<CleanupFn>(desc.Cleanup);

        _instance = _instantiate(descPtr, SampleRate);
        if (_instance == IntPtr.Zero)
            throw new InvalidOperationException("DeepFilter LADSPA plugin failed to instantiate.");

        _audioInBuffer = Marshal.AllocHGlobal(FrameSamples * sizeof(float));
        _audioOutBuffer = Marshal.AllocHGlobal(FrameSamples * sizeof(float));

        // Port order and default values read straight from the plugin's
        // own source (ladspa/src/lib.rs): 0=Audio In, 1=Audio Out,
        // 2=Attenuation Limit, 3=Min processing threshold, 4=Max ERB
        // threshold, 5=Max DF threshold, 6=Min Processing Buffer,
        // 7=Post Filter Beta. Each control default below is the plugin
        // author's own declared DefaultValue::Maximum/Minimum resolved
        // against that port's own min/max range - fixed here rather than
        // exposed as user-tunable settings.
        _connectPort(_instance, 0, _audioInBuffer);
        _connectPort(_instance, 1, _audioOutBuffer);
        ConnectFixedControl(2, 100f);  // Attenuation Limit (dB) - Maximum
        ConnectFixedControl(3, -15f);  // Min processing threshold (dB) - Minimum
        ConnectFixedControl(4, 35f);   // Max ERB processing threshold (dB) - Maximum
        ConnectFixedControl(5, 35f);   // Max DF processing threshold (dB) - Maximum
        ConnectFixedControl(6, 0f);    // Min Processing Buffer (frames) - Minimum
        ConnectFixedControl(7, 0f);    // Post Filter Beta - Minimum

        _activate?.Invoke(_instance);
    }

    private void ConnectFixedControl(uint port, float value)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(float));
        Marshal.Copy(new[] { value }, 0, buffer, 1);
        _controlBuffers.Add(buffer);
        _connectPort(_instance, port, buffer);
    }

    // Denoises exactly FrameSamples (480) normalized (-1..1) float
    // samples in place.
    public void Denoise(float[] frame)
    {
        Marshal.Copy(frame, 0, _audioInBuffer, FrameSamples);
        _run(_instance, FrameSamples);
        Marshal.Copy(_audioOutBuffer, frame, 0, FrameSamples);
    }

    public void Dispose()
    {
        _deactivate?.Invoke(_instance);
        _cleanup(_instance);
        Marshal.FreeHGlobal(_audioInBuffer);
        Marshal.FreeHGlobal(_audioOutBuffer);
        foreach (var buffer in _controlBuffers) Marshal.FreeHGlobal(buffer);
    }
}
