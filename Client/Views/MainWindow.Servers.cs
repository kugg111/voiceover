using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Voiceover.Client.Models;
using Voiceover.Client.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using UserControl = System.Windows.Controls.UserControl;

namespace Voiceover.Client.Views;

public partial class MainWindow
{
    private int? _currentServerId;
    private int? _currentChannelId;

    // Property, not a plain field, so every voice-state transition that
    // assigns it (join, leave, switch channels, channel-deleted) keeps the
    // in-game overlay's roster in sync automatically via SyncOverlayRoster,
    // rather than having to remember to update the overlay at each of the
    // several leave paths.
    private bool _canManageCurrentServer;

    // Gates typing @everyone/@here as a real ping (see SendCurrentMessage) -
    // recomputed alongside _canManageCurrentServer in LoadMembersPanelAsync.
    private bool _canMentionEveryone;

    // Gates the Ban List / Moderation Log buttons - mirrors
    // ServersController.GetBans/GetModerationLog's own ViewAuditLog gate
    // exactly, rather than the coarser _canManageCurrentServer (any
    // permission at all), so those buttons don't show for a Moderator who'd
    // just get a 403 clicking them.
    private bool _canViewAuditLog;
    private readonly BulkObservableCollection<ServerListItem> _servers = new();
    private readonly BulkObservableCollection<MemberListItem> _members = new();
    private readonly BulkObservableCollection<ChannelListItem> _textChannels = new();
    private readonly BulkObservableCollection<ChannelListItem> _voiceChannels = new();
    private ChannelListItem? _draggedChannelItem;
    private Point _channelDragStartPoint;

    // The current server's categories, ordered by Position - refreshed
    // alongside channels (see RefreshChannelsAsync) since the two are always
    // consumed together (grouping _textChannels/_voiceChannels by category,
    // and populating the "Move to Category" submenu on each channel row).
    private List<CategoryResponse> _categories = new();
    private readonly Dictionary<int, int> _unreadTextChannelCounts = new();

    // channelId -> the server it belongs to - needed to decrypt a channel
    // message's E2EE content (the per-server key is looked up by server id,
    // not channel id) for messages that arrive for a server other than the
    // one currently open, same reasoning as _unreadTextChannelCounts above.
    // Populated in LoadServersAsync (every server up front) and
    // RefreshChannelsAsync (safety net for channels created afterward).
    private readonly Dictionary<int, int> _channelServerIds = new();

    // Channels this device has already joined the SignalR group for -
    // JoinChannelAsync is idempotent server-side either way, but tracking
    // this client-side avoids re-issuing a hub call for every text channel
    // on every single server switch (see RefreshChannelsAsync).
    private readonly HashSet<int> _joinedChannelIds = new();

    // serverId -> its custom emoji, only populated for servers actually
    // opened this session (see RefreshEmojisAsync, called from
    // ServerButton_Click alongside RefreshChannelsAsync) - EmojiPickerPopup_
    // Opened reads this to add per-server custom-emoji buttons to the
    // reaction picker for a channel message.
    private readonly Dictionary<int, List<EmojiResponse>> _serverEmojis = new();

