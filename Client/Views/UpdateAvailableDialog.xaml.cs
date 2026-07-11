using System.Diagnostics;
using System.Windows;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public partial class UpdateAvailableDialog : FluentWindow
{
    private readonly string _downloadUrl;

    public UpdateAvailableDialog(VersionInfo latest)
    {
        InitializeComponent();

        // Installed and portable builds live in different places and update
        // differently (rerun the installer vs. re-extract the zip), so each
        // gets its own download - see UpdateChecker.IsInstalled.
        _downloadUrl = UpdateChecker.IsInstalled ? latest.InstallerUrl : latest.PortableUrl;

        VersionText.Text = $"Voiceover {latest.Version} is available (you have {UpdateChecker.CurrentVersion}).";
        DetailText.Text = UpdateChecker.IsInstalled
            ? "Downloads the new installer - run it to update over your existing install."
            : "Downloads the new portable build - extract it over your current folder to update.";
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        // Hands off to the browser/shell rather than silently self-replacing
        // a running single-file exe - that needs a separate updater process,
        // real extra complexity not worth it for a friends-scale app.
        Process.Start(new ProcessStartInfo(_downloadUrl) { UseShellExecute = true });
        Close();
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Close();
}
