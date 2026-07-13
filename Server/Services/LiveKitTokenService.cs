using Livekit.Server.Sdk.Dotnet;

namespace Voiceover.Server.Services;

// Mints short-lived LiveKit room-join tokens for voice channels. The actual
// media transport (SFU) is a separate self-hosted LiveKit deployment (see
// REDEPLOY.txt) - this server never touches audio itself, only vouches for
// who's allowed into which room. Not required at startup (unlike
// DATABASE_URL) so a local dev session without LiveKit credentials
// configured can still run everything else - only actually joining voice
// fails, with a clear error, until LIVEKIT_API_KEY/SECRET are set.
public class LiveKitTokenService
{
    private readonly string? _apiKey;
    private readonly string? _apiSecret;

    public string? ServerUrl { get; }

    public LiveKitTokenService(IConfiguration configuration)
    {
        _apiKey = configuration["LIVEKIT_API_KEY"];
        _apiSecret = configuration["LIVEKIT_API_SECRET"];
        ServerUrl = configuration["LIVEKIT_URL"];
    }

    // Room name mirrors ChatHub's VoiceGroupName convention ("voice-{channelId}")
    // even though the two systems are otherwise unrelated - keeps "which room
    // is this channel" recognizable at a glance. Identity is the user's own
    // numeric id (as a string) so the client can map a LiveKit
    // RemoteParticipant.Identity straight back to a userId with no separate
    // lookup table.
    public string CreateJoinToken(int userId, string username, int channelId) =>
        CreateJoinToken(userId, username, $"voice-{channelId}");

    // Generalized form - private calls (see CallSignalingService) use their
    // generated call id directly as the room name, since there's no DB
    // "channel" backing a call the way there is for server voice channels.
    public string CreateJoinToken(int userId, string username, string roomName)
    {
        if (_apiKey is null || _apiSecret is null)
            throw new InvalidOperationException(
                "LIVEKIT_API_KEY/LIVEKIT_API_SECRET are not configured. Set them as env vars (Railway) or via " +
                "`dotnet user-secrets set LIVEKIT_API_KEY \"...\"` for local dev - see DEPLOYMENT.txt.");

        var token = new AccessToken(_apiKey, _apiSecret)
            .WithIdentity(userId.ToString())
            .WithName(username)
            .WithGrants(new VideoGrants { RoomJoin = true, Room = roomName })
            .WithTtl(TimeSpan.FromHours(6));

        return token.ToJwt();
    }
}
