using System.IO;
using System.Text.Json;

namespace Voiceover.Client.Services;

public record SavedThemeSettings(string? AccentHex);

// Persists the user's chosen accent color (see SettingsPage's Appearance
// tab) as a plain JSON file, same pattern as VoiceSettingsStorage - a
// local-machine preference, not account data. Applied once, early in
// App.xaml.cs's OnStartup, by overriding the AccentBlurple StaticResource
// before any window is constructed: StaticResource lookups resolve at the
// point each control's template loads, which is after that override runs,
// so every window picks up the new color with zero DynamicResource
// plumbing needed. The tradeoff is a restart is required to see a change -
// acceptable here since this isn't a frequently-toggled setting.
public static class ThemeSettingsStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "themesettings.json");

    public static void Save(SavedThemeSettings settings)
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

    public static SavedThemeSettings? Load()
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            return JsonSerializer.Deserialize<SavedThemeSettings>(File.ReadAllText(FilePath));
        }
        catch
        {
            // Corrupted file - fall back to the default accent rather than crash on startup.
            return null;
        }
    }
}
