using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
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

public partial class MainWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly SignalRService _hub = new();
    private VoiceService? _voice;
    private readonly IdleDetector _idleDetector = new();

    // In-game voice overlay + its own dedicated global toggle hotkey (a
    // separate GlobalHotkeyService instance from VoiceService's PTT one, so
    // the two never fight over a single watched key). Both created in
    // MainWindow_Loaded, torn down in MainWindow_Closed.
    private OverlayWindow? _overlay;
    private GlobalHotkeyService? _overlayHotkey;

    // Set right before any Close() call that should really tear the app
    // down (tray Exit, log out, session expiry) - otherwise
    // MainWindow_Closing redirects a plain close to hide-to-tray instead.
    private bool _reallyExit;

    // Non-null only while a voice message is actively being recorded (see
    // RecordVoiceMessageButton_Click) - a short-lived capture instance,
    // unrelated to the always-open MicCaptureSource used during an active
    // voice channel session.
    private VoiceMessageRecorder? _voiceMessageRecorder;

    private int? _currentServerId;
    private int? _currentChannelId;

    // Property, not a plain field, so every voice-state transition that
    // assigns it (join, leave, switch channels, channel-deleted) keeps the
    // in-game overlay's roster in sync automatically via SyncOverlayRoster,
    // rather than having to remember to update the overlay at each of the
    // several leave paths.
    private int? _currentVoiceChannelIdBacking;
    private int? _currentVoiceChannelId
    {
        get => _currentVoiceChannelIdBacking;
        set
        {
            _currentVoiceChannelIdBacking = value;
            SyncOverlayRoster();
        }
    }

    private int? _dmActiveUserId;
    // Set by ReplyMessageButton_Click, cleared on send/cancel/context-switch
    // (see CancelReply) - the message SendCurrentMessage attaches as
    // replyToMessageId on the next send.
    private int? _replyingToMessageId;
    // Set by LoadMembersPanelAsync whenever a server is opened - whether the
    // current user can pin/unpin messages in it (owner/moderator only).
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
    private string? _dmActiveUsername;

    // "Load older messages" - whether the currently open channel/DM history
    // might have more before what's loaded (heuristic: the last page came
    // back full), and a re-entrancy guard for the load-more click itself.
    private bool _hasMoreHistory;
    private bool _isLoadingOlderMessages;

    // Private calls (see CallWindow) - at most one at a time, same
    // "one voice context" rule as _currentVoiceChannelId.
    private CallWindow? _callWindow;
    private string? _currentCallId;
    private System.Windows.Threading.DispatcherTimer? _ringTimeoutTimer;

    // One viewer window per remote participant currently screen-sharing.
    private readonly Dictionary<int, ScreenShareViewerWindow> _screenShareViewers = new();

    // Tracks who's currently sharing in the open voice channel and their
    // live RemoteVideoPlayback, without opening a viewer window for any of
    // them automatically - see OnRemoteScreenShareStarted/
    // ScreenShareIcon_MouseLeftButtonUp. The icon itself (bound to
    // IsScreenSharing/ScreenSharingIconVisibility on VoiceMemberItem) is
    // what tells you who's sharing; a viewer only opens if you click it.
    private readonly Dictionary<int, RemoteVideoPlayback> _activeScreenShares = new();

    // BulkObservableCollection where a Clear()+per-item-Add() reload pattern
    // is common (see ReplaceAll callers below) - one Reset notification
    // instead of N+1 individual ones. Plain ObservableCollection for the
    // rest (search results etc.), which only ever grow/shrink by a couple
    // of items at a time and don't reload wholesale.
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

    private DateTime _lastTypingNotify = DateTime.MinValue;

    public MainWindow(ApiService api)
    {
        InitializeComponent();
        _api = api;

        _messages.CollectionChanged += OnMessagesCollectionChanged;

        // /uploads now requires auth (see Server/Program.cs) - both image
        // caches need a way to attach a bearer token to their own HttpClient
        // requests. Must happen before anything below that could trigger a
        // fetch - AvatarView.ImageUrl's setter calls Refresh() immediately
        // (not gated on the control's own Loaded event), so setting
        // MyAvatarView.ImageUrl just a few lines down would otherwise race
        // this if it were done any later (e.g. in MainWindow_Loaded,
        // alongside SignalRService.ConnectAsync's own accessTokenProvider).
        AvatarImageCache.AccessTokenProvider = AttachmentImageCache.AccessTokenProvider =
            AttachmentAudioCache.AccessTokenProvider = _api.GetFreshAccessTokenAsync;

        MyAvatarView.DisplayName = _api.CurrentUsername ?? "?";
        MyAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;

        ServerList.ItemsSource = _servers;
        MemberList.ItemsSource = _members;

        // Grouped by ChannelListItem.CategoryName via a collection view
        // rather than a plain flat binding, so category headers render
        // between rows (see MainWindow.xaml's GroupStyle) while every other
        // mechanic - drag-and-drop reorder, unread badges, click handlers -
        // keeps operating on the flat _textChannels/_voiceChannels lists
        // exactly as before (RefreshChannelsAsync just orders items so each
        // category's channels are contiguous, which is all grouping needs).
        var textChannelsView = CollectionViewSource.GetDefaultView(_textChannels);
        textChannelsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChannelListItem.CategoryName)));
        TextChannelList.ItemsSource = textChannelsView;

        var voiceChannelsView = CollectionViewSource.GetDefaultView(_voiceChannels);
        voiceChannelsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChannelListItem.CategoryName)));
        VoiceChannelList.ItemsSource = voiceChannelsView;
        MessageList.ItemsSource = _messages;
        _messages.CollectionChanged += (_, _) => UpdateMessagesEmptyState();
        DmConversationList.ItemsSource = _dmConversations;
        _dmConversations.CollectionChanged += (_, _) => UpdateDmConversationsEmptyState();
        DmSearchResultsList.ItemsSource = _dmSearchResults;
        FriendList.ItemsSource = _friends;
        FriendRequestList.ItemsSource = _friendRequests;
        FriendSearchResultsList.ItemsSource = _friendSearchResults;

        _hub.MessageReceived += OnMessageReceived;
        _hub.MessageEdited += OnMessageEdited;
        _hub.MessageDeleted += OnMessageDeleted;
        _hub.DirectMessageReceived += OnDirectMessageReceived;
        _hub.DirectMessageEdited += OnDirectMessageEdited;
        _hub.DirectMessageDeleted += OnDirectMessageDeleted;
        _hub.DirectMessagesRead += (readerId, otherUserId, readAtUtc) => Dispatcher.Invoke(() => OnDirectMessagesRead(readerId, otherUserId, readAtUtc));
        _hub.MessageReactionToggled += (channelId, messageId, emoji, userId, added) => Dispatcher.Invoke(() => OnReactionToggled(messageId, emoji, userId, added));
        _hub.DirectMessageReactionToggled += (messageId, emoji, userId, added) => Dispatcher.Invoke(() => OnReactionToggled(messageId, emoji, userId, added));
        _hub.MessagePinned += (channelId, messageId, pinnedAt) => Dispatcher.Invoke(() => OnMessagePinned(messageId, true));
        _hub.MessageUnpinned += (channelId, messageId) => Dispatcher.Invoke(() => OnMessagePinned(messageId, false));
        _hub.MessagesBulkDeletedByUser += (channelId, userId) => Dispatcher.Invoke(() => OnMessagesBulkDeletedByUser(channelId, userId));
        _hub.YouWereBanned += serverId => Dispatcher.Invoke(() => OnYouWereBanned(serverId));
        _hub.YouWereKicked += serverId => Dispatcher.Invoke(() => OnYouWereKicked(serverId));
        _hub.ForceMuted += channelId => Dispatcher.Invoke(() => OnForceMuted(channelId));
        _hub.MemberKicked += (serverId, userId) => Dispatcher.Invoke(() => OnMemberKicked(serverId, userId));
        _hub.MemberBanned += (serverId, userId) => Dispatcher.Invoke(() => OnMemberBanned(serverId, userId));
        _hub.MemberRoleChanged += (serverId, userId) => Dispatcher.Invoke(() => OnMemberRoleChanged(serverId, userId));
        _hub.ChannelCreated += serverId => Dispatcher.Invoke(() => OnChannelCreated(serverId));
        _hub.ChannelDeleted += serverId => Dispatcher.Invoke(() => OnChannelDeleted(serverId));
        _hub.ServerEmojisChanged += serverId => Dispatcher.Invoke(() => OnServerEmojisChanged(serverId));
        _hub.ServerDeleted += serverId => Dispatcher.Invoke(() => OnServerDeleted(serverId));
        _hub.ServerRenamed += serverId => Dispatcher.Invoke(() => OnServerRenamed(serverId));
        _hub.UserTyping += OnUserTyping;
        _hub.DmUserTyping += (username, senderId) => Dispatcher.Invoke(() => OnDmUserTyping(username, senderId));
        _hub.VoiceUserJoined += OnVoiceUserJoined;
        _hub.VoiceUserLeft += OnVoiceUserLeft;
        _hub.UserSpeaking += OnUserSpeaking;
        _hub.UserMuted += OnUserMuted;
        _hub.UserDeafened += OnUserDeafened;
        _hub.FriendRequestReceived += OnFriendRequestReceived;
        _hub.FriendRequestAccepted += OnFriendRequestAccepted;
        _hub.IncomingCall += OnIncomingCall;
        _hub.CallAccepted += OnCallAccepted;
        _hub.CallDeclined += OnCallEndedRemotely;
        _hub.CallEnded += OnCallEndedRemotely;
        _hub.Reconnecting += () => Dispatcher.Invoke(() => SetConnectionStatusText("Reconnecting...", isAlert: true));
        _hub.Reconnected += OnReconnected;
        _hub.ConnectionClosed += () => Dispatcher.Invoke(() => SetConnectionStatusText("Disconnected", isAlert: true, isError: true));

        // Fires if the refresh token turns out to be dead (expired past its
        // 30-day life, or revoked - e.g. a "log out everywhere" from another
        // device) the next time ApiService tries to use it, not just at
        // startup - see ApiService.RefreshAccessTokenAsync.
        _api.SessionExpired += () => Dispatcher.Invoke(OnSessionExpired);

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    // ConnectionStatusText doubles as both the SignalR reconnect indicator
    // (this method) and a plain "Voice connected" note set directly
    // elsewhere - isAlert bolds it and isError reddens it so "Reconnecting.../
    // Disconnected" actually stand out from that easy-to-miss default amber,
    // instead of every state looking the same. Non-alert callers (including
    // the "" clear in OnReconnected, restoring the default look for whatever
    // gets shown next) fall back to the original always-amber styling.
    private void SetConnectionStatusText(string text, bool isAlert, bool isError = false)
    {
        ConnectionStatusText.Text = text;
        ConnectionStatusText.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x3F, 0x42))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xB2, 0x32));
        ConnectionStatusText.FontWeight = isAlert ? FontWeights.Bold : FontWeights.Normal;
    }

    // Same crash-log file App.xaml.cs's DispatcherUnhandledException handler
    // writes to, for exceptions thrown inside a fire-and-forget (`_ = ...`)
    // Task - those never reach that handler (it only sees exceptions on the
    // dispatcher thread's own call stack), so without this they'd vanish
    // with zero trace instead of at least being logged. No MessageBox here
    // deliberately - the call sites that use this are background actions
    // where popping an error dialog would be more disruptive than useful.
    private static void LogBackgroundException(Exception ex)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voiceover_client_crash.log"),
                $"{DateTime.Now:O}\n{ex}\n\n");
        }
        catch { }
    }

    // --- PageHost: in-window replacement for what used to be separate
    // popup Windows (Settings, ban list, moderation log, invites, call
    // history, pinned messages, search, edit permissions, transfer
    // ownership). NavigateTo swaps in a page UserControl and reveals
    // PageHost over the server/messages/friends content beneath it (which
    // is left untouched, not torn down); GoBack hides it again. No back
    // stack - nothing here currently navigates from one page into another. ---

    private const int PageHostAnimationDurationMs = 200;

    // Bumped on every NavigateTo/GoBack - lets GoBack's delayed
    // fade-out completion (see below) detect whether a newer navigation
    // has since superseded it, so a fast Back-then-forward click can't
    // have its stale animation wipe out the page that replaced it.
    private int _pageHostNavVersion;

    public void NavigateTo(UserControl page, string title)
    {
        _pageHostNavVersion++;

        PageHostTitleText.Text = title;
        PageHostContent.Content = page;
        PageHost.Visibility = Visibility.Visible;

        // Clears any animated value a previous GoBack's slide-out left
        // behind, same "held end value blocks the next animation" reasoning
        // as ShowModal.
        PageHostContent.BeginAnimation(OpacityProperty, null);
        PageHostContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        Wpf.Ui.Animations.TransitionAnimationProvider.ApplyTransition(
            PageHostContent, Wpf.Ui.Animations.Transition.FadeInWithSlide, PageHostAnimationDurationMs);
    }

    private void PageHostBackButton_Click(object sender, RoutedEventArgs e) => GoBack();

    public void GoBack()
    {
        var version = ++_pageHostNavVersion;

        // Reverse of NavigateTo's entrance - fades out while sliding down
        // instead of WPF-UI's up-and-in, then collapses/clears content once
        // the animation finishes (same Unloaded-unsubscribe reasoning as
        // before this animation existed, just delayed by the transition).
        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(PageHostAnimationDurationMs));
        fadeOut.Completed += (_, _) =>
        {
            if (_pageHostNavVersion != version) return; // superseded by a newer navigation
            PageHost.Visibility = Visibility.Collapsed;
            PageHostContent.Content = null;
        };
        PageHostContent.BeginAnimation(OpacityProperty, fadeOut);

        var slideOut = new DoubleAnimation(0, 24, TimeSpan.FromMilliseconds(PageHostAnimationDurationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        PageHostContentTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }

    // Ctrl+F opens message search; Esc closes whichever of PageHost/
    // ModalOverlay is on top. A window-level check rather than per-dialog
    // XAML (like the old Window-based dialogs got "for free" via
    // IsDefault/IsCancel) because PageHost pages don't have a Cancel button
    // to hang IsCancel off of. ModalOverlay's own Cancel/OK buttons already
    // get Escape for free from BuildModalButton's IsCancel wiring - except
    // the create-or-join shape, which has no Cancel button at all (see
    // CreateOrJoinAsync), so that one case needs handling here too. When
    // both PageHost and ModalOverlay happen to be open, the modal takes
    // priority since it's visually on top.
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ModalOverlay.Visibility == Visibility.Visible)
            {
                if (ModalCreateOrJoinPanel.Visibility == Visibility.Visible)
                {
                    CompleteModal(null);
                    e.Handled = true;
                }
            }
            else if (PageHost.Visibility == Visibility.Visible)
            {
                GoBack();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchMessagesButton_Click(this, e);
            e.Handled = true;
        }
    }

    // --- ModalOverlay: in-window replacement for ConfirmDialog/AlertDialog/
    // TextInputDialog/CreateOrJoinDialog. One scrim+card shown/hidden via
    // Visibility, driven by a TaskCompletionSource so callers can just
    // `await` a result the same way they used to read a dialog's .Result
    // property after ShowDialog() returned. ---

    private TaskCompletionSource<object?>? _modalTcs;
    private int _modalVersion;

    private enum ModalButtonStyle { Plain, Primary, Destructive }

    private const int ModalAnimationDurationMs = 200;

    private Task<object?> ShowModal()
    {
        _modalTcs = new TaskCompletionSource<object?>();
        _modalVersion++;

        // Clears any animated value a previous CompleteModal's fade/scale-out
        // left behind - BeginAnimation holds its end value on the property
        // (FillBehavior.HoldEnd) until explicitly cleared, which would
        // otherwise silently block ApplyTransition's own opacity animation
        // below from taking effect.
        ModalOverlay.BeginAnimation(OpacityProperty, null);
        ModalCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ModalCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ModalCardScale.ScaleX = 0.92;
        ModalCardScale.ScaleY = 0.92;

        ModalOverlay.Visibility = Visibility.Visible;
        Wpf.Ui.Animations.TransitionAnimationProvider.ApplyTransition(ModalOverlay, Wpf.Ui.Animations.Transition.FadeIn, ModalAnimationDurationMs);

        var scaleIn = new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(ModalAnimationDurationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ModalCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        ModalCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);

        return _modalTcs.Task;
    }

    private void CompleteModal(object? result)
    {
        // Purely decorative - the logical completion (TrySetResult below)
        // happens immediately regardless, same as before this animation was
        // added; only the visual hide is delayed by the fade/scale-out.
        // Captures the version so a chained ShowModal (e.g. CreateOrJoinAsync
        // immediately followed by PromptAsync) can't have ITS freshly-opened
        // overlay collapsed out from under it when this stale animation's
        // Completed fires ~200ms later - BeginAnimation(prop, null) replaces
        // the animated value but doesn't stop the old clock from still
        // ticking to completion in the background. Same race PageHost's
        // _pageHostNavVersion guards against for GoBack/navigate.
        var version = _modalVersion;
        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(ModalAnimationDurationMs));
        fadeOut.Completed += (_, _) =>
        {
            if (_modalVersion == version)
                ModalOverlay.Visibility = Visibility.Collapsed;
        };
        ModalOverlay.BeginAnimation(OpacityProperty, fadeOut);

        var scaleOut = new DoubleAnimation(1.0, 0.92, TimeSpan.FromMilliseconds(ModalAnimationDurationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        ModalCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
        ModalCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut);

        _modalTcs?.TrySetResult(result);
        _modalTcs = null;
    }

    private Button BuildModalButton(string text, ModalButtonStyle style, Action onClick)
    {
        (Brush background, Brush foreground) = style switch
        {
            ModalButtonStyle.Primary => ((Brush)FindResource("AccentBlurple"), (Brush)Brushes.White),
            ModalButtonStyle.Destructive => ((Brush)new SolidColorBrush(Color.FromRgb(0xF2, 0x3F, 0x42)), (Brush)Brushes.White),
            _ => ((Brush)Brushes.Transparent, (Brush)FindResource("TextNormal"))
        };
        var button = new Button
        {
            Content = text,
            Height = 36,
            MinWidth = 90,
            Padding = new Thickness(16, 0, 16, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = background,
            Foreground = foreground,
            FontWeight = style == ModalButtonStyle.Plain ? FontWeights.Normal : FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            // Lets Enter/Escape drive Confirm/Cancel the same way the old
            // Window-based dialogs did via IsDefault/IsCancel.
            IsDefault = style != ModalButtonStyle.Plain,
            IsCancel = style == ModalButtonStyle.Plain
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // Themed in-window replacement for MessageBox.Show(..., YesNo, ...) /
    // the old ConfirmDialog window.
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", bool destructive = false, string cancelText = "Cancel")
    {
        ModalTitleText.Text = title;
        ModalStandardPanel.Visibility = Visibility.Visible;
        ModalCreateOrJoinPanel.Visibility = Visibility.Collapsed;
        ModalMessageText.Text = message;
        ModalMessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        ModalInputBox.Visibility = Visibility.Collapsed;
        ModalCustomContentScroll.Visibility = Visibility.Collapsed;
        ModalButtonsPanel.Children.Clear();
        ModalButtonsPanel.Children.Add(BuildModalButton(cancelText, ModalButtonStyle.Plain, () => CompleteModal(false)));
        ModalButtonsPanel.Children.Add(BuildModalButton(confirmText,
            destructive ? ModalButtonStyle.Destructive : ModalButtonStyle.Primary, () => CompleteModal(true)));

        return await ShowModal() is true;
    }

    // Themed in-window replacement for MessageBox.Show(..., OK, ...) / the
    // old AlertDialog window.
    public async Task AlertAsync(string title, string message)
    {
        ModalTitleText.Text = title;
        ModalStandardPanel.Visibility = Visibility.Visible;
        ModalCreateOrJoinPanel.Visibility = Visibility.Collapsed;
        ModalMessageText.Text = message;
        ModalMessageText.Visibility = Visibility.Visible;
        ModalInputBox.Visibility = Visibility.Collapsed;
        ModalCustomContentScroll.Visibility = Visibility.Collapsed;
        ModalButtonsPanel.Children.Clear();
        var okButton = BuildModalButton("OK", ModalButtonStyle.Primary, () => CompleteModal(null));
        okButton.IsCancel = true; // single button - Escape dismisses same as OK
        ModalButtonsPanel.Children.Add(okButton);

        await ShowModal();
    }

    // Themed in-window replacement for the old TextInputDialog window. Null
    // return means cancelled, matching TextInputDialog.Result's convention.
    public async Task<string?> PromptAsync(string title, string label, string initialValue = "")
    {
        ModalTitleText.Text = title;
        ModalStandardPanel.Visibility = Visibility.Visible;
        ModalCreateOrJoinPanel.Visibility = Visibility.Collapsed;
        ModalMessageText.Text = label;
        ModalMessageText.Visibility = Visibility.Visible;
        ModalInputBox.Text = initialValue;
        ModalInputBox.Visibility = Visibility.Visible;
        ModalCustomContentScroll.Visibility = Visibility.Collapsed;
        ModalButtonsPanel.Children.Clear();
        ModalButtonsPanel.Children.Add(BuildModalButton("Cancel", ModalButtonStyle.Plain, () => CompleteModal(null)));
        ModalButtonsPanel.Children.Add(BuildModalButton("OK", ModalButtonStyle.Primary, () => CompleteModal(ModalInputBox.Text)));

        var task = ShowModal();
        // Deferred rather than called inline - the TextBox is still
        // Collapsed-turning-Visible in this same synchronous block, and
        // WPF won't hand focus to an element until layout has caught up.
        _ = Dispatcher.BeginInvoke(() => ModalInputBox.Focus());
        return await task as string;
    }

    // Themed in-window replacement for the old TransferOwnershipWindow -
    // shown from SettingsPage's delete-account flow when 1+ owned servers
    // have 2+ other members (no unambiguous auto-pick - a server with 0
    // other members is just deleted, and exactly 1 auto-promotes server-side
    // without ever reaching this picker). It never became a PageHost page of
    // its own - it's a small, transient decision nested inside the
    // delete-account flow, so it reuses ModalOverlay's standard shape plus a
    // dynamically built label+ComboBox pair per server. Null return means cancelled.
    public async Task<List<OwnershipTransfer>?> PickOwnershipTransfersAsync(List<OwnedServerNeedingTransferResponse> servers)
    {
        ModalTitleText.Text = "Transfer Ownership";
        ModalStandardPanel.Visibility = Visibility.Visible;
        ModalCreateOrJoinPanel.Visibility = Visibility.Collapsed;
        ModalMessageText.Text = "You own servers with other members - pick who takes over each one before your account is deleted.";
        ModalMessageText.Visibility = Visibility.Visible;
        ModalInputBox.Visibility = Visibility.Collapsed;

        var pickers = new Dictionary<int, System.Windows.Controls.ComboBox>();
        ModalCustomContent.Children.Clear();
        foreach (var server in servers)
        {
            var label = new System.Windows.Controls.TextBlock
            {
                Text = server.ServerName,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextNormal"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            var combo = new System.Windows.Controls.ComboBox
            {
                ItemsSource = server.Candidates,
                DisplayMemberPath = nameof(OwnershipCandidate.Username),
                SelectedIndex = 0,
                Margin = new Thickness(0, 0, 0, 16)
            };
            pickers[server.ServerId] = combo;
            ModalCustomContent.Children.Add(label);
            ModalCustomContent.Children.Add(combo);
        }
        ModalCustomContentScroll.Visibility = Visibility.Visible;

        ModalButtonsPanel.Children.Clear();
        ModalButtonsPanel.Children.Add(BuildModalButton("Cancel", ModalButtonStyle.Plain, () => CompleteModal(null)));
        ModalButtonsPanel.Children.Add(BuildModalButton("Continue", ModalButtonStyle.Primary, () =>
        {
            var selections = new List<OwnershipTransfer>();
            foreach (var (serverId, combo) in pickers)
            {
                if (combo.SelectedItem is OwnershipCandidate candidate)
                    selections.Add(new OwnershipTransfer(serverId, candidate.UserId));
            }
            CompleteModal(selections);
        }));

        var result = await ShowModal() as List<OwnershipTransfer>;
        ModalCustomContent.Children.Clear();
        ModalCustomContentScroll.Visibility = Visibility.Collapsed;
        return result;
    }

    // Owner-only (see DiscoverabilityMenuItem_Click / ServerListItem.
    // OwnerMenuItemVisibility) - same custom-content modal shape as
    // PickOwnershipTransfersAsync above (a checkbox + description textbox
    // instead of per-server ComboBoxes). Null return means cancelled.
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
    public async Task<string?> PromptPasswordAsync(string title, string label)
    {
        ModalTitleText.Text = title;
        ModalStandardPanel.Visibility = Visibility.Visible;
        ModalCreateOrJoinPanel.Visibility = Visibility.Collapsed;
        ModalMessageText.Text = label;
        ModalMessageText.Visibility = Visibility.Visible;
        ModalInputBox.Visibility = Visibility.Collapsed;

        ModalCustomContent.Children.Clear();
        var passwordBox = new System.Windows.Controls.PasswordBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            Background = (Brush)FindResource("BgDarker"),
            Foreground = (Brush)FindResource("TextNormal"),
            BorderThickness = new Thickness(0)
        };
        passwordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CompleteModal(passwordBox.Password); };
        ModalCustomContent.Children.Add(passwordBox);
        ModalCustomContentScroll.Visibility = Visibility.Visible;

        ModalButtonsPanel.Children.Clear();
        ModalButtonsPanel.Children.Add(BuildModalButton("Cancel", ModalButtonStyle.Plain, () => CompleteModal(null)));
        ModalButtonsPanel.Children.Add(BuildModalButton("Confirm", ModalButtonStyle.Primary, () => CompleteModal(passwordBox.Password)));

        var task = ShowModal();
        _ = Dispatcher.BeginInvoke(() => passwordBox.Focus());
        var result = await task as string;
        ModalCustomContent.Children.Clear();
        ModalCustomContentScroll.Visibility = Visibility.Collapsed;
        return result;
    }

    // Enrollment step 1+2 combined (Settings > My Account > Two-Factor
    // Authentication) - shows the QR code (decoded from the PNG bytes
    // AuthController.Setup2fa returned) plus the raw secret for manual
    // entry, and collects the confirm code in one modal. Null return means
    // cancelled.
    public async Task<string?> ShowTotpSetupAsync(string secret, string qrCodePngBase64)
    {
        ModalTitleText.Text = "Enable Two-Factor Authentication";
        ModalStandardPanel.Visibility = Visibility.Visible;
        ModalCreateOrJoinPanel.Visibility = Visibility.Collapsed;
        ModalMessageText.Text = "Scan this with Microsoft Authenticator, Google Authenticator, or any other TOTP app, then enter the 6-digit code it shows.";
        ModalMessageText.Visibility = Visibility.Visible;
        ModalInputBox.Visibility = Visibility.Collapsed;

        ModalCustomContent.Children.Clear();

        var qrBitmap = new System.Windows.Media.Imaging.BitmapImage();
        using (var stream = new System.IO.MemoryStream(Convert.FromBase64String(qrCodePngBase64)))
        {
            qrBitmap.BeginInit();
            qrBitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            qrBitmap.StreamSource = stream;
            qrBitmap.EndInit();
        }
        qrBitmap.Freeze();

        var qrImage = new System.Windows.Controls.Image
        {
            Source = qrBitmap,
            Width = 200,
            Height = 200,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var secretLabel = new System.Windows.Controls.TextBlock
        {
            Text = "Can't scan? Enter this key manually:",
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var secretBox = new System.Windows.Controls.TextBox
        {
            Text = secret,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            Padding = new Thickness(8, 6, 8, 6),
            Background = (Brush)FindResource("BgDarker"),
            Foreground = (Brush)FindResource("TextNormal"),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var codeLabel = new System.Windows.Controls.TextBlock
        {
            Text = "6-digit code",
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var codeBox = new System.Windows.Controls.TextBox
        {
            MaxLength = 6,
            Padding = new Thickness(8, 6, 8, 6),
            Background = (Brush)FindResource("BgDarker"),
            Foreground = (Brush)FindResource("TextNormal"),
            BorderThickness = new Thickness(0)
        };

        ModalCustomContent.Children.Add(qrImage);
        ModalCustomContent.Children.Add(secretLabel);
        ModalCustomContent.Children.Add(secretBox);
        ModalCustomContent.Children.Add(codeLabel);
        ModalCustomContent.Children.Add(codeBox);
        ModalCustomContentScroll.Visibility = Visibility.Visible;

        ModalButtonsPanel.Children.Clear();
        ModalButtonsPanel.Children.Add(BuildModalButton("Cancel", ModalButtonStyle.Plain, () => CompleteModal(null)));
        ModalButtonsPanel.Children.Add(BuildModalButton("Confirm", ModalButtonStyle.Primary, () => CompleteModal(codeBox.Text)));

        var result = await ShowModal() as string;
        ModalCustomContent.Children.Clear();
        ModalCustomContentScroll.Visibility = Visibility.Collapsed;
        return result;
    }

    // Shown exactly once, right after Confirm2fa succeeds - these codes are
    // never retrievable again afterward (only their BCrypt hashes are kept
    // server-side), so the checkbox-gated Continue button (no Cancel - the
    // codes were already generated and saved server-side by this point,
    // there's nothing left to cancel) makes sure this isn't dismissed by
    // an accidental click.
    public async Task<bool> ShowRecoveryCodesAsync(List<string> codes)
    {
        ModalTitleText.Text = "Save Your Recovery Codes";
        ModalStandardPanel.Visibility = Visibility.Visible;
        ModalCreateOrJoinPanel.Visibility = Visibility.Collapsed;
        ModalMessageText.Text = "Each code works once to sign in if you lose access to your authenticator app. Save them somewhere safe - they won't be shown again.";
        ModalMessageText.Visibility = Visibility.Visible;
        ModalInputBox.Visibility = Visibility.Collapsed;

        ModalCustomContent.Children.Clear();
        var codesText = new System.Windows.Controls.TextBox
        {
            Text = string.Join(Environment.NewLine, codes),
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
            Padding = new Thickness(8, 6, 8, 6),
            Background = (Brush)FindResource("BgDarker"),
            Foreground = (Brush)FindResource("TextNormal"),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var confirmCheck = new System.Windows.Controls.CheckBox
        {
            Content = "I've saved these codes somewhere safe",
            Foreground = (Brush)FindResource("TextNormal")
        };
        ModalCustomContent.Children.Add(codesText);
        ModalCustomContent.Children.Add(confirmCheck);
        ModalCustomContentScroll.Visibility = Visibility.Visible;

        ModalButtonsPanel.Children.Clear();
        var continueButton = BuildModalButton("Continue", ModalButtonStyle.Primary, () => CompleteModal(true));
        continueButton.IsEnabled = false;
        confirmCheck.Checked += (_, _) => continueButton.IsEnabled = true;
        confirmCheck.Unchecked += (_, _) => continueButton.IsEnabled = false;
        ModalButtonsPanel.Children.Add(continueButton);

        var result = await ShowModal() as bool?;
        ModalCustomContent.Children.Clear();
        ModalCustomContentScroll.Visibility = Visibility.Collapsed;
        return result == true;
    }

    private void ModalInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CompleteModal(ModalInputBox.Text);
    }

    // true = Create selected, false = Join selected, null = dismissed via
    // Esc (see MainWindow_PreviewKeyDown) - matches CreateOrJoinDialog's old
    // CreateSelected convention.
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
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_reallyExit || !TraySettingsStorage.MinimizeToTrayEnabled) return;

        e.Cancel = true;
        Hide();

        if (!TraySettingsStorage.HasShownTrayBalloon)
        {
            TrayIcon.ShowNotification("Voiceover", "Still running in the background - right-click the tray icon to exit.");
            TraySettingsStorage.HasShownTrayBalloon = true;
        }
    }

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => RestoreFromTray();
    private void TrayOpenMenuItem_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void TrayExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        Close();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _idleDetector.Dispose();

        // Default ShutdownMode is OnLastWindowClose - a still-open CallWindow
        // would otherwise keep the process alive after MainWindow closes.
        // Silent close: the server's own OnDisconnectedAsync cleanup already
        // treats a dropped connection as an implicit EndCall, so there's no
        // need to race an explicit EndCallAsync against the DisconnectAsync
        // below.
        _callWindow?.CloseSilently();

        // Same OnLastWindowClose reasoning as _callWindow above.
        foreach (var viewer in _screenShareViewers.Values.ToList())
            viewer.Close();

        // Same OnLastWindowClose reasoning again - a still-open (even if
        // hidden) overlay window would keep the process alive. Its global
        // keyboard hook must be released too.
        _overlayHotkey?.Dispose();
        _overlay?.Close();

        if (_voice is not null)
            await _voice.DisposeAsync();
        await _hub.DisconnectAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _hub.ConnectAsync(App.HubUrl, _api.GetFreshAccessTokenAsync);
        _voice = new VoiceService(_hub, _api.CurrentUserId!.Value);
        _voice.PeerConnected += userId => Dispatcher.Invoke(() =>
        {
            ConnectionStatusText.Text = "Voice connected";
        });
        _voice.PeerDisconnected += userId => Dispatcher.Invoke(() =>
        {
            ConnectionStatusText.Text = "";
        });
        _voice.LocalSpeakingChanged += isSpeaking => _ = OnLocalSpeakingChangedAsync(isSpeaking);
        _voice.RemoteScreenShareStarted += (userId, playback) => Dispatcher.Invoke(() => OnRemoteScreenShareStarted(userId, playback));
        _voice.RemoteScreenShareStopped += userId => Dispatcher.Invoke(() => OnRemoteScreenShareStopped(userId));
        // Mute can change from places that don't have a handle on this
        // button - Voice Settings' input mode switch, the PTT/push-to-mute
        // hotkey - so this is the one place that keeps it in sync regardless
        // of where the change came from (see VoiceService.MicMutedChanged).
        _voice.MicMutedChanged += isMuted =>
        {
            Dispatcher.Invoke(UpdateMuteButtonVisual);
            _ = OnLocalMutedChangedAsync(isMuted);

            // Audio feedback for your own mute state - the only reliable way
            // to know push-to-mute/talk actually registered while alt-tabbed
            // into a game with no UI visible at all.
            if (isMuted) NotificationService.PlayMuteSound();
            else NotificationService.PlayUnmuteSound();
        };
        _voice.DeafenedChanged += isDeafened => _ = OnLocalDeafenedChangedAsync(isDeafened);
        _voice.ScreenSharingChanged += isSharing => Dispatcher.Invoke(() => OnLocalScreenSharingChanged(isSharing));

        _hub.PresenceChanged += (userId, state) => Dispatcher.Invoke(() => OnPresenceChanged(userId, state));
        _hub.CustomStatusChanged += (userId, status) => Dispatcher.Invoke(() => OnCustomStatusChanged(userId, status));
        _idleDetector.IdleChanged += isIdle => _ = OnIdleChangedAsync(isIdle);
        _idleDetector.Start();

        InitializeOverlay();

        // Explicit and awaited, rather than relying on the server's own
        // OnConnectedAsync having already finished by the time StartAsync()
        // returns - that's not a guarantee, and without this, clicking into
        // a server fast enough after login could read your own presence
        // back as still Offline (GetMembers/GetFriends are one-shot reads
        // of PresenceService, not something that waits for the flag to land).
        await SetPresenceStateSafeAsync("Online");

        await LoadServersAsync();
    }

    // Presence reporting is best-effort - if the hub call fails for any
    // reason (an older/mismatched server that doesn't have this method
    // yet, a transient network issue), the app must keep working normally
    // rather than surfacing an error dialog for what's a non-critical
    // background update.
    private async Task SetPresenceStateSafeAsync(string state)
    {
        try
        {
            await _hub.SetPresenceStateAsync(state);
        }
        catch
        {
            // Best-effort - see comment above.
        }
    }

    // Away is suppressed while actively in a voice channel - being
    // mid-conversation shouldn't flip you to "away" just because you
    // haven't touched the mouse. _currentVoiceChannelId is cleared on every
    // leave path, so it's a more reliable "in a call" signal than anything
    // on VoiceService itself.
    private async Task OnIdleChangedAsync(bool isIdle)
    {
        if (isIdle && _currentVoiceChannelId is not null) return;
        await SetPresenceStateSafeAsync(isIdle ? "Away" : "Online");
    }

    // --- In-game voice overlay ---

    private void InitializeOverlay()
    {
        var settings = OverlaySettingsStorage.Load();

        _overlay = new OverlayWindow();
        _overlay.SetUserEnabled(settings.Enabled);
        _overlay.SetBackgroundOpacity(settings.BackgroundOpacity);
        // In case a voice channel is somehow already joined by now (e.g. a
        // reconnect flow), seed the roster immediately.
        SyncOverlayRoster();

        // A dedicated hotkey listener for the toggle, independent of PTT.
        _overlayHotkey = new GlobalHotkeyService { WatchedKey = settings.ToggleKey };
        _overlayHotkey.KeyDown += () => Dispatcher.Invoke(() => _overlay?.ToggleVisibility());
        _overlayHotkey.Start();
    }

    // Points the overlay at the live Members collection of whichever voice
    // channel is currently joined (or null when not in voice). Because it's
    // the same ObservableCollection instance the sidebar roster uses, later
    // joins/leaves/speaking changes flow to the overlay with no extra work.
    // Called automatically from the _currentVoiceChannelId setter.
    private void SyncOverlayRoster()
    {
        if (_overlay is null) return;

        var members = _currentVoiceChannelIdBacking is { } id
            ? FindVoiceChannelItem(id)?.Members
            : null;
        _overlay.SetRoster(members);
    }

    // Called by SettingsPage when the user changes the overlay enable toggle,
    // rebinds the toggle key, or adjusts the background transparency.
    public void ApplyOverlaySettings(bool enabled, System.Windows.Input.Key? toggleKey, double backgroundOpacity)
    {
        OverlaySettingsStorage.Save(new SavedOverlaySettings(enabled, toggleKey, backgroundOpacity));
        _overlay?.SetUserEnabled(enabled);
        _overlay?.SetBackgroundOpacity(backgroundOpacity);
        if (_overlayHotkey is not null) _overlayHotkey.WatchedKey = toggleKey;
    }

    public SavedOverlaySettings CurrentOverlaySettings => OverlaySettingsStorage.Load();

    private void OnPresenceChanged(int userId, string state)
    {
        var member = _members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) member.PresenceState = state;

        var friend = _friends.FirstOrDefault(f => f.UserId == userId);
        if (friend is not null) friend.PresenceState = state;
    }

    private void OnCustomStatusChanged(int userId, string? status)
    {
        var member = _members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) member.CustomStatus = status;

        var friend = _friends.FirstOrDefault(f => f.UserId == userId);
        if (friend is not null) friend.CustomStatus = status;
    }

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
    private async Task LoadVoiceRostersAsync(int serverId)
    {
        var rosters = await _hub.GetVoiceRostersForServerAsync(serverId);
        foreach (var roster in rosters)
        {
            var item = FindVoiceChannelItem(roster.ChannelId);
            if (item is null) continue;

            foreach (var member in roster.Members)
            {
                if (!item.Members.Any(m => m.UserId == member.UserId))
                    item.Members.Add(new VoiceMemberItem { UserId = member.UserId, Username = member.Username, AvatarUrl = App.ResolveUploadUrl(member.AvatarUrl), IsSelf = member.UserId == _api.CurrentUserId, Volume = SavedVolumePercent(member.UserId) });
            }
        }
    }

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
    private async void VoiceChannelChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;

        SaveCurrentDraft();

        _currentChannelId = channelId;
        CancelReply();
        if (_joinedChannelIds.Add(channelId))
            await _hub.JoinChannelAsync(channelId);

        ChannelNameText.Text = $"{FindChannelDisplayName(channelId) ?? "🔊 channel"} · Text Chat";
        DmCallButton.Visibility = Visibility.Collapsed;
        PinnedMessagesButton.Visibility = Visibility.Visible;
        SearchMessagesButton.Visibility = Visibility.Visible;
        ModerationLogButton.Visibility = _canViewAuditLog ? Visibility.Visible : Visibility.Collapsed;
        BanListButton.Visibility = _canViewAuditLog ? Visibility.Visible : Visibility.Collapsed;

        LoadDraftIntoInput();
        await LoadChannelHistoryAsync(channelId);
    }

    private async void VoiceChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int channelId }) return;
        if (_voice is null) return;

        // Re-clicking the channel you're already in used to tear down and
        // re-establish the whole voice connection for no reason - harmless
        // most of the time, but a rapid double-click could land the leave
        // and the rejoin close enough together to leave the audio endpoint
        // (and everyone else's view of this connection) in a half-torn-down
        // state, breaking capture/playback for the whole channel.
        if (_currentVoiceChannelId == channelId) return;

        // Calls and server voice channels are mutually exclusive contexts -
        // joining a channel while on a private call ends the call first,
        // mirrored by LeaveVoiceChannelIfActiveAsync on the call side.
        if (_callWindow is not null)
        {
            _ = EndOrDeclineCallAsync(_callWindow.CallId, _callWindow.State == CallWindowState.IncomingRinging);
            _callWindow.CloseSilently();
        }

        if (_currentVoiceChannelId.HasValue)
        {
            await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
            await _voice.LeaveAllAsync();
            RemoveSelfFromVoiceRoster(_currentVoiceChannelId.Value);
        }

        _currentVoiceChannelId = channelId;

        // Show ourselves locally right away, styled as "joining" (see
        // VoiceMemberItem.IsJoining), instead of waiting for the LiveKit
        // connection below - that takes a few seconds, and without this
        // there'd be no feedback at all that the click registered. Other
        // clients don't get told about the join at all until it actually
        // succeeds (see JoinVoiceChannelAsync further down), so nobody else
        // ever sees this placeholder.
        var item = FindVoiceChannelItem(channelId);
        VoiceMemberItem? selfItem = null;
        if (item is not null && _api.CurrentUserId is not null && _api.CurrentUsername is not null)
        {
            selfItem = new VoiceMemberItem { UserId = _api.CurrentUserId.Value, Username = _api.CurrentUsername, AvatarUrl = _api.CurrentUserAvatarUrl, IsSelf = true, IsJoining = true };
            item.Members.Add(selfItem);
        }
        ConnectionStatusText.Text = "Joining voice...";

        try
        {
            await _voice.JoinChannelAsync(channelId);
        }
        catch
        {
            if (item is not null && selfItem is not null) item.Members.Remove(selfItem);
            _currentVoiceChannelId = null;
            ConnectionStatusText.Text = "";
            await AlertAsync("Error", "Could not join voice - check your connection and try again.");
            return;
        }

        // Presence/roster bookkeeping (still SignalR, unchanged from the mesh
        // days) - deliberately called only now, after the audio connection
        // above actually succeeded, so other clients' rosters only pick up
        // this join once it's real rather than a few seconds early.
        var existingMembers = await _hub.JoinVoiceChannelAsync(channelId);

        if (item is not null)
        {
            item.Members.Clear();
            foreach (var m in existingMembers)
                item.Members.Add(new VoiceMemberItem { UserId = m.UserId, Username = m.Username, AvatarUrl = App.ResolveUploadUrl(m.AvatarUrl), IsSelf = m.UserId == _api.CurrentUserId, Volume = SavedVolumePercent(m.UserId) });
            if (_api.CurrentUserId is not null && _api.CurrentUsername is not null)
                item.Members.Add(new VoiceMemberItem { UserId = _api.CurrentUserId.Value, Username = _api.CurrentUsername, AvatarUrl = _api.CurrentUserAvatarUrl, IsSelf = true });
        }

        VoiceControlBar.Visibility = Visibility.Visible;
        UpdateMuteButtonVisual();
        UpdateDeafenButtonVisual();
        UpdateScreenShareButtonVisual();
        ConnectionStatusText.Text = "Joined voice";

        // Unconditional (not gated on window focus like OnVoiceUserJoined's
        // toast/sound for other people) - this is direct feedback that your
        // own join went through, so it needs to play even if you're the
        // only one in the channel and even while the app is focused.
        NotificationService.PlayVoiceJoinSound();
    }

    private void MuteMicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        _voice.IsMicMuted = !_voice.IsMicMuted;
        UpdateMuteButtonVisual();
    }

    private void UpdateMuteButtonVisual()
    {
        if (_voice is null) return;

        MuteMicButton.Content = _voice.IsMicMuted ? "🔇" : "🎤";
        MuteMicButton.ToolTip = _voice.IsMicMuted ? "Unmute Mic" : "Mute Mic";
        MuteMicButton.Background = _voice.IsMicMuted
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x3F, 0x42))
            : System.Windows.Media.Brushes.Transparent;
    }

    private void DeafenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        // Deafen forces mute to match (see VoiceService.IsDeafened), so
        // both buttons' visuals need refreshing, not just Deafen's own.
        _voice.IsDeafened = !_voice.IsDeafened;
        UpdateDeafenButtonVisual();
        UpdateMuteButtonVisual();
    }

    private void UpdateDeafenButtonVisual()
    {
        if (_voice is null) return;

        // Icon stays 🎧 in both states - reusing 🔇 here would make it
        // indistinguishable from the Mute Mic button's own active-state
        // icon, which is a genuinely different action (can't be heard vs.
        // can't hear anyone). Active/inactive is instead signaled by the
        // button's own Background fill (see VoiceControlIconButtonStyle),
        // since color emoji glyphs render from their own embedded palette
        // and ignore the Button's Foreground brush.
        DeafenButton.Content = "🎧";
        DeafenButton.ToolTip = _voice.IsDeafened ? "Undeafen" : "Deafen";
        DeafenButton.Background = _voice.IsDeafened
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0x3F, 0x42))
            : System.Windows.Media.Brushes.Transparent;
    }

    private void VoiceMemberVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_voice is null) return;
        if (sender is not FrameworkElement { DataContext: VoiceMemberItem member }) return;

        var volume = (float)(e.NewValue / 100.0);
        _voice.SetRemoteVolume(member.UserId, volume);
        UserVolumeStorage.SaveVolume(member.UserId, volume);
    }

    // --- Private calls (1:1, friends-only) - see CallWindow and
    // ChatHub's call signaling methods server-side. ---

    private async void CallFriendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int calleeId }) return;

        var friend = _friends.FirstOrDefault(f => f.UserId == calleeId);
        await InitiateCallAsync(calleeId, friend?.Username ?? "user", friend?.AvatarUrl);
    }

    private async void DmCallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dmActiveUserId is null) return;

        var avatarUrl = _dmConversations.FirstOrDefault(c => c.OtherUserId == _dmActiveUserId)?.OtherUserAvatarUrl;
        await InitiateCallAsync(_dmActiveUserId.Value, _dmActiveUsername ?? "user", avatarUrl);
    }

    private async Task InitiateCallAsync(int calleeId, string calleeUsername, string? calleeAvatarUrl)
    {
        if (_voice is null) return;
        if (_callWindow is not null) return; // already ringing/in a call

        await LeaveVoiceChannelIfActiveAsync();

        var callId = await _hub.InitiateCallAsync(calleeId);
        if (callId is null)
        {
            await AlertAsync("Error", "Couldn't start the call - you may not be friends, they're already in a call, or you're calling too fast.");
            return;
        }

        _currentCallId = callId;
        _callWindow = new CallWindow(callId, calleeId, calleeUsername, calleeAvatarUrl, isOutgoing: true,
            _api.CurrentUsername ?? "You", _api.CurrentUserAvatarUrl, _voice);
        _callWindow.Ended += wasDecline => _ = EndOrDeclineCallAsync(callId, wasDecline);
        _callWindow.Closed += (_, _) => OnCallWindowClosed(callId);
        _callWindow.Show();

        StartRingTimeout(callId);
    }

    private void OnIncomingCall(string callId, int callerId, string callerUsername, string? callerAvatarUrl)
    {
        Dispatcher.Invoke(() =>
        {
            if (_voice is null || _callWindow is not null) return; // already ringing/in a call - shouldn't happen (server allows one call at a time), guard anyway

            _currentCallId = callId;
            _callWindow = new CallWindow(callId, callerId, callerUsername, App.ResolveUploadUrl(callerAvatarUrl), isOutgoing: false,
                _api.CurrentUsername ?? "You", _api.CurrentUserAvatarUrl, _voice);
            _callWindow.Accepted += () => _ = AcceptIncomingCallAsync(callId);
            _callWindow.Ended += wasDecline => _ = EndOrDeclineCallAsync(callId, wasDecline);
            _callWindow.Closed += (_, _) => OnCallWindowClosed(callId);
            _callWindow.Show();

            NotificationService.StartIncomingCallSound();
        });
    }

    private async Task AcceptIncomingCallAsync(string callId)
    {
        if (_voice is null) return;
        NotificationService.StopIncomingCallSound();

        var accepted = await _hub.AcceptCallAsync(callId);
        if (!accepted)
        {
            _callWindow?.CloseSilently();
            return;
        }

        await LeaveVoiceChannelIfActiveAsync();
        await JoinActiveCallAsync(callId);
    }

    // Fires once the callee accepts our outgoing call - time for the caller
    // side to actually connect the LiveKit room (the callee connects from
    // AcceptIncomingCallAsync above instead, right after accepting).
    private void OnCallAccepted(string callId)
    {
        Dispatcher.Invoke(() =>
        {
            if (_currentCallId != callId) return;
            CancelRingTimeout();
            _ = JoinActiveCallAsync(callId);
        });
    }

    private async Task JoinActiveCallAsync(string callId)
    {
        if (_voice is null) return;

        try
        {
            await _voice.JoinCallAsync(callId);
        }
        catch
        {
            await AlertAsync("Error", "Could not join the call - check your connection and try again.");
            _ = _hub.EndCallAsync(callId);
            _callWindow?.CloseSilently();
            return;
        }

        _callWindow?.SwitchToActive();
    }

    private async Task EndOrDeclineCallAsync(string callId, bool wasDecline)
    {
        // Read before the hub call below - by the time this runs, State
        // still reflects whatever it was right before Close() (see
        // CallWindow_Closing's Ended?.Invoke), so it tells us exactly what
        // kind of end this was: declining an incoming ring, cancelling one
        // we placed, or hanging up an already-active call.
        if (_callWindow is not null)
        {
            var outcome = wasDecline ? "Declined" : _callWindow.State == CallWindowState.Active ? "Ended" : "Missed";
            _ = SendCallEventMessageAsync(_callWindow.OtherPartyUserId, outcome);
        }

        try
        {
            if (wasDecline) await _hub.DeclineCallAsync(callId);
            else await _hub.EndCallAsync(callId);
        }
        catch (Exception ex)
        {
            // Invoked fire-and-forget (`_ = EndOrDeclineCallAsync(...)`) from
            // multiple call sites, so an exception here has nowhere else to
            // go - without this it vanishes silently instead of reaching
            // DispatcherUnhandledException (that only catches exceptions on
            // the dispatcher thread's own call stack, not ones thrown inside
            // a discarded Task). The other party's client still recovers on
            // its own (see ChatHub.OnDisconnectedAsync's implicit end-call
            // handling), so this is log-and-move-on, not a user-facing error.
            LogBackgroundException(ex);
        }
    }

    // Best-effort, fire-and-forget - see CallEventMessage for why this reuses
    // the normal encrypted DM pipeline instead of a dedicated schema.
    private async Task SendCallEventMessageAsync(int otherUserId, string outcome)
    {
        try
        {
            var encrypted = await _api.E2ee.EncryptAsync(otherUserId, CallEventMessage.Format(outcome));
            if (encrypted is null) return; // keys not unlocked/established yet - skip rather than send plaintext
            await _hub.SendDirectMessageAsync(otherUserId, encrypted);
        }
        catch
        {
            // A missing call-event message isn't worth surfacing an error for.
        }
    }

    private void OnCallEndedRemotely(string callId)
    {
        Dispatcher.Invoke(() =>
        {
            if (_currentCallId != callId) return;

            // Only reachable here (rather than through our own Decline/Cancel
            // click) when the OTHER party ended things first - if we were
            // still the callee and still ringing, that means they cancelled
            // or timed out before we ever answered, i.e. a genuine missed call.
            if (_callWindow is { State: CallWindowState.IncomingRinging })
                NotificationService.ShowToast("Missed Call", $"{_callWindow.OtherPartyUsername} called you");

            _callWindow?.CloseSilently();
            _ = _voice?.LeaveAllAsync();
        });
    }

    private void OnCallWindowClosed(string callId)
    {
        if (_currentCallId == callId)
        {
            _currentCallId = null;
            _callWindow = null;
        }
        NotificationService.StopIncomingCallSound();
        CancelRingTimeout();
    }

    // Calls and server voice channels are mutually exclusive contexts (see
    // VoiceService) - leave the current channel first, same as switching
    // between two voice channels does.
    private async Task LeaveVoiceChannelIfActiveAsync()
    {
        if (!_currentVoiceChannelId.HasValue || _voice is null) return;

        await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
        await _voice.LeaveAllAsync();
        RemoveSelfFromVoiceRoster(_currentVoiceChannelId.Value);
        _currentVoiceChannelId = null;
        VoiceControlBar.Visibility = Visibility.Collapsed;
    }

    // Caller-side-only ring timeout (per the plan: handled client-side
    // rather than a server timer) - auto-cancels an outgoing call nobody
    // answered instead of ringing forever.
    private void StartRingTimeout(string callId)
    {
        CancelRingTimeout();
        var timeoutSeconds = _voice?.RingTimeoutSeconds ?? 40;
        _ringTimeoutTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(timeoutSeconds) };
        _ringTimeoutTimer.Tick += (_, _) =>
        {
            CancelRingTimeout();
            if (_currentCallId != callId) return;
            if (_callWindow is not null) _ = SendCallEventMessageAsync(_callWindow.OtherPartyUserId, "Missed");
            _ = _hub.EndCallAsync(callId);
            _callWindow?.CloseSilently();
        };
        _ringTimeoutTimer.Start();
    }

    private void CancelRingTimeout()
    {
        _ringTimeoutTimer?.Stop();
        _ringTimeoutTimer = null;
    }

    // --- Screen sharing (server voice channels - see VoiceService.
    // StartScreenShareAsync/StopScreenShareAsync and ScreenCaptureSource) ---

    private void ScreenShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        if (_voice.IsScreenSharing)
        {
            _ = StopScreenShareAsync();
            return;
        }

        // Always prompts for a quality preset - no remembered choice from a
        // previous share gets silently reused, so the user picks what to
        // share (and at what quality) fresh every time.
        ScreenSharePresetMenu.PlacementTarget = ScreenShareButton;
        ScreenSharePresetMenu.IsOpen = true;
    }

    // Resolution+bitrate pairs scale with the frame-rate target so a higher
    // fps preset isn't starved by the same cap a 30fps share would use -
    // see the Phase 2 spike notes on the plan for why a shared fixed cap
    // skewed its fps comparison. Native/120fps stays labelled experimental
    // in the UI since the spike couldn't confirm it holds up through the
    // SFU in practice.
    private async void ScreenShare480p30Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(30, 2_500_000, 854, 480);
    private async void ScreenShare720p60Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(60, 6_000_000, 1280, 720);
    private async void ScreenShareNative120Fps_Click(object sender, RoutedEventArgs e) => await StartScreenShareWithPresetAsync(120, 35_000_000, null, null);

    // Quick-share shortcut with a fixed 720p60 preset, skipping the preset
    // menu entirely (but still always prompting for a source, same as every
    // other share path - see StartScreenShareWithPresetAsync).
    private async void ScreenShareChooseSource_Click(object sender, RoutedEventArgs e)
    {
        if (_voice is null) return;

        var item = await PickScreenShareSourceAsync();
        if (item is null) return;

        await StartScreenShareWithItemAsync(item, 60, 6_000_000, 1280, 720);
    }

    private async Task StartScreenShareWithPresetAsync(uint fps, uint bitrate, int? maxWidth, int? maxHeight)
    {
        try
        {
            var item = await PickScreenShareSourceAsync();
            if (item is null) return;

            await StartScreenShareWithItemAsync(item, fps, bitrate, maxWidth, maxHeight);
        }
        catch (Exception ex)
        {
            // Invoked fire-and-forget (`_ = StartScreenShareWithPresetAsync(...)`)
            // from the preset menu click handlers - PickScreenShareSourceAsync
            // and StartScreenShareWithItemAsync each already show a themed
            // error for their own common failure modes, so this is just a
            // backstop for anything that escapes both of those.
            LogBackgroundException(ex);
        }
    }

    private async Task<Windows.Graphics.Capture.GraphicsCaptureItem?> PickScreenShareSourceAsync()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return await ScreenCaptureSource.PickItemAsync(hwnd);
        }
        catch (Exception ex)
        {
            await AlertAsync("Error", $"Could not open the screen/window picker:\n{ex.Message}");
            return null;
        }
    }

    private async Task StartScreenShareWithItemAsync(Windows.Graphics.Capture.GraphicsCaptureItem item, uint fps, uint bitrate, int? maxWidth, int? maxHeight)
    {
        if (_voice is null) return;

        try
        {
            await _voice.StartScreenShareAsync(item, fps, bitrate, maxWidth, maxHeight);
        }
        catch (Exception ex)
        {
            await AlertAsync("Error", $"Could not start screen sharing:\n{ex.Message}");
            return;
        }

        UpdateScreenShareButtonVisual();
    }

    private void OnLocalScreenSharingChanged(bool isSharing)
    {
        if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
        var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
        var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
        if (me is not null) me.IsScreenSharing = isSharing;
    }

    private async Task StopScreenShareAsync()
    {
        if (_voice is null) return;
        await _voice.StopScreenShareAsync();
        UpdateScreenShareButtonVisual();
    }

    private void UpdateScreenShareButtonVisual()
    {
        if (_voice is null) return;

        ScreenShareButton.Content = "🖥️";
        ScreenShareButton.ToolTip = _voice.IsScreenSharing ? "Stop Sharing" : "Share Screen";
        ScreenShareButton.Background = _voice.IsScreenSharing
            ? (System.Windows.Media.Brush)FindResource("AccentBlurple")
            : System.Windows.Media.Brushes.Transparent;
    }

    private void OnRemoteScreenShareStarted(int userId, RemoteVideoPlayback playback)
    {
        // _voice is shared between channel voice and private calls -
        // CallWindow subscribes to this same event for call-scoped shares
        // (see CallWindow.OnRemoteScreenShareStarted), so this handler must
        // stay out of the way when the active voice context is a call.
        if (!_currentVoiceChannelId.HasValue) return;

        var member = FindVoiceChannelItem(_currentVoiceChannelId.Value)?.Members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) member.IsScreenSharing = true;

        // No auto-opened viewer - just remember the live playback so a
        // later click on this member's TV icon (ScreenShareIcon_MouseLeftButtonUp)
        // has something to open.
        _activeScreenShares[userId] = playback;
    }

    private void OnRemoteScreenShareStopped(int userId)
    {
        if (!_currentVoiceChannelId.HasValue) return;

        var member = FindVoiceChannelItem(_currentVoiceChannelId.Value)?.Members.FirstOrDefault(m => m.UserId == userId);
        if (member is not null) member.IsScreenSharing = false;

        _activeScreenShares.Remove(userId);
        if (_screenShareViewers.Remove(userId, out var viewer))
            viewer.Close();
    }

    // Click target for the blue TV icon next to a sharing member's name -
    // the only way a viewer window opens now (see OnRemoteScreenShareStarted).
    // Clicking an already-open sharer's icon activates the existing window
    // instead of opening a second one.
    private void ScreenShareIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int userId }) return;
        if (!_activeScreenShares.TryGetValue(userId, out var playback)) return;
        if (_voice is null) return;

        if (_screenShareViewers.TryGetValue(userId, out var existing))
        {
            existing.Activate();
            return;
        }

        var member = FindVoiceChannelItem(_currentVoiceChannelId ?? -1)?.Members.FirstOrDefault(m => m.UserId == userId);
        var viewer = new ScreenShareViewerWindow(member?.Username ?? "Someone", playback, _voice, userId);
        _screenShareViewers[userId] = viewer;
        viewer.Closed += (_, _) => _screenShareViewers.Remove(userId);
        viewer.Show();
    }

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

    private async void LeaveVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVoiceChannelId is null || _voice is null) return;

        await _hub.LeaveVoiceChannelAsync(_currentVoiceChannelId.Value);
        await _voice.LeaveAllAsync();
        RemoveSelfFromVoiceRoster(_currentVoiceChannelId.Value);
        _currentVoiceChannelId = null;
        VoiceControlBar.Visibility = Visibility.Collapsed;
        ConnectionStatusText.Text = "";
    }

    // Removes just the local user's own entry from a voice channel's roster.
    // The old code called Members.Clear() here, which wiped out everyone
    // else still in the channel too - visible as "the whole list disappears"
    // the moment you leave, even though other people are still in the call.
    // Anyone else's departure is already handled correctly (one at a time)
    // by OnVoiceUserLeft reacting to the server's broadcast; this covers the
    // one case that broadcast doesn't reliably beat the UI update for - our
    // own leave, applied immediately rather than waiting on the round trip.
    private void RemoveSelfFromVoiceRoster(int channelId)
    {
        if (_api.CurrentUserId is null) return;
        var item = FindVoiceChannelItem(channelId);
        var self = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId);
        if (item is not null && self is not null) item.Members.Remove(self);
    }

    // UserVolumeStorage stores the 1.0-scale multiplier (matching
    // PlaybackVolume's own unit); VoiceMemberItem.Volume is the 0-200
    // percent scale the slider uses - this converts and defaults to 100%
    // (unchanged) for a user with no saved volume yet.
    private static double SavedVolumePercent(int userId) =>
        (UserVolumeStorage.GetVolume(userId) ?? 1.0f) * 100.0;

    // The session is already dead server-side by the time this fires (see
    // ApiService.SessionExpired) - just get the user back to a login screen,
    // no server call to make here unlike the normal logout button.
    private async void OnSessionExpired()
    {
        SessionStorage.Clear();
        await AlertAsync("Signed Out", "Your session has expired. Please log in again.");
        new LoginWindow().Show();
        _reallyExit = true;
        Close();
    }

    private async void LogOutButton_Click(object sender, RoutedEventArgs e)
    {
        // MainWindow_Closed already handles leaving voice / disconnecting the hub.
        // Revokes the refresh token server-side (best-effort - see
        // ApiService.LogoutAsync) so it can't be redeemed again even if
        // something copied it, not just wiping the local copy.
        await _api.LogoutAsync();
        SessionStorage.Clear();
        EndSessionAndShowLogin();
    }

    private void EndSessionAndShowLogin()
    {
        var login = new LoginWindow();
        login.Show();
        _reallyExit = true;
        Close();
    }

    // Called by SettingsPage after a successful account-delete - it already
    // cleared SessionStorage itself, this just does the same LoginWindow/Close
    // sequence LogOutButton_Click uses.
    public void HandleAccountDeleted() => EndSessionAndShowLogin();

    // Called by SettingsPage.Unloaded - My Account may have just changed the
    // avatar (ChangeAvatarButton_Click updates ApiService.CurrentUserAvatarUrl
    // directly), so refresh MainWindow's own bound copy once the page closes.
    public void RefreshMyAvatarView() => MyAvatarView.ImageUrl = _api.CurrentUserAvatarUrl;

    // Called by DiscoverServersPage after successfully joining a public
    // server, so it shows up in the rail immediately instead of waiting for
    // the next login/reconnect.
    public async Task ReloadServersAsync() => await LoadServersAsync();

    private void MyAvatarBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        NavigateTo(new SettingsPage(this, _api, _voice), "Settings");
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

    private void RecentCallsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(new CallHistoryPage(_api), "Recent Calls");
    }

    private void DiscoverServersButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo(new DiscoverServersPage(this, _api), "Discover Servers");

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
    private void CrossfadeSidebarPanel(System.Windows.Controls.Border show)
    {
        foreach (var panel in new[] { ServerSidebarPanel, MessagesSidebarPanel, FriendsSidebarPanel })
        {
            if (panel == show || panel.Visibility != Visibility.Visible) continue;

            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(120));
            fadeOut.Completed += (_, _) => panel.Visibility = Visibility.Collapsed;
            panel.BeginAnimation(OpacityProperty, fadeOut);
        }

        show.Visibility = Visibility.Visible;
        show.BeginAnimation(OpacityProperty, null);
        show.Opacity = 0;
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120)));
    }

    private void ShowServerSidebar()
    {
        CrossfadeSidebarPanel(ServerSidebarPanel);
        MembersPanel.Visibility = Visibility.Visible;

        if (_dmActiveUserId.HasValue || ChannelNameText.Text != "# select-a-channel")
        {
            _dmActiveUserId = null;
            _dmActiveUsername = null;
            _messages.Clear();
            CancelReply();
            ChannelNameText.Text = "# select-a-channel";
            DmCallButton.Visibility = Visibility.Collapsed;
            DmBlockUserButton.Visibility = Visibility.Collapsed;
            PinnedMessagesButton.Visibility = Visibility.Collapsed;
            SearchMessagesButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowMessagesSidebar()
    {
        CrossfadeSidebarPanel(MessagesSidebarPanel);
        MembersPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowFriendsSidebar()
    {
        CrossfadeSidebarPanel(FriendsSidebarPanel);
        MembersPanel.Visibility = Visibility.Collapsed;
    }

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
    private void RefreshLatestOwnMessageFlag()
    {
        var latestOwn = _messages.LastOrDefault(m => m.IsOwnMessage);
        foreach (var m in _messages)
            m.IsLatestOwnMessage = ReferenceEquals(m, latestOwn);
    }

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
    private void OnForceMuted(int channelId)
    {
        if (_voice is null || channelId != _currentVoiceChannelId) return;
        _voice.IsMicMuted = true;
        UpdateMuteButtonVisual();
    }

    private void OnMessageDeleted(int messageId, int channelId)
    {
        Dispatcher.Invoke(() =>
        {
            if (channelId != _currentChannelId) return;
            var item = FindMessageById(messageId);
            if (item is not null) _messages.Remove(item);
        });
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
    private void OnDmUserTyping(string username, int senderId)
    {
        if (senderId != _dmActiveUserId) return;

        ChannelNameText.Text = $"@{username}   (typing...)";
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

        // Same "fresh connection, no group memberships" reasoning as the two
        // rejoins above - without this, the bystander moderation/channel
        // broadcasts (MemberKicked, ChannelCreated, etc.) silently stop
        // reaching this client after any reconnect. The one-shot refresh
        // afterward reconciles anything that happened server-side during
        // the outage, since group membership alone doesn't replay missed
        // events.
        if (_currentServerId.HasValue)
        {
            await _hub.JoinServerPresenceAsync(_currentServerId.Value);
            await RefreshChannelsAsync(_currentServerId.Value);
            await LoadMembersPanelAsync(_currentServerId.Value);
        }

        Dispatcher.Invoke(() => SetConnectionStatusText("", isAlert: false));
    }

    private void OnVoiceUserJoined(int userId, string username, int channelId, string? avatarUrl)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            if (item is not null && !item.Members.Any(m => m.UserId == userId))
                item.Members.Add(new VoiceMemberItem { UserId = userId, Username = username, AvatarUrl = App.ResolveUploadUrl(avatarUrl), IsSelf = userId == _api.CurrentUserId, Volume = SavedVolumePercent(userId) });

            // Only for the voice channel you're actually sitting in, and
            // only when you're not looking at the app - the icon/roster
            // update above already covers "looking at it" case.
            if (channelId == _currentVoiceChannelId && userId != _api.CurrentUserId && !IsActive)
            {
                NotificationService.PlayVoiceJoinSound();
                NotificationService.ShowToast("Voice", $"{username} joined voice");
            }
        });
    }

    private void OnVoiceUserLeft(int userId, string username, int channelId)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var existing = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (item is not null && existing is not null) item.Members.Remove(existing);

            if (channelId == _currentVoiceChannelId && userId != _api.CurrentUserId && !IsActive)
            {
                NotificationService.PlayVoiceLeaveSound();
                NotificationService.ShowToast("Voice", $"{username} left voice");
            }
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

    private void OnUserMuted(int userId, int channelId, bool isMuted)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsMuted = isMuted;
        });
    }

    private void OnUserDeafened(int userId, int channelId, bool isDeafened)
    {
        Dispatcher.Invoke(() =>
        {
            var item = FindVoiceChannelItem(channelId);
            var member = item?.Members.FirstOrDefault(m => m.UserId == userId);
            if (member is not null) member.IsDeafened = isDeafened;
        });
    }

    // Mirrors OnLocalSpeakingChangedAsync - broadcast to whoever else is in
    // the channel, then reflect it on our own row too (the row for "you"
    // needs the icon just as much as everyone else's).
    private async Task OnLocalMutedChangedAsync(bool isMuted)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendMutedAsync(_currentVoiceChannelId.Value, isMuted);
        }
        catch
        {
            // Best-effort - a dropped mute-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsMuted = isMuted;
        });
    }

    private async Task OnLocalDeafenedChangedAsync(bool isDeafened)
    {
        try
        {
            if (_currentVoiceChannelId.HasValue)
                await _hub.SendDeafenedAsync(_currentVoiceChannelId.Value, isDeafened);
        }
        catch
        {
            // Best-effort - a dropped deafen-state update isn't worth surfacing an error for.
        }

        Dispatcher.Invoke(() =>
        {
            if (!_currentVoiceChannelId.HasValue || _api.CurrentUserId is null) return;
            var item = FindVoiceChannelItem(_currentVoiceChannelId.Value);
            var me = item?.Members.FirstOrDefault(m => m.UserId == _api.CurrentUserId.Value);
            if (me is not null) me.IsDeafened = isDeafened;
        });
    }

    // contentOverride lets a caller that already decrypted m.Content itself
    // (OnMessageReceived, working with a raw hub push) supply the plaintext
    // directly - callers going through LoadChannelHistoryAsync already
    // decrypt inline and pass the result the same way.
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

    private ChannelListItem? FindTextChannelItem(int channelId)
    {
        foreach (var c in _textChannels) if (c.Id == channelId) return c;
        return null;
    }

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
