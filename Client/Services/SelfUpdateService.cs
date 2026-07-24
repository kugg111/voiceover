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

        // Process.Start's return value was previously discarded entirely -
        // if it came back null (or the helper crashed immediately after
        // starting without this process ever knowing), execution fell
        // straight through to Shutdown() below with zero confirmation the
        // update would actually happen. On a mandatory update, where this
        // is the only path forward, that reads to the user as the app
        // simply vanishing - the "crash" reports trace back to exactly
        // this. A null return is the one failure Process.Start signals
        // synchronously (a Win32Exception, e.g. powershell.exe missing or
        // blocked by policy, is thrown and already propagates to the
        // caller's own try/catch); still can't detect the helper script
        // itself failing after it starts, but this closes the one gap that
        // was silent on the C# side.
        var helper = Process.Start(psi);
        if (helper is null)
            throw new InvalidOperationException("Failed to launch the update helper process.");

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

        # No trace previously survived a failed update - the script deleted
        # itself and the staging dir unconditionally at the end regardless
        # of what happened, making any report undiagnosable. This log is
        # the one thing that does survive.
        $logPath = Join-Path $env:TEMP 'voiceover_update_log.txt'
        function Log($msg) {
            try { Add-Content -Path $logPath -Value "$(Get-Date -Format o) $msg" -ErrorAction SilentlyContinue } catch {}
        }

        Log "Update started. ProcessId=$ProcessId TargetDir=$TargetDir"

        $exited = $false
        try {
            Wait-Process -Id $ProcessId -Timeout 30 -ErrorAction Stop
            $exited = $true
        } catch {
            Log "Old process did not confirm exit within 30s: $_"
        }
        Log "Old process exited: $exited"
        Start-Sleep -Milliseconds 500

        # Copy-Item's default $ErrorActionPreference ('Continue') meant a
        # single locked file (most importantly Client.exe itself, if the
        # old process hadn't fully released its handle yet despite the
        # Wait-Process above) failed as a silently-swallowed non-terminating
        # error - the rest of the copy proceeded, and Start-Process below
        # ran unconditionally afterward with no check that anything actually
        # got replaced, relaunching the OLD binary. That race is the root
        # cause behind "the update finishes but doesn't apply." Retrying the
        # whole copy (not per-file - simpler, and -Force makes re-copying
        # already-updated files harmless) a handful of times with a short
        # gap gives the old process's file handles time to fully release,
        # which is normally a matter of milliseconds once it's actually exited.
        $ErrorActionPreference = 'Stop'
        $copySucceeded = $false
        $attempts = 0
        while (-not $copySucceeded -and $attempts -lt 10) {
            $attempts++
            try {
                Copy-Item -Path (Join-Path $SourceDir '*') -Destination $TargetDir -Recurse -Force
                $copySucceeded = $true
                Log "Copy succeeded on attempt $attempts"
            } catch {
                Log "Copy attempt $attempts failed: $_"
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $copySucceeded) {
            Log "Copy never succeeded after $attempts attempts - relaunching whatever is currently at $ExePath, which may still be the old version."
        }

        Start-Process -FilePath $ExePath -WorkingDirectory $TargetDir
        Log "Relaunch attempted."

        Start-Sleep -Seconds 2
        Remove-Item -Path $SourceDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}
