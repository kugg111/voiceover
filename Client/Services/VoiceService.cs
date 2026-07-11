using System.Collections.Concurrent;
using System.Windows.Input;
using LiveKit.Rtc;

namespace Voiceover.Client.Services;

public enum VoiceInputMode
{
    VoiceActivity, // default: mic always "open"; noise suppression handles the rest
    PushToTalk,    // muted at rest, open only while the hotkey is held
    PushToMute     // open at rest, muted only while the hotkey is held
}

// Connects to the self-hosted LiveKit SFU (see REDEPLOY.txt) for voice
// audio. Replaces the old WebRTC mesh - one RTCPeerConnection per remote
// participant doesn't scale, and Railway (where the app server lives)
// doesn't support inbound UDP, which ruled out hosting a relay ourselves.
// One Room connection per joined channel now, instead of N-1 direct
// connections; LiveKit's server owns all track lifecycle (who's publishing,
// who should receive what), so there's no more manual SDP/ICE signaling or
// initiator-election logic to maintain here.
public class VoiceService : IAsyncDisposable
{
    private readonly SignalRService _hub;
    private readonly int _selfUserId;

    private Room? _room;
    private MicCaptureSource? _micCapture;
    private LocalAudioTrack? _localTrack;
    private readonly ConcurrentDictionary<int, RemoteAudioPlayback> _remotePlaybacks = new();

    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    // Fires when local mic volume crosses the speaking threshold in either
    // direction, so the UI can show a "you're speaking" indicator and relay
    // it to other participants (see MainWindow's use of SendSpeakingAsync).
    public event Action<bool>? LocalSpeakingChanged;

    private volatile bool _localIsSpeaking;
    private DateTime _lastLoudSampleUtc = DateTime.MinValue;
    private static readonly TimeSpan SpeakingHangover = TimeSpan.FromMilliseconds(400);
    private const int SpeakingRmsThreshold = 800; // empirical threshold for 16-bit PCM RMS

    // Device indices from AudioDeviceService (null/-1 = system default). Applied
    // when the mic capture/room connection is (re)created, so a change here
    // takes effect on the next channel join rather than hot-swapping an
    // active call.
    private int? _inputDeviceIndex;
    public int? InputDeviceIndex
    {
        get => _inputDeviceIndex;
        set { _inputDeviceIndex = value; SaveSettings(); }
    }

    private int? _outputDeviceIndex;
    public int? OutputDeviceIndex
    {
        get => _outputDeviceIndex;
        set { _outputDeviceIndex = value; SaveSettings(); }
    }

    // Mute can change from several independent places - the mute button,
    // deafen (couples to it), an input mode switch (PTT/push-to-mute reset
    // it to their resting state), and the PTT/push-to-mute hotkey itself -
    // so the UI can't just refresh its own button inline after calling this
    // setter anymore. MicMutedChanged is the one place any of them can
    // listen to stay in sync, instead of every caller needing to know about
    // every button that displays mute state.
    public event Action<bool>? MicMutedChanged;

    private bool _isMicMuted;
    public bool IsMicMuted
    {
        get => _isMicMuted;
        set
        {
            var changed = _isMicMuted != value;
            _isMicMuted = value;
            if (_micCapture is not null) _micCapture.MicMuted = value;

            // Muting stops processed-frame callbacks entirely (see
            // MicCaptureSource.OnMicDataAvailable), so the hangover-based
            // "still speaking" expiry in OnLocalRawSample never gets a
            // chance to run - clear it immediately instead of leaving the
            // speaking indicator stuck on until it happens to time out.
            if (value && _localIsSpeaking)
            {
                _localIsSpeaking = false;
                LocalSpeakingChanged?.Invoke(false);
            }

            if (changed) MicMutedChanged?.Invoke(value);
        }
    }

    private bool _noiseSuppressionEnabled = true;
    public bool NoiseSuppressionEnabled
    {
        get => _noiseSuppressionEnabled;
        set
        {
            _noiseSuppressionEnabled = value;
            if (_micCapture is not null) _micCapture.NoiseSuppressionEnabled = value;
            SaveSettings();
        }
    }

    // Not persisted (see SaveSettings) - deafen/mute are session states
    // like Discord's, not preferences, so a fresh login always starts
    // undeafened/unmuted regardless of how a previous session ended.
    public event Action<bool>? DeafenedChanged;

    private bool _isDeafened;

