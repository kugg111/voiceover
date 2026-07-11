using System.IO;
using System.Reflection;

namespace Voiceover.Client.Services;

// Detects whether a newer client build is available (see
// Server/Site/downloads/version.json) and whether this instance is running
// from the Inno Setup install location or as a portable extracted zip, so
// UpdateAvailableDialog can point at the right download.
public static class UpdateChecker
{
    // Trimmed to Major.Minor.Build - the assembly Version's Revision is
    // always 0 for a build from Client.csproj's <Version>, but
    // Version.Parse on a plain "1.0.0" string gives Revision -1, which
    // would otherwise never compare equal.
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    public static bool IsNewer(string remoteVersion) =>
        Version.TryParse(remoteVersion, out var remote) && remote > CurrentVersion;

    // Inno Setup (see installer/Voiceover.iss) always installs to this
    // fixed, per-user path - anything running from elsewhere (an extracted
    // portable zip, or a dev build's own output folder) is treated as
    // portable.
    public static bool IsInstalled
    {
        get
        {
            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Voiceover");
            return string.Equals(
                AppContext.BaseDirectory.TrimEnd('\\', '/'),
                installDir.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
