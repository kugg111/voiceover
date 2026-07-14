using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Voiceover.Client.Views;

public enum CallWindowState { OutgoingRinging, IncomingRinging, Active }

// Non-modal (Show(), not ShowDialog()) so the rest of the app stays usable
// while a call rings or is active - unlike ConfirmDialog, which this
// borrows its FluentWindow/Mica shell from, this window updates in place as
// a call moves from ringing to active instead of being closed and replaced.
// One instance covers both the incoming-ring and active-call surfaces from
// the plan, since most of the shell (avatar, name, roster) is shared and
// juggling two window lifetimes for one call added more risk than it saved.
public partial class CallWindow : FluentWindow
{
    public string CallId { get; }
    public CallWindowState State { get; private set; }
    public int OtherPartyUserId { get; }
    public string OtherPartyUsername { get; }

    // Raised on Accept (incoming ring only). MainWindow owns the actual
    // hub call + VoiceService.JoinCallAsync - this window only reports intent.
    public event Action? Accepted;

    // Raised when the user ends the call from this window (Decline, Cancel,
    // or Hang Up, depending on State at the time) - true means "this was a
    // decline of an incoming ring" (maps to ChatHub.DeclineCall), false
    // means cancel/hang-up (maps to ChatHub.EndCall). Also raised if the
    // window is closed via the title bar while ringing/active, so a
    // dismissed call still tears down server-side instead of leaving a
    // zombie session - see CloseSilently for the one case that should NOT
    // raise this (MainWindow reacting to a remote end).
    public event Action<bool>? Ended;

    private readonly VoiceService _voice;
    private readonly ObservableCollection<VoiceMemberItem> _roster = new();
    private readonly VoiceMemberItem _selfRosterItem;
    private readonly VoiceMemberItem _otherRosterItem;
    private bool _suppressEndedEvent;
    private ScreenShareViewerWindow? _remoteViewer;
    private RemoteVideoPlayback? _otherPartyPlayback;
    private DispatcherTimer? _durationTimer;
    private DateTime _activeStartUtc;

    public CallWindow(string callId, int otherUserId, string otherUsername, string? otherAvatarUrl, bool isOutgoing,
        string selfUsername, string? selfAvatarUrl, VoiceService voice)
    {
        InitializeComponent();
        CallId = callId;
        OtherPartyUserId = otherUserId;
        OtherPartyUsername = otherUsername;
        _voice = voice;

        RosterList.ItemsSource = _roster;
        OtherPartyAvatar.DisplayName = otherUsername;
        OtherPartyAvatar.ImageUrl = otherAvatarUrl;
        OtherPartyNameText.Text = otherUsername;

        _selfRosterItem = new VoiceMemberItem { Username = selfUsername, AvatarUrl = selfAvatarUrl, IsSelf = true };
        _otherRosterItem = new VoiceMemberItem { Username = otherUsername, AvatarUrl = otherAvatarUrl };

        // Mirrors MainWindow's own MicMutedChanged/DeafenedChanged/
        // LocalSpeakingChanged wiring for the channel sidebar - these fire
        // from background threads (hotkey hook, mic capture callback), so
        // every handler below dispatches rather than touching UI directly.
        _voice.MicMutedChanged += OnMicMutedChanged;
        _voice.DeafenedChanged += OnDeafenedChanged;
        _voice.LocalSpeakingChanged += OnLocalSpeakingChanged;
        _voice.ScreenSharingChanged += OnLocalScreenSharingChanged;
        _voice.RemoteScreenShareStarted += OnRemoteScreenShareStarted;
        _voice.RemoteScreenShareStopped += OnRemoteScreenShareStopped;

        State = isOutgoing ? CallWindowState.OutgoingRinging : CallWindowState.IncomingRinging;
        ApplyState();
    }

