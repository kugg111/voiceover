using System.Collections.Concurrent;
using LiveKit.Rtc;
using Voiceover.Client.Services;

namespace Voiceover.Client.Linux;

// Scoped-down Linux port of the WPF client's VoiceService
// (Client/Services/VoiceService.cs) - voice-activity join/leave/mute/
// deafen, device selection, and NSNet2 noise suppression. No push-to-talk
// hotkey (Win32-only anyway), screen share, or private calls yet - each is
// its own separate follow-up (see the Linux client plan's Phase 2 deferred
// list).
//
// Deliberately a standalone class rather than editing the WPF VoiceService
// or extracting a shared one - that class is production code with years
// of proven behavior; converging the two is a later consolidation once
// this is proven on real Linux hardware, not a day-one requirement.
public class VoiceServiceLinux : IAsyncDisposable
{
    private readonly SignalRService _hub;
    private readonly int _selfUserId;

    private Room? _room;
    private MicCaptureSourceLinux? _micCapture;
    private LocalAudioTrack? _localTrack;
    private readonly ConcurrentDictionary<int, RemoteAudioPlaybackLinux> _remotePlaybacks = new();

    public int? CurrentChannelId { get; private set; }

    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    // Fires when the local mic's (post-gain, post-noise-suppression)
    // signal crosses the speaking RMS threshold - same constant/hangover
    // logic as the WPF client's VoiceService.OnLocalRawSample.
    public event Action<bool>? LocalSpeakingChanged;

    private volatile bool _localIsSpeaking;
    private DateTime _lastLoudSampleUtc = DateTime.MinValue;
    private static readonly TimeSpan SpeakingHangover = TimeSpan.FromMilliseconds(400);
    private const int SpeakingRmsThreshold = 300;

    // Device indices from PortAudio's enumeration (null/-1 = system
    // default). Applied when the mic capture/room connection is (re)created,
    // so a change here takes effect on the next channel join rather than
    // hot-swapping an active call - same as the WPF client's own devices.
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

    private float _micGain = 4.0f;
    public float MicGain
    {
        get => _micGain;
        set
        {
            _micGain = value;
            if (_micCapture is not null) _micCapture.MicGain = value;
            SaveSettings();
        }
    }

    private void SaveSettings() =>
        VoiceSettingsStorageLinux.Save(new SavedVoiceSettingsLinux(
            InputDeviceIndex, OutputDeviceIndex, NoiseSuppressionEnabled, SuppressionMix, MicGain));

    private void LoadSettings()
    {
        var saved = VoiceSettingsStorageLinux.Load();
        if (saved is null) return;

        _inputDeviceIndex = saved.InputDeviceIndex;
        _outputDeviceIndex = saved.OutputDeviceIndex;
        _noiseSuppressionEnabled = saved.NoiseSuppressionEnabled;
        _suppressionMix = saved.SuppressionMix;
        _micGain = saved.MicGain > 0 ? saved.MicGain : 4.0f;
    }

    private bool _isMicMuted;
    public bool IsMicMuted
    {
        get => _isMicMuted;
        set
        {
            _isMicMuted = value;
            if (_micCapture is not null) _micCapture.MicMuted = value;

            if (value && _localIsSpeaking)
            {
                _localIsSpeaking = false;
                LocalSpeakingChanged?.Invoke(false);
            }
        }
    }

    private bool _isDeafened;
    public bool IsDeafened
    {
        get => _isDeafened;
        set
        {
            _isDeafened = value;
            foreach (var playback in _remotePlaybacks.Values) playback.Deafened = value;
            // Deafen also mutes, matches the WPF client's own coupling -
            // no point talking if you can't hear yourself.
            IsMicMuted = value || IsMicMuted;
        }
    }

    public VoiceServiceLinux(SignalRService hub, int selfUserId)
    {
        _hub = hub;
        _selfUserId = selfUserId;
        LoadSettings();
    }

    // Runs the actual join off the calling (UI) thread - constructing
    // MicCaptureSourceLinux loads the native PortAudio library (and the
    // NSNet2 ONNX session) the first time it runs in this process
    // (LoadLibrary + P/Invoke stub JIT is a real synchronous cost), same
    // reasoning the WPF client's JoinChannelAsync has for its own Task.Run
    // wrapper.
    public Task JoinChannelAsync(int channelId) => Task.Run(() => JoinChannelAsyncCore(channelId));

    private async Task JoinChannelAsyncCore(int channelId)
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
        CurrentChannelId = channelId;

        var micCapture = new MicCaptureSourceLinux(InputDeviceIndex ?? -1)
        {
            MicMuted = IsMicMuted,
            NoiseSuppressionEnabled = NoiseSuppressionEnabled,
            SuppressionMix = SuppressionMix,
            MicGain = MicGain
        };
        micCapture.OnProcessedFrame += OnLocalProcessedSample;
        _micCapture = micCapture;

        _localTrack = LocalAudioTrack.Create("mic", micCapture.Source);
        // Non-null once ConnectAsync above has completed successfully.
        await room.LocalParticipant!.PublishTrackAsync(_localTrack, new TrackPublishOptions(), CancellationToken.None);
    }

    private void OnTrackSubscribed(object? sender, TrackSubscribedEventArgs e)
    {
        if (!int.TryParse(e.Participant.Identity, out var userId)) return;
        if (e.Track is not RemoteAudioTrack audioTrack) return;

        var playback = new RemoteAudioPlaybackLinux(audioTrack, OutputDeviceIndex ?? -1) { Deafened = IsDeafened };
        _remotePlaybacks[userId] = playback;
    }

    private async void OnTrackUnsubscribed(object? sender, TrackSubscribedEventArgs e)
    {
        try
        {
            if (!int.TryParse(e.Participant.Identity, out var userId)) return;
            if (e.Track is not RemoteAudioTrack) return;
            if (_remotePlaybacks.TryRemove(userId, out var playback)) await playback.DisposeAsync();
        }
        catch { /* best-effort - see MainWindow's SignalR handler comments */ }
    }

    private async void OnParticipantDisconnected(object? sender, Participant e)
    {
        try
        {
            if (!int.TryParse(e.Identity, out var userId)) return;
            if (_remotePlaybacks.TryRemove(userId, out var playback)) await playback.DisposeAsync();
            PeerDisconnected?.Invoke(userId);
        }
        catch { /* best-effort */ }
    }

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
            _micCapture.OnProcessedFrame -= OnLocalProcessedSample;
            _micCapture.Dispose();
            _micCapture = null;
        }
        _localTrack = null;
        CurrentChannelId = null;

        if (_room is not null)
        {
            await _room.DisconnectAsync();
            _room.Dispose();
            _room = null;
        }
    }

    private void OnLocalProcessedSample(short[] pcm)
    {
        if (pcm.Length == 0 || CurrentChannelId is not { } channelId) return;

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
                _ = _hub.SendSpeakingAsync(channelId, true);
            }
        }
        else if (_localIsSpeaking && now - _lastLoudSampleUtc > SpeakingHangover)
        {
            _localIsSpeaking = false;
            LocalSpeakingChanged?.Invoke(false);
            _ = _hub.SendSpeakingAsync(channelId, false);
        }
    }

    public async ValueTask DisposeAsync() => await LeaveAllAsync();
}
