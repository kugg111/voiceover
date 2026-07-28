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

public class ServerListItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public int OwnerId { get; set; }

    // Set at construction (see LoadServersAsync) by comparing OwnerId to the
    // current user - same "computed once at construction, not via binding
    // to an external value" pattern VoiceMemberItem.IsSelf already uses.
    // Gates the Rename/Delete Server context-menu items, which only make
    // sense for the owner.
    public bool IsOwner { get; set; }
    public Visibility OwnerMenuItemVisibility => IsOwner ? Visibility.Visible : Visibility.Collapsed;

    // Current discoverability state - only read from when opening the
    // Discoverability modal (ShowDiscoverabilitySettingsAsync) to prefill it.
    public bool IsPublic { get; set; }
    public string? Description { get; set; }
}

public class VoiceMemberItem : INotifyPropertyChanged
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }

    // A volume slider only makes sense for someone else's audio, not your
    // own - set once at construction (see everywhere Members.Add happens).
    public bool IsSelf { get; set; }
    public Visibility VolumeSliderVisibility => IsSelf ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SelfMenuItemVisibility => IsSelf ? Visibility.Visible : Visibility.Collapsed;

    private bool _isSpeaking;
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            _isSpeaking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingDotVisibility)));
        }
    }

    public Visibility SpeakingDotVisibility => IsSpeaking ? Visibility.Visible : Visibility.Collapsed;

    // Set right before removal (see MainWindow.Voice.cs's AnimateAndRemoveMember)
    // to drive the row's IsLeaving DataTrigger (fade+slide-out) in
    // MainWindow.xaml, then the actual ObservableCollection.Remove is
    // delayed to let that animation finish - an instant Remove tears the
    // container down immediately with no exit-animation hook.
    private bool _isLeaving;
    public bool IsLeaving
    {
        get => _isLeaving;
        set
        {
            if (_isLeaving == value) return;
            _isLeaving = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLeaving)));
        }
    }

    // Set only on the local user's own row, only while their LiveKit
    // connection is still spinning up (see VoiceChannelButton_Click). Other
    // clients never see this - they aren't told about the join at all until
    // it actually succeeds, so this state never reaches them.
    private bool _isJoining;
    public bool IsJoining
    {
        get => _isJoining;
        set
        {
            if (_isJoining == value) return;
            _isJoining = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsJoining)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsernameForeground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JoiningTooltip)));
        }
    }

    public System.Windows.Media.Brush UsernameForeground =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[IsJoining ? "TextJoining" : "TextMuted"];

    public string? JoiningTooltip => IsJoining ? "Joining..." : null;

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMuted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MutedIconVisibility)));
        }
    }

    public Visibility MutedIconVisibility => IsMuted ? Visibility.Visible : Visibility.Collapsed;

    private bool _isDeafened;
    public bool IsDeafened
    {
        get => _isDeafened;
        set
        {
            if (_isDeafened == value) return;
            _isDeafened = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDeafened)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeafenedIconVisibility)));
        }
    }

    public Visibility DeafenedIconVisibility => IsDeafened ? Visibility.Visible : Visibility.Collapsed;

    private bool _isScreenSharing;
    public bool IsScreenSharing
    {
        get => _isScreenSharing;
        set
        {
            if (_isScreenSharing == value) return;
            _isScreenSharing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScreenSharing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenSharingIconVisibility)));
        }
    }

    public Visibility ScreenSharingIconVisibility => IsScreenSharing ? Visibility.Visible : Visibility.Collapsed;

    // 0-200%, 100 = unchanged. Persisted locally per-user (see
    // UserVolumeStorage) so it carries over the next time you're in a
    // voice channel with the same person, instead of resetting every join.
    private double _volume = 100;
    public double Volume
    {
        get => _volume;
        set
        {
            if (_volume == value) return;
            _volume = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeDisplay)));
        }
    }

    public string VolumeDisplay => $"{(int)Volume}%";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ChannelListItem : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    // Set from ChannelResponse.CategoryId at construction (see
    // MainWindow.RefreshChannelsAsync) - CategoryName is the grouping key
    // TextChannelList/VoiceChannelList's ItemsControl.GroupStyle bind to
    // (empty string for "uncategorized", sorted to the top - see
    // RefreshChannelsAsync's ordering). Plain properties, not
    // INotifyPropertyChanged-backed, since a category change always comes
    // through a full RefreshChannelsAsync/ReplaceAll rather than an in-place
    // edit of an existing item.
    public int? CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;

    // Only populated/shown for voice channels - who's currently connected.
    public ObservableCollection<VoiceMemberItem> Members { get; } = new();

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnread)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCountDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadBadgeVisibility)));
        }
    }

    public bool HasUnread => UnreadCount > 0;
    public string UnreadCountDisplay => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public Visibility UnreadBadgeVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

