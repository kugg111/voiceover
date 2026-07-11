using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using SIPSorcery.Net;

namespace Voiceover.Client.Services;

// Simple JSON payloads exchanged over the SignalR "VoiceSignal" relay.
internal record IceCandidatePayload(string Candidate, string? SdpMid, int? SdpMLineIndex);

public enum VoiceInputMode
{
    VoiceActivity, // default: mic always "open"; the noise gate handles the rest
    PushToTalk,    // muted at rest, open only while the hotkey is held
    PushToMute     // open at rest, muted only while the hotkey is held
}

public class VoiceService : IDisposable
{
    private readonly SignalRService _hub;
    private readonly int _selfUserId;

    private int? _activeChannelId;

    // One RTCPeerConnection + audio endpoint per remote participant (mesh topology).
    // Fine for small voice channels (a handful of people); doesn't scale to large
    // rooms the way a media server (SFU) would, but keeps the server dumb/simple.
    private readonly ConcurrentDictionary<int, RTCPeerConnection> _peerConnections = new();
    private readonly ConcurrentDictionary<int, OpusAudioEndPoint> _audioEndPoints = new();

    private static readonly RTCConfiguration IceConfig = new()
    {
        iceServers = new List<RTCIceServer>
        {
            new() { urls = "stun:stun.l.google.com:19302" }
        }
    };

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
    // when a peer connection is created, so a change here takes effect on the
    // next channel join/new peer rather than hot-swapping an active call.
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

    // Forwards to OpusAudioEndPoint's static flags - static because a mesh
    // call has one audio endpoint per remote peer, and muting/gating needs
    // to apply to the mic across all of them at once. Exposed here so the UI
    // only has to know about VoiceService, not the audio endpoint internals.
    // Mute can now change from several independent places - the mute
    // button, deafen (couples to it), an input mode switch (PTT/push-to-
    // mute reset it to their resting state), and the PTT/push-to-mute
    // hotkey itself - so the UI can't just refresh its own button inline
    // after calling this setter anymore. MicMutedChanged is the one place
    // any of them can listen to stay in sync, instead of every caller
    // needing to know about every button that displays mute state.
    public event Action<bool>? MicMutedChanged;

