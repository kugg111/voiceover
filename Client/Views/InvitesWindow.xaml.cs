using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CopyIconVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckIconVisibility)));
        }
    }

    // Briefly swaps to a checkmark after copying so the click has visible
    // feedback, then reverts on its own (see InvitesWindow.CopyButton_Click).
    public Visibility CopyIconVisibility => JustCopied ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CheckIconVisibility => JustCopied ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

// A popup by deliberate choice, not an in-window PageHost page like the
// rest of MainWindow's secondary UI - a small, single-purpose list that
// doesn't benefit from taking over the whole window.
public partial class InvitesWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly int _serverId;
    private readonly ObservableCollection<InviteListItem> _invites = new();

    public InvitesWindow(ApiService api, int serverId)
    {
        InitializeComponent();
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
            MessageBox.Show("Could not generate an invite.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
