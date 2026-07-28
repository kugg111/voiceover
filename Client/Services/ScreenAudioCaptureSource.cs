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
//
// WasapiLoopbackCapture taps the default render endpoint's *entire* mix -
// not just game/media audio, but also everything RemoteAudioPlayback is
// currently sending to that same device (every other participant's voice,
// including - once a viewer opens this share - their own, round-tripped
// back to them). See RenderedVoiceReferenceBuffer for the fix: since this
// is a digital tap of a signal the app itself already knows exactly (not
// acoustic mic pickup), that known reference gets subtracted out of the
// capture before publishing, sample for sample.
public class ScreenAudioCaptureSource : IDisposable
{
    private const int FrameDurationMs = 20;

    // How far back to look in RenderedVoiceReferenceBuffer for the audio
    // that corresponds to a just-captured loopback block - covers the
    // render buffer + loopback capture round trip through WASAPI's shared-
    // mode audio engine, which isn't directly measurable from NAudio's API
    // and varies a little by device/driver. Chosen as a reasonable default
    // rather than an exact measurement (unlike NSNet2's delay compensation,
    // which is a fixed algorithmic delay - this one is hardware-dependent);
    // live-test against a real device and adjust if cancellation sounds
    // early/late (a residual pre-echo or post-echo "flutter" on your own
    // voice, rather than the loud direct echo this fixes, indicates a
    // misalignment worth retuning).
    private const int DelayCompensationMs = 40;

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
        int frameCount = floatCount / _channels;

        // Echo-cancellation reference - see RenderedVoiceReferenceBuffer and
        // this class's own header comment. Only meaningful at 48kHz, the
        // fixed rate that buffer is kept in (matching LiveKit's own
        // AudioStream format) - on the rare device whose mix format isn't
        // 48kHz, this comes back all zeros and subtraction below is a no-op,
        // same "best effort, never block the feature" fallback as a failed
        // WasapiLoopbackCapture construction above.
        var reference = _sampleRate == 48000
            ? RenderedVoiceReferenceBuffer.TakeReference(frameCount, DelayCompensationMs)
            : new short[frameCount];

        var incoming = new short[floatCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                int i = frame * _channels + ch;
                float sample = BitConverter.ToSingle(args.Buffer, i * 4);
                short pcm = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                // Same mono reference subtracted from every channel - this
                // app's own voice playback was itself upmixed identically
                // to every channel by the shared-mode audio engine on the
                // way out (WaveOutEvent publishes a mono WaveFormat; see
                // RemoteAudioPlayback), so that's the correct match here too.
                incoming[i] = (short)Math.Clamp(pcm - reference[frame], short.MinValue, short.MaxValue);
            }
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
