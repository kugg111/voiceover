using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Voiceover.Client.Services;

// ToggleKey defaults to F8 rather than a letter key on purpose - the overlay
// toggle is a single global key (no modifier chord, matching the PTT hotkey
// model in GlobalHotkeyService), so a function key is far less likely to
// collide with in-game keybinds than a letter would.
// BackgroundOpacity is the overlay card's background alpha (0 = fully
// transparent, 1 = fully opaque). Default 0.15 = 85% transparent, per the
// requested look. Only the background is affected - member names/avatars stay
// fully opaque and readable regardless.
public record SavedOverlaySettings(
    bool Enabled = true,
    Key? ToggleKey = Key.F8,
    double BackgroundOpacity = 0.15);

// Local-machine preference (whether the in-game voice overlay is on, and
// which key toggles it), same plain-JSON, no-DPAPI approach as
// VoiceSettingsStorage - not account data, not sensitive. Unlike that class,
// Load never returns null: a missing/corrupt file falls back to the record's
// own defaults so callers don't each have to special-case it.
public static class OverlaySettingsStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "overlaysettings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(SavedOverlaySettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Best-effort - losing a saved preference isn't worth crashing over.
        }
    }

    public static SavedOverlaySettings Load()
    {
        if (!File.Exists(FilePath)) return new SavedOverlaySettings();

        try
        {
            return JsonSerializer.Deserialize<SavedOverlaySettings>(File.ReadAllText(FilePath), Options)
                ?? new SavedOverlaySettings();
        }
        catch
        {
            return new SavedOverlaySettings();
        }
    }
}
