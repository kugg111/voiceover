using System.IO;
using System.Text.Json;

namespace Voiceover.Client.Services;

// Personal, client-local unsent-message draft, keyed by channel and by DM
// conversation (other user's id) - retained across switching away from and
// back to a channel/DM within a session, and across app restarts. Never
// sent anywhere (drafts are, by definition, not-yet-encrypted plaintext -
// keeping this purely local is the only option under this app's E2EE
// model anyway). Same local-file shape/location convention as
// NotificationMuteStorage.
public static class DraftStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "drafts.json");

    private class Drafts
    {
        public Dictionary<int, string> Channels { get; set; } = new();
        public Dictionary<int, string> DirectMessages { get; set; } = new();
    }

    // Loaded once and kept in memory, same reasoning as NotificationMuteStorage.Cache.
    private static readonly Lazy<Drafts> Cache = new(Load);

    public static string GetChannelDraft(int channelId) => Cache.Value.Channels.GetValueOrDefault(channelId, string.Empty);
    public static string GetDmDraft(int otherUserId) => Cache.Value.DirectMessages.GetValueOrDefault(otherUserId, string.Empty);

    // Empty/whitespace clears the stored draft rather than keeping an
    // empty-string entry around forever - a channel/DM the user has never
    // typed anything unsent into shouldn't grow the file.
    public static void SetChannelDraft(int channelId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) Cache.Value.Channels.Remove(channelId);
        else Cache.Value.Channels[channelId] = text;
        Save();
    }

    public static void SetDmDraft(int otherUserId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) Cache.Value.DirectMessages.Remove(otherUserId);
        else Cache.Value.DirectMessages[otherUserId] = text;
        Save();
    }

    private static Drafts Load()
    {
        if (!File.Exists(FilePath)) return new Drafts();

        try
        {
            return JsonSerializer.Deserialize<Drafts>(File.ReadAllText(FilePath)) ?? new Drafts();
        }
        catch
        {
            // Corrupted/outdated file - fall back to defaults rather than crash on startup.
            return new Drafts();
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
            // Best-effort - losing a draft isn't worth crashing the app over.
        }
    }
}
