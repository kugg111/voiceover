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

public partial class MainWindow : FluentWindow
{
    private readonly ApiService _api;
    private readonly SignalRService _hub = new();
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
        ConnectionStatusText.Foreground = isError ? ThemeBrushes.Danger : ThemeBrushes.Away;
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
            ModalButtonStyle.Destructive => (ThemeBrushes.Danger, (Brush)Brushes.White),
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)App.ShowExistingInstanceMessage)
        {
            RestoreFromTray();
            handled = true;
        }
        return IntPtr.Zero;
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
        // Listens for App.ShowExistingInstanceMessage, posted by a second
        // launch's process (see App.BringExistingInstanceToForeground) when
        // this instance is already running - restoring via this window's
        // own Show()/Activate() (RestoreFromTray) rather than letting the
        // other process manipulate the HWND directly, which desyncs WPF's
        // internal Visibility state and leaves ui:FluentWindow's backdrop
        // uncomposited (a blank window) after coming back from the tray.
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            hwndSource.AddHook(WndProc);

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
    private void MyAvatarBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        NavigateTo(new SettingsPage(this, _api, _voice), "Settings");
    }

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

}