    // Deafen always drives mute to match its own new state, same as
    // Discord: turning deafen on also mutes (no point talking if you can't
    // hear yourself), turning it off restores mic to whatever "at rest"
    // means for the current input mode - PushToTalk's resting state is
    // muted-until-held, so undeafening shouldn't silently force the mic
    // open there. Muting on its own doesn't touch deafen - only the
    // deafen control couples the two.
    public bool IsDeafened
    {
        get => _isDeafened;
        set
        {
            _isDeafened = value;
            foreach (var playback in _remotePlaybacks.Values) playback.Deafened = value;
            IsMicMuted = value || _inputMode == VoiceInputMode.PushToTalk;
            DeafenedChanged?.Invoke(value);
        }
    }

    private readonly GlobalHotkeyService _hotkey = new();
    private VoiceInputMode _inputMode = VoiceInputMode.VoiceActivity;

    public VoiceInputMode InputMode
    {
        get => _inputMode;
        set
        {
            if (_inputMode == value) return;
            _inputMode = value;
            ApplyInputMode();
            SaveSettings();
        }
    }

    public Key PushToTalkKey
    {
        get => _hotkey.WatchedKey;
        set { _hotkey.WatchedKey = value; SaveSettings(); }
    }

    // Devices, noise suppression, input mode and hotkey are local-machine
    // preferences ("which mic", "which key") rather than account data, so
    // they're saved to a local file (VoiceSettingsStorage) instead of the
    // database - they survive a log out/in on this machine, same as before
    // this existed they'd silently reset to defaults every login.
    private void SaveSettings() =>
        VoiceSettingsStorage.Save(new SavedVoiceSettings(
            InputDeviceIndex, OutputDeviceIndex, NoiseSuppressionEnabled, _inputMode, PushToTalkKey));

    private void LoadSettings()
    {
        var saved = VoiceSettingsStorage.Load();
        if (saved is null) return;

        _inputDeviceIndex = saved.InputDeviceIndex;
        _outputDeviceIndex = saved.OutputDeviceIndex;
        _noiseSuppressionEnabled = saved.NoiseSuppressionEnabled;
        _hotkey.WatchedKey = saved.PushToTalkKey;

        if (saved.InputMode != VoiceInputMode.VoiceActivity)
        {
            _inputMode = saved.InputMode;
            ApplyInputMode();
        }
    }

    // Resets the mic to whatever "at rest" means for the newly selected
    // mode, and only keeps the global hotkey hook installed while a push
    // mode is actually in use - no reason to have a systemwide keyboard
    // hook running for voice-activity mode, where it'd never be used.
    private void ApplyInputMode()
    {
        _hotkey.Stop();

        switch (_inputMode)
        {
            case VoiceInputMode.PushToTalk:
                IsMicMuted = true;
                _hotkey.Start();
                break;
            case VoiceInputMode.PushToMute:
                IsMicMuted = false;
                _hotkey.Start();
                break;
            default:
                IsMicMuted = false;
                break;
        }
    }

    private void OnHotkeyDown()
    {
        if (_inputMode == VoiceInputMode.PushToTalk) IsMicMuted = false;
        else if (_inputMode == VoiceInputMode.PushToMute) IsMicMuted = true;
    }

    private void OnHotkeyUp()
    {
        if (_inputMode == VoiceInputMode.PushToTalk) IsMicMuted = true;
        else if (_inputMode == VoiceInputMode.PushToMute) IsMicMuted = false;
    }

    // Per-remote-participant volume (1.0 = unchanged). No-op for a userId
    // that isn't currently connected - the slider only makes sense/exists
    // for people you're actually in a call with, see MainWindow's voice
    // member list.
    public void SetRemoteVolume(int userId, float volume)
    {
        if (_remotePlaybacks.TryGetValue(userId, out var playback))
            playback.PlaybackVolume = volume;
    }

    public VoiceService(SignalRService hub, int selfUserId)
    {
        _hub = hub;
        _selfUserId = selfUserId;

        _hotkey.KeyDown += OnHotkeyDown;
        _hotkey.KeyUp += OnHotkeyUp;

        LoadSettings();
    }

