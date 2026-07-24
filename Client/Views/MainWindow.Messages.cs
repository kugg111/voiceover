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
    private VoiceMessageRecorder? _voiceMessageRecorder;

    private int? _replyingToMessageId;
    // Set by LoadMembersPanelAsync whenever a server is opened - whether the
    // current user can pin/unpin messages in it (owner/moderator only).
    private bool _hasMoreHistory;
    private bool _isLoadingOlderMessages;

    // Private calls (see CallWindow) - at most one at a time, same
    // "one voice context" rule as _currentVoiceChannelId.
    private readonly BulkObservableCollection<MessageListItem> _messages = new();
    // Mirrors _messages, kept in sync purely by listening to its own
    // CollectionChanged (wired once, see constructor) rather than requiring
    // every existing _messages.Add/Remove/Clear/PrependRange/ReplaceAll call
    // site to be touched - lets reaction/edit/pin/delete events resolve
    // their target message in O(1) instead of an O(n) FirstOrDefault scan
    // that only gets more expensive the longer a channel's history has been
    // scrolled into.
    private readonly Dictionary<int, MessageListItem> _messagesById = new();

    private MessageListItem? FindMessageById(int id) =>
        _messagesById.TryGetValue(id, out var item) ? item : null;

    // BulkObservableCollection's bulk operations (PrependRange/ReplaceAll)
    // raise a single Reset rather than itemized events, so those are
    // handled by a full rebuild - cheap relative to how rarely they fire
    // (once per channel switch/history page, not once per message).
    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (MessageListItem item in e.NewItems!) _messagesById[item.Id] = item;
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (MessageListItem item in e.OldItems!) _messagesById.Remove(item.Id);
                break;
            default:
                _messagesById.Clear();
                foreach (var item in _messages) _messagesById[item.Id] = item;
                break;
        }
    }
    private DateTime _lastTypingNotify = DateTime.MinValue;

    private void SaveCurrentDraft()
    {
        if (_dmActiveUserId.HasValue) DraftStorage.SetDmDraft(_dmActiveUserId.Value, MessageInput.Text);
        else if (_currentChannelId.HasValue) DraftStorage.SetChannelDraft(_currentChannelId.Value, MessageInput.Text);
    }

    // Counterpart to SaveCurrentDraft - loads whichever channel/DM is now
    // current's stored draft (empty string if none) into the compose box.
    // Called after _currentChannelId/_dmActiveUserId has already been
    // reassigned to the newly-opened conversation.
    private void LoadDraftIntoInput()
    {
        MessageInput.Text = _dmActiveUserId.HasValue
            ? DraftStorage.GetDmDraft(_dmActiveUserId.Value)
            : _currentChannelId.HasValue
                ? DraftStorage.GetChannelDraft(_currentChannelId.Value)
                : string.Empty;
    }

    private async Task LoadChannelHistoryAsync(int channelId)
    {
        if (_currentServerId is null) return;

        // Decrypted in parallel, not one at a time - Task.WhenAll preserves
        // the input order (still oldest-first, matching what the server
        // returned), same fix already applied to
        // ApiService.GetDmConversationsAsync this session. Each message
        // carries its own AuthorId + WrappedKeyForMe (see
        // MessageResponse/E2eeService.DecryptChannelMessageAsync), so no
        // separate per-server key lookup/bootstrap is needed here anymore.
        var history = await _api.GetMessageHistoryAsync(channelId);
        var items = await Task.WhenAll(history.Select(async m =>
            ToListItem(m, await _api.E2ee.DecryptChannelMessageAsync(m.AuthorId, m.WrappedKeyForMe, m.Content))));
        foreach (var item in items) ResolveReplyPreview(item, items);
        _messages.ReplaceAll(items);
        SetHasMoreHistory(items.Length);

        ScrollToBottom();
    }

    // Opens a voice channel's text chat - a separate action from
    // VoiceChannelButton_Click (which joins the voice audio), reachable via
    // its own 💬 icon on the row (see MainWindow.xaml). Messages aren't
    // restricted by Channel.Type server-side (ChatHub.SendMessage/JoinChannel
    // never check it - only MessagesController's history query cares about
    // ChannelId, not Type), so this reuses the exact same message pane,
    // compose box, and send/receive pipeline a text channel already gets;
    // nothing server-side needed changing for this feature. Deliberately
    // doesn't track unread counts the way text channels do (_unreadTextChannelCounts
    // stays text-channel-only) - a smaller, acceptable scope gap rather than
    // reworking that tracking to be type-agnostic too.
    private void RefreshLatestOwnMessageFlag()
    {
        var latestOwn = _messages.LastOrDefault(m => m.IsOwnMessage);
        foreach (var m in _messages)
            m.IsLatestOwnMessage = ReferenceEquals(m, latestOwn);
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dmActiveUserId.HasValue)
        {
            await AlertAsync("Not Supported", "Attachments aren't supported in direct messages yet.");
            return;
        }

        if (_currentChannelId is null)
        {
            await AlertAsync("No Channel Selected", "Select a channel first.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Supported files (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.txt;*.zip)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.txt;*.zip"
        };

        if (dialog.ShowDialog() != true) return;

        await SendAttachmentAsync(dialog.FileName);
    }

    // Click-to-start/click-to-stop (not hold-to-record) - matches the
    // composer's other buttons, which are all click-once. This app's only
    // "hold" interaction (push-to-talk) is a hotkey for hands-free use
    // during an active call, a different context from holding a mouse
    // button down for up to two minutes.
    private async void RecordVoiceMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceMessageRecorder is { IsRecording: true })
        {
            await StopAndSendVoiceMessageAsync();
            return;
        }

        if (_dmActiveUserId.HasValue)
        {
            await AlertAsync("Not Supported", "Voice messages aren't supported in direct messages yet.");
            return;
        }

        if (_currentChannelId is null)
        {
            await AlertAsync("No Channel Selected", "Select a channel first.");
            return;
        }

        var recorder = new VoiceMessageRecorder();
        recorder.OnElapsedTick += elapsed =>
            VoiceMessageRecordingText.Text = $"Recording... {(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        recorder.OnMaxDurationReached += () =>
        {
            VoiceMessageRecordingText.Text = "Stopped - max length reached";
            _ = StopAndSendVoiceMessageAsync();
        };

        try
        {
            recorder.Start();
        }
        catch (InvalidOperationException ex)
        {
            await AlertAsync("Error", ex.Message);
            recorder.Dispose();
            return;
        }

        _voiceMessageRecorder = recorder;
        RecordVoiceMessageButton.Content = "⏹";
        VoiceMessageRecordingBanner.Visibility = Visibility.Visible;
        VoiceMessageRecordingText.Text = "Recording... 0:00";
    }

    // Shared by RecordVoiceMessageButton_Click's stop-click path and the
    // recorder's own max-duration auto-stop.
    private async Task StopAndSendVoiceMessageAsync()
    {
        var recorder = _voiceMessageRecorder;
        if (recorder is null) return;
        _voiceMessageRecorder = null;

        var tempFile = recorder.Stop();
        recorder.Dispose();

        RecordVoiceMessageButton.Content = "🎤";
        VoiceMessageRecordingBanner.Visibility = Visibility.Collapsed;

        // Reuses the exact same upload+send path AttachButton_Click/
        // MessageInput_Pasting already use for any other file - a voice
        // message is just an attachment whose extension (.wav) happens to
        // signal the player-control rendering client-side.
        try
        {
            await SendAttachmentAsync(tempFile);
        }
        finally
        {
            try { System.IO.File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }

    private void MessageInputArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // Drag-and-drop counterpart to AttachButton_Click - same guards (no
    // attachments in DMs yet, need a channel selected), reusing
    // SendAttachmentAsync for the actual upload+send. Loops for a multi-file
    // drop, sending each as its own message the same way multiple picks
    // from the file dialog would have to be done one at a time anyway.
    private async void MessageInputArea_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        if (_dmActiveUserId.HasValue)
        {
            await AlertAsync("Not Supported", "Attachments aren't supported in direct messages yet.");
            return;
        }

        if (_currentChannelId is null)
        {
            await AlertAsync("No Channel Selected", "Select a channel first.");
            return;
        }

        foreach (var path in paths)
            await SendAttachmentAsync(path);
    }

    // Shared by AttachButton_Click (file picker) and MessageInput_Pasting
    // (Ctrl+V an image) - both end up with a local file path to upload and
    // send as an attachment-only message.
    private async Task SendAttachmentAsync(string filePath)
    {
        if (_currentChannelId is null || _currentServerId is not { } serverId) return;

        var (upload, error) = await _api.UploadFileAsync(filePath);
        if (upload is null)
        {
            await AlertAsync("Error", error ?? "Upload failed.");
            return;
        }

        // Content is always encrypted, even when empty (attachment-only) -
        // so the receive/history paths never need to guess whether a given
        // row is ciphertext or genuinely plaintext-empty.
        var encrypted = await _api.E2ee.EncryptForChannelAsync(serverId, string.Empty);
        if (encrypted is null)
        {
            await AlertAsync("Encryption unavailable", "Couldn't send - encryption isn't ready for this server yet.");
            return;
        }

        await _hub.SendMessageAsync(_currentChannelId.Value, encrypted.Content, encrypted.RecipientKeys, upload.Url);
    }

    private void AttachmentLink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string relativeUrl }) return;
        OpenAttachmentUrl(relativeUrl);
    }

    private void OpenImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string relativeUrl }) return;
        OpenAttachmentUrl(relativeUrl);
    }

    private static void OpenAttachmentUrl(string relativeUrl)
    {
        // Attachments are served by the ASP.NET Core server as static files.
        var fullUrl = App.ApiBaseUrl.TrimEnd('/') + relativeUrl;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullUrl) { UseShellExecute = true });
    }

    // Ctrl+V into the message box - if the clipboard holds an image (e.g.
    // copied from a browser/screenshot tool) rather than text, intercept the
    // paste and send it as an attachment instead of dumping raw image data
    // into the text field. Falls through to normal text pasting otherwise.
    private async void MessageInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        // Read off the paste's own data object rather than re-querying the
        // live OS clipboard (Clipboard.ContainsImage/GetImage) - besides
        // being the documented approach for DataObject.Pasting, it also
        // covers apps/browsers that only place a "PNG" clipboard format on
        // the clipboard without the legacy CF_DIB/CF_BITMAP formats
        // Clipboard.ContainsImage() looks for, which otherwise silently
        // fails for images copied from some websites.
        var data = e.SourceDataObject;
        System.Windows.Media.Imaging.BitmapSource? bitmapSource = null;

        // "PNG" is checked first, deliberately: WPF's DataFormats.Bitmap
        // advertises itself as present (GetDataPresent returns true) even
        // for clipboard sources that only really have a "PNG" format, via
        // WPF's own automatic format-conversion machinery. When that
        // promised conversion then fails, GetData(DataFormats.Bitmap)
        // comes back null and the whole paste silently no-ops - checking
        // the real "PNG" format first avoids ever hitting that path.
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is System.IO.MemoryStream pngStream)
        {
            bitmapSource = new System.Windows.Media.Imaging.PngBitmapDecoder(
                pngStream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        }
        else if (data.GetDataPresent(DataFormats.Bitmap))
        {
            bitmapSource = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
        }

        if (bitmapSource is null) return;

        e.CancelCommand();

        if (_dmActiveUserId.HasValue)
        {
            await AlertAsync("Not Supported", "Attachments aren't supported in direct messages yet.");
            return;
        }

        if (_currentChannelId is null)
        {
            await AlertAsync("No Channel Selected", "Select a channel first.");
            return;
        }

        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"voiceover-paste-{Guid.NewGuid():N}.png");
        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));
            await using (var stream = System.IO.File.Create(tempFile))
                encoder.Save(stream);

            await SendAttachmentAsync(tempFile);
        }
        finally
        {
            try { System.IO.File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendCurrentMessage();

    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SendCurrentMessage();
    }

    private async void MessageInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_currentChannelId is null && _dmActiveUserId is null) return;

        // Throttle typing notifications to at most once every 2 seconds.
        if ((DateTime.UtcNow - _lastTypingNotify).TotalSeconds < 2) return;
        _lastTypingNotify = DateTime.UtcNow;

        if (_dmActiveUserId.HasValue) await _hub.NotifyDmTypingAsync(_dmActiveUserId.Value);
        else if (_currentChannelId.HasValue) await _hub.NotifyTypingAsync(_currentChannelId.Value);
    }

    // Word-boundary match so "@everyone" and "@here" are caught but
    // "@everyonelse" or "@hereford" aren't - case-insensitive since Discord's
    // own @everyone/@here aren't case-sensitive either.
    private static readonly Regex EveryoneOrHerePattern = new(@"@(everyone|here)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task SendCurrentMessage()
    {
        if (string.IsNullOrWhiteSpace(MessageInput.Text)) return;

        var replyToMessageId = _replyingToMessageId;

        if (_dmActiveUserId.HasValue)
        {
            // Encrypted client-side before it ever reaches the hub - see
            // E2eeService. A null result means this device's keys aren't
            // unlocked yet or the recipient hasn't set up E2EE (no public
            // key on file) - surfaced instead of silently sending plaintext.
            var encrypted = await _api.E2ee.EncryptAsync(_dmActiveUserId.Value, MessageInput.Text.Trim());
            if (encrypted is null)
            {
                await AlertAsync("Encryption unavailable",
                    "Couldn't send an encrypted message right now - either your own encryption keys aren't unlocked yet, or this person hasn't logged in since secure messaging was added. Try logging out and back in.");
                return;
            }

            await _hub.SendDirectMessageAsync(_dmActiveUserId.Value, encrypted, replyToMessageId);
            MessageInput.Clear();
            SaveCurrentDraft(); // clears the now-stale stored draft (see SetDmDraft's empty-text handling)
            CancelReply();
            return;
        }

        if (_currentChannelId is null || _currentServerId is not { } serverId) return;

        // Compose-time gate, not a server-enforced one - message content is
        // E2EE ciphertext (see Message.Content), so ChatHub.SendMessage can
        // never inspect whether this actually contains "@everyone"/"@here".
        // ServerPermission.MentionEveryone can only ever be checked here,
        // client-side, before encrypting - same documented tradeoff as
        // MessageContentRenderer's own @mention highlighting.
        if (!_canMentionEveryone && EveryoneOrHerePattern.IsMatch(MessageInput.Text))
        {
            await AlertAsync("Missing permission", "You don't have permission to mention @everyone or @here in this server.");
            return;
        }

        var encryptedChannelMessage = await _api.E2ee.EncryptForChannelAsync(serverId, MessageInput.Text.Trim());
        if (encryptedChannelMessage is null)
        {
            await AlertAsync("Encryption unavailable",
                "Couldn't send an encrypted message right now - either your own encryption keys aren't unlocked yet, or this server's member list couldn't be loaded.");
            return;
        }

        await _hub.SendMessageAsync(_currentChannelId.Value, encryptedChannelMessage.Content, encryptedChannelMessage.RecipientKeys, replyToMessageId: replyToMessageId);
        MessageInput.Clear();
        SaveCurrentDraft(); // clears the now-stale stored draft (see SetChannelDraft's empty-text handling)
        CancelReply();
    }

    // Tag="{Binding}" on both the hover icon and the context menu item (see
    // MainWindow.xaml) - either one hands back the whole MessageListItem,
    // matching ReactButton_Click's existing convention for this row.
    private void ReplyMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MessageListItem item }) return;

        _replyingToMessageId = item.Id;
        ReplyBannerText.Text = $"Replying to {item.AuthorUsername}";
        ReplyBanner.Visibility = Visibility.Visible;
        MessageInput.Focus();
    }

    // Same Tag="{Binding}" convention as ReplyMessageButton_Click above -
    // opens a destination picker rather than acting immediately, since
    // unlike Reply (which only ever targets the currently-open conversation)
    // a forward's destination could be any channel/DM this user has access
    // to (see ForwardMessagePage).
    private void ForwardMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MessageListItem item }) return;

        NavigateTo(new ForwardMessagePage(this, _api, _hub, item.Content, item.AuthorUsername), "Forward Message");
    }

    private void CancelReplyButton_Click(object sender, RoutedEventArgs e) => CancelReply();

    // Also called on send (the reply state doesn't carry over to the next
    // message) and whenever the open channel/DM changes - a reply banner
    // pointing at a message from a conversation that's no longer open would
    // otherwise linger and attach the wrong replyToMessageId to whatever
    // gets typed next.
    private void CancelReply()
    {
        _replyingToMessageId = null;
        ReplyBanner.Visibility = Visibility.Collapsed;
    }

    // Shared by both the right-click context menu items and the hover
    // edit/delete icons on each message row (see MainWindow.xaml) - both
    // are just a FrameworkElement carrying the message id in Tag, whichever
    // one the user actually clicks.
    private void EditMessageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;

        var item = FindMessageById(messageId);
        if (item is null) return;

        item.EditingContent = item.Content;
        item.IsEditing = true;
    }

    private async void DeleteMessageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;

        if (!await ConfirmAsync("Confirm Delete", "Delete this message?", "Delete", destructive: true)) return;

        // Channel delete is shown for every message and left to the server
        // to authorize (author or moderator/owner) - a non-author,
        // non-moderator click lands here and gets a Forbid, surfaced below,
        // rather than being hidden client-side (would need a cached role
        // lookup just to decide visibility). DMs are gated client-side
        // instead (MessageListItem.DeleteMenuVisibility), so this branch is
        // always the current user's own message there.
        bool success;
        if (_currentChannelId.HasValue)
            success = await _api.DeleteMessageAsync(_currentChannelId.Value, messageId);
        else if (_dmActiveUserId.HasValue)
            success = await _api.DeleteDirectMessageAsync(_dmActiveUserId.Value, messageId);
        else
            return;

        if (success)
        {
            var item = FindMessageById(messageId);
            if (item is not null) _messages.Remove(item);
        }
        else
        {
            await AlertAsync("Error", "Could not delete that message (you may lack permission).");
        }
    }

    private async void PinMessageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId } || !_currentChannelId.HasValue) return;

        var success = await _api.PinMessageAsync(_currentChannelId.Value, messageId);
        if (success)
        {
            var item = FindMessageById(messageId);
            if (item is not null) item.IsPinned = true;
        }
    }

    private async void UnpinMessageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId } || !_currentChannelId.HasValue) return;

        var success = await _api.UnpinMessageAsync(_currentChannelId.Value, messageId);
        if (success)
        {
            var item = FindMessageById(messageId);
            if (item is not null) item.IsPinned = false;
        }
    }

    // Broadcast from MessagesController.Pin/Unpin - only found/updated if
    // this message happens to already be loaded in _messages (the currently
    // open channel); PinnedMessagesPage always fetches fresh from the
    // server when opened, so it doesn't need a live-update path of its own.
    private void OnMessagePinned(int messageId, bool isPinned)
    {
        var item = FindMessageById(messageId);
        if (item is not null) item.IsPinned = isPinned;
    }

    private void PinnedMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_currentChannelId.HasValue || !_currentServerId.HasValue) return;
        NavigateTo(new PinnedMessagesPage(_api, _currentServerId.Value, _currentChannelId.Value, _canManageCurrentServer), "Pinned Messages");
    }

    private void SearchMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentChannelId.HasValue && _currentServerId.HasValue)
            NavigateTo(new MessageSearchPage(this, _api, _currentServerId.Value, _currentChannelId.Value, null, ""), "Search Messages");
        else if (_dmActiveUserId.HasValue)
            NavigateTo(new MessageSearchPage(this, _api, null, null, _dmActiveUserId.Value, _dmActiveUsername ?? "them"), "Search Messages");
    }

    private async void SaveMessageEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;
        var item = FindMessageById(messageId);
        if (item is null) return;

        var newContent = item.EditingContent.Trim();
        if (string.IsNullOrEmpty(newContent))
        {
            item.IsEditing = false;
            return;
        }

        DateTime? editedAt = null;
        string? newContentFromServer = null;

        if (_currentChannelId.HasValue && _currentServerId is { } serverId)
        {
            var encryptedEdit = await _api.E2ee.EncryptForChannelAsync(serverId, newContent);
            if (encryptedEdit is not null)
            {
                var updated = await _api.EditMessageAsync(_currentChannelId.Value, messageId, encryptedEdit.Content, encryptedEdit.RecipientKeys);
                newContentFromServer = updated is not null ? await _api.E2ee.DecryptChannelMessageAsync(updated.AuthorId, updated.WrappedKeyForMe, updated.Content) : null;
                editedAt = updated?.EditedAt;
            }
        }
        else if (_dmActiveUserId.HasValue)
        {
            var updated = await _api.EditDirectMessageAsync(_dmActiveUserId.Value, messageId, newContent);
            newContentFromServer = updated?.Content;
            editedAt = updated?.EditedAt;
        }

        if (newContentFromServer is not null)
        {
            item.Content = newContentFromServer;
            item.IsEdited = editedAt is not null;
        }

        item.IsEditing = false;
    }

    private void CancelMessageEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int messageId }) return;
        var item = FindMessageById(messageId);
        if (item is not null) item.IsEditing = false;
    }

    // async void - invoked directly from a SignalR background thread, not
    // the UI thread, so an uncaught exception here would crash the whole
    // process rather than being caught by App.xaml.cs's
    // DispatcherUnhandledException (which only sees exceptions raised on
    // the UI thread's Dispatcher). Every async-void hub handler in this
    // file wraps its body the same way for that reason.
    private async void OnMessageReceived(MessageResponse msg)
    {
        try
        {
            var isOwnMessage = msg.AuthorId == _api.CurrentUserId;

            // Pushed straight from the hub, so still opaque ciphertext (see
            // ChatHub.SendMessage) - decrypt before this touches any UI-bound
            // state. msg carries its own AuthorId + WrappedKeyForMe, so no
            // separate per-server key lookup is needed here anymore.
            // _channelServerIds is still consulted below for the mute check
            // (this can arrive for a channel far from whatever's open right
            // now - that's the whole point of the unread-count path below).
            _channelServerIds.TryGetValue(msg.ChannelId, out var serverId);
            var content = await _api.E2ee.DecryptChannelMessageAsync(msg.AuthorId, msg.WrappedKeyForMe, msg.Content);

            Dispatcher.Invoke(() =>
            {
                var isCurrentlyOpen = msg.ChannelId == _currentChannelId;

                if (isCurrentlyOpen)
                {
                    var item = ToListItem(msg, content);
                    ResolveReplyPreview(item, _messages);
                    _messages.Add(item);
                    ScrollToBottom();
                }
                else if (!isOwnMessage)
                {
                    // Someone else's message landed in a channel that isn't open
                    // right now - bump its unread count rather than just
                    // dropping it. _unreadTextChannelCounts is the source of
                    // truth (survives even if that channel's server has never
                    // been opened, so there's no ChannelListItem yet to mark);
                    // also update the item directly if it happens to already be
                    // loaded, for an instant UI update.
                    var newCount = _unreadTextChannelCounts.GetValueOrDefault(msg.ChannelId) + 1;
                    _unreadTextChannelCounts[msg.ChannelId] = newCount;
                    var item = FindTextChannelItem(msg.ChannelId);
                    if (item is not null) item.UnreadCount = newCount;
                }

                // Plays whenever you're not actually looking at this channel
                // right now - either a different channel/view is open, or this
                // one is open but the window itself isn't focused. Being on a
                // different view is the common case (e.g. sitting in Friends or
                // another channel) and was previously silent because this only
                // checked window focus, not which view was open. Personal
                // channel/server notification mute (NotificationMuteStorage,
                // distinct from the moderation mute-member permission) also
                // suppresses this - a muted channel/server still updates the
                // unread badge above, it just doesn't sound/toast.
                var isMuted = NotificationMuteStorage.IsChannelMuted(msg.ChannelId) || NotificationMuteStorage.IsServerMuted(serverId);
                // @everyone/@here pierces through a channel/server mute -
                // same reasoning Discord itself uses (a broad ping should
                // still reach you even if you've quieted routine chatter),
                // and this app has no separate "suppress @everyone" toggle
                // to fall back to. Detected the same lightweight, non-
                // security way as the isMentioned check below.
                var isEveryoneMention = EveryoneOrHerePattern.IsMatch(content);
                if (!isOwnMessage && (!isCurrentlyOpen || !IsActive) && (!isMuted || isEveryoneMention))
                {
                    NotificationService.PlayMessageSound();
                    if (!isCurrentlyOpen)
                    {
                        // Not validated against real membership (see
                        // MessageContentRenderer) - a false-positive "@word"
                        // match against your own username is exceedingly
                        // unlikely in practice, and this is purely a
                        // notification title choice, not a security boundary.
                        var isMentioned = _api.CurrentUsername is { Length: > 0 } myName &&
                            content.Contains($"@{myName}", StringComparison.OrdinalIgnoreCase);
                        var preview = content.Length > 80 ? content[..80] + "…" : content;
                        var title = isEveryoneMention
                            ? $"@everyone mentioned in {FindChannelDisplayName(msg.ChannelId) ?? "a channel"}"
                            : isMentioned
                                ? $"You were mentioned in {FindChannelDisplayName(msg.ChannelId) ?? "a channel"}"
                                : FindChannelDisplayName(msg.ChannelId) ?? "New message";
                        NotificationService.ShowToast(title, preview);
                    }
                }
            });
        }
        catch
        {
            // Best-effort - a dropped/failed live-message update isn't worth
            // taking the app down for (the next history reload will show it).
        }
    }

    private async void OnMessageEdited(MessageResponse msg)
    {
        try
        {
            // Only worth decrypting if this channel is actually open below
            // (matches the early-return this had before) - still has to happen
            // before Dispatcher.Invoke since it's async.
            if (msg.ChannelId != _currentChannelId) return;
            var content = await _api.E2ee.DecryptChannelMessageAsync(msg.AuthorId, msg.WrappedKeyForMe, msg.Content);

            Dispatcher.Invoke(() =>
            {
                var item = FindMessageById(msg.Id);
                if (item is null) return;

                item.Content = content;
                item.IsEdited = msg.EditedAt is not null;
            });
        }
        catch
        {
            // Best-effort - see OnMessageReceived.
        }
    }

    // From MessagesController.DeleteAllFromUser (member context menu's
    // "Purge Messages") - removes every matching row from the currently
    // open channel in one pass, same as OnMessageDeleted just for many ids
    // at once instead of a single broadcast per message.
    private void OnMessagesBulkDeletedByUser(int channelId, int userId)
    {
        if (channelId != _currentChannelId) return;
        foreach (var item in _messages.Where(m => m.AuthorId == userId).ToList())
            _messages.Remove(item);
    }

    // ChatHub pushes this via Clients.User(...) the moment ServersController.Ban
    // or KickMember runs - if we're currently looking at that server, back out
    // of it the same way LeaveServerMenuItem_Click does, then refresh the
    // server list so it disappears from the rail entirely.
    private void OnMessageDeleted(int messageId, int channelId)
    {
        Dispatcher.Invoke(() =>
        {
            if (channelId != _currentChannelId) return;
            var item = FindMessageById(messageId);
            if (item is not null) _messages.Remove(item);
        });
    }

    private void OnReactionToggled(int messageId, string emoji, int userId, bool added)
    {
        var message = FindMessageById(messageId);
        if (message is null) return;

        var reaction = message.Reactions.FirstOrDefault(r => r.Emoji == emoji);
        if (added)
        {
            if (reaction is null)
            {
                reaction = new ReactionItem { MessageId = messageId, IsChannelMessage = message.IsChannelMessage, Emoji = emoji };
                message.Reactions.Add(reaction);
            }
            reaction.Count++;
            if (userId == _api.CurrentUserId) reaction.ReactedByMe = true;
        }
        else if (reaction is not null)
        {
            reaction.Count--;
            if (userId == _api.CurrentUserId) reaction.ReactedByMe = false;
            if (reaction.Count <= 0) message.Reactions.Remove(reaction);
        }
    }

    // Adds this server's custom-emoji buttons to the picker's WrapPanel
    // (below the 48 static unicode ones already in XAML) each time the
    // popup opens - rebuilt fresh every open rather than added once, since
    // this DataTemplate instance gets recycled across different messages/
    // rows as the ListBox virtualizes (see MainWindow.xaml's MessageList).
    // Walks Popup.Child/Border.Child/ScrollViewer.Content directly (the
    // exact structure written in XAML) rather than FindName, to avoid
    // relying on DataTemplate NameScope behavior. Dynamically-added Buttons
    // still pick up the WrapPanel.Resources implicit Style (Width/Height/
    // ContentTemplate/etc) automatically - WPF resolves implicit styles by
    // TargetType when an element is added to the tree, not just at parse
    // time - so only Tag/Content/ToolTip/Click need setting here.
    // Fade+slide-in for the first-run nudge, matching the emoji-picker's
    // pop-in - Popup itself has no built-in "fade + slide from the
    // placement-target side" animation, only a plain PopupAnimation enum.
    private void OnboardingNudgePopup_Opened(object sender, EventArgs e)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        OnboardingNudgeCard.BeginAnimation(OpacityProperty, null);
        OnboardingNudgeCard.Opacity = 0;
        OnboardingNudgeCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(150)));

        OnboardingNudgeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        OnboardingNudgeTranslate.X = -8;
        OnboardingNudgeTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
    }

    private void EmojiPickerPopup_Opened(object sender, EventArgs e)
    {
        const int staticEmojiCount = 48;

        if (sender is not System.Windows.Controls.Primitives.Popup
            {
                Child: System.Windows.Controls.Border { Child: ScrollViewer { Content: System.Windows.Controls.WrapPanel panel } } border,
                DataContext: MessageListItem message
            }) return;

        // Scale-pop the card itself - Popup's own PopupAnimation="Fade" only
        // handles opacity, so this is layered on top for the "pop open"
        // feel. A fresh ScaleTransform each open is simpler than a named
        // one in the DataTemplate, since this popup is recycled per-row.
        border.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.9, 0.9);
        border.RenderTransform = scale;
        var popEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = popEase });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1.0, TimeSpan.FromMilliseconds(120)) { EasingFunction = popEase });

        while (panel.Children.Count > staticEmojiCount)
            panel.Children.RemoveAt(panel.Children.Count - 1);

        if (!message.IsChannelMessage || _currentServerId is not int serverId) return;
        if (!_serverEmojis.TryGetValue(serverId, out var emojis)) return;

        foreach (var emoji in emojis)
        {
            var button = new Button { Tag = message, Content = CustomEmojiRegistry.TokenFor(emoji.Id), ToolTip = emoji.Name };
            button.Click += ReactionMenuItem_Click;
            panel.Children.Add(button);
        }
    }

    // Opens immediately on a plain left-click instead of requiring a
    // right-click - a plain Button.ContextMenu requires that by default.
    private void ReactButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MessageListItem item }) return;
        item.IsEmojiPickerOpen = !item.IsEmojiPickerOpen;
    }

    // Tag is the whole MessageListItem (see MainWindow.xaml) so this has
    // both the message id and whether it's a channel message or a DM
    // without needing a second lookup; Content is either a literal emoji
    // glyph (one of the 48 static picker Buttons) or a "custom:{id}" token
    // (one of EmojiPickerPopup_Opened's dynamically-added ones) - either way
    // it's the exact string ToggleMessageReaction expects.
    private async void ReactionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MessageListItem message } button) return;
        var emoji = button.Content?.ToString();
        if (string.IsNullOrEmpty(emoji)) return;

        message.IsEmojiPickerOpen = false;

        try
        {
            if (message.IsChannelMessage) await _hub.ToggleMessageReactionAsync(message.Id, emoji);
            else await _hub.ToggleDirectMessageReactionAsync(message.Id, emoji);
        }
        catch { /* best-effort, same as other reaction-toggle failures */ }
    }

    private async void ReactionPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ReactionItem reaction }) return;

        try
        {
            if (reaction.IsChannelMessage) await _hub.ToggleMessageReactionAsync(reaction.MessageId, reaction.Emoji);
            else await _hub.ToggleDirectMessageReactionAsync(reaction.MessageId, reaction.Emoji);
        }
        catch { /* best-effort, same as other reaction-toggle failures */ }
    }

    // The Messages icon itself lights up while any conversation has an
    // unread dot - cleared only once that specific conversation is opened
    // (not just by visiting the Messages view), same as most chat apps.
    private void OnUserTyping(string username, int channelId)
    {
        if (channelId != _currentChannelId) return;

        Dispatcher.Invoke(() =>
        {
            ChannelNameText.Text = $"{FindChannelDisplayName(channelId)}   ({username} is typing...)";
        });
    }

    // DM equivalent of OnUserTyping above - ChannelNameText is the same
    // shared header both the channel and DM views repurpose (see the "@" -
    // prefixed assignment in DmConversation_Click), so this just reuses it
    // with the "@username" shape instead of "# channel-name".
    private MessageListItem ToListItem(MessageResponse m, string? contentOverride = null)
    {
        var item = new MessageListItem
        {
            Id = m.Id,
            AuthorId = m.AuthorId,
            AuthorUsername = m.AuthorUsername,
            AuthorAvatarUrl = App.ResolveUploadUrl(m.AuthorAvatarUrl),
            Content = contentOverride ?? m.Content,
            TimeDisplay = m.SentAt.ToLocalTime().ToString("t"),
            AttachmentUrl = m.AttachmentUrl,
            IsEdited = m.EditedAt is not null,
            IsOwnMessage = m.AuthorId == _api.CurrentUserId,
            IsChannelMessage = true,
            CanManageServer = _canManageCurrentServer,
            IsPinned = m.PinnedAt is not null,
            ReplyToMessageId = m.ReplyToMessageId,
            ReplyToAuthorId = m.ReplyToAuthorId,
            ForwardedFromAuthorUsername = m.ForwardedFromAuthorUsername
        };
        PopulateReactions(item, m.Reactions);
        return item;
    }

    // Fills in ReplyPreviewAuthor/Text for a reply row from whichever
    // messages are actually available client-side right now - pool is
    // either the batch just being constructed (bulk history loads, where
    // _messages itself is still empty/stale at construction time) or the
    // live _messages collection (single-message receive, where it's already
    // populated with everything loaded so far). Falls back to the
    // placeholder set in MessageListItem's own field initializer if the
    // original isn't in the given pool - it may be older than what's paged
    // in, or the referenced row may have since been deleted.
    private static void ResolveReplyPreview(MessageListItem item, IEnumerable<MessageListItem> pool)
    {
        if (item.ReplyToMessageId is not { } replyId) return;

        var original = pool.FirstOrDefault(x => x.Id == replyId);
        if (original is null) return;

        item.ReplyPreviewAuthor = original.AuthorUsername;
        item.ReplyPreviewText = original.Content.Length > 80 ? original.Content[..80] + "…" : original.Content;
    }

    // ToListItem/ToDmListItem's own object initializers can't populate
    // Reactions (get-only ObservableCollection, not a settable property) -
    // done as a separate step right after construction instead.
    private void PopulateReactions(MessageListItem item, List<ReactionSummaryResponse>? reactions)
    {
        if (reactions is null) return;
        foreach (var r in reactions)
            item.Reactions.Add(new ReactionItem { MessageId = item.Id, IsChannelMessage = item.IsChannelMessage, Emoji = r.Emoji, Count = r.Count, ReactedByMe = r.ReactedByMe });
    }

    // DirectMessageResponse doesn't carry a sender avatar (1:1 DMs - you
    // already know who you're talking to, unlike a multi-sender channel) -
    // pull it from context instead: our own cached avatar, or the open
    // conversation's cached one for the other side.
    // contentOverride lets a caller that already decrypted dm.Content itself
    // (OnDirectMessageReceived, working with a raw hub push that never went
    // through ApiService's transparent decrypt) supply the plaintext
    // directly - callers going through ApiService.GetDmHistoryAsync already
    // get plaintext in dm.Content and don't need it.
    private void ScrollToBottom() => GetMessageListScrollViewer()?.ScrollToEnd();

    // Only shown once a channel/DM is actually open and confirmed empty -
    // not while nothing is selected yet, where "# select-a-channel"/"Select
    // a conversation" already communicates that state.
    private void UpdateMessagesEmptyState()
    {
        var isConversationOpen = _currentChannelId.HasValue || _dmActiveUserId.HasValue;
        MessagesEmptyText.Visibility = isConversationOpen && _messages.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private ScrollViewer? _messageListScrollViewer;

    private ScrollViewer? GetMessageListScrollViewer()
    {
        _messageListScrollViewer ??= FindVisualChild<ScrollViewer>(MessageList);
        return _messageListScrollViewer;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }

        return null;
    }

    // A full page (server take=50 default) suggests there might be more
    // before it; anything short of that means we've hit the start of history.
    private void SetHasMoreHistory(int lastPageCount)
    {
        _hasMoreHistory = lastPageCount >= 50;
        LoadOlderMessagesButton.Visibility = _hasMoreHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LoadOlderMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingOlderMessages || !_hasMoreHistory) return;
        if (_messages.FirstOrDefault() is not { } oldest) return;

        _isLoadingOlderMessages = true;
        LoadOlderMessagesButton.IsEnabled = false;

        try
        {
            var olderItems = await FetchOlderMessagesAsync(oldest.Id);
            SetHasMoreHistory(olderItems.Count);
            if (olderItems.Count == 0) return;

            // Inserting above the current viewport shifts everything down
            // unless the scroll offset is corrected by the same amount the
            // content grew - ExtentHeight only reflects the new layout after
            // a layout pass, hence the Yield before reading it again.
            var scrollViewer = GetMessageListScrollViewer();
            var oldExtentHeight = scrollViewer?.ExtentHeight ?? 0;
            _messages.PrependRange(olderItems);
            RefreshLatestOwnMessageFlag();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            scrollViewer?.ScrollToVerticalOffset((scrollViewer.ExtentHeight) - oldExtentHeight);
        }
        finally
        {
            _isLoadingOlderMessages = false;
            LoadOlderMessagesButton.IsEnabled = true;
        }
    }

    // Fetches and decrypts one page of history older than beforeId - shared
    // by LoadOlderMessagesButton_Click and JumpToMessageAsync's own paging
    // loop, which needs the same fetch without that button's specific
    // scroll-position bookkeeping.
    private async Task<List<MessageListItem>> FetchOlderMessagesAsync(int beforeId)
    {
        List<MessageListItem> olderItems;
        if (_dmActiveUserId.HasValue)
        {
            var history = await _api.GetDmHistoryAsync(_dmActiveUserId.Value, beforeId);
            olderItems = history.Select(m => ToDmListItem(m)).ToList();
        }
        else if (_currentChannelId.HasValue && _currentServerId.HasValue)
        {
            var history = await _api.GetMessageHistoryAsync(_currentChannelId.Value, beforeId);
            var decrypted = await Task.WhenAll(history.Select(async m =>
                ToListItem(m, await _api.E2ee.DecryptChannelMessageAsync(m.AuthorId, m.WrappedKeyForMe, m.Content))));
            olderItems = decrypted.ToList();
        }
        else
        {
            return new List<MessageListItem>();
        }

        foreach (var item in olderItems) ResolveReplyPreview(item, olderItems);
        return olderItems;
    }

    private const int JumpToMessagePageCap = 20;

    // Called from MessageSearchPage when a result is clicked - by the time
    // this runs the search page has already closed itself (GoBack), so the
    // channel/DM view underneath is visible again with _currentChannelId/
    // _dmActiveUserId still pointing at whatever conversation the search was
    // launched from (search results can only ever be for that same
    // conversation, since MessageSearchPage takes it as a constructor
    // argument rather than reading "current" state itself). If the target
    // message isn't in the currently-loaded window, walks "load older"
    // pages (same cursor pagination LoadOlderMessagesButton uses) until it's
    // found - capped at JumpToMessagePageCap so a message from very deep
    // history doesn't hang this on an unbounded fetch; past the cap this
    // just leaves the conversation open without scrolling/highlighting
    // rather than blocking indefinitely.
    public async Task JumpToMessageAsync(int messageId)
    {
        if (_isLoadingOlderMessages) return;

        var pagesWalked = 0;
        while (!_messagesById.ContainsKey(messageId) && _hasMoreHistory && pagesWalked < JumpToMessagePageCap)
        {
            if (_messages.FirstOrDefault() is not { } oldest) break;

            _isLoadingOlderMessages = true;
            try
            {
                var olderItems = await FetchOlderMessagesAsync(oldest.Id);
                SetHasMoreHistory(olderItems.Count);
                if (olderItems.Count == 0) break;
                _messages.PrependRange(olderItems);
                RefreshLatestOwnMessageFlag();
            }
            finally
            {
                _isLoadingOlderMessages = false;
            }

            pagesWalked++;
        }

        var target = FindMessageById(messageId);
        if (target is null) return;

        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        MessageList.ScrollIntoView(target);
        await HighlightMessageRowAsync(target);
    }

    private static async Task HighlightMessageRowAsync(MessageListItem item)
    {
        item.IsHighlighted = true;
        await Task.Delay(1200);
        item.IsHighlighted = false;
    }

}
