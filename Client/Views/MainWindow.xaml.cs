using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Microsoft.Win32;

namespace Voiceover.Client.Views;

public class ServerListItem
{
    public int Id { get; set; }
    public string Initial { get; set; } = "?";
}

public class VoiceMemberItem : INotifyPropertyChanged
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;

    private bool _isSpeaking;
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            _isSpeaking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingIndicatorVisibility)));
        }
    }

    public Visibility SpeakingIndicatorVisibility => IsSpeaking ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ChannelListItem
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    // Only populated/shown for voice channels - who's currently connected.
    public ObservableCollection<VoiceMemberItem> Members { get; } = new();
}

public class MessageListItem
{
    public string AuthorUsername { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string TimeDisplay { get; set; } = string.Empty;
    public string? AttachmentUrl { get; set; }

    public string AttachmentDisplay => AttachmentUrl is null ? "" : $"📎 {System.IO.Path.GetFileName(AttachmentUrl)}";
    public Visibility ContentVisibility => string.IsNullOrEmpty(Content) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AttachmentVisibility => AttachmentUrl is null ? Visibility.Collapsed : Visibility.Visible;
}

public partial class MainWindow : Window
{
    private readonly ApiService _api;
    private readonly SignalRService _hub = new();
    private VoiceService? _voice;

    private int? _currentServerId;
    private int? _currentChannelId;
    private int? _currentVoiceChannelId;

    private readonly ObservableCollection<ServerListItem> _servers = new();
    private readonly ObservableCollection<ChannelListItem> _textChannels = new();
    private readonly ObservableCollection<ChannelListItem> _voiceChannels = new();
    private readonly ObservableCollection<MessageListItem> _messages = new();

    private DateTime _lastTypingNotify = DateTime.MinValue;

    public MainWindow(ApiService api)
    {
        InitializeComponent();
        _api = api;

        ServerList.ItemsSource = _servers;
        TextChannelList.ItemsSource = _textChannels;
        VoiceChannelList.ItemsSource = _voiceChannels;
        MessageList.ItemsSource = _messages;

        _hub.MessageReceived += OnMessageReceived;
        _hub.UserTyping += OnUserTyping;
        _hub.VoiceUserJoined += OnVoiceUserJoined;
        _hub.VoiceUserLeft += OnVoiceUserLeft;
        _hub.UserSpeaking += OnUserSpeaking;
        _hub.Reconnecting += () => Dispatcher.Invoke(() => ConnectionStatusText.Text = "Reconnecting...");
        _hub.Reconnected += OnReconnected;
        _hub.ConnectionClosed += () => Dispatcher.Invoke(() => ConnectionStatusText.Text = "Disconnected");

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_voice is not null)
            await _voice.LeaveAllAsync();
        await _hub.DisconnectAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _hub.ConnectAsync(App.HubUrl, _api.Token!);
        _voice = new VoiceService(_hub, _api.CurrentUserId!.Value);
        _voice.PeerConnected += userId => Dispatcher.Invoke(() => ConnectionStatusText.Text = "Voice connected");
        _voice.PeerDisconnected += userId => Dispatcher.Invoke(() => ConnectionStatusText.Text = "");
        _voice.LocalSpeakingChanged += isSpeaking => _ = OnLocalSpeakingChangedAsync(isSpeaking);
        await LoadServersAsync();
    }

    private async Task LoadServersAsync()
    {
        var servers = await _api.GetMyServersAsync();
        _servers.Clear();
        foreach (var s in servers)
            _servers.Add(new ServerListItem { Id = s.Id, Initial = s.Name.Length > 0 ? s.Name[0].ToString().ToUpper() : "?" });
    }

    private async Task RefreshChannelsAsync(int serverId)
    {
        var channels = await _api.GetChannelsAsync(serverId);
        _textChannels.Clear();
        _voiceChannels.Clear();
        foreach (var c in channels)
        {
            var item = new ChannelListItem { Id = c.Id, DisplayName = c.Type == "Text" ? $"# {c.Name}" : $"🔊 {c.Name}" };
            if (c.Type == "Text") _textChannels.Add(item);
            else _voiceChannels.Add(item);
        }
    }

