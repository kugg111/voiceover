using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Voiceover.Client.Services;

public record SavedVoiceSettings(
    int? InputDeviceIndex,
    int? OutputDeviceIndex,
    bool NoiseSuppressionEnabled,
    VoiceInputMode InputMode,
    Key? PushToTalkKey,
    MouseButton? PushToTalkMouseButton = null);

// Persists voice preferences (devices, noise suppression, input mode, PTT/
// push-to-mute hotkey) locally so they survive a log out/in - these are
// local-machine preferences (which mic, which key), not account data, so
// they live in a plain JSON file rather than the database. Unlike
// SessionStorage this isn't sensitive, so no DPAPI encryption.
public static class VoiceSettingsStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "voicesettings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(SavedVoiceSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Best-effort - losing a saved preference isn't worth crashing the app over.
        }
    }

    public static SavedVoiceSettings? Load()
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            return JsonSerializer.Deserialize<SavedVoiceSettings>(File.ReadAllText(FilePath), Options);
        }
        catch
        {
            // Corrupted/outdated file - fall back to defaults rather than crash on startup.
            return null;
        }
    }
}