    private void ApplyState()
    {
        switch (State)
        {
            case CallWindowState.OutgoingRinging:
                StatusText.Text = "Calling...";
                RingingButtonsPanel.Visibility = Visibility.Visible;
                ActivePanel.Visibility = Visibility.Collapsed;
                AcceptButton.Visibility = Visibility.Collapsed;
                EndButton.Content = "Cancel";
                break;
            case CallWindowState.IncomingRinging:
                StatusText.Text = "Incoming call...";
                RingingButtonsPanel.Visibility = Visibility.Visible;
                ActivePanel.Visibility = Visibility.Collapsed;
                AcceptButton.Visibility = Visibility.Visible;
                EndButton.Content = "Decline";
                break;
            case CallWindowState.Active:
                RingingButtonsPanel.Visibility = Visibility.Collapsed;
                ActivePanel.Visibility = Visibility.Visible;
                _roster.Add(_selfRosterItem);
                _roster.Add(_otherRosterItem);
                UpdateMuteVisual(_voice.IsMicMuted);
                UpdateDeafenVisual(_voice.IsDeafened);
                // Topmost matters while ringing (so you actually notice the
                // call) but just gets in the way once you're mid-conversation
                // and trying to do other things - drop it once connected.
                Topmost = false;
                StartDurationTimer();
                break;
        }
    }

    // Called by MainWindow once VoiceService.JoinCallAsync (accept) or the
    // remote's CallAccepted event (outgoing) confirms audio actually connected.
    public void SwitchToActive()
    {
        State = CallWindowState.Active;
        ApplyState();
    }

    private void StartDurationTimer()
    {
        _activeStartUtc = DateTime.UtcNow;
        UpdateDurationText();

        _durationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _durationTimer.Tick += (_, _) => UpdateDurationText();
        _durationTimer.Start();
    }

    private void UpdateDurationText()
    {
        var elapsed = DateTime.UtcNow - _activeStartUtc;
        StatusText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e) => Accepted?.Invoke();

    private void EndButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MuteButton_Click(object sender, RoutedEventArgs e) => _voice.IsMicMuted = !_voice.IsMicMuted;

    private void DeafenToggleButton_Click(object sender, RoutedEventArgs e) => _voice.IsDeafened = !_voice.IsDeafened;