    private async void ServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int serverId }) return;
        _currentServerId = serverId;

        var servers = await _api.GetMyServersAsync();
        var server = servers.Find(s => s.Id == serverId);
        ServerNameText.Text = server?.Name ?? "Server";

        await RefreshChannelsAsync(serverId);
    }

    private async void ChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;

        if (_currentChannelId.HasValue)
            await _hub.LeaveChannelAsync(_currentChannelId.Value);

        _currentChannelId = channelId;
        await _hub.JoinChannelAsync(channelId);

        var channelItem = FindChannelDisplayName(channelId);
        ChannelNameText.Text = channelItem ?? "# channel";

        var history = await _api.GetMessageHistoryAsync(channelId);
        _messages.Clear();
        foreach (var m in history)
            _messages.Add(ToListItem(m));

        ScrollToBottom();
    }

    private async void VoiceChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;
        if (_voice is null) return;

        if (_currentVoiceChannelId.HasValue)
        {
            await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
            await _voice.LeaveAllAsync();
            FindVoiceChannelItem(_currentVoiceChannelId.Value)?.Members.Clear();
        }

        _currentVoiceChannelId = channelId;
        _voice.SetActiveChannel(channelId);
        var existingMembers = await _hub.JoinVoiceChannelAsync(channelId);

        var item = FindVoiceChannelItem(channelId);
        if (item is not null)
        {
            item.Members.Clear();
            foreach (var m in existingMembers)
                item.Members.Add(new VoiceMemberItem { UserId = m.UserId, Username = m.Username });
            if (_api.CurrentUserId is not null && _api.CurrentUsername is not null)
                item.Members.Add(new VoiceMemberItem { UserId = _api.CurrentUserId.Value, Username = _api.CurrentUsername });
        }

        LeaveVoiceButton.Visibility = Visibility.Visible;
        ConnectionStatusText.Text = "Joined voice";
    }

    private async void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show("Click Yes to create a new server, or No to join one with an invite code.",
            "Create or Join?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;

        if (choice == MessageBoxResult.Yes)
        {
            var name = PromptForText("Server name:");
            if (string.IsNullOrWhiteSpace(name)) return;

            var server = await _api.CreateServerAsync(name);
            if (server is not null)
                await LoadServersAsync();
        }
        else
        {
            var code = PromptForText("Invite code:");
            if (string.IsNullOrWhiteSpace(code)) return;

            var (success, error) = await _api.JoinByInviteAsync(code.Trim());
            if (!success)
            {
                MessageBox.Show(error ?? "Could not join with that invite code.", "Join Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            await LoadServersAsync();
        }
    }

    private async void AddChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServerId is null)
        {
            MessageBox.Show("Select a server first.");
            return;
        }

        var name = PromptForText("Channel name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var isVoice = MessageBox.Show("Make this a voice channel?", "Channel Type",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        await _api.CreateChannelAsync(_currentServerId.Value, name, isVoice ? "Voice" : "Text");
        await RefreshChannelsAsync(_currentServerId.Value);
    }

    private async void DeleteChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId } || _currentServerId is null) return;

        var confirm = MessageBox.Show("Delete this channel? This cannot be undone.", "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var success = await _api.DeleteChannelAsync(_currentServerId.Value, channelId);
        if (!success)
        {
            MessageBox.Show("Could not delete this channel (you may lack permission).", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (channelId == _currentChannelId)
        {
            await _hub.LeaveChannelAsync(_currentChannelId.Value);
            _currentChannelId = null;
            _messages.Clear();
            ChannelNameText.Text = "# select-a-channel";
        }

        if (_voice is not null && channelId == _currentVoiceChannelId)
        {
            await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
            await _voice.LeaveAllAsync();
            _currentVoiceChannelId = null;
            LeaveVoiceButton.Visibility = Visibility.Collapsed;
            ConnectionStatusText.Text = "";
        }

        await RefreshChannelsAsync(_currentServerId.Value);
    }

    private async void LeaveVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVoiceChannelId is null || _voice is null) return;

        await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
        await _voice.LeaveAllAsync();
        FindVoiceChannelItem(_currentVoiceChannelId.Value)?.Members.Clear();
        _currentVoiceChannelId = null;
        LeaveVoiceButton.Visibility = Visibility.Collapsed;
        ConnectionStatusText.Text = "";
    }

    private void LogOutButton_Click(object sender, RoutedEventArgs e)
    {
        // MainWindow_Closed already handles leaving voice / disconnecting the hub.
        var login = new LoginWindow();
        login.Show();
        Close();
    }

    private async void MembersButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServerId is null)
        {
            MessageBox.Show("Select a server first.");
            return;
        }

        var window = new MembersWindow(_api, _currentServerId.Value);
        window.Owner = this;
        window.ShowDialog();
    }

    private void VoiceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        var window = new VoiceSettingsWindow(_voice);
        window.Owner = this;
        window.ShowDialog();
    }

    private void DirectMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new DirectMessageWindow(_api, _hub);
        window.Owner = this;
        window.Show();
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentChannelId is null)
        {
            MessageBox.Show("Select a channel first.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Supported files (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.txt;*.zip)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.txt;*.zip"
        };

        if (dialog.ShowDialog() != true) return;

        var upload = await _api.UploadFileAsync(dialog.FileName);
        if (upload is null)
        {
            MessageBox.Show("Upload failed.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await _hub.SendMessageAsync(_currentChannelId.Value, string.Empty, upload.Url);
    }

    private void AttachmentLink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string relativeUrl }) return;

        // Attachments are served by the ASP.NET Core server as static files.
        var fullUrl = App.ApiBaseUrl.TrimEnd('/') + relativeUrl;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullUrl) { UseShellExecute = true });
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendCurrentMessage();

    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SendCurrentMessage();
    }

    private async void MessageInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_currentChannelId is null) return;

        // Throttle typing notifications to at most once every 2 seconds.
        if ((DateTime.UtcNow - _lastTypingNotify).TotalSeconds < 2) return;
        _lastTypingNotify = DateTime.UtcNow;

        await _hub.NotifyTypingAsync(_currentChannelId.Value);
    }

    private async Task SendCurrentMessage()
    {
        if (_currentChannelId is null || string.IsNullOrWhiteSpace(MessageInput.Text)) return;

        await _hub.SendMessageAsync(_currentChannelId.Value, MessageInput.Text.Trim());
        MessageInput.Clear();
    }

    private void OnMessageReceived(MessageResponse msg)
    {
        if (msg.ChannelId != _currentChannelId) return;

        Dispatcher.Invoke(() =>
        {
            _messages.Add(ToListItem(msg));
            ScrollToBottom();
        });
    }

    private void OnUserTyping(string username, int channelId)
    {
        if (channelId != _currentChannelId) return;

        Dispatcher.Invoke(() =>
        {
            ChannelNameText.Text = $"{FindChannelDisplayName(channelId)}   ({username} is typing...)";
        });
    }


    // SignalR's automatic reconnect gives us a fresh connection with no group
    // memberships, so whatever channel/voice channel the user had open needs
    // to be re-joined explicitly - otherwise messages/voice presence silently
    // stop arriving until the user manually switches channels.
    private async void OnReconnected()
    {
        if (_currentChannelId.HasValue)
            await _hub.JoinChannelAsync(_currentChannelId.Value);

        if (_currentVoiceChannelId.HasValue)
            await _hub.JoinVoiceChannelAsync(_currentVoiceChannelId.Value);

        Dispatcher.Invoke(() => ConnectionStatusText.Text = "");
    }

    private void OnVoiceUserJoined(int userId, string username, int channelId)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            if (item is not null && !item.Members.Any(m => m.UserId == userId))
                item.Members.Add(new VoiceMemberItem { UserId = userId, Username = username });
        });
    }

    private void OnVoiceUserLeft(int userId, string username, int channelId)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var existing = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (item is not null && existing is not null) item.Members.Remove(existing);
        });
    }

    private void OnUserSpeaking(int userId, int channelId, bool isSpeaking)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsSpeaking = isSpeaking;
        });
    }

    // Fired from VoiceService's background audio-level detection, so this
    // isn't on the UI thread yet.
    private async Task OnLocalSpeakingChangedAsync(bool isSpeaking)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendSpeakingAsync(_currentVoiceChannelId.Value, isSpeaking);
        }
        catch
        {
            // Best-effort - a dropped speaking-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsSpeaking = isSpeaking;
        });
    }

    private static MessageListItem ToListItem(MessageResponse m) => new()
    {
        AuthorUsername = m.AuthorUsername,
        Content = m.Content,
        TimeDisplay = m.SentAt.ToLocalTime().ToString("t"),
        AttachmentUrl = m.AttachmentUrl
    };

    private string? FindChannelDisplayName(int channelId)
    {
        foreach (var c in _textChannels) if (c.Id == channelId) return c.DisplayName;
        foreach (var c in _voiceChannels) if (c.Id == channelId) return c.DisplayName;
        return null;
    }

    private ChannelListItem? FindVoiceChannelItem(int channelId)
    {
        foreach (var c in _voiceChannels) if (c.Id == channelId) return c;
        return null;
    }

    private void ScrollToBottom() => MessageScroll.ScrollToEnd();

    private static string? PromptForText(string label)
    {
        // Simple inline input dialog to keep this scaffold dependency-free.
        // Swap for a proper WPF dialog/UserControl as the app grows.
        var window = new Window
        {
            Width = 320,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = label,
            ResizeMode = ResizeMode.NoResize
        };

        var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var textBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 8, 0, 8) };
        var okButton = new System.Windows.Controls.Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };

        string? result = null;
        okButton.Click += (_, _) => { result = textBox.Text; window.Close(); };

        stack.Children.Add(new System.Windows.Controls.TextBlock { Text = label });
        stack.Children.Add(textBox);
        stack.Children.Add(okButton);
        window.Content = stack;

        window.ShowDialog();
        return result;
    }
}
