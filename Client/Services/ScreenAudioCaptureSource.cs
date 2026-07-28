using LiveKit.Rtc;
using NAudio.Wave;

namespace Voiceover.Client.Services;

// Captures the system's default playback device via WASAPI loopback (game/
// video/music audio actually playing on this machine) and exposes it as a
// LiveKit AudioSource, published as a second track alongside the screen-
// share video track (see VoiceService.StartScreenShareAsync) - "screen share
// with sound", matching what a real desktop-capture app gives viewers.
//
// Best-effort by design, same as MicCaptureSource's optional noise-
// suppression backends: WasapiLoopbackCapture can fail to construct in odd
// audio-driver configurations (no default render device, exclusive-mode
// device in use elsewhere), and that must never block starting a screen
// share - it just means the share goes out video-only, same as before this
// feature existed.
public class ScreenAudioCaptureSource : IDisposable
{
    private const int FrameDurationMs = 20;

    public AudioSource Source { get; }

    private readonly WasapiLoopbackCapture? _capture;
    private readonly int _sampleRate;
    private readonly int _channels;

    // Total interleaved samples per 20ms frame (samplesPerChannel * channels) -
    // WasapiLoopbackCapture always mixes down to the device's own mix format
    // (typically 48kHz/2ch IEEE float), not a fixed one this class picks, so
    // this is computed from whatever that turns out to be.
    private readonly int _samplesPerFrame;
    private readonly List<short> _accumulator = new();

    public ScreenAudioCaptureSource()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
        }
        catch
        {
            _capture = null;
        }

        _sampleRate = _capture?.WaveFormat.SampleRate ?? 48000;
        _channels = _capture?.WaveFormat.Channels ?? 2;
        _samplesPerFrame = (_sampleRate / 1000 * FrameDurationMs) * _channels;

        Source = new AudioSource(_sampleRate, _channels, 1000);

        if (_capture is null) return;

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        // WasapiLoopbackCapture always hands back IEEE float samples
        // regardless of the device's own bit depth - convert to the 16-bit
        // interleaved PCM AudioFrame expects.
        int floatCount = args.BytesRecorded / 4;
        var incoming = new short[floatCount];
        for (int i = 0; i < floatCount; i++)
        {
            float sample = BitConverter.ToSingle(args.Buffer, i * 4);
            incoming[i] = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
        }
        _accumulator.AddRange(incoming);

        while (_accumulator.Count >= _samplesPerFrame)
        {
            var frame = _accumulator.GetRange(0, _samplesPerFrame).ToArray();
            _accumulator.RemoveRange(0, _samplesPerFrame);

            var audioFrame = new AudioFrame(frame, _sampleRate, _channels, _samplesPerFrame / _channels, null);
            Source.CaptureFrame(audioFrame);
        }
    }

    public void Dispose()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
        }
        Source.Dispose();
    }
}
