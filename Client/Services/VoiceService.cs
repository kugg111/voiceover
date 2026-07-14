using System.Collections.Concurrent;
using System.Windows.Input;
using LiveKit.Rtc;
using SoundFlow.Extensions.WebRtc.Apm;
using Voiceover.Client.Models;
using Windows.Graphics.Capture;
using TrackSource = LiveKit.Proto.TrackSource;

namespace Voiceover.Client.Services;

public enum VoiceInputMode
{
    VoiceActivity, // default: mic always "open"; noise suppression handles the rest
    PushToTalk,    // muted at rest, open only while the hotkey is held
    PushToMute     // open at rest, muted only while the hotkey is held
}

// Which noise-suppression engine MicCaptureSource runs captured audio
// through - see the PackageReference comments in Client.csproj for why
// there's a choice at all instead of just one. WebRtcApm is first/default
// so existing saved settings (missing this field entirely) deserialize to
// today's behavior unchanged.
public enum NoiseSuppressionBackend
{
    WebRtcApm,
    RNNoise,
    DeepFilterNet
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

    private ScreenCaptureSource? _screenCapture;
    private LocalVideoTrack? _screenShareTrack;
    private readonly ConcurrentDictionary<int, RemoteVideoPlayback> _remoteVideoPlaybacks = new();

    // System-audio track published alongside the screen-share video track
    // (see ScreenAudioCaptureSource) - kept entirely separate from the mic
    // track/_remotePlaybacks above, since a sharing participant publishes
    // both at once and each side needs its own playback/volume/deafen state.
    private ScreenAudioCaptureSource? _screenAudioCapture;
    private LocalAudioTrack? _screenAudioTrack;
    private readonly ConcurrentDictionary<int, RemoteAudioPlayback> _remoteScreenAudioPlaybacks = new();

    public bool IsScreenSharing => _screenCapture is not null;

    // Fires when the LOCAL user's own screen-share state changes - lets the
    // UI update this client's own roster row without polling IsScreenSharing.
    public event Action<bool>? ScreenSharingChanged;

    // Fires when a remote participant starts/stops sharing their screen -
    // one video track max per participant (this app doesn't support camera
    // video, only screen share), so userId is enough to key on.
    public event Action<int, RemoteVideoPlayback>? RemoteScreenShareStarted;
    public event Action<int>? RemoteScreenShareStopped;

    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    // Fires when local mic volume crosses the speaking threshold in either
    // direction, so the UI can show a "you're speaking" indicator and relay
    // it to other participants (see MainWindow's use of SendSpeakingAsync).
    public event Action<bool>? LocalSpeakingChanged;

    private volatile bool _localIsSpeaking;
    private DateTime _lastLoudSampleUtc = DateTime.MinValue;
    private static readonly TimeSpan SpeakingHangover = TimeSpan.FromMilliseconds(400);

    // RMS threshold on the post-noise-suppression, post-gain signal (see
    // MicCaptureSource.OnProcessedFrame) - i.e. this measures exactly what
    // actually gets published, so the dot is meant to mean "audible to
    // others" rather than "detected as speech". Originally 800 (~-32 dBFS),
    // which was tuned for clear speech and missed quieter but still
    // noticeable background noise that leaks past noise suppression -
    // lowered to be more sensitive to that. Still well above the near-silent
    // noise floor (raw ADC hiss boosted by MicGain typically sits in the
    // tens), so it shouldn't light up on pure silence.
    private const int SpeakingRmsThreshold = 300; // ~-41 dBFS

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

    private NoiseSuppressionBackend _noiseSuppressionBackend = NoiseSuppressionBackend.WebRtcApm;
    public NoiseSuppressionBackend NoiseSuppressionBackend
    {
        get => _noiseSuppressionBackend;
        set
        {
            _noiseSuppressionBackend = value;
            if (_micCapture is not null) _micCapture.NoiseSuppressionBackend = value;
            SaveSettings();
        }
    }

