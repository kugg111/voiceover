using System.Runtime.InteropServices;
using LiveKit.Rtc;
using PortAudioSharp;

namespace Voiceover.Client.Linux;

// Linux equivalent of the WPF client's RemoteAudioPlayback
// (Client/Services/RemoteAudioPlayback.cs). LiveKit already decoded and
// jitter-buffered the remote track by the time frames reach AudioStream -
// this class's job is routing those frames to a real speaker device.
//
// PortAudio's output stream pulls samples via a callback on its own
// real-time thread, while LiveKit's frame stream pushes samples in from an
// async read loop - the queue below is the seam between the two,
// filling the same role NAudio's BufferedWaveProvider (DiscardOnBufferOverflow)
// played in the WPF client's own version.
public class RemoteAudioPlaybackLinux : IAsyncDisposable
{
    private readonly int _outputDeviceIndex;
    private readonly AudioStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    private readonly object _bufferLock = new();
    private readonly Queue<short> _buffer = new();
    private const int MaxBufferedSamples = 48000 * 2; // ~2 seconds at 48kHz mono

    private PortAudioSharp.Stream? _outStream;

    public float PlaybackVolume { get; set; } = 1.0f;

    // Keeps the read loop draining LiveKit's internal buffering even while
    // deafened, same reasoning the WPF client's RemoteAudioPlayback uses -
    // just skips queuing the samples for actual playback.
    public bool Deafened { get; set; }

    public RemoteAudioPlaybackLinux(RemoteAudioTrack track, int outputDeviceIndex)
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
                if (Deafened) continue;

                var pcm = frameEvent.Frame.DataArray;
                EnsurePlaybackStarted(frameEvent.Frame.SampleRate, frameEvent.Frame.NumChannels);

                if (PlaybackVolume != 1.0f) ApplyGain(pcm, PlaybackVolume);

                lock (_bufferLock)
                {
                    foreach (var sample in pcm) _buffer.Enqueue(sample);
                    while (_buffer.Count > MaxBufferedSamples) _buffer.Dequeue();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Dispose - the stream read loop was cancelled, not an error.
        }
    }

    private void EnsurePlaybackStarted(int sampleRate, int channels)
    {
        if (_outStream is not null) return;

        PortAudioBootstrap.EnsureInitialized();

        var deviceIndex = _outputDeviceIndex >= 0 ? _outputDeviceIndex : PortAudio.DefaultOutputDevice;
        if (deviceIndex == PortAudio.NoDevice) return;

        var deviceInfo = PortAudio.GetDeviceInfo(deviceIndex);
        var parameters = new StreamParameters
        {
            device = deviceIndex,
            channelCount = channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = deviceInfo.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _outStream = new PortAudioSharp.Stream(null, parameters, sampleRate, 960, StreamFlags.NoFlag, OnPlaybackCallback, IntPtr.Zero);
        _outStream.Start();
    }

    // Runs on PortAudio's own real-time thread - zero-fills whatever the
    // queue can't cover (buffer underrun), same silence-on-starvation
    // behavior NAudio's BufferedWaveProvider has.
    private StreamCallbackResult OnPlaybackCallback(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userDataPtr)
    {
        var samples = new short[frameCount];
        lock (_bufferLock)
        {
            for (var i = 0; i < frameCount && _buffer.Count > 0; i++)
                samples[i] = _buffer.Dequeue();
        }
        Marshal.Copy(samples, 0, output, (int)frameCount);
        return StreamCallbackResult.Continue;
    }

    // Same tanh soft-knee limiter as the WPF client's RemoteAudioPlayback.
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

        if (_outStream is not null)
        {
            try { _outStream.Stop(); } catch { }
            _outStream.Close();
            _outStream.Dispose();
        }
        await _stream.DisposeAsync();
    }
}
