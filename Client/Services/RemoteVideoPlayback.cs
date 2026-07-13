using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiveKit.Proto;
using LiveKit.Rtc;

namespace Voiceover.Client.Services;

// Renders one remote participant's screen-share track. LiveKit's engine
// already decoded it by the time frames reach VideoStream - this class's
// only job is turning BGRA frames into a WriteableBitmap the UI can bind to,
// the video analog of RemoteAudioPlayback routing decoded audio to a speaker.
public class RemoteVideoPlayback : IAsyncDisposable
{
    private readonly VideoStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    private WriteableBitmap? _bitmap;
    public ImageSource? Bitmap => _bitmap;

    // Raised on the UI thread whenever a new frame has been written into
    // Bitmap - the viewer only needs to know a redraw happened, not rebind.
    public event Action? FrameUpdated;

    // Guards a single in-flight BeginInvoke plus the frame it'll render -
    // see RenderFrame for why this exists instead of a plain
    // Dispatcher.Invoke per frame.
    private readonly object _frameLock = new();
    private VideoFrame? _pendingFrame;
    private bool _dispatchQueued;

    public RemoteVideoPlayback(RemoteVideoTrack track)
    {
        _stream = VideoStream.FromTrack(track, VideoBufferType.Bgra, 4);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frameEvent in _stream.WithCancellation(ct))
                RenderFrame(frameEvent.Frame);
        }
        catch (OperationCanceledException)
        {
            // Expected on DisposeAsync - the stream read loop was cancelled, not an error.
        }
    }

    // A blocking Dispatcher.Invoke per frame here would stall this read loop
    // (and, upstream, LiveKit's own buffering) whenever the UI thread is
    // busy with something else - at 60-120fps that's a real risk, not just
    // a theoretical one. BeginInvoke instead, but only ever one queued at a
    // time: if a dispatch is already pending when a new frame arrives, swap
    // in the newer frame and skip queuing another - the pending callback
    // always renders whatever's freshest when it finally runs, so frames
    // get coalesced/dropped under load instead of piling up unboundedly.
    private void RenderFrame(VideoFrame frame)
    {
        lock (_frameLock)
        {
            _pendingFrame = frame;
            if (_dispatchQueued) return;
            _dispatchQueued = true;
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(DrawPendingFrame));
    }

    // WriteableBitmap is tied to the dispatcher that created it - frames
    // arrive on this class's own background read loop, so both creation and
    // every write below have to go through the main UI dispatcher regardless
    // of which thread constructed this class.
    private void DrawPendingFrame()
    {
        VideoFrame frame;
        lock (_frameLock)
        {
            frame = _pendingFrame!;
            _pendingFrame = null;
            _dispatchQueued = false;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);

        var stride = frame.Width * 4;
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.DataBytes, stride, 0);
        FrameUpdated?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _readLoop; }
        catch { /* already handled inside the loop, nothing to surface here */ }
        _cts.Dispose();

        await _stream.DisposeAsync();
    }
}
