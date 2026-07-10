using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Windows;

namespace DiscordClone.Client.Services;

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
    private readonly ConcurrentDictionary<int, WindowsAudioEndPoint> _audioEndPoints = new();

    private static readonly RTCConfiguration IceConfig = new()
    {
        iceServers = new List<RTCIceServer>
        {
            new() { urls = "stun:stun.l.google.com:19302" }
        }
    };

    public event Action<int>? PeerConnected;
    public event Action<int>? PeerDisconnected;

    // Device indices from AudioDeviceService (null/-1 = system default). Applied
    // when a peer connection is created, so a change here takes effect on the
    // next channel join/new peer rather than hot-swapping an active call.
    public int? InputDeviceIndex { get; set; }
    public int? OutputDeviceIndex { get; set; }

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
        var audioEndPoint = new WindowsAudioEndPoint(new AudioEncoder(), OutputDeviceIndex ?? -1, InputDeviceIndex ?? -1);
        audioEndPoint.RestrictFormats(f => f.Codec == SIPSorceryMedia.Abstractions.AudioCodecsEnum.PCMU);

        var pc = new RTCPeerConnection(IceConfig);

        var audioTrack = new MediaStreamTrack(audioEndPoint.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv);
        pc.addTrack(audioTrack);

        // Negotiated format applies to both directions since we're using the
        // same codec (PCMU) for send and receive.
        SIPSorceryMedia.Abstractions.AudioFormat? negotiatedFormat = null;
        pc.OnAudioFormatsNegotiated += formats =>
        {
            negotiatedFormat = formats.First();
            audioEndPoint.SetAudioSourceFormat(negotiatedFormat.Value);
            audioEndPoint.SetAudioSinkFormat(negotiatedFormat.Value);
        };

        // Mic -> outgoing RTP.
        audioEndPoint.OnAudioSourceEncodedSample += pc.SendAudio;

        // Incoming RTP -> speaker. GotAudioRtp is obsolete in this SIPSorceryMedia
        // version in favor of GotEncodedMediaFrame, which takes the decoded RTP
        // fields pre-packaged instead of the raw header.
        pc.OnRtpPacketReceived += (remoteEndPoint, mediaType, rtpPkt) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio && negotiatedFormat is not null)
            {
                var frame = new SIPSorceryMedia.Abstractions.EncodedAudioFrame(
                    0, negotiatedFormat.Value, rtpPkt.Header.Timestamp, rtpPkt.Payload);
                audioEndPoint.GotEncodedMediaFrame(frame);
            }
        };

        pc.onicecandidate += async candidate =>
        {
            if (candidate is null) return;
            var json = JsonSerializer.Serialize(new IceCandidatePayload(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
            await _hub.SendVoiceSignalAsync(remoteUserId, channelId, "ice-candidate", json);
        };

        pc.onconnectionstatechange += async state =>
        {
            // NOTE: StartAudio()/CloseAudio() are the combined IAudioEndPoint
            // methods as of SIPSorceryMedia.Windows ~10.x. If these don't exist
            // on your installed version, check IntelliSense for StartAudioSink()/
            // StartAudioSource() (older API split them) and call both instead.
            if (state == RTCPeerConnectionState.connected)
            {
                await audioEndPoint.StartAudio();
                PeerConnected?.Invoke(remoteUserId);
            }
            else if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
            {
                await audioEndPoint.CloseAudio();
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

    private async Task ClosePeerAsync(int userId)
    {
        if (_peerConnections.TryRemove(userId, out var pc))
            pc.close();

        if (_audioEndPoints.TryRemove(userId, out var audioEndPoint))
            await audioEndPoint.CloseAudio();

        PeerDisconnected?.Invoke(userId);
    }
}
