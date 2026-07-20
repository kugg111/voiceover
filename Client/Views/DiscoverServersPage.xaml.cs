using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Voiceover.Client.Models;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class DiscoverServerItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DescriptionDisplay { get; set; } = string.Empty;
    public string MemberCountDisplay { get; set; } = string.Empty;
}

public partial class DiscoverServersPage : UserControl
{
    private readonly MainWindow _mainWindow;
    private readonly ApiService _api;
    private readonly ObservableCollection<DiscoverServerItem> _servers = new();
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public DiscoverServersPage(MainWindow mainWindow, ApiService api)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _api = api;
        ServerList.ItemsSource = _servers;

        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await LoadAsync();
        };

        Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => _searchDebounce.Stop();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async Task LoadAsync()
    {
        List<DiscoverServerResponse> servers;
        try
        {
            servers = await _api.DiscoverServersAsync(SearchBox.Text);
        }
        catch
        {
            // Same reasoning as BanListPage.LoadAsync's own try/catch - a
            // network blip shouldn't surface as an uncaught exception out of
            // this Loaded event's async lambda.
            EmptyStateText.Text = "Could not load public servers - try again later.";
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        _servers.Clear();
        foreach (var s in servers)
        {
            _servers.Add(new DiscoverServerItem
            {
                Id = s.Id,
                Name = s.Name,
                DescriptionDisplay = string.IsNullOrEmpty(s.Description) ? "No description." : s.Description,
                MemberCountDisplay = s.MemberCount == 1 ? "1 member" : $"{s.MemberCount} members"
            });
        }

        EmptyStateText.Text = "No public servers found.";
        EmptyStateText.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int serverId }) return;

        var (success, error) = await _api.JoinPublicServerAsync(serverId);
        if (!success)
        {
            await _mainWindow.AlertAsync("Error", error ?? "Could not join this server.");
            return;
        }

        await _mainWindow.ReloadServersAsync();
        _mainWindow.GoBack();
    }
}
