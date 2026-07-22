using System.IO;
using System.Text.Json;

namespace Voiceover.Client.Services;

// Personal, client-local notification-mute preference, keyed by channel, by
// server, and by DM conversation (other user's id) - distinct from the
// moderation ServerPermission.MuteMembers permission (that mutes someone's
// mic for everyone; this only silences notifications for whoever set it, on
// this device). Same local-file shape/location convention as UserVolumeStorage.
public static class NotificationMuteStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "notificationmutes.json");

    private class MuteSets
    {
        public HashSet<int> Channels { get; set; } = new();
        public HashSet<int> Servers { get; set; } = new();
        public HashSet<int> DirectMessages { get; set; } = new();
    }

    // Loaded once and kept in memory - this file is only ever written by
    // this same process's own toggles below (no concurrent writer to worry
    // about), and OnMessageReceived needs to check this on every incoming
    // message without hitting disk each time.
    private static readonly Lazy<MuteSets> Cache = new(Load);

    public static bool IsChannelMuted(int channelId) => Cache.Value.Channels.Contains(channelId);
    public static bool IsServerMuted(int serverId) => Cache.Value.Servers.Contains(serverId);
    public static bool IsDmMuted(int otherUserId) => Cache.Value.DirectMessages.Contains(otherUserId);

    public static void SetChannelMuted(int channelId, bool muted)
    {
        if (muted) Cache.Value.Channels.Add(channelId);
        else Cache.Value.Channels.Remove(channelId);
        Save();
    }

    public static void SetServerMuted(int serverId, bool muted)
    {
        if (muted) Cache.Value.Servers.Add(serverId);
        else Cache.Value.Servers.Remove(serverId);
        Save();
    }

    public static void SetDmMuted(int otherUserId, bool muted)
    {
        if (muted) Cache.Value.DirectMessages.Add(otherUserId);
        else Cache.Value.DirectMessages.Remove(otherUserId);
        Save();
    }

    private static MuteSets Load()
    {
        if (!File.Exists(FilePath)) return new MuteSets();

        try
        {
            return JsonSerializer.Deserialize<MuteSets>(File.ReadAllText(FilePath)) ?? new MuteSets();
        }
        catch
        {
            // Corrupted/outdated file - fall back to defaults rather than crash on startup.
            return new MuteSets();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Cache.Value));
        }
        catch
        {
            // Best-effort - losing a saved mute preference isn't worth crashing the app over.
        }
    }
}