// Denormalizes MessageId/IsChannelMessage onto each reaction pill itself
// (rather than looking them up via RelativeSource from the pill's own
// DataContext) so ReactionPill_Click has everything it needs directly off
// the bound item - simplest way to thread both "which message" and "which
// emoji" through a nested ItemsControl's click handler.
public class ReactionItem : INotifyPropertyChanged
{
    public int MessageId { get; set; }
    public bool IsChannelMessage { get; set; }
    public string Emoji { get; set; } = string.Empty;

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        }
    }

    private bool _reactedByMe;
    public bool ReactedByMe
    {
        get => _reactedByMe;
        set
        {
            if (_reactedByMe == value) return;
            _reactedByMe = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReactedByMe)));
        }
    }

    public string Display => $"{Emoji} {Count}";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class MessageListItem : INotifyPropertyChanged
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string AuthorUsername { get; set; } = string.Empty;
    public string? AuthorAvatarUrl { get; set; }
    public string TimeDisplay { get; set; } = string.Empty;
    public string? AttachmentUrl { get; set; }
    public ObservableCollection<ReactionItem> Reactions { get; } = new();

    // Both set at construction (ToListItem/ToDmListItem). Edit is
    // author-only everywhere - moderators can remove someone else's words,
    // not rewrite them. Delete for a channel message is shown to everyone
    // and left to the server to actually authorize (author or a
    // moderator/owner - see MessagesController.Delete); for a DM there's no
    // moderation concept, so it's only shown for your own messages,
    // matching the server's author-only rule exactly with no round trip
    // needed just to decide whether to show the menu item.
    public bool IsOwnMessage { get; set; }
    public bool IsChannelMessage { get; set; }
    public Visibility EditMenuVisibility => IsOwnMessage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteMenuVisibility => IsOwnMessage || IsChannelMessage ? Visibility.Visible : Visibility.Collapsed;

    // Pinning is channel-only (no DM equivalent, matching Discord) and
    // requires being able to manage the server - set at construction from
    // MainWindow's own _canManageCurrentServer, same as CanKick/
    // CanChangeRole are precomputed once on MemberListItem rather than
    // re-checked per binding.
    public bool CanManageServer { get; set; }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinMenuVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnpinMenuVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinnedTagVisibility)));
        }
    }

    public Visibility PinMenuVisibility => IsChannelMessage && CanManageServer && !IsPinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnpinMenuVisibility => IsChannelMessage && CanManageServer && IsPinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PinnedTagVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    private string _content = string.Empty;
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentVisibility)));
        }
    }

    private bool _isEdited;
    public bool IsEdited
    {
        get => _isEdited;
        set
        {
            if (_isEdited == value) return;
            _isEdited = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEdited)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditedTagVisibility)));
        }
    }

    // DM read receipts only - null for channel messages (no per-recipient
    // read state there) and for the other party's own messages. Set from
    // DirectMessageResponse.ReadAt at load time, and live-updated from
    // ChatHub's DirectMessagesRead event (see OnDirectMessagesRead).
    private bool _isRead;
    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value) return;
            _isRead = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRead)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadReceiptVisibility)));
        }
    }

    // Only the single most recent own message in the conversation should
    // ever show the receipt (matches Discord/most chat apps) - showing it
    // under every read message gets noisy fast in a long conversation. Kept
    // up to date by MainWindow.RefreshLatestOwnMessageFlag whenever the
    // message list changes (load, prepend-older, new arrival).
    private bool _isLatestOwnMessage;
    public bool IsLatestOwnMessage
    {
        get => _isLatestOwnMessage;
        set
        {
            if (_isLatestOwnMessage == value) return;
            _isLatestOwnMessage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLatestOwnMessage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadReceiptVisibility)));
        }
    }

    public Visibility ReadReceiptVisibility => IsOwnMessage && !IsChannelMessage && IsRead && IsLatestOwnMessage ? Visibility.Visible : Visibility.Collapsed;

    // Local-only UI state (not persisted/broadcast) - true while this row's
    // inline edit box is open, swapping the read-only TextBlock for an
    // editable TextBox in the DataTemplate.
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditBoxVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentVisibility)));
        }
    }

    // What the edit TextBox is bound to - separate from Content so
    // Cancel can discard in-progress typing without touching the real value.
    public string EditingContent { get; set; } = string.Empty;

    // Local-only UI state, same pattern as IsEditing above - true while this
    // row's emoji picker Popup is open. Bound Mode=TwoWay so the Popup's own
    // StaysOpen="False" auto-dismiss (clicking elsewhere) flips this back
    // without needing an explicit close handler for that case.
    private bool _isEmojiPickerOpen;
    public bool IsEmojiPickerOpen
    {
        get => _isEmojiPickerOpen;
        set
        {
            if (_isEmojiPickerOpen == value) return;
            _isEmojiPickerOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmojiPickerOpen)));
        }
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    private bool IsImageAttachment => AttachmentUrl is not null &&
        ImageExtensions.Contains(System.IO.Path.GetExtension(AttachmentUrl));

    // .wav is reserved for voice messages recorded in the composer (see
    // VoiceMessageRecorder/UploadController.AllowedExtensions) - not a type
    // a regular file attachment would otherwise use, so the extension alone
    // is an unambiguous signal, no separate attachment-type column needed.
    private bool IsVoiceAttachment => AttachmentUrl is not null &&
        string.Equals(System.IO.Path.GetExtension(AttachmentUrl), ".wav", StringComparison.OrdinalIgnoreCase);

    // AttachmentUrl is the server-relative /uploads/... path returned by
    // UploadController - needs the API base prepended before it's a real
    // downloadable/renderable URL, same as AttachmentLink_MouseLeftButtonUp
    // already does for the "open" click.
    public string? AttachmentFullUrl => AttachmentUrl is null ? null : App.ApiBaseUrl.TrimEnd('/') + AttachmentUrl;

    public string AttachmentDisplay => AttachmentUrl is null ? "" : $"📎 {System.IO.Path.GetFileName(AttachmentUrl)}";
    public Visibility ContentVisibility => !IsEditing && !string.IsNullOrEmpty(Content) ? Visibility.Visible : Visibility.Collapsed;
    // Image attachments render inline instead of as a click-through link,
    // and voice messages get their own player control - the file-link row
    // only shows for everything else (pdf/txt/zip).
    public Visibility FileAttachmentVisibility => AttachmentUrl is not null && !IsImageAttachment && !IsVoiceAttachment ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageAttachmentVisibility => AttachmentUrl is not null && IsImageAttachment ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VoiceAttachmentVisibility => AttachmentUrl is not null && IsVoiceAttachment ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditedTagVisibility => IsEdited ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditBoxVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;

    // Set from MessageResponse/DirectMessageResponse.ReplyToMessageId at
    // construction. ReplyPreviewAuthor/Text are resolved separately (see
    // MainWindow.ResolveReplyPreview) since the referenced message's
    // decrypted content is only available client-side, and only if that
    // original message happens to still be loaded - a null ReplyToMessageId
    // means "not a reply" (ReplyPreviewVisibility hides the quote line
    // entirely), while a non-null one with no resolved preview falls back to
    // a placeholder rather than leaving the quote line blank.
    public int? ReplyToMessageId { get; set; }
    public int? ReplyToAuthorId { get; set; }
    public string ReplyPreviewAuthor { get; set; } = string.Empty;
    public string ReplyPreviewText { get; set; } = "Original message";
    public Visibility ReplyPreviewVisibility => ReplyToMessageId.HasValue ? Visibility.Visible : Visibility.Collapsed;
    public string ReplyPreviewDisplay => string.IsNullOrEmpty(ReplyPreviewAuthor) ? ReplyPreviewText : $"{ReplyPreviewAuthor}: {ReplyPreviewText}";

    // Set from MessageResponse/DirectMessageResponse.ForwardedFromAuthorUsername
    // at construction - a plain snapshot label, not resolved like the reply
    // preview above (see Message.ForwardedFromAuthorUsername server-side for
    // why there's no equivalent "jump to original" for a forward).
    public string? ForwardedFromAuthorUsername { get; set; }
    public Visibility ForwardedBannerVisibility => ForwardedFromAuthorUsername is not null ? Visibility.Visible : Visibility.Collapsed;

    // Briefly flashed true by MainWindow.HighlightMessageRowAsync when
    // jumping here from a search result (see MessageSearchPage), then set
    // back false after a short delay - a plain property swap rather than a
    // Storyboard ColorAnimation, since animating a Background brush from
    // XAML requires it to be a non-frozen SolidColorBrush and Style-Setter-
    // created brushes get frozen by default, which is easy to get wrong.
    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value) return;
            _isHighlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
        }
    }

    private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromArgb(90, 88, 101, 242));
    public Brush RowBackground => IsHighlighted ? HighlightBrush : Brushes.Transparent;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class UserSearchResultItem
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}

