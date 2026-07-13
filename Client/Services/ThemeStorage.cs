using System.IO;

namespace Voiceover.Client.Services;

// Local-machine display preference, same tradeoff as VoiceSettingsStorage
// (plain file, no DPAPI - not sensitive, not account data). A single bool
// in a one-line file rather than JSON since there's nothing else to store yet.
public static class ThemeStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "theme.txt");

    public static void SaveIsLightTheme(bool isLightTheme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, isLightTheme ? "light" : "dark");
        }
        catch
        {
            // Best-effort - losing a saved preference isn't worth crashing the app over.
        }
    }

    public static bool LoadIsLightTheme()
    {
        try
        {
            return File.Exists(FilePath) && File.ReadAllText(FilePath).Trim() == "light";
        }
        catch
        {
            return false;
        }
    }
}
