using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Voiceover.Client.Views;

public class InviteListItem
{
    public string Code { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

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

    // Click any existing code to copy it again - handy once the list has
    // more than the one you just generated.
    private void InviteRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InviteListItem item })
            Clipboard.SetText(item.Code);
    }
}