public class DmConversationListItem : INotifyPropertyChanged
{
    public int OtherUserId { get; set; }
    public string OtherUsername { get; set; } = string.Empty;
    public string? OtherUserAvatarUrl { get; set; }
    public string LastMessagePreview { get; set; } = string.Empty;
    public DateTime LastMessageAt { get; set; }

    public string PreviewDisplay => LastMessagePreview;

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnread)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCountDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadBadgeVisibility)));
        }
    }

    public bool HasUnread => UnreadCount > 0;
    public string UnreadCountDisplay => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public Visibility UnreadBadgeVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class FriendListItem : INotifyPropertyChanged
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }

    private string _presenceState = "Offline";
    public string PresenceState
    {
        get => _presenceState;
        set
        {
            if (_presenceState == value) return;
            _presenceState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnlineDotVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AwayDotVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OfflineDotVisibility)));
        }
    }

    public Visibility OnlineDotVisibility => PresenceState == "Online" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayDotVisibility => PresenceState == "Away" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OfflineDotVisibility => PresenceState == "Offline" ? Visibility.Visible : Visibility.Collapsed;

    private string? _customStatus;
    public string? CustomStatus
    {
        get => _customStatus;
        set
        {
            if (_customStatus == value) return;
            _customStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomStatusVisibility)));
        }
    }

    public Visibility CustomStatusVisibility => string.IsNullOrEmpty(CustomStatus) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class FriendRequestListItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Direction { get; set; } = string.Empty; // "Incoming" or "Outgoing"

    public Visibility AcceptButtonVisibility => Direction == "Incoming" ? Visibility.Visible : Visibility.Collapsed;
    public string SecondaryActionLabel => Direction == "Incoming" ? "Decline" : "Cancel";
}

