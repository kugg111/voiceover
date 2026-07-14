namespace Voiceover.Server.Hubs;

// Shared SignalR group-name helpers - single source of truth for the naming
// convention, used both inside ChatHub itself and by controllers that need
// to broadcast to a group via IHubContext<ChatHub> (e.g.
// MessagesController.Pin, ServersController's moderation broadcasts). Was
// previously duplicated as private statics in both ChatHub and
// MessagesController.
public static class HubGroups
{
    public static string Channel(int channelId) => $"channel-{channelId}";
    public static string Voice(int channelId) => $"voice-{channelId}";
    public static string ServerPresence(int serverId) => $"server-presence-{serverId}";
}