    // Fetches a LiveKit join token for this channel (identity = our own
    // numeric userId, see LiveKitTokenService server-side) and connects.
    // Replaces the old SetActiveChannel + ConnectToExistingMembersAsync pair
    // from the mesh days - there's no "connect to everyone already there"
    // step anymore, LiveKit's server handles fanning existing participants'
    // tracks out to us as part of the room connection itself.
    public async Task JoinChannelAsync(int channelId)
    {
        var response = await _hub.GetLiveKitTokenAsync(channelId);
        if (string.IsNullOrEmpty(response.ServerUrl))
            throw new InvalidOperationException("Voice chat isn't configured on the server yet.");

        var room = new Room();
        room.TrackSubscribed += OnTrackSubscribed;
        room.TrackUnsubscribed += OnTrackUnsubscribed;
        room.ParticipantDisconnected += OnParticipantDisconnected;
        room.Connected += (_, _) => PeerConnected?.Invoke(_selfUserId);
        room.Disconnected += (_, _) => PeerDisconnected?.Invoke(_selfUserId);

        await room.ConnectAsync(response.ServerUrl, response.Token, new RoomOptions(), CancellationToken.None);
        _room = room;

        var micCapture = new MicCaptureSource(InputDeviceIndex ?? -1)
        {
            MicMuted = IsMicMuted,
            NoiseSuppressionEnabled = NoiseSuppressionEnabled
        };
        // Local speaking-indicator detection, straight off the fully
        // processed PCM MicCaptureSource already produces (post noise-
        // suppression, post-gain) - reacts the same way the published
        // audio actually sounds.
        micCapture.OnProcessedFrame += OnLocalRawSample;
        _micCapture = micCapture;

        _localTrack = LocalAudioTrack.Create("mic", micCapture.Source);
        // Non-null once ConnectAsync above has completed successfully.
        await room.LocalParticipant!.PublishTrackAsync(_localTrack, new TrackPublishOptions(), CancellationToken.None);
    }

    private void OnTrackSubscribed(object? sender, TrackSubscribedEventArgs e)
    {
        if (e.Track is not RemoteAudioTrack audioTrack) return;
        if (!int.TryParse(e.Participant.Identity, out var userId)) return;

        var playback = new RemoteAudioPlayback(audioTrack, OutputDeviceIndex ?? -1) { Deafened = IsDeafened };
        _remotePlaybacks[userId] = playback;
    }

    private async void OnTrackUnsubscribed(object? sender, TrackSubscribedEventArgs e)
    {
        if (!int.TryParse(e.Participant.Identity, out var userId)) return;
        if (_remotePlaybacks.TryRemove(userId, out var playback))
            await playback.DisposeAsync();
    }

    private async void OnParticipantDisconnected(object? sender, Participant e)
    {
        if (!int.TryParse(e.Identity, out var userId)) return;

        // TrackUnsubscribed should already have cleaned this up - a
        // participant disconnecting always implies their tracks going away
        // too - but this covers it defensively in case that event doesn't
        // fire for some reason.
        if (_remotePlaybacks.TryRemove(userId, out var playback))
            await playback.DisposeAsync();

        PeerDisconnected?.Invoke(userId);
    }

    // Leaves the current room and tears down capture/playback, but keeps
    // the hotkey hook and saved settings alive - called both when actually
    // leaving voice and when switching from one channel to another (leave
    // then immediately JoinChannelAsync the new one).
    public async Task LeaveAllAsync()
    {
        if (_localIsSpeaking)
        {
            _localIsSpeaking = false;
            LocalSpeakingChanged?.Invoke(false);
        }

        foreach (var userId in _remotePlaybacks.Keys.ToList())
            if (_remotePlaybacks.TryRemove(userId, out var playback))
                await playback.DisposeAsync();

        if (_micCapture is not null)
        {
            _micCapture.OnProcessedFrame -= OnLocalRawSample;
            _micCapture.Dispose();
            _micCapture = null;
        }
        _localTrack = null;

        if (_room is not null)
        {
            await _room.DisconnectAsync();
            _room.Dispose();
            _room = null;
        }
    }

    private void OnLocalRawSample(short[] pcm)
    {
        if (pcm.Length == 0) return;

        long sumSquares = 0;
        foreach (var s in pcm) sumSquares += (long)s * s;
        var rms = Math.Sqrt(sumSquares / (double)pcm.Length);

        var now = DateTime.UtcNow;
        if (rms > SpeakingRmsThreshold)
        {
            _lastLoudSampleUtc = now;
            if (!_localIsSpeaking)
            {
                _localIsSpeaking = true;
                LocalSpeakingChanged?.Invoke(true);
            }
        }
        else if (_localIsSpeaking && now - _lastLoudSampleUtc > SpeakingHangover)
        {
            _localIsSpeaking = false;
            LocalSpeakingChanged?.Invoke(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await LeaveAllAsync();
        _hotkey.Dispose();
    }
}