    private void ScreenShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice.IsScreenSharing)
        {
            _ = _voice.StopScreenShareAsync();
            return;
        }

        ScreenSharePresetMenu.PlacementTarget = ScreenShareButton;
        ScreenSharePresetMenu.IsOpen = true;
    }

    // Same resolution/bitrate-scales-with-fps reasoning as MainWindow's
    // channel-voice screen share buttons - see the Phase 2 spike notes on
    // the plan.
    private async void ScreenShare480p30Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(30, 2_500_000, 854, 480);
    private async void ScreenShare720p60Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(60, 6_000_000, 1280, 720);
    private async void ScreenShareNative120Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(120, 35_000_000, null, null);

    // Quick-share shortcut with a fixed 720p60 preset - see MainWindow's
    // identical handler for why (always prompts for a source either way).
    private async void ScreenShareChooseSource_Click(object sender, RoutedEventArgs e)
    {
        var item = await PickScreenShareSourceAsync();
        if (item is null) return;

        await StartScreenShareWithItemAsync(item, 60, 6_000_000, 1280, 720);
    }

    private async Task StartScreenShareWithPresetAsync(uint fps, uint bitrate, int? maxWidth, int? maxHeight)
    {
        var item = await PickScreenShareSourceAsync();
        if (item is null) return;

        await StartScreenShareWithItemAsync(item, fps, bitrate, maxWidth, maxHeight);
    }

    private async Task<Windows.Graphics.Capture.GraphicsCaptureItem?> PickScreenShareSourceAsync()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return await ScreenCaptureSource.PickItemAsync(hwnd);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the screen/window picker:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private async Task StartScreenShareWithItemAsync(Windows.Graphics.Capture.GraphicsCaptureItem item, uint fps, uint bitrate, int? maxWidth, int? maxHeight)
    {
        try
        {
            await _voice.StartScreenShareAsync(item, fps, bitrate, maxWidth, maxHeight);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start screen sharing:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
    }

    private void OnLocalScreenSharingChanged(bool isSharing) => Dispatcher.Invoke(() =>
    {
        _selfRosterItem.IsScreenSharing = isSharing;
        UpdateScreenShareButtonVisual(isSharing);
    });

    // 1:1 call - any remote share/stop necessarily refers to the other
    // party, no need to key off userId the way MainWindow's multi-person
    // channel roster does.
    private void OnRemoteScreenShareStarted(int userId, RemoteVideoPlayback playback) => Dispatcher.Invoke(() =>
    {
        _otherRosterItem.IsScreenSharing = true;
        // No auto-opened viewer - just remember the live playback so a
        // later click on the TV icon (RemoteScreenShareIcon_MouseLeftButtonUp)
        // has something to open.
        _otherPartyPlayback = playback;
    });

    private void OnRemoteScreenShareStopped(int userId) => Dispatcher.Invoke(() =>
    {
        _otherRosterItem.IsScreenSharing = false;
        _otherPartyPlayback = null;
        _remoteViewer?.Close();
        _remoteViewer = null;
    });

    // Click target for the blue TV icon next to the other party's name -
    // the only way the viewer window opens now. Clicking it again while
    // already open just activates the existing window.
    private void RemoteScreenShareIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_otherPartyPlayback is null) return;

        if (_remoteViewer is not null)
        {
            _remoteViewer.Activate();
            return;
        }

        _remoteViewer = new ScreenShareViewerWindow(_otherRosterItem.Username, _otherPartyPlayback);
        _remoteViewer.Closed += (_, _) => _remoteViewer = null;
        _remoteViewer.Show();
    }

    private void UpdateScreenShareButtonVisual(bool isSharing)
    {
        ScreenShareButton.Content = isSharing ? "🖥️ Stop Sharing" : "🖥️ Share Screen";
        ScreenShareButton.Foreground = isSharing
            ? (Brush)FindResource("AccentBlurple")
            : (Brush)FindResource("TextMuted");
    }

    private void OnMicMutedChanged(bool isMuted) => Dispatcher.Invoke(() => UpdateMuteVisual(isMuted));
    private void OnDeafenedChanged(bool isDeafened) => Dispatcher.Invoke(() =>
    {
        UpdateDeafenVisual(isDeafened);
        UpdateMuteVisual(_voice.IsMicMuted);
    });
    private void OnLocalSpeakingChanged(bool isSpeaking) => Dispatcher.Invoke(() => _selfRosterItem.IsSpeaking = isSpeaking);

    private void UpdateMuteVisual(bool isMuted)
    {
        MuteButton.Content = isMuted ? "🔇 Unmute" : "🎤 Mute";
        MuteButton.Foreground = isMuted ? new SolidColorBrush(Color.FromRgb(0xF2, 0x3F, 0x42)) : (Brush)FindResource("TextMuted");
    }

    private void UpdateDeafenVisual(bool isDeafened)
    {
        DeafenToggleButton.Content = isDeafened ? "🎧 Undeafen" : "🎧 Deafen";
        DeafenToggleButton.Foreground = isDeafened ? new SolidColorBrush(Color.FromRgb(0xF2, 0x3F, 0x42)) : (Brush)FindResource("TextMuted");
    }

    // Used when MainWindow is closing this window itself in reaction to a
    // remote event (call declined/ended elsewhere, or a local join failure
    // it's already reporting/handling) - Ended must NOT fire in that case,
    // since MainWindow already knows and would otherwise re-send an
    // EndCall/DeclineCall the server-side session no longer needs.
    public void CloseSilently()
    {
        _suppressEndedEvent = true;
        Close();
    }

    private void CallWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _durationTimer?.Stop();

        _voice.MicMutedChanged -= OnMicMutedChanged;
        _voice.DeafenedChanged -= OnDeafenedChanged;
        _voice.LocalSpeakingChanged -= OnLocalSpeakingChanged;
        _voice.ScreenSharingChanged -= OnLocalScreenSharingChanged;
        _voice.RemoteScreenShareStarted -= OnRemoteScreenShareStarted;
        _voice.RemoteScreenShareStopped -= OnRemoteScreenShareStopped;

        // The call is ending either way - no reason to leave the other
        // party's share window open once this window (and, via
        // VoiceService.LeaveAllAsync, the underlying room) is gone too.
        _remoteViewer?.Close();

        if (!_suppressEndedEvent)
            Ended?.Invoke(State == CallWindowState.IncomingRinging);
    }
}