    // Only meaningful for the DeepFilterNet backend - how aggressively it
    // suppresses detected noise (0-100dB, matching the slider DeepFilterNet's
    // own demo shows). See LadspaHost.AttenuationLimit for the LADSPA
    // control-port plumbing this ultimately drives.
    private float _deepFilterAttenuationLimit = LadspaHost.AttenuationLimitMax;
    public float DeepFilterAttenuationLimit
    {
        get => _deepFilterAttenuationLimit;
        set
        {
            _deepFilterAttenuationLimit = value;
            if (_micCapture is not null) _micCapture.DeepFilterAttenuationLimit = value;
            SaveSettings();
        }
    }

    // Only meaningful for the DeepFilterNet backend - how much smoothing/
    // artifact-reduction the plugin applies after its main filter pass
    // (0-1). See LadspaHost.PostFilterBeta for the LADSPA control-port
    // plumbing this ultimately drives.
    private float _deepFilterPostFilterBeta = LadspaHost.PostFilterBetaMin;
    public float DeepFilterPostFilterBeta
    {
        get => _deepFilterPostFilterBeta;
        set
        {
            _deepFilterPostFilterBeta = value;
            if (_micCapture is not null) _micCapture.DeepFilterPostFilterBeta = value;
            SaveSettings();
        }
    }

    // Only meaningful for the WebRtcApm backend - how aggressively its
    // noise suppression runs (Low/Moderate/High/VeryHigh). Was hardcoded to
    // High before this existed.
    private NoiseSuppressionLevel _webRtcNoiseSuppressionLevel = NoiseSuppressionLevel.High;
    public NoiseSuppressionLevel WebRtcNoiseSuppressionLevel
    {
        get => _webRtcNoiseSuppressionLevel;
        set
        {
            _webRtcNoiseSuppressionLevel = value;
            if (_micCapture is not null) _micCapture.WebRtcNoiseSuppressionLevel = value;
            SaveSettings();
        }
    }

    // Applies to whichever backend is selected - 0-1 wet/dry blend of the
    // suppressed signal against the raw captured signal, so an
    // over-suppressing backend (eating quiet speech, "musical noise"
    // artifacts) can be backed off without switching backends entirely.
    // 1 (fully processed) matches every backend's behavior before this
    // setting existed.
    private float _suppressionMix = 1f;
    public float SuppressionMix
    {
        get => _suppressionMix;
        set
        {
            _suppressionMix = value;
            if (_micCapture is not null) _micCapture.SuppressionMix = value;
            SaveSettings();
        }
    }

