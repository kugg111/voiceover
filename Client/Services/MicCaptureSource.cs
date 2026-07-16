using LiveKit.Rtc;
using NAudio.Wave;

namespace Voiceover.Client.Services;

// Captures the mic, boosts it, runs it through real noise suppression, and
// hands processed frames to a LiveKit AudioSource for publishing. Used to be
// half of OpusAudioEndPoint - LiveKit's own engine now owns Opus encoding
// and jitter buffering, so this class only does what's still actually this
// app's job: NAudio device capture and handing frames to a
// NoiseSuppressionProcessor (gain + noise suppression - RNNoise or NSNet2,
// not a hand-rolled RMS gate - see the PR that introduced it for why that
// mattered). The processor is its own class so the Settings "Test Mic"
// preview can share the exact same frame-processing logic instead of
// duplicating it.
public class MicCaptureSource : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameDurationMs = 20;
    private const int SamplesPerFrame = SampleRate / 1000 * FrameDurationMs; // 960

    public float MicGain { get => _processor.MicGain; set => _processor.MicGain = value; }
    public bool MicMuted { get; set; }
    public bool NoiseSuppressionEnabled { get => _processor.Enabled; set => _processor.Enabled = value; }
    public NoiseSuppressionBackend NoiseSuppressionBackend { get => _processor.Backend; set => _processor.Backend = value; }
    public float SuppressionMix { get => _processor.SuppressionMix; set => _processor.SuppressionMix = value; }

    // Fires with the fully processed frame (post noise-suppression, post-
    // gain) right before it's handed to LiveKit - used for the local
    // speaking-indicator detection in VoiceService, so it reacts to the same
    // signal that's actually published rather than the raw mic input.
    public event Action<short[]>? OnProcessedFrame;

    public AudioSource Source { get; }

    private readonly int _inputDeviceIndex;
    private WaveInEvent? _waveIn;
    private readonly List<short> _captureAccumulator = new();

    // Always constructed (not gated behind the device-validity checks
    // below) so every property above stays safely settable even when this
    // machine has no usable input device - VoiceService relies on that,
    // same as before this class delegated to a separate processor.
    private readonly NoiseSuppressionProcessor _processor = new();

    public MicCaptureSource(int inputDeviceIndex)
    {
        _inputDeviceIndex = inputDeviceIndex;

        Source = new AudioSource(SampleRate, Channels, 1000);

        if (WaveInEvent.DeviceCount == 0) return;
        if (_inputDeviceIndex >= 0 && _inputDeviceIndex >= WaveInEvent.DeviceCount) return;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = _inputDeviceIndex,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = FrameDurationMs,
            NumberOfBuffers = 2
        };
        _waveIn.DataAvailable += OnMicDataAvailable;
        _waveIn.StartRecording();
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (MicMuted)
        {
            // Drop whatever was mid-accumulation rather than let it sit and
            // get sent as a stale burst the moment the mic is unmuted.
            _captureAccumulator.Clear();
            return;
        }

        int sampleCount = args.BytesRecorded / 2;
        var incoming = new short[sampleCount];
        Buffer.BlockCopy(args.Buffer, 0, incoming, 0, args.BytesRecorded);
        _captureAccumulator.AddRange(incoming);

        // NAudio's actual callback buffer size isn't guaranteed to line up
        // exactly with a clean frame boundary - accumulate and slice off
        // exact 20ms/960-sample frames rather than assuming each callback
        // already is one.
        while (_captureAccumulator.Count >= SamplesPerFrame)
        {
            var frame = _captureAccumulator.GetRange(0, SamplesPerFrame).ToArray();
            _captureAccumulator.RemoveRange(0, SamplesPerFrame);

            _processor.ProcessFrame(frame);

            OnProcessedFrame?.Invoke(frame);

            var audioFrame = new AudioFrame(frame, SampleRate, Channels, SamplesPerFrame, null);
            Source.CaptureFrame(audioFrame);
        }
    }

    public void Dispose()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnMicDataAvailable;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }
        _processor.Dispose();
        Source.Dispose();
    }
}
