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
    private int? _dmActiveUserId;
    // Set by ReplyMessageButton_Click, cleared on send/cancel/context-switch
    // (see CancelReply) - the message SendCurrentMessage attaches as
    // replyToMessageId on the next send.
    private string? _dmActiveUsername;

    // "Load older messages" - whether the currently open channel/DM history
    // might have more before what's loaded (heuristic: the last page came
    // back full), and a re-entrancy guard for the load-more click itself.
    private readonly BulkObservableCollection<DmConversationListItem> _dmConversations = new();
    private readonly ObservableCollection<UserSearchResultItem> _dmSearchResults = new();
    private readonly BulkObservableCollection<FriendListItem> _friends = new();
    private readonly BulkObservableCollection<FriendRequestListItem> _friendRequests = new();
    private readonly ObservableCollection<UserSearchResultItem> _friendSearchResults = new();

    // Source of truth for text-channel unread counts, kept independent of
    // _textChannels - a message can arrive for a channel before that
    // channel's server has ever been opened (so no ChannelListItem exists
    // yet to mark), unlike DM conversations, which get created fresh on
    // arrival regardless of prior UI state. channelId -> unread count.
    private void DmConversationButton_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int otherUserId } button) return;
        if (button.ContextMenu?.Items[0] is System.Windows.Controls.MenuItem muteItem)
            muteItem.Header = NotificationMuteStorage.IsDmMuted(otherUserId) ? "Unmute Notifications" : "Mute Notifications";
    }

    private void ToggleDmMuteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int otherUserId }) return;
        NotificationMuteStorage.SetDmMuted(otherUserId, !NotificationMuteStorage.IsDmMuted(otherUserId));
    }

    private async void MessagesButton_Click(object sender, RoutedEventArgs e)
    {
        await LeaveServerContentAsync();
        ShowMessagesSidebar();

        _dmActiveUserId = null;
        _dmActiveUsername = null;
        _messages.Clear();
        CancelReply();
        ChannelNameText.Text = "Select a conversation";
        DmCallButton.Visibility = Visibility.Collapsed;
        DmBlockUserButton.Visibility = Visibility.Collapsed;
        PinnedMessagesButton.Visibility = Visibility.Collapsed;
        SearchMessagesButton.Visibility = Visibility.Collapsed;
        ModerationLogButton.Visibility = Visibility.Collapsed;
        BanListButton.Visibility = Visibility.Collapsed;
        DmSearchBox.Clear();
        _dmSearchResults.Clear();

        // Re-clicking the Messages icon shouldn't silently clear unread
        // counts nobody's actually read yet - carry them over by
        // conversation partner.
        var previouslyUnread = _dmConversations.Where(c => c.HasUnread).ToDictionary(c => c.OtherUserId, c => c.UnreadCount);

        var conversations = await _api.GetDmConversationsAsync();
        _dmConversations.ReplaceAll(conversations.OrderByDescending(c => c.LastMessageAt).Select(c =>
        {
            var item = ToDmConversationItem(c);
            item.UnreadCount = previouslyUnread.GetValueOrDefault(c.OtherUserId);
            return item;
        }));
        UpdateMessagesUnreadBadge();
    }

    private async void FriendsButton_Click(object sender, RoutedEventArgs e)
    {
        await LeaveServerContentAsync();
        ShowFriendsSidebar();

        _dmActiveUserId = null;
        _dmActiveUsername = null;
        _messages.Clear();
        CancelReply();
        ChannelNameText.Text = "Select a conversation";
        DmCallButton.Visibility = Visibility.Collapsed;
        DmBlockUserButton.Visibility = Visibility.Collapsed;
        PinnedMessagesButton.Visibility = Visibility.Collapsed;
        SearchMessagesButton.Visibility = Visibility.Collapsed;
        ModerationLogButton.Visibility = Visibility.Collapsed;
        BanListButton.Visibility = Visibility.Collapsed;
        FriendSearchBox.Clear();
        _friendSearchResults.Clear();

        await LoadFriendsAsync();
        await LoadFriendRequestsAsync();
    }

    // Called when leaving the server view entirely (Messages/Friends button
    // clicked). Deliberately does NOT leave text channel SignalR groups - the
    // app stays joined to all of them everywhere (see LoadServersAsync) so
    // unread dots keep working while browsing Messages/Friends, same as DMs.
    private async void DmSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = DmSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            _dmSearchResults.Clear();
            return;
        }

        var results = await _api.SearchUsersAsync(query);
        _dmSearchResults.Clear();
        foreach (var r in results.Where(r => r.Id != _api.CurrentUserId))
            _dmSearchResults.Add(new UserSearchResultItem { Id = r.Id, Username = r.Username, AvatarUrl = App.ResolveUploadUrl(r.AvatarUrl) });
    }

    private async void DmSearchResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId } button) return;
        var username = (button.DataContext as UserSearchResultItem)?.Username ?? "user";
        await OpenDmConversation(userId, username);
    }

    private async void DmConversation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId }) return;
        var convo = _dmConversations.FirstOrDefault(c => c.OtherUserId == userId);
        await OpenDmConversation(userId, convo?.OtherUsername ?? "user");
    }

    private async Task OpenDmConversation(int userId, string username)
    {
        SaveCurrentDraft();

        _dmActiveUserId = userId;
        _dmActiveUsername = username;
        CancelReply();
        ChannelNameText.Text = $"@{username}";
        DmCallButton.Visibility = Visibility.Visible;
        DmBlockUserButton.Visibility = Visibility.Visible;
        PinnedMessagesButton.Visibility = Visibility.Collapsed;
        SearchMessagesButton.Visibility = Visibility.Visible;
        ModerationLogButton.Visibility = Visibility.Collapsed;
        BanListButton.Visibility = Visibility.Collapsed;
        LoadDraftIntoInput();

        var convo = _dmConversations.FirstOrDefault(c => c.OtherUserId == userId);
        if (convo is not null) convo.UnreadCount = 0;
        UpdateMessagesUnreadBadge();

        DmSearchBox.Clear();
        _dmSearchResults.Clear();

        var history = await _api.GetDmHistoryAsync(userId);
        var items = history.Select(m => ToDmListItem(m)).ToList();
        foreach (var item in items) ResolveReplyPreview(item, items);
        _messages.ReplaceAll(items);
        SetHasMoreHistory(history.Count);
        RefreshLatestOwnMessageFlag();

        ScrollToBottom();

        // Best-effort - marks the other party's messages as read now that
        // this conversation is open. Failure here shouldn't block viewing.
        try { await _hub.MarkDmReadAsync(userId); } catch { }
    }

    // Only the most recent own message in _messages should show a read
    // receipt (see MessageListItem.IsLatestOwnMessage) - recomputed instead
    // of tracked incrementally since the "latest" one can change from
    // several different call sites (initial load, prepend-older, a new
    // message arriving) and a single scan is cheap at realistic history sizes.
    private async Task LoadFriendsAsync()
    {
        var friends = await _api.GetFriendsAsync();
        _friends.ReplaceAll(friends.Select(f => new FriendListItem { UserId = f.UserId, Username = f.Username, AvatarUrl = App.ResolveUploadUrl(f.AvatarUrl), PresenceState = f.PresenceState, CustomStatus = f.CustomStatus }));
    }

    private async Task LoadFriendRequestsAsync()
    {
        var requests = await _api.GetFriendRequestsAsync();
        _friendRequests.ReplaceAll(requests.Select(r => new FriendRequestListItem { Id = r.Id, UserId = r.UserId, Username = r.Username, Direction = r.Direction, AvatarUrl = App.ResolveUploadUrl(r.AvatarUrl) }));
    }

    private async void FriendSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = FriendSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            _friendSearchResults.Clear();
            return;
        }

        var results = await _api.SearchUsersAsync(query);
        var existingIds = _friends.Select(f => f.UserId)
            .Concat(_friendRequests.Select(r => r.UserId))
            .ToHashSet();

        _friendSearchResults.Clear();
        foreach (var r in results.Where(r => r.Id != _api.CurrentUserId && !existingIds.Contains(r.Id)))
            _friendSearchResults.Add(new UserSearchResultItem { Id = r.Id, Username = r.Username, AvatarUrl = App.ResolveUploadUrl(r.AvatarUrl) });
    }

    private async void AddFriendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId }) return;

        var (success, error) = await _api.SendFriendRequestAsync(userId);
        if (!success)
        {
            await AlertAsync("Error", error ?? "Could not send friend request.");
            return;
        }

        FriendSearchBox.Clear();
        _friendSearchResults.Clear();
        await LoadFriendsAsync();
        await LoadFriendRequestsAsync();
    }

    private async void FriendRequestAcceptButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int friendshipId }) return;

        await _api.AcceptFriendRequestAsync(friendshipId);
        await LoadFriendsAsync();
        await LoadFriendRequestsAsync();
    }

    private async void FriendRequestRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int friendshipId }) return;

        await _api.RemoveFriendshipAsync(friendshipId);
        await LoadFriendRequestsAsync();
    }

    private async void FriendListItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int userId } button) return;
        var username = (button.DataContext as FriendListItem)?.Username ?? "user";
        await OpenDmConversation(userId, username);
    }

    // Blocking also drops any existing friendship server-side (see
    // FriendsController.Block), so a full refetch is enough to make the
    // blocked user disappear from this list too, without needing to also
    // reach into _friends and remove it manually.
    private async void BlockUserMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: int userId }) return;

        if (!await ConfirmAsync("Block User", "Block this user? They won't be able to send you friend requests or direct messages.", "Block", destructive: true)) return;

        var success = await _api.BlockUserAsync(userId);
        if (!success)
        {
            await AlertAsync("Error", "Could not block this user.");
            return;
        }

        await LoadFriendsAsync();
    }

    private async void DmBlockUserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dmActiveUserId is not int userId) return;

        if (!await ConfirmAsync("Block User", $"Block {_dmActiveUsername}? They won't be able to send you friend requests or direct messages.", "Block", destructive: true)) return;

        var success = await _api.BlockUserAsync(userId);
        if (!success)
        {
            await AlertAsync("Error", "Could not block this user.");
            return;
        }

        // Same DM-conversation reset FriendsButton_Click does - inlined
        // rather than awaiting that handler directly, since it's declared
        // async void (Click handlers aren't awaitable).
        _dmActiveUserId = null;
        _dmActiveUsername = null;
        _messages.Clear();
        CancelReply();
        ChannelNameText.Text = "Select a conversation";
        DmCallButton.Visibility = Visibility.Collapsed;
        DmBlockUserButton.Visibility = Visibility.Collapsed;
        PinnedMessagesButton.Visibility = Visibility.Collapsed;
        SearchMessagesButton.Visibility = Visibility.Collapsed;
        ModerationLogButton.Visibility = Visibility.Collapsed;
        BanListButton.Visibility = Visibility.Collapsed;

        await LoadFriendsAsync();
    }

    private void OnFriendRequestReceived(int friendshipId, int requesterId, string requesterUsername)
    {
        Dispatcher.Invoke(async () =>
        {
            if (FriendsSidebarPanel.Visibility == Visibility.Visible)
                await LoadFriendRequestsAsync();
        });
    }

    private void OnFriendRequestAccepted(int friendshipId, int accepterId)
    {
        Dispatcher.Invoke(async () =>
        {
            if (FriendsSidebarPanel.Visibility == Visibility.Visible)
            {
                await LoadFriendsAsync();
                await LoadFriendRequestsAsync();
            }
        });
    }

    private async void OnDirectMessageReceived(DirectMessageResponse dm)
    {
        try
        {
            var otherUserId = dm.SenderId == _api.CurrentUserId ? dm.RecipientId : dm.SenderId;
            var isOwnMessage = dm.SenderId == _api.CurrentUserId;

            // Pushed straight from the hub, so still opaque ciphertext (see
            // ChatHub.SendDirectMessage) - decrypt before this touches any
            // UI-bound state, same as the REST paths in ApiService already do
            // transparently for history/conversation loads.
            var content = await _api.E2ee.DecryptAsync(otherUserId, dm.Content);

            Dispatcher.Invoke(() =>
            {
                // Bump/update the conversation list regardless of whether it's
                // currently visible, so it's accurate next time Messages is opened.
                var existing = _dmConversations.FirstOrDefault(c => c.OtherUserId == otherUserId);
                if (existing is not null) _dmConversations.Remove(existing);

                var isCurrentlyOpen = _dmActiveUserId == otherUserId;
                var newUnreadCount = !isOwnMessage && !isCurrentlyOpen ? (existing?.UnreadCount ?? 0) + 1 : 0;
                _dmConversations.Insert(0, new DmConversationListItem
                {
                    OtherUserId = otherUserId,
                    OtherUsername = existing?.OtherUsername ?? _dmActiveUsername ?? "user",
                    OtherUserAvatarUrl = existing?.OtherUserAvatarUrl,
                    LastMessagePreview = CallEventMessage.Prettify(content),
                    LastMessageAt = dm.SentAt,
                    UnreadCount = newUnreadCount
                });

                if (isCurrentlyOpen)
                {
                    var item = ToDmListItem(dm, content);
                    ResolveReplyPreview(item, _messages);
                    _messages.Add(item);
                    RefreshLatestOwnMessageFlag();
                    ScrollToBottom();

                    // The conversation is already open, so this incoming
                    // message is effectively seen immediately - mark it read
                    // right away instead of waiting for the next time the
                    // conversation is (re)opened.
                    if (!isOwnMessage)
                    {
                        var senderId = otherUserId;
                        _ = Task.Run(async () => { try { await _hub.MarkDmReadAsync(senderId); } catch { } });
                    }
                }

                UpdateMessagesUnreadBadge();

                // Same "not actually looking at this conversation" logic as
                // OnMessageReceived - either a different view is open, or this
                // DM is open but the window itself isn't focused. Skipped
                // entirely for call-event messages (missed/declined/ended) -
                // OnCallEndedRemotely already shows its own dedicated "Missed
                // Call" toast for the one case that actually needs one, and
                // this generic path has no idea how to prettify the content
                // beyond the raw sentinel text.
                if (!isOwnMessage && (!isCurrentlyOpen || !IsActive) && !CallEventMessage.IsCallEvent(content)
                    && !NotificationMuteStorage.IsDmMuted(otherUserId))
                {
                    NotificationService.PlayMessageSound();
                    var preview = content.Length > 80 ? content[..80] + "…" : content;
                    NotificationService.ShowToast($"{existing?.OtherUsername ?? _dmActiveUsername ?? "New message"}", preview);
                }
            });
        }
        catch
        {
            // Best-effort - see OnMessageReceived.
        }
    }

    private async void OnDirectMessageEdited(DirectMessageResponse dm)
    {
        try
        {
            var otherUserId = dm.SenderId == _api.CurrentUserId ? dm.RecipientId : dm.SenderId;

            // Only worth decrypting if this conversation is actually open below
            // (matches the early-return this had before) - still has to happen
            // before Dispatcher.Invoke since it's async.
            if (otherUserId != _dmActiveUserId) return;
            var content = await _api.E2ee.DecryptAsync(otherUserId, dm.Content);

            Dispatcher.Invoke(() =>
            {
                var item = FindMessageById(dm.Id);
                if (item is null) return;

                item.Content = content;
                item.IsEdited = dm.EditedAt is not null;
            });
        }
        catch
        {
            // Best-effort - see OnMessageReceived.
        }
    }

    private void OnDirectMessageDeleted(int messageId, int senderId, int recipientId)
    {
        var otherUserId = senderId == _api.CurrentUserId ? recipientId : senderId;

        Dispatcher.Invoke(() =>
        {
            if (otherUserId != _dmActiveUserId) return;
            var item = FindMessageById(messageId);
            if (item is not null) _messages.Remove(item);
        });
    }

    // readerId just read our messages in this conversation (see
    // ChatHub.MarkDmRead) - if that conversation is the one currently open,
    // flip all our own already-rendered messages to "read" live. If it's
    // not open right now, no UI update is needed: next time it's opened,
    // ToDmListItem populates IsRead from the persisted ReadAt column.
    private void OnDirectMessagesRead(int readerId, int otherUserId, DateTime readAtUtc)
    {
        if (_dmActiveUserId != readerId) return;
        foreach (var m in _messages.Where(m => m.IsOwnMessage))
            m.IsRead = true;
    }

    // Shared by both MessageReactionToggled (channel) and
    // DirectMessageReactionToggled (DM) - only the message's own broadcast
    // includes a channelId/otherUserId, but this app only ever has one
    // conversation's worth of messages loaded into _messages at a time, so
    // finding the target by Id alone is enough; if it's not found, that
    // message isn't currently open/loaded and there's nothing to update.
    private int _lastMessagesUnreadTotal;

    private void UpdateMessagesUnreadBadge()
    {
        var total = _dmConversations.Sum(c => c.UnreadCount);
        MessagesUnreadBadge.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        MessagesUnreadBadgeText.Text = total > 99 ? "99+" : total.ToString();

        // Bumps only when unread count actually grows (a new message
        // arriving) - not when it's cleared/reduced by opening a
        // conversation. Same pop-scale pattern EmojiPickerPopup_Opened
        // already uses for new custom emoji tiles.
        if (total > _lastMessagesUnreadTotal)
        {
            MessagesUnreadBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            MessagesUnreadBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            var bump = new DoubleAnimation(1.0, 1.4, TimeSpan.FromMilliseconds(150))
            {
                AutoReverse = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            MessagesUnreadBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
            MessagesUnreadBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, bump);
        }
        _lastMessagesUnreadTotal = total;
    }

    private void OnDmUserTyping(string username, int senderId)
    {
        if (senderId != _dmActiveUserId) return;

        ChannelNameText.Text = $"@{username}   (typing...)";
    }


    // SignalR's automatic reconnect gives us a fresh connection with no group
    // memberships, so whatever channel/voice channel the user had open needs
    // to be re-joined explicitly - otherwise messages/voice presence silently
    // stop arriving until the user manually switches channels.
    private MessageListItem ToDmListItem(DirectMessageResponse dm, string? contentOverride = null)
    {
        var item = new MessageListItem
        {
            Id = dm.Id,
            AuthorId = dm.SenderId,
            AuthorUsername = dm.SenderId == _api.CurrentUserId ? "You" : (_dmActiveUsername ?? "them"),
            AuthorAvatarUrl = dm.SenderId == _api.CurrentUserId
                ? _api.CurrentUserAvatarUrl
                : _dmConversations.FirstOrDefault(c => c.OtherUserId == _dmActiveUserId)?.OtherUserAvatarUrl,
            Content = CallEventMessage.Prettify(contentOverride ?? dm.Content),
            TimeDisplay = dm.SentAt.ToLocalTime().ToString("t"),
            IsEdited = dm.EditedAt is not null,
            IsOwnMessage = dm.SenderId == _api.CurrentUserId,
            IsChannelMessage = false,
            IsRead = dm.ReadAt is not null,
            ReplyToMessageId = dm.ReplyToMessageId,
            ReplyToAuthorId = dm.ReplyToAuthorId,
            ForwardedFromAuthorUsername = dm.ForwardedFromAuthorUsername
        };
        PopulateReactions(item, dm.Reactions);
        return item;
    }

    private static DmConversationListItem ToDmConversationItem(DmConversationResponse c) => new()
    {
        OtherUserId = c.OtherUserId,
        OtherUsername = c.OtherUsername,
        OtherUserAvatarUrl = App.ResolveUploadUrl(c.OtherUserAvatarUrl),
        LastMessagePreview = CallEventMessage.Prettify(c.LastMessagePreview),
        LastMessageAt = c.LastMessageAt
    };

    private void UpdateDmConversationsEmptyState()
    {
        DmConversationsEmptyText.Visibility = _dmConversations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // MessageList used to be a plain ItemsControl inside a manually-declared
    // ScrollViewer x:Name="MessageScroll" - switched to a ListBox for real UI
    // virtualization (see MainWindow.xaml), which means it now owns its own
    // internal ScrollViewer instead of a named one this code can reference
    // directly. Found once via VisualTreeHelper and cached - the visual tree
    // under a ListBox doesn't get rebuilt during the window's lifetime, so
    // one lookup is enough.
}
