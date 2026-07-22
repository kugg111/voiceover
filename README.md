# Voiceover

A Discord-style chat app: an ASP.NET Core server (REST API + SignalR) and a
WPF desktop client. Servers, roles, invites, channel categories, text
channels, direct messages, friends, file/voice-message attachments, custom
emoji, voice channels, private 1:1 calls with screen sharing, and
end-to-end encrypted messaging.

**Live app:** [voiceover-app.hu](https://www.voiceover-app.hu/) (or the
[Railway subdomain](https://voiceover-production-c32a.up.railway.app/)) —
grab the Windows installer or a portable ZIP from the
[download page](https://www.voiceover-app.hu/download.html).

## Features

- **Auth**: register/login with JWT access tokens + revocable refresh
  tokens, passwords hashed with BCrypt, optional TOTP two-factor
  authentication (with one-time recovery codes)
- **Servers, channels & categories**: create servers, text and voice
  channels, drag-and-drop reordering, and channel categories/folders to
  group them
- **Roles & granular permissions**: Owner/Moderator/Member, with eight
  independently-grantable Moderator permissions (manage channels, kick/ban,
  manage messages, mute members, mention @everyone/@here, manage roles,
  manage server settings, view the audit log) — the owner decides exactly
  what each Moderator can do
- **Invites & discovery**: any member can generate a shareable invite code;
  server owners can also opt in to a public directory so anyone can find
  and join without an invite
- **Real-time text chat**: SignalR-backed messaging, typing indicators
  (channels and DMs), @mentions and permission-gated @everyone/@here,
  reactions, reply/quote, pinned messages, message forwarding to any
  channel or DM, in-app search, edit and delete (with moderator bulk-delete
  and per-channel slow mode), numeric unread badges, "load older messages"
  pagination, and per-conversation draft persistence across restarts
- **Custom server emoji**: upload images as reactable custom emoji scoped
  to a server, alongside the built-in emoji picker
- **Direct messages, friends & voice-channel chat**: search users by
  username, send/accept friend requests, 1:1 real-time DMs with read
  receipts and per-conversation notification muting, plus a text-chat pane
  on every voice channel so you don't need to join the call to type
- **End-to-end encryption**: every channel message and DM is encrypted
  client-side (ECDH key exchange + HKDF + AES-256-GCM) before it ever
  reaches the server. Channel messages use a fresh, random one-time key per
  message, individually wrapped for every current member — so a brand new
  member can read (and send) messages the moment they join, with no
  "wait for another member to grant you access" step. The server only
  ever stores and relays ciphertext it has no key to read
- **Voice channels**: routed through a self-hosted [LiveKit](https://livekit.io/)
  SFU (not a peer mesh — scales to larger channels without an N² connection
  blowup). Includes mute, deafen, per-user volume, push-to-talk / push-to-mute
  (keyboard or mouse button), and a voice-activity mode
- **Private voice calls**: 1:1 calls with any friend, outside any
  server/channel — ringing with a configurable timeout, accept/decline, a
  call duration timer, and a persisted Recent Calls history
- **Screen sharing**: share a window or monitor in a voice channel or a
  private call, with selectable resolution/framerate presets and your
  system audio (WASAPI loopback) published alongside the video
- **Noise suppression**: three selectable engines — RNNoise, [NSNet2](https://github.com/microsoft/DNS-Challenge),
  and a streaming port of Meta's [Denoiser](https://github.com/facebookresearch/denoiser)
  (Demucs-based, running via LibTorch) — real denoisers, not a hand-rolled
  volume gate. NSNet2 can run on the GPU (DirectML, any DX12 adapter,
  selectable when more than one is installed) instead of the CPU, and an
  optional Silero VAD pre-roll gate mutes confidently-silent stretches
  before they reach the denoiser
- **Voice messages**: record and send a short voice clip directly in a
  channel or DM, played back inline
- **Presence**: Online / Away / Offline status, Away triggered by
  real system idle detection; custom status text visible to friends and
  server members
- **Avatars & server icons** (resized/compressed on upload), toast/sound
  notifications gated on window focus, missed-call notifications,
  minimize-to-tray with background notifications
- **Account controls**: export your data (profile, server memberships,
  friends) as JSON, or delete your account outright — ownership of any
  server with other members is handled automatically (auto-promote the
  sole remaining member, or prompt you to pick a successor) rather than
  ever leaving a server ownerless
- **Moderation tools**: bans, a per-server moderation audit log, remote
  mute, slow mode, and bulk message deletion by user
- **Self-updating client**: a pre-login screen checks for new releases
  before the login window even appears, offering to install in the
  background — no manual download needed after the first install. Updates
  can also be flagged mandatory, blocking login until the user updates
- **Security hardening**: ASP.NET Core rate limiting on auth/messages/calls,
  TLS-required Postgres connection, short-lived JWTs with server-side
  revocation, upload magic-byte validation, security response headers
- **Fluent Design UI** ([WPF-UI](https://github.com/lepoco/wpfui)) with a
  Discord-inspired dark theme and subtle motion (page/modal transitions,
  message entrance animation, sidebar crossfade) — settings, moderation
  tools, and other secondary views are in-window pages inside the main
  window rather than separate popups
- **Admin dashboard**: a web page (`/admin`) gated behind an `IsAdmin` flag
  on the account, for developer use — browse servers/channels/members,
  search users, rename an account or reset its password, delete a server

## Architecture

```
Client (WPF, Windows)  <-- REST + SignalR -->  Server (ASP.NET Core)  <--> Postgres
        |                                              |
        `---------------- LiveKit SFU <---- join tokens minted by Server
```

The server never touches voice/screen-share media itself — it only
authenticates users, persists app data (including E2EE ciphertext it can't
decrypt), and mints short-lived LiveKit join tokens; audio and video flow
directly between clients and the LiveKit deployment.

## Project layout

```
Voiceover.sln
Server/                 ASP.NET Core Web API + SignalR
  Models/               EF Core entities (User, GuildServer, Channel, Category,
                         Message, MessageRecipientKey, Membership, Invite,
                         Emoji, DirectMessage, Friendship, Block, BannedUser,
                         ModerationLogEntry, RefreshToken, CallRecord,
                         StoredFile, TotpRecoveryCode, AdminAuditLogEntry)
  Data/                 AppDbContext + migrations
  Services/             PermissionService, PresenceService, VoicePresenceService,
                         LiveKitTokenService, CallSignalingService,
                         MessageRateLimiter, SlowModeLimiter, CallRateLimiter,
                         UserAvatarCache, ModerationLogService,
                         ServerDeletionService, CleanupService, AdminService
  Controllers/          Auth, Servers, Channels, Categories, Messages,
                         Emojis, Invites, Users, Friends, DirectMessages,
                         Upload, Calls, Admin
  Hubs/ChatHub.cs        messages, typing, DMs, reactions, voice/presence
                         signaling, private call signaling (ring/accept/
                         decline/end)
  Auth/                  JWT token issuing/validation
  Site/                  Public landing page (served at the app's root URL);
                         admin/ is the developer dashboard (see Features)
Client/                  WPF desktop app
  Views/                 LoginWindow, RegisterWindow, MainWindow — hosts most
                         secondary UI as in-window pages via a PageHost/
                         ModalOverlay pattern (SettingsPage, BanListPage,
                         ModerationLogPage, CallHistoryPage,
                         PinnedMessagesPage, MessageSearchPage,
                         EditPermissionsPage, CategoryManagementPage,
                         EmojiManagementPage, ForwardMessagePage) instead of
                         separate popup windows. Remaining popups are
                         InvitesWindow, CallWindow (a non-modal call HUD),
                         ScreenShareViewerWindow, ToastNotificationWindow,
                         and UpdateGateWindow (pre-login update check).
                         Shared controls: AvatarView, VoiceSettingsPanel
  Services/              ApiService (REST), SignalRService (real-time),
                         VoiceService + MicCaptureSource/ScreenCaptureSource/
                         ScreenAudioCaptureSource (LiveKit audio/video,
                         noise suppression, screen share), NoiseSuppressionProcessor
                         (RNNoise/NSNet2/Facebook Denoiser + GPU backend +
                         Silero VAD pre-roll gate), E2eeService,
                         CustomEmojiRegistry, DraftStorage, IdleDetector,
                         NotificationMuteStorage, SelfUpdateService
  Models/                Client-side DTOs
  installer/             Inno Setup script for the Windows installer
  native/                Vendored native runtime deps (NSNet2/Silero VAD
                         ONNX models, Facebook Denoiser's LibTorch runtime +
                         exported TorchScript model) - tracked via Git LFS,
                         see .gitattributes
```

## Running it locally

1. **.NET 8 SDK**, Windows 10/11 (the client uses Windows-only audio/capture
   APIs).
2. **Database**: a Postgres instance (local, Docker, or any hosted free tier).
   Set the connection string as a standard Postgres URI:
   ```
   cd Server
   dotnet user-secrets set DATABASE_URL "postgresql://user:pass@host:port/dbname"
   ```
3. **Voice chat** needs a LiveKit deployment (self-hosted or
   [LiveKit Cloud](https://cloud.livekit.io/) has a free tier). Set:
   ```
   dotnet user-secrets set LIVEKIT_API_KEY "..."
   dotnet user-secrets set LIVEKIT_API_SECRET "..."
   dotnet user-secrets set LIVEKIT_URL "wss://your-livekit-host"
   ```
   Everything else works without this — only actually joining a voice
   channel or call needs it, and fails with a clear error if it's missing.
4. **Run the server**: `dotnet run` from `Server/` — applies EF Core
   migrations automatically on startup, listens on `http://localhost:5220`.
5. **Run the client**: `dotnet run` from `Client/` (or set as the startup
   project in Visual Studio). Point it at your local server by editing the
   `ApiBaseUrl`/`HubUrl` constants in `Client/App.xaml.cs`.
6. Register an account, create a server, and invite a friend — or run a
   second client instance and register a second account to test locally.

Cloning the repo pulls `Client/native/`'s large files through Git LFS
automatically if you have `git-lfs` installed (`git lfs install` once,
system-wide, if you haven't already).

## Tech stack

- **Server**: ASP.NET Core 8, EF Core + Npgsql (Postgres), SignalR, JWT bearer
  auth, BCrypt, Serilog (console + Postgres sink)
- **Client**: WPF (.NET 8), WPF-UI (Fluent Design), NAudio (device/loopback
  capture), LiveKit's .NET client SDK, Windows.Graphics.Capture (screen share)
- **Voice/video**: self-hosted LiveKit SFU; RNNoise / NSNet2 / a streaming
  port of Meta's Denoiser (ONNX Runtime and LibTorch, NSNet2 optionally
  DirectML GPU-accelerated) for noise suppression, with a Silero VAD
  (ONNX Runtime) pre-gate
- **Encryption**: ECDH (P-256) + HKDF + AES-256-GCM, entirely client-side
- **Deployment**: server on [Railway](https://railway.app/) with a managed
  Postgres add-on; client installer/ZIP hosted as GitHub Releases

## Contributing

This started as a personal project, so there's no formal contribution process
yet — issues and PRs are welcome regardless.
