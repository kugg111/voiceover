using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Voiceover.Client.Models;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

public class ForwardDestinationItem
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsDm { get; set; }
    // For a DM destination this is the other user's id; for a channel
    // destination this is the channel id (ServerId carries which server it
    // belongs to, needed separately to pick the right E2EE server key).
    public int TargetId { get; set; }
    public int ServerId { get; set; }
}

// Destination picker for the "Forward" action (see MainWindow.
// ForwardMessageButton_Click) - unlike Reply, which only ever targets the
// currently-open conversation, a forward can go to any channel/DM this user
// has access to, so this fetches every server's channel list plus the DM
// conversation list fresh each time it opens rather than reusing whatever
// happens to already be loaded for the currently-open server.
public partial class ForwardMessagePage : UserControl
{
    private readonly MainWindow _mainWindow;
    private readonly ApiService _api;
    private readonly SignalRService _hub;
    private readonly string _content;
    private readonly string _originalAuthorUsername;
    private readonly ObservableCollection<ForwardDestinationItem> _destinations = new();

    public ForwardMessagePage(MainWindow mainWindow, ApiService api, SignalRService hub, string content, string originalAuthorUsername)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _api = api;
        _hub = hub;
        _content = content;
        _originalAuthorUsername = originalAuthorUsername;
        DestinationList.ItemsSource = _destinations;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        List<DmConversationResponse> conversations;
        List<GuildServerResponse> servers;
        try
        {
            conversations = await _api.GetDmConversationsAsync();
            servers = await _api.GetMyServersAsync();
        }
        catch
        {
            EmptyStateText.Text = "Could not load destinations - try again later.";
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        foreach (var c in conversations)
            _destinations.Add(new ForwardDestinationItem { DisplayName = $"👤 {c.OtherUsername}", IsDm = true, TargetId = c.OtherUserId });

        // Independent per-server HTTP calls run concurrently, same reasoning
        // as MainWindow.LoadServersAsync's own parallel channel-list fetch.
        var channelLists = await Task.WhenAll(servers.Select(s => _api.GetChannelsAsync(s.Id)));
        for (var i = 0; i < servers.Count; i++)
        {
            foreach (var channel in channelLists[i].Where(c => c.Type == "Text"))
                _destinations.Add(new ForwardDestinationItem
                {
                    DisplayName = $"# {channel.Name}  ({servers[i].Name})",
                    IsDm = false,
                    TargetId = channel.Id,
                    ServerId = servers[i].Id
                });
        }

        EmptyStateText.Text = "No destinations available - join a server or start a DM first.";
        EmptyStateText.Visibility = _destinations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DestinationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ForwardDestinationItem dest } button) return;

        // Guards against a double-click firing two sends while the first
        // encrypt+send round trip is still in flight.
        button.IsEnabled = false;

        if (dest.IsDm)
        {
            var encrypted = await _api.E2ee.EncryptAsync(dest.TargetId, _content);
            if (encrypted is null)
            {
                button.IsEnabled = true;
                await _mainWindow.AlertAsync("Encryption unavailable",
                    "Couldn't forward this message - either your own encryption keys aren't unlocked yet, or this person hasn't set up secure messaging.");
                return;
            }

            await _hub.SendDirectMessageAsync(dest.TargetId, encrypted, forwardedFromAuthorUsername: _originalAuthorUsername);
        }
        else
        {
            var encrypted = await _api.E2ee.EncryptForChannelAsync(dest.ServerId, _content);
            if (encrypted is null)
            {
                button.IsEnabled = true;
                await _mainWindow.AlertAsync("Encryption unavailable",
                    "Couldn't forward this message - either your own encryption keys aren't unlocked yet, or that server's member list couldn't be loaded.");
                return;
            }

            await _hub.SendMessageAsync(dest.TargetId, encrypted.Content, encrypted.RecipientKeys, forwardedFromAuthorUsername: _originalAuthorUsername);
        }

        _mainWindow.GoBack();
    }
}
