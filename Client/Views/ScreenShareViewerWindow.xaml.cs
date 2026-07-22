using System.Windows;
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
    private readonly VoiceService _voice;
    private readonly int _sharerUserId;

    // AudioVolumeSlider's XAML sets a literal Value="100" (not a binding),
    // which differs from Slider's own default of 0 - so WPF fires
    // ValueChanged synchronously while InitializeComponent() is still
    // parsing the tree, before this constructor's body has assigned _voice/
    // _sharerUserId (and possibly before the sibling VolumeDisplayText
    // TextBlock, declared later in the same panel, even exists yet).
    // _initialized only flips true once everything is actually wired up, so
    // that spurious first call is a safe no-op instead of a NullReferenceException.
    private bool _initialized;

    public ScreenShareViewerWindow(string sharerUsername, RemoteVideoPlayback playback, VoiceService voice, int sharerUserId)
    {
        InitializeComponent();
        _playback = playback;
        _voice = voice;
        _sharerUserId = sharerUserId;

        Title = $"{sharerUsername}'s screen";
        ViewerTitleBar.Title = Title;
        ShareImage.Source = _playback.Bitmap;

        _initialized = true;

        // Starts wherever this device's audio for them already is (the
        // UserVolumeStorage default applied when the share's audio track
        // was first subscribed, see VoiceService.OnTrackSubscribed) rather
        // than silently resetting to 100% every time this window opens.
        AudioVolumeSlider.Value = _voice.GetRemoteScreenAudioVolume(_sharerUserId) * 100.0;

        // Audio/video only actually play/render while this window is open
        // (see RemoteAudioPlayback.IsListening/RemoteVideoPlayback.
        // IsRendering) - opening turns both on; OnClosed below turns them
        // back off, so an unwatched share never plays through the speakers
        // or burns CPU decoding frames nobody's looking at.
        _voice.SetRemoteScreenAudioListening(_sharerUserId, true);
        _playback.IsRendering = true;

        _playback.FrameUpdated += OnFrameUpdated;
    }

    // Runs on the UI thread already - RemoteVideoPlayback raises this from
    // inside its own Dispatcher.Invoke.
    private void OnFrameUpdated()
    {
        if (!ReferenceEquals(ShareImage.Source, _playback.Bitmap))
            ShareImage.Source = _playback.Bitmap;
    }

    private void AudioVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;

        VolumeDisplayText.Text = $"{(int)e.NewValue}%";
        _voice.SetRemoteScreenAudioVolume(_sharerUserId, (float)(e.NewValue / 100.0));
    }

    protected override void OnClosed(EventArgs e)
    {
        _playback.FrameUpdated -= OnFrameUpdated;
        _playback.IsRendering = false;
        _voice.SetRemoteScreenAudioListening(_sharerUserId, false);
        base.OnClosed(e);
    }
}
