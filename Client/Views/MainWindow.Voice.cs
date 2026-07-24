using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using UserControl = System.Windows.Controls.UserControl;

namespace Voiceover.Client.Views;

public partial class MainWindow
{
    private VoiceService? _voice;
    private int? _currentVoiceChannelIdBacking;
    private int? _currentVoiceChannelId
    {
        get => _currentVoiceChannelIdBacking;
        set
        {
            _currentVoiceChannelIdBacking = value;
            SyncOverlayRoster();
        }
    }

    private CallWindow? _callWindow;
    private string? _currentCallId;
    private System.Windows.Threading.DispatcherTimer? _ringTimeoutTimer;

    // One viewer window per remote participant currently screen-sharing.
    private readonly Dictionary<int, ScreenShareViewerWindow> _screenShareViewers = new();

    // Tracks who's currently sharing in the open voice channel and their
    // live RemoteVideoPlayback, without opening a viewer window for any of
    // them automatically - see OnRemoteScreenShareStarted/
    // ScreenShareIcon_MouseLeftButtonUp. The icon itself (bound to
    // IsScreenSharing/ScreenSharingIconVisibility on VoiceMemberItem) is
    // what tells you who's sharing; a viewer only opens if you click it.
    private readonly Dictionary<int, RemoteVideoPlayback> _activeScreenShares = new();

    // BulkObservableCollection where a Clear()+per-item-Add() reload pattern
    // is common (see ReplaceAll callers below) - one Reset notification
    // instead of N+1 individual ones. Plain ObservableCollection for the
    // rest (search results etc.), which only ever grow/shrink by a couple
    // of items at a time and don't reload wholesale.
    private void SyncOverlayRoster()
    {
        if (_overlay is null) return;

        var members = _currentVoiceChannelIdBacking is { } id
            ? FindVoiceChannelItem(id)?.Members
            : null;
        _overlay.SetRoster(members);
    }

    // Called by SettingsPage when the user changes the overlay enable toggle,
    // rebinds the toggle key, or adjusts the background transparency.
    private async Task LoadVoiceRostersAsync(int serverId)
    {
        var rosters = await _hub.GetVoiceRostersForServerAsync(serverId);
        foreach (var roster in rosters)
        {
            var item = FindVoiceChannelItem(roster.ChannelId);
            if (item is null) continue;

            foreach (var member in roster.Members)
            {
                if (!item.Members.Any(m => m.UserId == member.UserId))
                    item.Members.Add(new VoiceMemberItem { UserId = member.UserId, Username = member.Username, AvatarUrl = App.ResolveUploadUrl(member.AvatarUrl), IsSelf = member.UserId == _api.CurrentUserId, Volume = SavedVolumePercent(member.UserId) });
            }
        }
    }

    private async void VoiceChannelChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;

        SaveCurrentDraft();

        _currentChannelId = channelId;
        CancelReply();
        if (_joinedChannelIds.Add(channelId))
            await _hub.JoinChannelAsync(channelId);

        ChannelNameText.Text = $"{FindChannelDisplayName(channelId) ?? "🔊 channel"} · Text Chat";
        DmCallButton.Visibility = Visibility.Collapsed;
        PinnedMessagesButton.Visibility = Visibility.Visible;
        SearchMessagesButton.Visibility = Visibility.Visible;
        ModerationLogButton.Visibility = _canViewAuditLog ? Visibility.Visible : Visibility.Collapsed;
        BanListButton.Visibility = _canViewAuditLog ? Visibility.Visible : Visibility.Collapsed;

