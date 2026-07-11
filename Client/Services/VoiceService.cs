using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using SIPSorcery.Net;

namespace Voiceover.Client.Services;

// Simple JSON payloads exchanged over the SignalR "VoiceSignal" relay.
internal record IceCandidatePayload(string Candidate, string? SdpMid, int? SdpMLineIndex);

public class VoiceService
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
    public int? InputDeviceIndex { get; set; }
    public int? OutputDeviceIndex { get; set; }

    // Forwards to OpusAudioEndPoint's static flags - static because a mesh
    // call has one audio endpoint per remote peer, and muting/gating needs
    // to apply to the mic across all of them at once. Exposed here so the UI
    // only has to know about VoiceService, not the audio endpoint internals.
    public bool IsMicMuted
    {
        get => OpusAudioEndPoint.MicMuted;
        set
        {
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
        }
    }

    public bool NoiseSuppressionEnabled
    {
        get => OpusAudioEndPoint.NoiseSuppressionEnabled;
        set => OpusAudioEndPoint.NoiseSuppressionEnabled = value;
    }

    public VoiceService(SignalRService hub, int selfUserId)
    {
        _hub = hub;
        _selfUserId = selfUserId;

        _hub.VoiceUserJoined += OnVoiceUserJoined;
        _hub.VoiceUserLeft += OnVoiceUserLeft;
        _hub.VoiceSignalReceived += OnVoiceSignalReceived;
    }

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