    public async Task<SetDiscoverableRequest?> ShowDiscoverabilitySettingsAsync(bool currentIsPublic, string? currentDescription)
    {
        ModalTitleText.Text = "Discoverability";
        ModalStandardPanel.Visibility = Visibility.Visible;
        ModalCreateOrJoinPanel.Visibility = Visibility.Collapsed;
        ModalMessageText.Text = "Let anyone browse and join this server from Discover Servers, without needing an invite.";
        ModalMessageText.Visibility = Visibility.Visible;
        ModalInputBox.Visibility = Visibility.Collapsed;

        ModalCustomContent.Children.Clear();
        var isPublicCheck = new System.Windows.Controls.CheckBox
        {
            Content = "List this server in the public directory",
            IsChecked = currentIsPublic,
            Foreground = (Brush)FindResource("TextNormal"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var descriptionLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Description (optional, shown in the directory)",
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var descriptionBox = new System.Windows.Controls.TextBox
        {
            Text = currentDescription ?? "",
            MaxLength = 300,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Height = 70,
            Padding = new Thickness(8, 6, 8, 6),
            Background = (Brush)FindResource("BgDarker"),
            Foreground = (Brush)FindResource("TextNormal"),
            BorderThickness = new Thickness(0)
        };
        ModalCustomContent.Children.Add(isPublicCheck);
        ModalCustomContent.Children.Add(descriptionLabel);
        ModalCustomContent.Children.Add(descriptionBox);
        ModalCustomContentScroll.Visibility = Visibility.Visible;

        ModalButtonsPanel.Children.Clear();
        ModalButtonsPanel.Children.Add(BuildModalButton("Cancel", ModalButtonStyle.Plain, () => CompleteModal(null)));
        ModalButtonsPanel.Children.Add(BuildModalButton("Save", ModalButtonStyle.Primary, () =>
            CompleteModal(new SetDiscoverableRequest(isPublicCheck.IsChecked == true, descriptionBox.Text))));

        var result = await ShowModal() as SetDiscoverableRequest;
        ModalCustomContent.Children.Clear();
        ModalCustomContentScroll.Visibility = Visibility.Collapsed;
        return result;
    }

    // Same shape as PromptAsync, but with a masked PasswordBox instead of a
    // plain TextBox - used wherever re-entering the account password is the
    // "prove you're still you" gate for a security-sensitive action (e.g.
    // Disable2FaButton_Click), which deserves not being shown in plaintext
    // on screen the way a server description or username would be.
    public async Task<bool?> CreateOrJoinAsync()
    {
        ModalTitleText.Text = "Add a Server";
        ModalStandardPanel.Visibility = Visibility.Collapsed;
        ModalCreateOrJoinPanel.Visibility = Visibility.Visible;

        return await ShowModal() as bool?;
    }

    private void ModalCreateButton_Click(object sender, RoutedEventArgs e) => CompleteModal(true);
    private void ModalJoinButton_Click(object sender, RoutedEventArgs e) => CompleteModal(false);

    // Hooked via XAML (Closing="MainWindow_Closing"), same as
    // PreviewKeyDown - runs before MainWindow_Closed below, which only ever
    // fires for a real close now (tray Exit, log out, session expiry, or
    // the tray setting turned off).
    private async Task LoadServersAsync()
    {
        var servers = await _api.GetMyServersAsync();
        _servers.ReplaceAll(servers.Select(s => new ServerListItem
        {
            Id = s.Id,
            Name = s.Name,
            IconUrl = App.ResolveUploadUrl(s.IconUrl),
            OwnerId = s.OwnerId,
            IsOwner = s.OwnerId == _api.CurrentUserId,
            IsPublic = s.IsPublic,
            Description = s.Description
        }));
        OnboardingNudgePopup.IsOpen = _servers.Count == 0;

        // Join every text channel's SignalR group across every server the
        // user belongs to - not just whichever one happens to be open right
        // now. ReceiveMessage only reaches clients that are in a channel's
        // own group, so without this, unread dots could only ever work while
        // browsing the exact server a message landed in - unlike DMs, which
        // reach you regardless of what you're looking at (Clients.User, not
        // a group).
        //
        // The per-server channel list fetches are independent HTTP calls, so
        // they run concurrently instead of one at a time (HttpClient is safe
        // for concurrent use). The actual SignalR joins below stay
        // sequential - HubConnection isn't documented as safe for concurrent
        // invocation from multiple threads - but only for channels this
        // device hasn't already joined (_joinedChannelIds), so a later
        // reload (e.g. RefreshChannelsAsync) doesn't redo them.
        var channelLists = await Task.WhenAll(servers.Select(s => _api.GetChannelsAsync(s.Id)));

        foreach (var channels in channelLists)
        {
            foreach (var c in channels)
                _channelServerIds[c.Id] = c.GuildServerId;

            foreach (var c in channels.Where(c => c.Type == "Text" && _joinedChannelIds.Add(c.Id)))
                await _hub.JoinChannelAsync(c.Id);
        }
    }

    private async Task RefreshChannelsAsync(int serverId)
    {
        var channels = await _api.GetChannelsAsync(serverId);
        _categories = await _api.GetCategoriesAsync(serverId);
        var categoryById = _categories.ToDictionary(c => c.Id);

        // Stable-sorts so each category's channels stay contiguous (needed
        // for the ItemsControl.GroupStyle grouping in MainWindow.xaml to
        // render one header per category instead of interleaving) - -1 for
        // uncategorized channels puts them first, matching Discord's own
        // convention. GetChannelsAsync already returns channels ordered by
        // their own Position, and OrderBy is stable, so within one category
        // (or the uncategorized group) that relative order is preserved.
        var orderedChannels = channels.OrderBy(c =>
            c.CategoryId is { } categoryId && categoryById.TryGetValue(categoryId, out var cat) ? cat.Position : -1);

        var textItems = new List<ChannelListItem>();
        var voiceItems = new List<ChannelListItem>();
        foreach (var c in orderedChannels)
        {
            _channelServerIds[c.Id] = c.GuildServerId;

            var item = new ChannelListItem
            {
                Id = c.Id,
                DisplayName = c.Type == "Text" ? $"# {c.Name}" : $"🔊 {c.Name}",
                UnreadCount = c.Type == "Text" ? _unreadTextChannelCounts.GetValueOrDefault(c.Id) : 0,
                CategoryId = c.CategoryId,
                CategoryName = c.CategoryId is { } cid && categoryById.TryGetValue(cid, out var category) ? category.Name : string.Empty
            };
            if (c.Type == "Text") textItems.Add(item);
            else voiceItems.Add(item);
        }
        _textChannels.ReplaceAll(textItems);
        _voiceChannels.ReplaceAll(voiceItems);

        // Safety net for channels created after the initial LoadServersAsync
        // sweep (e.g. someone else added one) - only joins ones this device
        // hasn't already joined, instead of unconditionally rejoining every
        // text channel on every single server switch.
        foreach (var c in textItems.Where(c => _joinedChannelIds.Add(c.Id)))
            await _hub.JoinChannelAsync(c.Id);
    }

    // Populates _serverEmojis and CustomEmojiRegistry for this server -
    // called on every ServerButton_Click (matches RefreshChannelsAsync's own
    // scope: only the currently-open server needs a fresh list) and again
    // whenever ServerEmojisChanged fires (someone added/removed one).
    private async Task RefreshEmojisAsync(int serverId)
    {
        var emojis = await _api.GetServerEmojisAsync(serverId);
        _serverEmojis[serverId] = emojis;
        foreach (var emoji in emojis)
        {
            var url = App.ResolveUploadUrl(emoji.ImageUrl);
            if (url is not null) CustomEmojiRegistry.Register(emoji.Id, url);
        }
    }

    private async void ServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int serverId }) return;