    public bool IsMicMuted
    {
        get => OpusAudioEndPoint.MicMuted;
        set
        {
            var changed = OpusAudioEndPoint.MicMuted != value;
            OpusAudioEndPoint.MicMuted = value;

            // Muting stops raw-sample callbacks entirely (see
            // OpusAudioEndPoint.OnMicDataAvailable), so the hangover-based
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

    public bool NoiseSuppressionEnabled
    {
        get => OpusAudioEndPoint.NoiseSuppressionEnabled;
        set { OpusAudioEndPoint.NoiseSuppressionEnabled = value; SaveSettings(); }
    }

    // Not persisted (see SaveSettings) - deafen/mute are session states
    // like Discord's, not preferences, so a fresh login always starts
    // undeafened/unmuted regardless of how a previous session ended.
    public event Action<bool>? DeafenedChanged;

    // Deafen always drives mute to match its own new state, same as
    // Discord: turning deafen on also mutes (no point talking if you can't
    // hear yourself), turning it off restores mic to whatever "at rest"
    // means for the current input mode - PushToTalk's resting state is
    // muted-until-held, so undeafening shouldn't silently force the mic
    // open there. Muting on its own doesn't touch deafen - only the
    // deafen control couples the two.
    public bool IsDeafened
    {
        get => OpusAudioEndPoint.Deafened;
        set
        {
            OpusAudioEndPoint.Deafened = value;
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
        OpusAudioEndPoint.NoiseSuppressionEnabled = saved.NoiseSuppressionEnabled;
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

    // Per-remote-peer volume (1.0 = unchanged). No-op for a userId that
    // isn't currently connected - the slider only makes sense/exists for
    // people you're actually in a call with, see MainWindow's voice member
    // list.
    public void SetRemoteVolume(int userId, float volume)
    {
        if (_audioEndPoints.TryGetValue(userId, out var endpoint))
            endpoint.PlaybackVolume = volume;
    }

    public VoiceService(SignalRService hub, int selfUserId)
    {
        _hub = hub;
        _selfUserId = selfUserId;

        _hub.VoiceUserJoined += OnVoiceUserJoined;
        _hub.VoiceUserLeft += OnVoiceUserLeft;
        _hub.VoiceSignalReceived += OnVoiceSignalReceived;

        _hotkey.KeyDown += OnHotkeyDown;
        _hotkey.KeyUp += OnHotkeyUp;

        LoadSettings();
    }

    public void Dispose() => _hotkey.Dispose();

    public void SetActiveChannel(int channelId) => _activeChannelId = channelId;

    public async Task LeaveAllAsync()
    {
        _activeChannelId = null;
        foreach (var userId in _peerConnections.Keys.ToList())
            await ClosePeerAsync(userId);

        if (_localIsSpeaking)
        {
            _localIsSpeaking = false;
            LocalSpeakingChanged?.Invoke(false);
        }
    }

    // Called when someone (including a peer already in the channel when we
    // join) shows up. To avoid both sides sending competing offers, only the
    // participant with the lower user id initiates.
    private async void OnVoiceUserJoined(int userId, string username, int channelId)
    {
        if (channelId != _activeChannelId || userId == _selfUserId) return;
        if (_peerConnections.ContainsKey(userId)) return;

        if (_selfUserId < userId)
            await CreatePeerConnectionAsync(userId, channelId, isInitiator: true);
        else
            await CreatePeerConnectionAsync(userId, channelId, isInitiator: false);
    }

    private async void OnVoiceUserLeft(int userId, string username, int channelId)
    {
        if (channelId != _activeChannelId) return;
        await ClosePeerAsync(userId);
    }

    private async void OnVoiceSignalReceived(int fromUserId, int channelId, string signalType, string payload)
    {
        if (channelId != _activeChannelId) return;

        if (!_peerConnections.TryGetValue(fromUserId, out var pc))
        {
            // We received a signal (likely an offer) before we'd set up our side -
            // create the connection now as the non-initiator.
            await CreatePeerConnectionAsync(fromUserId, channelId, isInitiator: false);
            _peerConnections.TryGetValue(fromUserId, out pc);
        }

        if (pc is null) return;

        switch (signalType)
        {
            case "offer":
                pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = payload, type = RTCSdpType.offer });
                var answer = pc.createAnswer();
                await pc.setLocalDescription(answer);
                await _hub.SendVoiceSignalAsync(fromUserId, channelId, "answer", answer.sdp);
                break;

            case "answer":
                pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = payload, type = RTCSdpType.answer });
                break;

            case "ice-candidate":
                var candidate = JsonSerializer.Deserialize<IceCandidatePayload>(payload);
                if (candidate is not null)
                {
                    pc.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = candidate.Candidate,
                        sdpMid = candidate.SdpMid,
                        sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0)
                    });
                }
                break;
        }
    }

    private async Task CreatePeerConnectionAsync(int remoteUserId, int channelId, bool isInitiator)
    {
        // OpusAudioEndPoint talks to NAudio and Concentus (Opus) directly rather
        // than going through SIPSorceryMedia.Windows' WindowsAudioEndPoint - see
        // its own comments for why (mic gain, jitter buffer, an RTP timestamp
        // bug in the upstream Encode/Decode duration handling).
        var audioEndPoint = new OpusAudioEndPoint(OutputDeviceIndex ?? -1, InputDeviceIndex ?? -1);

        var pc = new RTCPeerConnection(IceConfig);

        var audioTrack = new MediaStreamTrack(audioEndPoint.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv);
        pc.addTrack(audioTrack);

        // Mic -> outgoing RTP.
        audioEndPoint.OnAudioSourceEncodedSample += pc.SendAudio;

        // Mic -> local speaking-indicator detection, straight off the raw PCM
        // OpusAudioEndPoint already captures (post-gain, so this reacts the
        // same way the encoded audio actually sounds) - no need to decode our
        // own outgoing Opus frames back to PCM just to measure volume.
        audioEndPoint.OnAudioSourceRawSample += (samplingRate, durationMs, pcm) => OnLocalRawSample(pcm);

        // Incoming RTP -> jitter-buffered decode -> speaker. Feeds the real RTP
        // sequence number through so OpusAudioEndPoint can reorder/conceal
        // properly, unlike the IAudioSink.GotEncodedMediaFrame interface method
        // (used by the old WindowsAudioEndPoint path) which has no sequence
        // number to work with.
        pc.OnRtpPacketReceived += (remoteEndPoint, mediaType, rtpPkt) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio)
                audioEndPoint.SubmitEncodedFrame((ushort)rtpPkt.Header.SequenceNumber, rtpPkt.Payload);
        };

        pc.onicecandidate += async candidate =>
        {
            if (candidate is null) return;
            var json = JsonSerializer.Serialize(new IceCandidatePayload(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
            await _hub.SendVoiceSignalAsync(remoteUserId, channelId, "ice-candidate", json);
        };

        pc.onconnectionstatechange += async state =>
        {
            // WindowsAudioEndPoint implements IAudioSource (mic) and IAudioSink
            // (speaker) as two independent lifecycles - StartAudio()/CloseAudio()
            // only starts/stops the microphone capture, and StartAudioSink()/
            // CloseAudioSink() only starts/stops speaker playback. Both are
            // required; calling only StartAudio() (as this used to) means the
            // mic captures and sends fine but nothing ever plays back - RTP
            // flows correctly in both directions with no errors, just silence.
            // OpusAudioEndPoint keeps the same two-lifecycle shape.
            if (state == RTCPeerConnectionState.connected)
            {
                await audioEndPoint.StartAudio();
                await audioEndPoint.StartAudioSink();
                PeerConnected?.Invoke(remoteUserId);
            }
            else if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
            {
                await audioEndPoint.CloseAudio();
                await audioEndPoint.CloseAudioSink();
                PeerDisconnected?.Invoke(remoteUserId);
            }
        };

        _peerConnections[remoteUserId] = pc;
        _audioEndPoints[remoteUserId] = audioEndPoint;

        if (isInitiator)
        {
            var offer = pc.createOffer();
            await pc.setLocalDescription(offer);
            await _hub.SendVoiceSignalAsync(remoteUserId, channelId, "offer", offer.sdp);
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

    private async Task ClosePeerAsync(int userId)
    {
        if (_peerConnections.TryRemove(userId, out var pc))
            pc.close();

        if (_audioEndPoints.TryRemove(userId, out var audioEndPoint))
        {
            await audioEndPoint.CloseAudio();
            await audioEndPoint.CloseAudioSink();
        }

        PeerDisconnected?.Invoke(userId);
    }
}