        LoadDraftIntoInput();
        await LoadChannelHistoryAsync(channelId);
    }

    private async void VoiceChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;
        if (_voice is null) return;

        // Re-clicking the channel you're already in used to tear down and
        // re-establish the whole voice connection for no reason - harmless
        // most of the time, but a rapid double-click could land the leave
        // and the rejoin close enough together to leave the audio endpoint
        // (and everyone else's view of this connection) in a half-torn-down
        // state, breaking capture/playback for the whole channel.
        if (_currentVoiceChannelId == channelId) return;

        // Calls and server voice channels are mutually exclusive contexts -
        // joining a channel while on a private call ends the call first,
        // mirrored by LeaveVoiceChannelIfActiveAsync on the call side.
        if (_callWindow is not null)
        {
            _ = EndOrDeclineCallAsync(_callWindow.CallId, _callWindow.State == CallWindowState.IncomingRinging);
            _callWindow.CloseSilently();
        }

        if (_currentVoiceChannelId.HasValue)
        {
            await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
            // Off the UI thread, same as JoinChannelAsync's own Task.Run
            // wrapping - LeaveAllAsync synchronously tears down the old
            // MicCaptureSource/NoiseSuppressionProcessor, including native
            // model disposal. Calling it directly here (as before) meant
            // that teardown could run straight on the dispatcher thread on
            // a channel switch, stalling the app's own UI for however long
            // native disposal took.
            await Task.Run(() => _voice.LeaveAllAsync());
            RemoveSelfFromVoiceRoster(_currentVoiceChannelId.Value);
        }

        _currentVoiceChannelId = channelId;

        // Show ourselves locally right away, styled as "joining" (see
        // VoiceMemberItem.IsJoining), instead of waiting for the LiveKit
        // connection below - that takes a few seconds, and without this
        // there'd be no feedback at all that the click registered. Other
        // clients don't get told about the join at all until it actually
        // succeeds (see JoinVoiceChannelAsync further down), so nobody else
        // ever sees this placeholder.
        var item = FindVoiceChannelItem(channelId);
        VoiceMemberItem? selfItem = null;
        if (item is not null && _api.CurrentUserId is not null && _api.CurrentUsername is not null)
        {
            selfItem = new VoiceMemberItem { UserId = _api.CurrentUserId.Value, Username = _api.CurrentUsername, AvatarUrl = _api.CurrentUserAvatarUrl, IsSelf = true, IsJoining = true };
            item.Members.Add(selfItem);
        }
        ConnectionStatusText.Text = "Joining voice...";

        try
        {
            await _voice.JoinChannelAsync(channelId);
        }
        catch
        {
            if (item is not null && selfItem is not null) item.Members.Remove(selfItem);
            _currentVoiceChannelId = null;
            ConnectionStatusText.Text = "";
            await AlertAsync("Error", "Could not join voice - check your connection and try again.");
            return;
        }

        // Presence/roster bookkeeping (still SignalR, unchanged from the mesh
        // days) - deliberately called only now, after the audio connection
        // above actually succeeded, so other clients' rosters only pick up
        // this join once it's real rather than a few seconds early.
        var existingMembers = await _hub.JoinVoiceChannelAsync(channelId);

        if (item is not null)
        {
            item.Members.Clear();
            foreach (var m in existingMembers)
                item.Members.Add(new VoiceMemberItem { UserId = m.UserId, Username = m.Username, AvatarUrl = App.ResolveUploadUrl(m.AvatarUrl), IsSelf = m.UserId == _api.CurrentUserId, Volume = SavedVolumePercent(m.UserId) });
            if (_api.CurrentUserId is not null && _api.CurrentUsername is not null)
                item.Members.Add(new VoiceMemberItem { UserId = _api.CurrentUserId.Value, Username = _api.CurrentUsername, AvatarUrl = _api.CurrentUserAvatarUrl, IsSelf = true });
        }

        VoiceControlBar.Visibility = Visibility.Visible;
        UpdateMuteButtonVisual();
        UpdateDeafenButtonVisual();
        UpdateScreenShareButtonVisual();
        ConnectionStatusText.Text = "Joined voice";

        // Unconditional (not gated on window focus like OnVoiceUserJoined's
        // toast/sound for other people) - this is direct feedback that your
        // own join went through, so it needs to play even if you're the
        // only one in the channel and even while the app is focused.
        NotificationService.PlayVoiceJoinSound();
    }

    private void MuteMicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        _voice.IsMicMuted = !_voice.IsMicMuted;
        UpdateMuteButtonVisual();
    }

    private void UpdateMuteButtonVisual()
    {
        if (_voice is null) return;

        MuteMicButton.Content = _voice.IsMicMuted ? "🔇" : "🎤";
        MuteMicButton.ToolTip = _voice.IsMicMuted ? "Unmute Mic" : "Mute Mic";
        MuteMicButton.Background = _voice.IsMicMuted ? ThemeBrushes.Danger : System.Windows.Media.Brushes.Transparent;
    }

    private void DeafenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        // Deafen forces mute to match (see VoiceService.IsDeafened), so
        // both buttons' visuals need refreshing, not just Deafen's own.
        _voice.IsDeafened = !_voice.IsDeafened;
        UpdateDeafenButtonVisual();
        UpdateMuteButtonVisual();
    }

    private void UpdateDeafenButtonVisual()
    {
        if (_voice is null) return;

        // Icon stays 🎧 in both states - reusing 🔇 here would make it
        // indistinguishable from the Mute Mic button's own active-state
        // icon, which is a genuinely different action (can't be heard vs.
        // can't hear anyone). Active/inactive is instead signaled by the
        // button's own Background fill (see VoiceControlIconButtonStyle),
        // since color emoji glyphs render from their own embedded palette
        // and ignore the Button's Foreground brush.
        DeafenButton.Content = "🎧";
        DeafenButton.ToolTip = _voice.IsDeafened ? "Undeafen" : "Deafen";
        DeafenButton.Background = _voice.IsDeafened ? ThemeBrushes.Danger : System.Windows.Media.Brushes.Transparent;
    }

    private void VoiceMemberVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_voice is null) return;
        if (sender is not FrameworkElement { DataContext: VoiceMemberItem member }) return;

        var volume = (float)(e.NewValue / 100.0);
        _voice.SetRemoteVolume(member.UserId, volume);
        UserVolumeStorage.SaveVolume(member.UserId, volume);
    }

    // --- Private calls (1:1, friends-only) - see CallWindow and
    // ChatHub's call signaling methods server-side. ---

    private async void CallFriendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int calleeId }) return;

        var friend = _friends.FirstOrDefault(f => f.UserId == calleeId);
        await InitiateCallAsync(calleeId, friend?.Username ?? "user", friend?.AvatarUrl);
    }

    private async void DmCallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dmActiveUserId is null) return;

        var avatarUrl = _dmConversations.FirstOrDefault(c => c.OtherUserId == _dmActiveUserId)?.OtherUserAvatarUrl;
        await InitiateCallAsync(_dmActiveUserId.Value, _dmActiveUsername ?? "user", avatarUrl);
    }

    private async Task InitiateCallAsync(int calleeId, string calleeUsername, string? calleeAvatarUrl)
    {
        if (_voice is null) return;
        if (_callWindow is not null) return; // already ringing/in a call

        await LeaveVoiceChannelIfActiveAsync();

        var callId = await _hub.InitiateCallAsync(calleeId);
        if (callId is null)
        {
            await AlertAsync("Error", "Couldn't start the call - you may not be friends, they're already in a call, or you're calling too fast.");
            return;
        }

        _currentCallId = callId;
        _callWindow = new CallWindow(callId, calleeId, calleeUsername, calleeAvatarUrl, isOutgoing: true,
            _api.CurrentUsername ?? "You", _api.CurrentUserAvatarUrl, _voice);
        _callWindow.Ended += wasDecline => _ = EndOrDeclineCallAsync(callId, wasDecline);
        _callWindow.Closed += (_, _) => OnCallWindowClosed(callId);
        _callWindow.Show();

        StartRingTimeout(callId);
    }

    private void OnIncomingCall(string callId, int callerId, string callerUsername, string? callerAvatarUrl)
    {
        Dispatcher.Invoke(() =>
        {
            if (_voice is null || _callWindow is not null) return; // already ringing/in a call - shouldn't happen (server allows one call at a time), guard anyway

            _currentCallId = callId;
            _callWindow = new CallWindow(callId, callerId, callerUsername, App.ResolveUploadUrl(callerAvatarUrl), isOutgoing: false,
                _api.CurrentUsername ?? "You", _api.CurrentUserAvatarUrl, _voice);
            _callWindow.Accepted += () => _ = AcceptIncomingCallAsync(callId);
            _callWindow.Ended += wasDecline => _ = EndOrDeclineCallAsync(callId, wasDecline);
            _callWindow.Closed += (_, _) => OnCallWindowClosed(callId);
            _callWindow.Show();

            NotificationService.StartIncomingCallSound();
        });
    }

    private async Task AcceptIncomingCallAsync(string callId)
    {
        if (_voice is null) return;
        NotificationService.StopIncomingCallSound();

        var accepted = await _hub.AcceptCallAsync(callId);
        if (!accepted)
        {
            _callWindow?.CloseSilently();
            return;
        }

        await LeaveVoiceChannelIfActiveAsync();
        await JoinActiveCallAsync(callId);
    }

    // Fires once the callee accepts our outgoing call - time for the caller
    // side to actually connect the LiveKit room (the callee connects from
    // AcceptIncomingCallAsync above instead, right after accepting).
    private void OnCallAccepted(string callId)
    {
        Dispatcher.Invoke(() =>
        {
            if (_currentCallId != callId) return;
            CancelRingTimeout();
            _ = JoinActiveCallAsync(callId);
        });
    }

    private async Task JoinActiveCallAsync(string callId)
    {
        if (_voice is null) return;

        try
        {
            await _voice.JoinCallAsync(callId);
        }
        catch
        {
            await AlertAsync("Error", "Could not join the call - check your connection and try again.");
            _ = _hub.EndCallAsync(callId);
            _callWindow?.CloseSilently();
            return;
        }

        _callWindow?.SwitchToActive();
    }

    private async Task EndOrDeclineCallAsync(string callId, bool wasDecline)
    {
        // Read before the hub call below - by the time this runs, State
        // still reflects whatever it was right before Close() (see
        // CallWindow_Closing's Ended?.Invoke), so it tells us exactly what
        // kind of end this was: declining an incoming ring, cancelling one
        // we placed, or hanging up an already-active call.
        if (_callWindow is not null)
        {
            var outcome = wasDecline ? "Declined" : _callWindow.State == CallWindowState.Active ? "Ended" : "Missed";
            _ = SendCallEventMessageAsync(_callWindow.OtherPartyUserId, outcome);
        }

        try
        {
            if (wasDecline) await _hub.DeclineCallAsync(callId);
            else await _hub.EndCallAsync(callId);
        }
        catch (Exception ex)
        {
            // Invoked fire-and-forget (`_ = EndOrDeclineCallAsync(...)`) from
            // multiple call sites, so an exception here has nowhere else to
            // go - without this it vanishes silently instead of reaching
            // DispatcherUnhandledException (that only catches exceptions on
            // the dispatcher thread's own call stack, not ones thrown inside
            // a discarded Task). The other party's client still recovers on
            // its own (see ChatHub.OnDisconnectedAsync's implicit end-call
            // handling), so this is log-and-move-on, not a user-facing error.
            LogBackgroundException(ex);
        }
    }

    // Best-effort, fire-and-forget - see CallEventMessage for why this reuses
    // the normal encrypted DM pipeline instead of a dedicated schema.
    private async Task SendCallEventMessageAsync(int otherUserId, string outcome)
    {
        try
        {
            var encrypted = await _api.E2ee.EncryptAsync(otherUserId, CallEventMessage.Format(outcome));
            if (encrypted is null) return; // keys not unlocked/established yet - skip rather than send plaintext
            await _hub.SendDirectMessageAsync(otherUserId, encrypted);
        }
        catch
        {
            // A missing call-event message isn't worth surfacing an error for.
        }
    }

    private void OnCallEndedRemotely(string callId)
    {
        Dispatcher.Invoke(() =>
        {
            if (_currentCallId != callId) return;

            // Only reachable here (rather than through our own Decline/Cancel
            // click) when the OTHER party ended things first - if we were
            // still the callee and still ringing, that means they cancelled
            // or timed out before we ever answered, i.e. a genuine missed call.
            if (_callWindow is { State: CallWindowState.IncomingRinging })
                NotificationService.ShowToast("Missed Call", $"{_callWindow.OtherPartyUsername} called you");

            _callWindow?.CloseSilently();
            _ = _voice?.LeaveAllAsync();
        });
    }

    private void OnCallWindowClosed(string callId)
    {
        if (_currentCallId == callId)
        {
            _currentCallId = null;
            _callWindow = null;
        }
        NotificationService.StopIncomingCallSound();
        CancelRingTimeout();
    }

    // Calls and server voice channels are mutually exclusive contexts (see
    // VoiceService) - leave the current channel first, same as switching
    // between two voice channels does.
    private async Task LeaveVoiceChannelIfActiveAsync()
    {
        if (!_currentVoiceChannelId.HasValue || _voice is null) return;

        await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
        await _voice.LeaveAllAsync();
        RemoveSelfFromVoiceRoster(_currentVoiceChannelId.Value);
        _currentVoiceChannelId = null;
        VoiceControlBar.Visibility = Visibility.Collapsed;
    }

    // Caller-side-only ring timeout (per the plan: handled client-side
    // rather than a server timer) - auto-cancels an outgoing call nobody
    // answered instead of ringing forever.
    private void StartRingTimeout(string callId)
    {
        CancelRingTimeout();
        var timeoutSeconds = _voice?.RingTimeoutSeconds ?? 40;
        _ringTimeoutTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(timeoutSeconds) };
        _ringTimeoutTimer.Tick += (_, _) =>
        {
            CancelRingTimeout();
            if (_currentCallId != callId) return;
            if (_callWindow is not null) _ = SendCallEventMessageAsync(_callWindow.OtherPartyUserId, "Missed");
            _ = _hub.EndCallAsync(callId);
            _callWindow?.CloseSilently();
        };
        _ringTimeoutTimer.Start();
    }

    private void CancelRingTimeout()
    {
        _ringTimeoutTimer?.Stop();
        _ringTimeoutTimer = null;
    }

    // --- Screen sharing (server voice channels - see VoiceService.
    // StartScreenShareAsync/StopScreenShareAsync and ScreenCaptureSource) ---

    private void ScreenShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        if (_voice.IsScreenSharing)
        {
            _ = StopScreenShareAsync();
            return;
        }

        // Always prompts for a quality preset - no remembered choice from a
        // previous share gets silently reused, so the user picks what to
        // share (and at what quality) fresh every time.
        ScreenSharePresetMenu.PlacementTarget = ScreenShareButton;
        ScreenSharePresetMenu.IsOpen = true;
    }

    // Resolution+bitrate pairs scale with the frame-rate target so a higher
    // fps preset isn't starved by the same cap a 30fps share would use -
    // see the Phase 2 spike notes on the plan for why a shared fixed cap
    // skewed its fps comparison. Native/120fps stays labelled experimental
    // in the UI since the spike couldn't confirm it holds up through the
    // SFU in practice.
    private async void ScreenShare480p30Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(30, 2_500_000, 854, 480);
    private async void ScreenShare720p60Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(60, 6_000_000, 1280, 720);
    private async void ScreenShareNative120Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(120, 35_000_000, null, null);

    // Quick-share shortcut with a fixed 720p60 preset, skipping the preset
    // menu entirely (but still always prompting for a source, same as every
    // other share path - see StartScreenShareWithPresetAsync).
    private async void ScreenShareChooseSource_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        var item = await PickScreenShareSourceAsync();
        if (item is null) return;

        await StartScreenShareWithItemAsync(item, 60, 6_000_000, 1280, 720);
    }

    private async Task StartScreenShareWithPresetAsync(uint fps, uint bitrate, int? maxWidth, int? maxHeight)
    {
        try
        {
            var item = await PickScreenShareSourceAsync();
            if (item is null) return;

            await StartScreenShareWithItemAsync(item, fps, bitrate, maxWidth, maxHeight);
        }
        catch (Exception ex)
        {
            // Invoked fire-and-forget (`_ = StartScreenShareWithPresetAsync(...)`)
            // from the preset menu click handlers - PickScreenShareSourceAsync
            // and StartScreenShareWithItemAsync each already show a themed
            // error for their own common failure modes, so this is just a
            // backstop for anything that escapes both of those.
            LogBackgroundException(ex);
        }
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
            await AlertAsync("Error", $"Could not open the screen/window picker:\n{ex.Message}");
            return null;
        }
    }

    private async Task StartScreenShareWithItemAsync(Windows.Graphics.Capture.GraphicsCaptureItem item, uint fps, uint bitrate, int? maxWidth, int? maxHeight)
    {
        if (_voice is null) return;

        try
        {
            await _voice.StartScreenShareAsync(item, fps, bitrate, maxWidth, maxHeight);
        }
        catch (Exception ex)
        {
            await AlertAsync("Error", $"Could not start screen sharing:\n{ex.Message}");
            return;
        }

        UpdateScreenShareButtonVisual();
    }

    private void OnLocalScreenSharingChanged(bool isSharing)
    {
        if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
        var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
        var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
        if (me is not null) me.IsScreenSharing = isSharing;
    }

    private async Task StopScreenShareAsync()
    {
        if (_voice is null) return;
        await _voice.StopScreenShareAsync();
        UpdateScreenShareButtonVisual();
    }

    private void UpdateScreenShareButtonVisual()
    {
        if (_voice is null) return;

        ScreenShareButton.Content = "🖥️";
        ScreenShareButton.ToolTip = _voice.IsScreenSharing ? "Stop Sharing" : "Share Screen";
        ScreenShareButton.Background = _voice.IsScreenSharing
            ? (System.Windows.Media.Brush)FindResource("AccentBlurple")
            : System.Windows.Media.Brushes.Transparent;
    }

    private void OnRemoteScreenShareStarted(int userId, RemoteVideoPlayback playback)
    {
        // _voice is shared between channel voice and private calls -
        // CallWindow subscribes to this same event for call-scoped shares
        // (see CallWindow.OnRemoteScreenShareStarted), so this handler must
        // stay out of the way when the active voice context is a call.
        if (!_currentVoiceChannelId.HasValue) return;

        var member = FindVoiceChannelItem(_currentVoiceChannelId.Value)?.Members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) member.IsScreenSharing = true;

        // No auto-opened viewer - just remember the live playback so a
        // later click on this member's TV icon (ScreenShareIcon_MouseLeftButtonUp)
        // has something to open.
        _activeScreenShares[userId] = playback;
    }

    private void OnRemoteScreenShareStopped(int userId)
    {
        if (!_currentVoiceChannelId.HasValue) return;

        var member = FindVoiceChannelItem(_currentVoiceChannelId.Value)?.Members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) member.IsScreenSharing = false;

        _activeScreenShares.Remove(userId);
        if (_screenShareViewers.Remove(userId, out var viewer))
            viewer.Close();
    }

    // Click target for the blue TV icon next to a sharing member's name -
    // the only way a viewer window opens now (see OnRemoteScreenShareStarted).
    // Clicking an already-open sharer's icon activates the existing window
    // instead of opening a second one.
    private void ScreenShareIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int userId }) return;
        if (!_activeScreenShares.TryGetValue(userId, out var playback)) return;
        if (_voice is null) return;

        if (_screenShareViewers.TryGetValue(userId, out var existing))
        {
            existing.Activate();
            return;
        }

        var member = FindVoiceChannelItem(_currentVoiceChannelId ?? -1)?.Members.FirstOrDefault(m => m.UserId == userId);
        var viewer = new ScreenShareViewerWindow(member?.Username ?? "Someone", playback, _voice, userId);
        _screenShareViewers[userId] = viewer;
        viewer.Closed += (_, _) => _screenShareViewers.Remove(userId);
        viewer.Show();
    }

    private async void LeaveVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVoiceChannelId is null || _voice is null) return;

        await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
        await _voice.LeaveAllAsync();
        RemoveSelfFromVoiceRoster(_currentVoiceChannelId.Value);
        _currentVoiceChannelId = null;
        VoiceControlBar.Visibility = Visibility.Collapsed;
        ConnectionStatusText.Text = "";
    }

    // Removes just the local user's own entry from a voice channel's roster.
    // The old code called Members.Clear() here, which wiped out everyone
    // else still in the channel too - visible as "the whole list disappears"
    // the moment you leave, even though other people are still in the call.
    // Anyone else's departure is already handled correctly (one at a time)
    // by OnVoiceUserLeft reacting to the server's broadcast; this covers the
    // one case that broadcast doesn't reliably beat the UI update for - our
    // own leave, applied immediately rather than waiting on the round trip.
    private void RemoveSelfFromVoiceRoster(int channelId)
    {
        if (_api.CurrentUserId is null) return;
        var item = FindVoiceChannelItem(channelId);
        var self = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId);
        if (item is not null && self is not null) item.Members.Remove(self);
    }

    // UserVolumeStorage stores the 1.0-scale multiplier (matching
    // PlaybackVolume's own unit); VoiceMemberItem.Volume is the 0-200
    // percent scale the slider uses - this converts and defaults to 100%
    // (unchanged) for a user with no saved volume yet.
    private static double SavedVolumePercent(int userId) =>
        (UserVolumeStorage.GetVolume(userId) ?? 1.0f) * 100.0;

    // The session is already dead server-side by the time this fires (see
    // ApiService.SessionExpired) - just get the user back to a login screen,
    // no server call to make here unlike the normal logout button.
    private void RecentCallsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(new CallHistoryPage(_api), "Recent Calls");
    }

    private void OnForceMuted(int channelId)
    {
        if (_voice is null || channelId != _currentVoiceChannelId) return;
        _voice.IsMicMuted = true;
        UpdateMuteButtonVisual();
    }

    private void OnVoiceUserJoined(int userId, string username, int channelId, string? avatarUrl)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            if (item is not null && !item.Members.Any(m => m.UserId == userId))
                item.Members.Add(new VoiceMemberItem { UserId = userId, Username = username, AvatarUrl = App.ResolveUploadUrl(avatarUrl), IsSelf = userId == _api.CurrentUserId, Volume = SavedVolumePercent(userId) });

            // Only for the voice channel you're actually sitting in, and
            // only when you're not looking at the app - the icon/roster
            // update above already covers "looking at it" case.
            if (channelId == _currentVoiceChannelId && userId != _api.CurrentUserId && !IsActive)
            {
                NotificationService.PlayVoiceJoinSound();
                NotificationService.ShowToast("Voice", $"{username} joined voice");
            }
        });
    }

    private void OnVoiceUserLeft(int userId, string username, int channelId)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var existing = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (item is not null && existing is not null) item.Members.Remove(existing);

            if (channelId == _currentVoiceChannelId && userId != _api.CurrentUserId && !IsActive)
            {
                NotificationService.PlayVoiceLeaveSound();
                NotificationService.ShowToast("Voice", $"{username} left voice");
            }
        });
    }

    private void OnUserSpeaking(int userId, int channelId, bool isSpeaking)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsSpeaking = isSpeaking;
        });
    }

    // The speaking ring's pulse (MainWindow.xaml, the "SidebarSpeakingPulse"
    // BeginStoryboard) is RepeatBehavior="Forever" - if the member it
    // belongs to leaves the voice channel while still mid-speech, the row's
    // container gets torn down and removed from the visual tree, but
    // there's no guarantee WPF runs the DataTrigger's ExitActions
    // (StopStoryboard) first - a forcibly-removed container isn't the same
    // as the bound property naturally flipping IsSpeaking back to false.
    // The still-ticking "Forever" clock then keeps trying to apply its next
    // value to this Ellipse's RenderTransform after the container's local
    // value has already been torn down, which throws "Cannot animate ...
    // on an immutable object instance" on every tick - explains the
    // cascade of repeated error dialogs on join-then-leave. Explicitly
    // detaching the animation here, on Unloaded, guarantees the clock stops
    // regardless of whether the trigger's own ExitActions ran.
    private void SpeakingRing_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Shapes.Ellipse { RenderTransform: ScaleTransform scale })
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    // Fired from VoiceService's background audio-level detection, so this
    // isn't on the UI thread yet.
    private async Task OnLocalSpeakingChangedAsync(bool isSpeaking)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendSpeakingAsync(_currentVoiceChannelId.Value, isSpeaking);
        }
        catch
        {
            // Best-effort - a dropped speaking-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsSpeaking = isSpeaking;
        });
    }

    private void OnUserMuted(int userId, int channelId, bool isMuted)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsMuted = isMuted;
        });
    }

    private void OnUserDeafened(int userId, int channelId, bool isDeafened)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsDeafened = isDeafened;
        });
    }

    // Mirrors OnLocalSpeakingChangedAsync - broadcast to whoever else is in
    // the channel, then reflect it on our own row too (the row for "you"
    // needs the icon just as much as everyone else's).
    private async Task OnLocalMutedChangedAsync(bool isMuted)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendMutedAsync(_currentVoiceChannelId.Value, isMuted);
        }
        catch
        {
            // Best-effort - a dropped mute-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsMuted = isMuted;
        });
    }

    private async Task OnLocalDeafenedChangedAsync(bool isDeafened)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendDeafenedAsync(_currentVoiceChannelId.Value, isDeafened);
        }
        catch
        {
            // Best-effort - a dropped deafen-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsDeafened = isDeafened;
        });
    }

    // contentOverride lets a caller that already decrypted m.Content itself
    // (OnMessageReceived, working with a raw hub push) supply the plaintext
    // directly - callers going through LoadChannelHistoryAsync already
    // decrypt inline and pass the result the same way.
    private ChannelListItem? FindVoiceChannelItem(int channelId)
    {
        foreach (var c in _voiceChannels) if (c.Id == channelId) return c;
        return null;
    }

}
