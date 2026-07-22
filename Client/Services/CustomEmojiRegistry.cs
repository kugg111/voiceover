using System.Collections.Concurrent;

namespace Voiceover.Client.Services;

// Maps a "custom:{id}" reaction token (see ChatHub.ToggleMessageReaction
// server-side) to the emoji's full image URL, so EmojiImageBehavior can
// resolve a reaction pill or picker button to a real image without every
// binding needing its own round-trip. Populated whenever a server's emoji
// list loads (see MainWindow.RefreshEmojisAsync) - a token for an emoji from
// a server not currently open just won't resolve yet, same graceful-null
// handling this app already gives a not-yet-loaded avatar/attachment image.
public static class CustomEmojiRegistry
{
    public const string Prefix = "custom:";

    private static readonly ConcurrentDictionary<int, string> UrlById = new();

    public static string TokenFor(int emojiId) => Prefix + emojiId;

    public static void Register(int emojiId, string url) => UrlById[emojiId] = url;

    public static bool TryGetUrl(string token, out string url)
    {
        url = string.Empty;
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(token.AsSpan(Prefix.Length), out var id) && UrlById.TryGetValue(id, out url!);
    }
}
