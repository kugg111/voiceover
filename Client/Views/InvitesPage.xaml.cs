using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class InviteListItem : INotifyPropertyChanged
{
    public string Code { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;

    private bool _justCopied;
    public bool JustCopied
    {
        get => _justCopied;
        set
        {
            if (_justCopied == value) return;
            _justCopied = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JustCopied)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CopyIcon)));
        }
    }

    // Briefly swaps to a checkmark after copying so the click has visible
    // feedback, then reverts on its own (see InvitesPage.CopyButton_Click).
    public string CopyIcon => JustCopied ? "✅" : "📋";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class InvitesPage : UserControl
{
    private readonly MainWindow _mainWindow;
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly ObservableCollection<InviteListItem> _invites = new();

    public InvitesPage(MainWindow mainWindow, ApiService api, int serverId)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _api = api;
        _serverId = serverId;
        InviteList.ItemsSource = _invites;

        Loaded += async (_, _) => await LoadInvitesAsync();
    }

    private async Task LoadInvitesAsync()
    {
        var invites = await _api.ListInvitesAsync(_serverId);

        _invites.Clear();
        foreach (var i in invites)
        {
            var expiry = i.ExpiresAt is null ? "Never expires" : $"Expires {i.ExpiresAt.Value.ToLocalTime():g}";
            var uses = i.MaxUses is null ? $"{i.UseCount} uses" : $"{i.UseCount}/{i.MaxUses} uses";
            _invites.Add(new InviteListItem { Code = i.Code, Details = $"{expiry}   ·   {uses}" });
        }

        EmptyStateText.Visibility = _invites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateButton.IsEnabled = false;
        var invite = await _api.CreateInviteAsync(_serverId, expiresInHours: 24 * 7); // 1 week
        GenerateButton.IsEnabled = true;

        if (invite is null)
        {
            await _mainWindow.AlertAsync("Error", "Could not generate an invite.");
            return;
        }

        Clipboard.SetText(invite.Code);
        await LoadInvitesAsync();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InviteListItem item }) return;

        Clipboard.SetText(item.Code);
        item.JustCopied = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            item.JustCopied = false;
            timer.Stop();
        };
        timer.Start();
    }
}