public class MemberListItem : INotifyPropertyChanged
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsSelf { get; set; }

    // Only the owner can promote/demote (matches the old Members popup);
    // owner and moderator can both kick (matches KickMember's own
    // CanManageServerAsync check) - neither ever applies to the owner's own
    // row, nor to your own row regardless of your role (a moderator
    // right-clicking themselves shouldn't see "Kick" just because they can
    // kick everyone else who isn't the owner).
    public bool CanChangeRole { get; set; }
    public bool CanKick { get; set; }

    // Ban/Purge use the same eligibility as Kick (KickMembers/ManageMessages
    // respectively - see PermissionService.HasPermissionAsync); Edit
    // Permissions is Owner-only and only meaningful for a Moderator target,
    // same restriction as CanChangeRole.
    public bool CanBan { get; set; }
    public bool CanPurge { get; set; }
    public bool CanEditPermissions { get; set; }
    public int Permissions { get; set; }

    public string RoleButtonLabel => Role == "Moderator" ? "Demote" : "Promote";
    public string NextRole => Role == "Moderator" ? "Member" : "Moderator";
    public Visibility RoleButtonVisibility => CanChangeRole ? Visibility.Visible : Visibility.Collapsed;
    public Visibility KickButtonVisibility => CanKick ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BanButtonVisibility => CanBan ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PurgeButtonVisibility => CanPurge ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditPermissionsVisibility => CanEditPermissions && Role == "Moderator" ? Visibility.Visible : Visibility.Collapsed;

    private string _presenceState = "Offline";
    public string PresenceState
    {
        get => _presenceState;
        set
        {
            if (_presenceState == value) return;
            _presenceState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnlineDotVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AwayDotVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OfflineDotVisibility)));
        }
    }

    public Visibility OnlineDotVisibility => PresenceState == "Online" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayDotVisibility => PresenceState == "Away" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OfflineDotVisibility => PresenceState == "Offline" ? Visibility.Visible : Visibility.Collapsed;

    private string? _customStatus;
    public string? CustomStatus
    {
        get => _customStatus;
        set
        {
            if (_customStatus == value) return;
            _customStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomStatusVisibility)));
        }
    }

    public Visibility CustomStatusVisibility => string.IsNullOrEmpty(CustomStatus) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
}

