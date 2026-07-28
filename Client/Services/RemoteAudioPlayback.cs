using LiveKit.Rtc;
using NAudio.Wave;

namespace Voiceover.Client.Services;

// Plays one remote participant's audio track. LiveKit's engine already
// decoded and jitter-buffered it by the time frames reach AudioStream - this
// class's only job is routing those frames to a real speaker device with
// per-user volume and deafen support, since LiveKit's SDK hands you decoded
// frames but doesn't do OS audio output itself.
public class RemoteAudioPlayback : IAsyncDisposable
{
    private readonly int _outputDeviceIndex;
    private readonly AudioStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _waveProvider;

    public float PlaybackVolume { get; set; } = 1.0f;

    // Keeps the read loop running (so LiveKit's internal buffering doesn't
    // back up) but skips the final "send it to the speaker" step, same
    // reasoning OpusAudioEndPoint's deafen handling used to have for its own
    // jitter buffer.
    public bool Deafened { get; set; }

    // Same gating mechanism as Deafened, for a different reason: screen-share
    // system audio (see VoiceService's "system-audio" track) should only
    // actually reach the speaker while a ScreenShareViewerWindow is open for
    // it - otherwise sharing your system audio would blast through
    // everyone's speakers the instant it starts, completely unwatched.
    // Regular mic playback leaves this at its default true, since voice
    // channel audio should always play regardless of any window being open.
    public bool IsListening { get; set; } = true;

    public RemoteAudioPlayback(RemoteAudioTrack track, int outputDeviceIndex)
    {
        _outputDeviceIndex = outputDeviceIndex;
        _stream = new AudioStream(track, 48000, 1, null, 100, null, null);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frameEvent in _stream.WithCancellation(ct))
            {
                if (Deafened || !IsListening) continue;

                var frame = frameEvent.Frame;
                var pcm = frame.DataArray;

                EnsurePlaybackStarted(frame.SampleRate, frame.NumChannels);

                if (PlaybackVolume != 1.0f)
                    ApplyGain(pcm, PlaybackVolume);

                var bytes = new byte[pcm.Length * 2];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                _waveProvider?.AddSamples(bytes, 0, bytes.Length);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Dispose - the stream read loop was cancelled, not an error.
        }
    }

    private void EnsurePlaybackStarted(int sampleRate, int channels)
    {
        if (_waveOut is not null) return;

        var format = new WaveFormat(sampleRate, 16, channels);
        _waveProvider = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        _waveOut = new WaveOutEvent { DeviceNumber = _outputDeviceIndex };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();
    }

    // Same tanh soft-knee limiter as MicCaptureSource.ApplyGain, applied
    // here to the per-user volume slider instead of mic boost.
    private static void ApplyGain(short[] pcm, float gain)
    {
        for (int i = 0; i < pcm.Length; i++)
        {
            float normalized = (pcm[i] * gain) / short.MaxValue;
            float limited = (float)Math.Tanh(normalized);
            pcm[i] = (short)(limited * short.MaxValue);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _readLoop; }
        catch { /* already handled inside the loop, nothing to surface here */ }
        _cts.Dispose();

        _waveOut?.Stop();
        _waveOut?.Dispose();
        await _stream.DisposeAsync();
    }
}
