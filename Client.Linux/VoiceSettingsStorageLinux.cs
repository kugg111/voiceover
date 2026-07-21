using System.Text.Json;

namespace Voiceover.Client.Linux;

// Local-machine voice preferences (which mic/speaker, noise suppression) -
// simplified from the WPF client's VoiceSettingsStorage, which also stores
// a push-to-talk hotkey (System.Windows.Input.Key/MouseButton, WPF-only
// types) and a noise-suppression backend choice (RNNoise isn't ported to
// Linux yet - see MicCaptureSourceLinux, NSNet2 is the only backend here).
// Lives in the same %AppData%/Voiceover-equivalent location on Linux
// (Environment.SpecialFolder.ApplicationData maps to ~/.config there).
public record SavedVoiceSettingsLinux(
    int? InputDeviceIndex,
    int? OutputDeviceIndex,
    bool NoiseSuppressionEnabled,
    float SuppressionMix,
    float MicGain);

public static class VoiceSettingsStorageLinux
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "voicesettings_linux.json");

    public static void Save(SavedVoiceSettingsLinux settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Best-effort - losing a saved preference isn't worth crashing the app over.
        }
    }

    public static SavedVoiceSettingsLinux? Load()
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            return JsonSerializer.Deserialize<SavedVoiceSettingsLinux>(File.ReadAllText(FilePath));
        }
        catch
        {
            // Corrupted/outdated file - fall back to defaults rather than crash on startup.
            return null;
        }
    }
}
