using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

// Resizable, non-modal viewer for a remote participant's screen share -
// RemoteVideoPlayback owns the actual WriteableBitmap and keeps writing new
// frames into it as they arrive, so this window just needs to (re)bind
// Image.Source when FrameUpdated says the bitmap instance itself changed
// (RemoteVideoPlayback recreates it if the source resolution changes).
public partial class ScreenShareViewerWindow : FluentWindow
{
    private readonly RemoteVideoPlayback _playback;

    public ScreenShareViewerWindow(string sharerUsername, RemoteVideoPlayback playback)
    {
        InitializeComponent();
        _playback = playback;

        Title = $"{sharerUsername}'s screen";
        ViewerTitleBar.Title = Title;
        ShareImage.Source = _playback.Bitmap;

        _playback.FrameUpdated += OnFrameUpdated;
    }

    // Runs on the UI thread already - RemoteVideoPlayback raises this from
    // inside its own Dispatcher.Invoke.
    private void OnFrameUpdated()
    {
        if (!ReferenceEquals(ShareImage.Source, _playback.Bitmap))
            ShareImage.Source = _playback.Bitmap;
    }

    protected override void OnClosed(EventArgs e)
    {
        _playback.FrameUpdated -= OnFrameUpdated;
        base.OnClosed(e);
    }
}