    // How long an outgoing private call rings before auto-cancelling (see
    // MainWindow.StartRingTimeout) - a local-machine preference like the
    // rest of this class's settings, not something worth a server round trip.
    private int _ringTimeoutSeconds = 40;
    public int RingTimeoutSeconds
    {
        get => _ringTimeoutSeconds;
        set
        {
            _ringTimeoutSeconds = value;
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
            foreach (var playback in _remoteScreenAudioPlaybacks.Values) playback.Deafened = value;
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

    // Mutually exclusive with PushToTalkMouseButton - setting one clears
    // the other (see GlobalHotkeyService.WatchedKey/WatchedMouseButton).
    public Key? PushToTalkKey
    {
        get => _hotkey.WatchedKey;
        set { _hotkey.WatchedKey = value; SaveSettings(); }
    }

    public MouseButton? PushToTalkMouseButton
    {
        get => _hotkey.WatchedMouseButton;
        set { _hotkey.WatchedMouseButton = value; SaveSettings(); }
    }

    // Devices, noise suppression, input mode and hotkey are local-machine
    // preferences ("which mic", "which key") rather than account data, so
    // they're saved to a local file (VoiceSettingsStorage) instead of the
    // database - they survive a log out/in on this machine, same as before
    // this existed they'd silently reset to defaults every login.
    private void SaveSettings() =>
        VoiceSettingsStorage.Save(new SavedVoiceSettings(
            InputDeviceIndex, OutputDeviceIndex, NoiseSuppressionEnabled, _inputMode, PushToTalkKey, PushToTalkMouseButton, _noiseSuppressionBackend, _deepFilterAttenuationLimit, _ringTimeoutSeconds,
            _webRtcNoiseSuppressionLevel, _deepFilterPostFilterBeta, _suppressionMix));

    private void LoadSettings()
    {
        var saved = VoiceSettingsStorage.Load();
        if (saved is null) return;

        _inputDeviceIndex = saved.InputDeviceIndex;
        _outputDeviceIndex = saved.OutputDeviceIndex;
        _noiseSuppressionEnabled = saved.NoiseSuppressionEnabled;
        _noiseSuppressionBackend = saved.NoiseSuppressionBackend;
        _deepFilterAttenuationLimit = saved.DeepFilterAttenuationLimit;
        _ringTimeoutSeconds = saved.RingTimeoutSeconds;
        _webRtcNoiseSuppressionLevel = saved.WebRtcNoiseSuppressionLevel;
        _deepFilterPostFilterBeta = saved.DeepFilterPostFilterBeta;
        _suppressionMix = saved.SuppressionMix;

        // Mouse button takes priority if somehow both are set in the saved
        // file (shouldn't happen given the mutual-exclusivity setters, but
        // the file is hand-editable/could be from an older version) -
        // assigning WatchedMouseButton second means it wins by clearing
        // WatchedKey right back out.
        _hotkey.WatchedKey = saved.PushToTalkKey;
        _hotkey.WatchedMouseButton = saved.PushToTalkMouseButton;

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

    // Runs the actual join off the calling (UI) thread - see ConnectRoomAsync
    // for why. Task.Run's delegate has no captured SynchronizationContext, so
    // every await inside the join path keeps resuming on a thread-pool
    // thread instead of hopping back to the dispatcher.
    public Task JoinChannelAsync(int channelId) => Task.Run(() => JoinChannelAsyncCore(channelId));

    // Same join machinery, for a private call instead of a server voice
    // channel - see ChatHub.GetCallToken server-side. A call's LiveKit room
    // is named after its generated call id rather than a DB channel id,
    // but everything past "get a token" is identical, hence sharing
    // ConnectRoomAsync below instead of duplicating it.
    public Task JoinCallAsync(string callId) => Task.Run(() => JoinCallAsyncCore(callId));

    private async Task JoinChannelAsyncCore(int channelId)
    {
        var response = await _hub.GetLiveKitTokenAsync(channelId);
        if (string.IsNullOrEmpty(response.ServerUrl))
            throw new InvalidOperationException("Voice chat isn't configured on the server yet.");
        await ConnectRoomAsync(response);
    }

    private async Task JoinCallAsyncCore(string callId)
    {
        var response = await _hub.GetCallTokenAsync(callId);
        if (string.IsNullOrEmpty(response.ServerUrl))
            throw new InvalidOperationException("Voice chat isn't configured on the server yet.");
        await ConnectRoomAsync(response);
    }

    // Connects a LiveKit Room and publishes the local mic track - the part
    // that's identical whether the token came from a channel join or a
    // private call (identity = our own numeric userId either way, see
    // LiveKitTokenService server-side). Replaces the old SetActiveChannel +
    // ConnectToExistingMembersAsync pair from the mesh days - there's no
    // "connect to everyone already there" step anymore, LiveKit's server
    // handles fanning existing participants' tracks out to us as part of
    // the room connection itself.
    //
    // Constructing MicCaptureSource below loads three native libraries
    // (WebRTC APM, RNNoise, and the ~50MB DeepFilterNet LADSPA plugin) the
    // first time any of them run in this process - LoadLibrary plus P/Invoke
    // stub JIT compilation is a real, synchronous, one-time cost. Without the
    // Task.Run wrapper on the callers above, that would run straight on the
    // WPF dispatcher thread (since nothing here uses ConfigureAwait(false))
    // and freeze the whole UI - including the mouse cursor - for however
    // long it takes. Everything downstream (RemoteAudioPlayback creation,
    // _remotePlaybacks) is already thread-safe/UI-agnostic, and MainWindow
    // already marshals PeerConnected/PeerDisconnected back to the UI thread
    // via Dispatcher.Invoke, so running this whole method off-thread is safe.
    private async Task ConnectRoomAsync(LiveKitJoinResponse response)
    {
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
            NoiseSuppressionEnabled = NoiseSuppressionEnabled,
            NoiseSuppressionBackend = NoiseSuppressionBackend,
            DeepFilterAttenuationLimit = DeepFilterAttenuationLimit,
            DeepFilterPostFilterBeta = DeepFilterPostFilterBeta,
            WebRtcNoiseSuppressionLevel = WebRtcNoiseSuppressionLevel,
            SuppressionMix = SuppressionMix
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
        if (!int.TryParse(e.Participant.Identity, out var userId)) return;

        if (e.Track is RemoteAudioTrack audioTrack)
        {
            var playback = new RemoteAudioPlayback(audioTrack, OutputDeviceIndex ?? -1)
            {
                Deafened = IsDeafened,
                PlaybackVolume = UserVolumeStorage.GetVolume(userId) ?? 1.0f
            };

            // Two possible audio tracks per participant now (mic + this
            // person's screen-share system audio, see
            // ScreenAudioCaptureSource) - Name distinguishes which one this
            // subscription is for so they land in separate dictionaries
            // instead of one overwriting the other.
            if (audioTrack.Name == "system-audio")
                _remoteScreenAudioPlaybacks[userId] = playback;
            else
                _remotePlaybacks[userId] = playback;
        }
        else if (e.Track is RemoteVideoTrack videoTrack)
        {
            var playback = new RemoteVideoPlayback(videoTrack);
            _remoteVideoPlaybacks[userId] = playback;
            RemoteScreenShareStarted?.Invoke(userId, playback);
        }
    }

    private async void OnTrackUnsubscribed(object? sender, TrackSubscribedEventArgs e)
    {
        if (!int.TryParse(e.Participant.Identity, out var userId)) return;

        // Scoped to the specific track kind that went away - a participant
        // can have both an audio and a screen-share video track at once, so
        // one ending (e.g. they stop sharing) must not tear down the other.
        if (e.Track is RemoteAudioTrack audioTrack)
        {
            if (audioTrack.Name == "system-audio")
            {
                if (_remoteScreenAudioPlaybacks.TryRemove(userId, out var screenAudioPlayback))
                    await screenAudioPlayback.DisposeAsync();
            }
            else if (_remotePlaybacks.TryRemove(userId, out var playback))
            {
                await playback.DisposeAsync();
            }
        }
        else if (e.Track is RemoteVideoTrack)
        {
            if (_remoteVideoPlaybacks.TryRemove(userId, out var playback))
            {
                await playback.DisposeAsync();
                RemoteScreenShareStopped?.Invoke(userId);
            }
        }
    }

    private async void OnParticipantDisconnected(object? sender, Participant e)
    {
        if (!int.TryParse(e.Identity, out var userId)) return;

        // TrackUnsubscribed should already have cleaned these up - a
        // participant disconnecting always implies their tracks going away
        // too - but this covers it defensively in case that event doesn't
        // fire for some reason.
        if (_remotePlaybacks.TryRemove(userId, out var playback))
            await playback.DisposeAsync();

        if (_remoteScreenAudioPlaybacks.TryRemove(userId, out var screenAudioPlayback))
            await screenAudioPlayback.DisposeAsync();

        if (_remoteVideoPlaybacks.TryRemove(userId, out var videoPlayback))
        {
            await videoPlayback.DisposeAsync();
            RemoteScreenShareStopped?.Invoke(userId);
        }

        PeerDisconnected?.Invoke(userId);
    }

    // Publishes a screen/window capture as a video track - maxFramerate
    // drives both the capture pipeline's actual frame rate ceiling (see
    // ScreenCaptureSource) and what LiveKit is told to expect, so encoder
    // bitrate allocation matches what's really being sent. maxWidth/
    // maxHeight cap the published resolution (null = native/uncapped).
    // Only one share at a time; starting a new one while already sharing
    // stops the old capture first rather than publishing two video tracks.
    public async Task StartScreenShareAsync(GraphicsCaptureItem item, uint maxFramerate, uint maxBitrate,
        int? maxWidth = null, int? maxHeight = null)
    {
        if (_room?.LocalParticipant is null) return;
        if (_screenCapture is not null) await StopScreenShareAsync();

        var capture = new ScreenCaptureSource(item, maxWidth, maxHeight);
        var track = LocalVideoTrack.Create("screen", capture.Source);
        var options = new TrackPublishOptions
        {
            Source = TrackSource.SourceScreenshare,
            VideoEncoding = new VideoEncodingOptions { MaxFramerate = maxFramerate, MaxBitrate = maxBitrate },
            // Lets LiveKit generate lower-quality simulcast layers alongside
            // the full one, so a viewer on a weaker connection gets a
            // downgraded layer from the SFU instead of the sender's raw
            // target bitrate regardless of what the viewer can actually take.
            Simulcast = true
        };

        await _room.LocalParticipant.PublishTrackAsync(track, options, CancellationToken.None);

        _screenCapture = capture;
        _screenShareTrack = track;
        ScreenSharingChanged?.Invoke(true);

        await StartScreenAudioAsync();
    }

    // Best-effort second track alongside the video one above - a failure
    // here (see ScreenAudioCaptureSource for why loopback capture can fail)
    // must never take down an otherwise-working video-only share.
    private async Task StartScreenAudioAsync()
    {
        if (_room?.LocalParticipant is null) return;

        try
        {
            var audioCapture = new ScreenAudioCaptureSource();
            var audioTrack = LocalAudioTrack.Create("system-audio", audioCapture.Source);
            var audioOptions = new TrackPublishOptions { Source = TrackSource.SourceScreenshareAudio };
            await _room.LocalParticipant.PublishTrackAsync(audioTrack, audioOptions, CancellationToken.None);

            _screenAudioCapture = audioCapture;
            _screenAudioTrack = audioTrack;
        }
        catch
        {
            _screenAudioCapture?.Dispose();
            _screenAudioCapture = null;
            _screenAudioTrack = null;
        }
    }

    public async Task StopScreenShareAsync()
    {
        if (_screenShareTrack is null) return;

        if (_room?.LocalParticipant is not null)
        {
            // Best-effort - if the room is already tearing down (e.g. this
            // raced LeaveAllAsync), there's nothing left to unpublish from.
            try { await _room.LocalParticipant.UnpublishTrackAsync(_screenShareTrack.Sid, CancellationToken.None); }
            catch { }

            if (_screenAudioTrack is not null)
            {
                try { await _room.LocalParticipant.UnpublishTrackAsync(_screenAudioTrack.Sid, CancellationToken.None); }
                catch { }
            }
        }

        _screenShareTrack.Dispose();
        _screenShareTrack = null;
        _screenCapture?.Dispose();
        _screenCapture = null;

        _screenAudioTrack?.Dispose();
        _screenAudioTrack = null;
        _screenAudioCapture?.Dispose();
        _screenAudioCapture = null;

        ScreenSharingChanged?.Invoke(false);
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

        foreach (var userId in _remoteScreenAudioPlaybacks.Keys.ToList())
            if (_remoteScreenAudioPlaybacks.TryRemove(userId, out var screenAudioPlayback))
                await screenAudioPlayback.DisposeAsync();

        foreach (var userId in _remoteVideoPlaybacks.Keys.ToList())
        {
            if (_remoteVideoPlaybacks.TryRemove(userId, out var videoPlayback))
            {
                await videoPlayback.DisposeAsync();
                RemoteScreenShareStopped?.Invoke(userId);
            }
        }

        // Screen share isn't unpublished via StopScreenShareAsync here - the
        // room is about to be disconnected/disposed wholesale anyway, so
        // there's no separate participant left to notify.
        _screenShareTrack?.Dispose();
        _screenShareTrack = null;
        _screenCapture?.Dispose();
        _screenCapture = null;

        _screenAudioTrack?.Dispose();
        _screenAudioTrack = null;
        _screenAudioCapture?.Dispose();
        _screenAudioCapture = null;

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