        ShowServerSidebar();

        // Note: text channel SignalR groups are deliberately NOT left here -
        // the app stays joined to every text channel across every server (see
        // LoadServersAsync) so unread dots keep working no matter which
        // server or view is currently open, same as DMs already do.
        if (_currentServerId.HasValue && _currentServerId.Value != serverId)
            await _hub.LeaveServerPresenceAsync(_currentServerId.Value);

        _currentServerId = serverId;
        await _hub.JoinServerPresenceAsync(serverId);

        // _servers is already populated (LoadServersAsync runs at login/
        // reconnect) - no need to refetch the whole list just to read one
        // server's name back out of it.
        var server = _servers.FirstOrDefault(s => s.Id == serverId);
        ServerNameText.Text = server?.Name ?? "Server";

        await RefreshChannelsAsync(serverId);
        await RefreshEmojisAsync(serverId);
        await LoadVoiceRostersAsync(serverId);
        await LoadMembersPanelAsync(serverId);
    }

    private async Task LoadMembersPanelAsync(int serverId)
    {
        var members = await _api.GetMembersAsync(serverId);
        var self = members.FirstOrDefault(m => m.UserId == _api.CurrentUserId);
        var isOwner = self?.Role == "Owner";
        var canManageServer = isOwner || self?.Role == "Moderator";
        _canManageCurrentServer = canManageServer;

        // Granular checks (Ban/Purge) mirror PermissionService.HasPermissionAsync
        // exactly: Owner always true, Moderator only if the specific bit is
        // set. Kick/promote-demote stay on the coarser existing rule
        // (any Moderator) - only the two newer moderation actions got
        // split out as individually toggleable in this batch.
        var selfPermissions = (ServerPermission)(self?.Permissions ?? 0);
        var hasKick = isOwner || (self?.Role == "Moderator" && selfPermissions.HasFlag(ServerPermission.KickMembers));
        var hasManageMessages = isOwner || (self?.Role == "Moderator" && selfPermissions.HasFlag(ServerPermission.ManageMessages));
        var hasManageRoles = isOwner || (self?.Role == "Moderator" && selfPermissions.HasFlag(ServerPermission.ManageRoles));
        _canMentionEveryone = isOwner || (self?.Role == "Moderator" && selfPermissions.HasFlag(ServerPermission.MentionEveryone));
        _canViewAuditLog = isOwner || (self?.Role == "Moderator" && selfPermissions.HasFlag(ServerPermission.ViewAuditLog));

        _members.ReplaceAll(members.Select(m =>
        {
            var isSelf = m.UserId == _api.CurrentUserId;
            return new MemberListItem
            {
                UserId = m.UserId,
                Username = m.Username,
                AvatarUrl = App.ResolveUploadUrl(m.AvatarUrl),
                Role = m.Role,
                IsSelf = isSelf,
                // Mirrors ServersController.ChangeRole's server-side gate
                // (ManageRoles, Owner implicitly included).
                CanChangeRole = hasManageRoles && m.Role != "Owner" && !isSelf,
                CanKick = canManageServer && m.Role != "Owner" && !isSelf,
                CanBan = hasKick && m.Role != "Owner" && !isSelf,
                CanPurge = hasManageMessages && !isSelf,
                // Stays owner-only, unlike CanChangeRole above - mirrors
                // SetPermissions' own server-side gate (see that endpoint's
                // comment for why editing another Moderator's exact bits
                // isn't delegable via ManageRoles).
                CanEditPermissions = isOwner && m.Role == "Moderator" && !isSelf,
                Permissions = m.Permissions,
                PresenceState = m.PresenceState,
                CustomStatus = m.CustomStatus
            };
        }));
    }

    // Even with both menu items hidden, an empty ContextMenu would still
    // pop open as a bare sliver - cancel it outright for your own row so
    // right-clicking yourself truly does nothing.
    private void MemberRow_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MemberListItem { IsSelf: true } })
            e.Handled = true;
    }

    private async void MemberRoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int userId } || _currentServerId is null) return;

        var item = _members.FirstOrDefault(m => m.UserId == userId);
        if (item is null) return;

        var success = await _api.ChangeRoleAsync(_currentServerId.Value, userId, item.NextRole);
        if (!success)
        {
            await AlertAsync("Error", "Could not change this member's role.");
            return;
        }

        await LoadMembersPanelAsync(_currentServerId.Value);
    }

    private async void MemberKickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int userId } || _currentServerId is null) return;

        if (!await ConfirmAsync("Confirm Kick", "Remove this member from the server?", "Kick", destructive: true)) return;

        var success = await _api.KickMemberAsync(_currentServerId.Value, userId);
        if (!success)
        {
            await AlertAsync("Error", "Could not kick this member (you may lack permission, or they're the owner).");
            return;
        }

        await LoadMembersPanelAsync(_currentServerId.Value);
    }

    private async void MemberBanButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int userId } || _currentServerId is null) return;

        if (!await ConfirmAsync("Confirm Ban",
            "Ban this member? They won't be able to rejoin via any invite link until unbanned.", "Ban", destructive: true)) return;

        var (success, error) = await _api.BanMemberAsync(_currentServerId.Value, userId, reason: null);
        if (!success)
        {
            await AlertAsync("Error", error ?? "Could not ban this member.");
            return;
        }

        await LoadMembersPanelAsync(_currentServerId.Value);
    }

    private async void MemberPurgeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int userId } || _currentChannelId is null) return;

        if (!await ConfirmAsync("Confirm Purge",
            "Delete every message this member sent in the current channel? This cannot be undone.", "Purge", destructive: true)) return;

        var success = await _api.DeleteAllMessagesFromUserAsync(_currentChannelId.Value, userId);
        if (!success)
            await AlertAsync("Error", "Could not purge this member's messages.");
    }

    private void MemberEditPermissionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: MemberListItem member } || _currentServerId is null) return;
        NavigateTo(new EditPermissionsPage(this, _api, _currentServerId.Value, member.UserId, member.Username, (ServerPermission)member.Permissions),
            $"Permissions for {member.Username}");
    }

    private void ModerationLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServerId is null) return;
        NavigateTo(new ModerationLogPage(_api, _currentServerId.Value, _hub), "Moderation Log");
    }

    private void BanListButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentServerId is null) return;
        NavigateTo(new BanListPage(_api, _currentServerId.Value, _hub), "Banned Users");
    }

    // Populates each voice channel's member list from a server-wide snapshot,
    // so anyone who opens a server sees who's currently in voice without
    // having joined anything themselves. Uses the same idempotent-add pattern
    // as OnVoiceUserJoined below, so this doesn't fight with a live event
    // arriving for the same person around the same time.
    private async void ChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;

        SaveCurrentDraft();

        // Deliberately doesn't leave the previous channel's group - all text
        // channels in the open server stay joined (see RefreshChannelsAsync)
        // so unread dots keep working for channels you switch away from too.
        _currentChannelId = channelId;
        CancelReply();
        await _hub.JoinChannelAsync(channelId);

        _unreadTextChannelCounts.Remove(channelId);
        var thisChannelItem = FindTextChannelItem(channelId);
        if (thisChannelItem is not null) thisChannelItem.UnreadCount = 0;

        var channelItem = FindChannelDisplayName(channelId);
        ChannelNameText.Text = channelItem ?? "# channel";
        DmCallButton.Visibility = Visibility.Collapsed;
        PinnedMessagesButton.Visibility = Visibility.Visible;
        SearchMessagesButton.Visibility = Visibility.Visible;
        ModerationLogButton.Visibility = _canViewAuditLog ? Visibility.Visible : Visibility.Collapsed;
        BanListButton.Visibility = _canViewAuditLog ? Visibility.Visible : Visibility.Collapsed;

        LoadDraftIntoInput();
        await LoadChannelHistoryAsync(channelId);
    }

    // Captures whatever's currently sitting unsent in the compose box into
    // the outgoing channel/DM's draft slot - called right before switching
    // to a different channel or DM (see ChannelButton_Click/
    // OpenDmConversation), using the OLD _currentChannelId/_dmActiveUserId
    // (still set at the point this runs, before either gets reassigned).
    private async void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        OnboardingNudgePopup.IsOpen = false;

        var createSelected = await CreateOrJoinAsync();

        if (createSelected == true)
        {
            var name = await PromptAsync("Create a Server", "Server name:");
            if (string.IsNullOrWhiteSpace(name)) return;

            var server = await _api.CreateServerAsync(name);
            if (server is not null)
                await LoadServersAsync();
        }
        else if (createSelected == false)
        {
            var code = await PromptAsync("Join with a Code", "Invite code:");
            if (string.IsNullOrWhiteSpace(code)) return;

            var (success, error) = await _api.JoinByInviteAsync(code.Trim());
            if (!success)
            {
                await AlertAsync("Join Failed", error ?? "Could not join with that invite code.");
                return;
            }
            await LoadServersAsync();
        }
    }

    private async void AddChannelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        var isVoice = await ConfirmAsync("Choose channel type", "", "Voice Channel", cancelText: "Text Channel");

        var name = await PromptAsync("Add Channel", "Channel name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        var created = await _api.CreateChannelAsync(serverId, name, isVoice ? "Voice" : "Text");

        // Join the new channel's group regardless of which server is open -
        // otherwise it wouldn't get unread dots until the next full
        // LoadServersAsync (e.g. next login).
        if (created is not null && !isVoice)
            await _hub.JoinChannelAsync(created.Id);

        // Only the currently-open server's channel list is visible - refresh
        // it if that's the one a channel was just added to.
        if (serverId == _currentServerId)
            await RefreshChannelsAsync(serverId);
    }

    private void InvitesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        new InvitesWindow(_api, serverId) { Owner = this }.ShowDialog();
    }

    // Visible to every member (like AddChannelMenuItem_Click above) -
    // EmojisController enforces ManageChannels server-side, same pattern as
    // every other server-management action this app doesn't bother hiding
    // client-side.
    private void ManageEmojiMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        NavigateTo(new EmojiManagementPage(this, _api, serverId), "Server Emoji");
    }

    // Visible to every member (like ManageEmojiMenuItem_Click above) -
    // CategoriesController enforces ManageChannels server-side.
    private void ManageCategoriesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        NavigateTo(new CategoryManagementPage(this, _api, serverId), "Channel Categories");
    }

    private async void LeaveServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        if (!await ConfirmAsync("Confirm Leave", "Leave this server?", "Leave", destructive: true)) return;

        var (success, error) = await _api.LeaveServerAsync(serverId);
        if (!success)
        {
            await AlertAsync("Error", error ?? "Could not leave this server.");
            return;
        }

        if (serverId == _currentServerId)
        {
            _currentServerId = null;
            _textChannels.Clear();
            _voiceChannels.Clear();
            ServerNameText.Text = "Select a server";
            ChannelNameText.Text = "# select-a-channel";
            _messages.Clear();
            CancelReply();
        }

        await LoadServersAsync();
    }

    // Owner-only (see ServerListItem.OwnerMenuItemVisibility gating the menu
    // item itself) and permanent. Calls the same reset-and-reload helper
    // OnYouWereKicked uses rather than waiting on the ServerDeleted
    // broadcast to reach this client's own connection - that broadcast only
    // reaches connections that already joined this server's presence group
    // (i.e. had it open at some point this session), which isn't guaranteed
    // just from right-clicking it in the rail.
    private async void DeleteServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        if (!await ConfirmAsync("Delete Server",
            "This permanently deletes the server and everything in it - channels, messages, members. This cannot be undone.",
            "Delete Server", destructive: true)) return;

        var (success, error) = await _api.DeleteServerAsync(serverId);
        if (!success)
        {
            await AlertAsync("Error", error ?? "Could not delete this server.");
            return;
        }

        await LeaveServerLocallyIfCurrentlyViewing(serverId);
    }

    // Owner-only, same gating as DeleteServerMenuItem_Click above.
    private async void RenameServerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        var current = _servers.FirstOrDefault(s => s.Id == serverId)?.Name ?? "";
        var name = await PromptAsync("Rename Server", "New name:", current);
        if (string.IsNullOrWhiteSpace(name) || name == current) return;

        var (success, error) = await _api.RenameServerAsync(serverId, name);
        if (!success)
        {
            await AlertAsync("Error", error ?? "Could not rename this server.");
            return;
        }

        await RefreshServerNameLocallyAsync(serverId);
    }

    // Owner-only, same gating as RenameServerMenuItem_Click above.
    private async void DiscoverabilityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;

        var current = _servers.FirstOrDefault(s => s.Id == serverId);
        if (current is null) return;

        var req = await ShowDiscoverabilitySettingsAsync(current.IsPublic, current.Description);
        if (req is null) return;

        var (success, error) = await _api.SetDiscoverableAsync(serverId, req.IsPublic, req.Description);
        if (!success)
        {
            await AlertAsync("Error", error ?? "Could not update discoverability settings.");
            return;
        }

        await LoadServersAsync();
    }

    // Sets the "Mute Notifications"/"Unmute Notifications" label to match
    // this server's current state right before the menu actually opens -
    // items[2] is the mute entry's fixed position in the ContextMenu below
    // (Add Channel, Invites, Mute Notifications, Leave Server).
    private void ServerButton_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int serverId } button) return;
        if (button.ContextMenu?.Items[2] is System.Windows.Controls.MenuItem muteItem)
            muteItem.Header = NotificationMuteStorage.IsServerMuted(serverId) ? "Unmute Notifications" : "Mute Notifications";
    }

    // Personal preference (NotificationMuteStorage) - distinct from any
    // moderation permission, this only silences notifications for whoever
    // toggles it, on this device.
    private void ToggleServerMuteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int serverId }) return;
        NotificationMuteStorage.SetServerMuted(serverId, !NotificationMuteStorage.IsServerMuted(serverId));
    }

    // Same reasoning as ServerButton_ContextMenuOpening above - items[0] is
    // the mute entry's fixed position (the only item in this menu).
    private async void SetSlowModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int channelId } || _currentServerId is null) return;

        var input = await PromptAsync("Set Slow Mode", "Seconds between messages for regular members (0 to disable):");
        if (input is null) return;
        if (!int.TryParse(input, out var seconds) || seconds < 0)
        {
            await AlertAsync("Invalid Value", "Enter a whole number of seconds (0 or more).");
            return;
        }

        var success = await _api.SetSlowModeAsync(_currentServerId.Value, channelId, seconds);
        if (!success)
            await AlertAsync("Error", "Could not set slow mode (you may lack permission).");
    }

    private async void RenameChannelMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int channelId } || _currentServerId is null) return;

        // DisplayName carries a "# "/"🔊 " prefix (see RefreshChannelsAsync)
        // - strip it back off so the prompt starts from the plain name.
        var current = FindChannelDisplayName(channelId);
        var initial = current is null ? "" : current[(current.IndexOf(' ') + 1)..];

        var name = await PromptAsync("Rename Channel", "New name:", initial);
        if (string.IsNullOrWhiteSpace(name) || name == initial) return;

        var (success, error) = await _api.RenameChannelAsync(_currentServerId.Value, channelId, name);
        if (!success)
            await AlertAsync("Error", error ?? "Could not rename this channel (you may lack permission).");
    }

    // Same reasoning as ServerButton_ContextMenuOpening above - items[1] is
    // the mute entry's fixed position (Set Slow Mode..., Mute Notifications,
    // Rename Channel, Move to Category).
    private void ChannelButton_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId, DataContext: ChannelListItem item } button) return;
        if (button.ContextMenu?.Items[1] is System.Windows.Controls.MenuItem muteItem)
            muteItem.Header = NotificationMuteStorage.IsChannelMuted(channelId) ? "Unmute Notifications" : "Mute Notifications";
        if (button.ContextMenu?.Items[3] is System.Windows.Controls.MenuItem moveItem)
            PopulateMoveToCategorySubmenu(moveItem, channelId, item.CategoryId);
    }

    // Voice row's context menu has no mute entry, just Rename/Delete/Move to
    // Category - only the submenu needs rebuilding each open.
    private void VoiceChannelButton_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId, DataContext: ChannelListItem item } button) return;
        if (button.ContextMenu?.Items[2] is System.Windows.Controls.MenuItem moveItem)
            PopulateMoveToCategorySubmenu(moveItem, channelId, item.CategoryId);
    }

    // A plain value-tuple Tag doesn't work here - C# pattern matching can't
    // type-check a nullable value type inside a boxed tuple (CS8116), since
    // Nullable<T> boxes as either null or a bare T, not as Nullable<T>. A
    // record's positional pattern deconstructs via Deconstruct() instead of
    // a runtime type check, so it doesn't hit that restriction.
    private record MoveToCategoryTarget(int ChannelId, int? CategoryId);

    // Rebuilt fresh on every context-menu open (see the two handlers above)
    // rather than bound declaratively, since the set of categories to offer
    // isn't known until this server's categories are loaded, and can change
    // between opens (see RefreshChannelsAsync/_categories).
    private void PopulateMoveToCategorySubmenu(System.Windows.Controls.MenuItem submenu, int channelId, int? currentCategoryId)
    {
        submenu.Items.Clear();

        var noneItem = new System.Windows.Controls.MenuItem
        {
            Header = "(None)", IsCheckable = true, IsChecked = currentCategoryId is null,
            Tag = new MoveToCategoryTarget(channelId, null)
        };
        noneItem.Click += MoveChannelToCategoryMenuItem_Click;
        submenu.Items.Add(noneItem);

        if (_categories.Count > 0) submenu.Items.Add(new System.Windows.Controls.Separator());

        foreach (var category in _categories)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = category.Name, IsCheckable = true, IsChecked = currentCategoryId == category.Id,
                Tag = new MoveToCategoryTarget(channelId, category.Id)
            };
            item.Click += MoveChannelToCategoryMenuItem_Click;
            submenu.Items.Add(item);
        }
    }

    private async void MoveChannelToCategoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: MoveToCategoryTarget(var channelId, var categoryId) } || _currentServerId is null) return;

        var success = await _api.SetChannelCategoryAsync(_currentServerId.Value, channelId, categoryId);
        if (success) await RefreshChannelsAsync(_currentServerId.Value);
        else await AlertAsync("Error", "Could not move this channel (you may lack permission).");
    }

    private void ToggleChannelMuteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int channelId }) return;
        NotificationMuteStorage.SetChannelMuted(channelId, !NotificationMuteStorage.IsChannelMuted(channelId));
    }

    private async void DeleteChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId } || _currentServerId is null) return;

        if (!await ConfirmAsync("Confirm Delete", "Delete this channel? This cannot be undone.", "Delete", destructive: true)) return;

        var success = await _api.DeleteChannelAsync(_currentServerId.Value, channelId);
        if (!success)
        {
            await AlertAsync("Error", "Could not delete this channel (you may lack permission).");
            return;
        }

        if (channelId == _currentChannelId)
        {
            await _hub.LeaveChannelAsync(_currentChannelId.Value);
            _currentChannelId = null;
            _messages.Clear();
            CancelReply();
            ChannelNameText.Text = "# select-a-channel";
        }

        if (_voice is not null && channelId == _currentVoiceChannelId)
        {
            await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
            await _voice.LeaveAllAsync();
            _currentVoiceChannelId = null;
            VoiceControlBar.Visibility = Visibility.Collapsed;
            ConnectionStatusText.Text = "";
        }

        await RefreshChannelsAsync(_currentServerId.Value);
    }

    // Drag-and-drop channel reordering. Shared by both the text- and
    // voice-channel row templates (each row's outer Grid carries these three
    // handlers) - which BulkObservableCollection a drag belongs to is
    // resolved by membership check in ChannelRow_Drop rather than needing
    // separate handler pairs per list.
    private void ChannelRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _channelDragStartPoint = e.GetPosition(null);
        _draggedChannelItem = (sender as FrameworkElement)?.DataContext as ChannelListItem;
    }

    private void ChannelRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedChannelItem is null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _channelDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _channelDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Cleared before DoDragDrop (whose modal loop blocks this method)
        // so a stray move after the drop doesn't start a second drag.
        var dragged = _draggedChannelItem;
        _draggedChannelItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, dragged, DragDropEffects.Move);
    }

    private async void ChannelRow_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChannelListItem targetItem }) return;
        if (e.Data.GetData(typeof(ChannelListItem)) is not ChannelListItem draggedItem) return;
        if (ReferenceEquals(draggedItem, targetItem) || _currentServerId is null) return;
        // Cross-category drags aren't supported here - moving a channel to a
        // different category is an explicit action (see the "Move to
        // Category" submenu on each channel's context menu) rather than an
        // implicit side effect of a reorder drop.
        if (draggedItem.CategoryId != targetItem.CategoryId) return;

        BulkObservableCollection<ChannelListItem> list;
        if (_textChannels.Contains(draggedItem) && _textChannels.Contains(targetItem)) list = _textChannels;
        else if (_voiceChannels.Contains(draggedItem) && _voiceChannels.Contains(targetItem)) list = _voiceChannels;
        else return; // dragging between the text and voice lists isn't supported

        var oldIndex = list.IndexOf(draggedItem);
        var newIndex = list.IndexOf(targetItem);
        if (oldIndex < 0 || newIndex < 0) return;

        // Optimistic reorder - RefreshChannelsAsync below reverts it if the
        // API call fails (e.g. permission lost mid-drag).
        list.Move(oldIndex, newIndex);

        var success = await _api.ReorderChannelsAsync(_currentServerId.Value, list.Select(c => c.Id).ToList());
        if (!success) await RefreshChannelsAsync(_currentServerId.Value);
    }

    public async Task ReloadServersAsync() => await LoadServersAsync();

    private void DiscoverServersButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(new DiscoverServersPage(this, _api), "Discover Servers");

    private async Task LeaveServerContentAsync()
    {
        _currentChannelId = null;

        if (_currentServerId.HasValue)
            await _hub.LeaveServerPresenceAsync(_currentServerId.Value);
    }

    // Crossfades between the three mutually-exclusive Grid.Column="1" panels
    // (ServerSidebarPanel/MessagesSidebarPanel/FriendsSidebarPanel) instead of
    // an instant Visibility snap - they occupy the same fixed-width column so
    // fading one out while fading the other in reads as a single transition
    // rather than a flicker.
    private async void OnYouWereBanned(int serverId) => await LeaveServerLocallyIfCurrentlyViewing(serverId);

    // KickMember doesn't block rejoining (unlike a ban), but the already-open
    // client still needs to drop the server from view immediately - otherwise
    // it sits in the rail until next reload and clicking into it just throws
    // 403s from every endpoint.
    private async void OnYouWereKicked(int serverId) => await LeaveServerLocallyIfCurrentlyViewing(serverId);

    private async Task LeaveServerLocallyIfCurrentlyViewing(int serverId)
    {
        if (serverId == _currentServerId)
        {
            _currentServerId = null;
            _textChannels.Clear();
            _voiceChannels.Clear();
            ServerNameText.Text = "Select a server";
            ChannelNameText.Text = "# select-a-channel";
            _messages.Clear();
            CancelReply();
        }

        await LoadServersAsync();
    }

    // Bystander-facing counterparts to OnYouWereKicked/OnYouWereBanned above -
    // these fire for every other member with this server open, not just the
    // affected user, so a member/ban list that's currently visible doesn't
    // go stale until someone manually switches away and back. Refetch
    // rather than patch the in-memory list, same as the targeted handlers.
    private async void OnMemberKicked(int serverId, int userId)
    {
        if (serverId == _currentServerId) await LoadMembersPanelAsync(serverId);
    }

    private async void OnMemberBanned(int serverId, int userId)
    {
        if (serverId == _currentServerId) await LoadMembersPanelAsync(serverId);
    }

    // Also covers permission changes (SetPermissions reuses this same
    // event) - both change what the member panel should show, and letting
    // a demoted/promoted member's own client refetch is how it picks up
    // its own new capability buttons.
    private async void OnMemberRoleChanged(int serverId, int userId)
    {
        if (serverId == _currentServerId) await LoadMembersPanelAsync(serverId);
    }

    private async void OnChannelCreated(int serverId)
    {
        if (serverId == _currentServerId) await RefreshChannelsAsync(serverId);
    }

    private async void OnChannelDeleted(int serverId)
    {
        if (serverId == _currentServerId) await RefreshChannelsAsync(serverId);
    }

    private async void OnServerEmojisChanged(int serverId)
    {
        if (_serverEmojis.ContainsKey(serverId) || serverId == _currentServerId) await RefreshEmojisAsync(serverId);
    }

    // Bystander-facing counterpart to DeleteServerMenuItem_Click's own
    // cleanup below - fires for every other member with this server open
    // (the caller who actually deleted it handles its own UI reset inline,
    // not via this broadcast). Reuses the same "server no longer available"
    // recovery OnYouWereKicked already does.
    private async void OnServerDeleted(int serverId) => await LeaveServerLocallyIfCurrentlyViewing(serverId);

    // Fires for the caller (RenameServerMenuItem_Click doesn't do its own
    // local patch, unlike Delete) and every bystander with this server's
    // list open. LoadServersAsync refetches the new name; the header text
    // also needs its own update since it's read once at ServerButton_Click
    // time rather than bound live to the ServerListItem.
    private async void OnServerRenamed(int serverId) => await RefreshServerNameLocallyAsync(serverId);

    // Shared by the broadcast handler above (bystanders) and
    // RenameServerMenuItem_Click (the caller's own connection) - the caller
    // can't rely solely on receiving its own ServerRenamed broadcast since
    // right-clicking a server from the rail doesn't guarantee this
    // connection has joined its presence group yet (same reasoning
    // DeleteServerMenuItem_Click's comment gives for its own local reset).
    private async Task RefreshServerNameLocallyAsync(int serverId)
    {
        await LoadServersAsync();
        if (serverId == _currentServerId)
        {
            var server = _servers.FirstOrDefault(s => s.Id == serverId);
            ServerNameText.Text = server?.Name ?? "Server";
        }
    }

    // A moderator force-muted us (ChatHub.ForceMuteUser) - just flips our
    // own mic mute state; VoiceService.MicMutedChanged (already subscribed,
    // see OnLocalMutedChangedAsync) handles broadcasting it to everyone else
    // the same way a self-initiated mute does, so there's no separate
    // broadcast call needed here.
    private string? FindChannelDisplayName(int channelId)
    {
        foreach (var c in _textChannels) if (c.Id == channelId) return c.DisplayName;
        foreach (var c in _voiceChannels) if (c.Id == channelId) return c.DisplayName;
        return null;
    }

    private ChannelListItem? FindTextChannelItem(int channelId)
    {
        foreach (var c in _textChannels) if (c.Id == channelId) return c;
        return null;
    }

}
