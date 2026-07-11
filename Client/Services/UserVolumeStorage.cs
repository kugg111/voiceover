using System.IO;
using System.Text.Json;

namespace Voiceover.Client.Services;

// Persists per-user playback volume locally, keyed by userId, so you don't
// have to re-adjust the same person's volume every time you're in a voice
// channel together. A local-machine preference like VoiceSettingsStorage's
// device/hotkey settings, not account data - lives in its own small file so
// a volume change doesn't touch/re-save those unrelated settings.
public static class UserVolumeStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "uservolumes.json");

    // 1.0 = unchanged, matching OpusAudioEndPoint/RemoteAudioPlayback's own
    // PlaybackVolume unit - callers convert to/from the 0-200 percent scale
    // the UI slider uses.
    public static void SaveVolume(int userId, float volume)
    {
        try
        {
            var all = LoadAll();
            all[userId] = volume;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
        catch
        {
            // Best-effort - losing a saved volume isn't worth crashing the app over.
        }
    }

    public static float? GetVolume(int userId) =>
        LoadAll().TryGetValue(userId, out var volume) ? volume : null;

    private static Dictionary<int, float> LoadAll()
    {
        if (!File.Exists(FilePath)) return new();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, float>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // Corrupted/outdated file - fall back to defaults rather than crash on startup.
            return new();
        }
    }
}
