using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;

namespace Voiceover.Client.Services;

// Downloads the new portable build and swaps it in place, instead of just
// handing the user a browser download - works identically for the Inno
// Setup install and a portable extracted zip, since both are just "whatever
// folder this exe is currently running from" (AppContext.BaseDirectory).
//
// A running exe can't overwrite its own files, so the actual file swap is
// done by a short-lived PowerShell helper script: it waits for this process
// to exit, copies the freshly-downloaded files over the current install
// folder, relaunches, and deletes itself. Local user data (session,
// voice/volume settings) lives under %APPDATA%\Voiceover\, outside the
// install folder entirely, so none of that is touched by the swap.
public static class SelfUpdateService
{
    public static async Task DownloadAndApplyAsync(ApiService api, string zipUrl, IProgress<double>? progress)
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), "VoiceoverUpdate_" + Guid.NewGuid().ToString("N"));
        var zipPath = stagingDir + ".zip";
        Directory.CreateDirectory(stagingDir);

        await api.DownloadFileAsync(zipUrl, zipPath, progress);
        ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);
        File.Delete(zipPath);

        var targetDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exePath = Path.Combine(targetDir, "Client.exe");
        var scriptPath = Path.Combine(Path.GetTempPath(), "voiceover_update_" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, UpdateScript);

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-ProcessId");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("-SourceDir");
        psi.ArgumentList.Add(stagingDir);
        psi.ArgumentList.Add("-TargetDir");
        psi.ArgumentList.Add(targetDir);
        psi.ArgumentList.Add("-ExePath");
        psi.ArgumentList.Add(exePath);
        Process.Start(psi);

        // The helper script waits for this process to exit before touching
        // any files - shut down immediately instead of making it wait out
        // its own timeout for nothing.
        Application.Current.Shutdown();
    }

    private const string UpdateScript = """
        param(
            [int]$ProcessId,
            [string]$SourceDir,
            [string]$TargetDir,
            [string]$ExePath
        )

        try { Wait-Process -Id $ProcessId -Timeout 30 -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Milliseconds 500

        Copy-Item -Path (Join-Path $SourceDir '*') -Destination $TargetDir -Recurse -Force

        Start-Process -FilePath $ExePath -WorkingDirectory $TargetDir

        Start-Sleep -Seconds 2
        Remove-Item -Path $SourceDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}
