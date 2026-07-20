using System.IO;
using System.Text.Json;

namespace Voiceover.Client.Services;

// Whether closing MainWindow hides it to the system tray instead of
// exiting the app (see MainWindow's Closing handler) - lets notifications
// keep firing while the window is "closed." Same local-file shape/location
// convention as NotificationMuteStorage/UserVolumeStorage.
public static class TraySettingsStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "traysettings.json");

    private class Settings
    {
        public bool MinimizeToTrayEnabled { get; set; } = true;
        public bool HasShownTrayBalloon { get; set; }
    }

    // Loaded once and kept in memory - only this process writes this file,
    // and MainWindow's Closing handler needs to check this on every close
    // without hitting disk each time.
    private static readonly Lazy<Settings> Cache = new(Load);

    public static bool MinimizeToTrayEnabled
    {
        get => Cache.Value.MinimizeToTrayEnabled;
        set { Cache.Value.MinimizeToTrayEnabled = value; Save(); }
    }

    public static bool HasShownTrayBalloon
    {
        get => Cache.Value.HasShownTrayBalloon;
        set { Cache.Value.HasShownTrayBalloon = value; Save(); }
    }

    private static Settings Load()
    {
        if (!File.Exists(FilePath)) return new Settings();

        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // Corrupted/outdated file - fall back to defaults rather than crash on startup.
            return new Settings();
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
            // Best-effort - losing this preference isn't worth crashing the app over.
        }
    }
}
