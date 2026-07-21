# Voiceover

A Discord-style chat app: an ASP.NET Core server (REST API + SignalR) and a
WPF desktop client. Servers, roles, invites, text channels, direct messages,
friends, file attachments, voice channels, private 1:1 calls with screen
sharing, and end-to-end encrypted messaging.

**Live app:** [voiceover-app.hu](https://www.voiceover-app.hu/) (or the
[Railway subdomain](https://voiceover-production-c32a.up.railway.app/)) —
grab the Windows installer or a portable ZIP from the
[download page](https://www.voiceover-app.hu/download.html).

## Features

- **Auth**: register/login with JWT access tokens + revocable refresh tokens,
  passwords hashed with BCrypt
- **Servers & channels**: create servers, text and voice channels, drag-free
  reordering by position
- **Roles & permissions**: Owner/Moderator/Member — mods can create channels,
  kick members, delete others' messages; only the owner can change roles
- **Invites**: any member can generate a shareable invite code; a popup
  lists every active invite with one-click copy
- **Real-time text chat**: SignalR-backed messaging, typing indicators, edit
  and delete, numeric unread badges, and "load older messages" pagination
- **Direct messages & friends**: search users by username, send/accept
  friend requests, 1:1 real-time DMs with read receipts
- **End-to-end encryption**: channel messages and DMs are encrypted
  client-side (ECDH key exchange + HKDF + AES-256-GCM) — the server only
  ever stores and relays ciphertext it can't read
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
- **Noise suppression**: two selectable engines — RNNoise and
  [NSNet2](https://github.com/microsoft/DNS-Challenge) — real denoisers, not
  a hand-rolled volume gate. NSNet2 can run on the GPU (DirectML, any DX12
  adapter, selectable when more than one is installed) instead of the CPU,
  and an optional Silero VAD pre-roll gate mutes confidently-silent stretches
  before they reach the denoiser
- **Presence**: Online / Away / Offline status, Away triggered by
  real system idle detection; custom status text visible to friends and
  server members
- **Avatars & server icons**, toast/sound notifications gated on window
  focus, missed-call notifications
- **Self-updating client**: a pre-login screen checks for new releases before
  the login window even appears, offering to install in the background — no
  manual download needed after the first install. Updates can also be
  flagged mandatory, blocking login until the user updates
- **Security hardening**: ASP.NET Core rate limiting on auth/messages/calls,
  TLS-required Postgres connection, short-lived JWTs with server-side
  revocation, DM content encrypted at rest
- **Fluent Design UI** ([WPF-UI](https://github.com/lepoco/wpfui)) with a
  Discord-inspired dark theme — settings, moderation tools, and other
  secondary views are in-window pages inside the main window rather than
  separate popups
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
  Models/               EF Core entities (User, GuildServer, Channel, Message,
                         Membership, Invite, DirectMessage, Friendship,
                         RefreshToken, ServerMemberKey, CallRecord)
  Data/                 AppDbContext + migrations
  Services/             PermissionService, PresenceService, VoicePresenceService,
                         LiveKitTokenService, CallSignalingService,
                         MessageRateLimiter, CallRateLimiter, UserAvatarCache,
                         AdminService
  Controllers/          Auth, Servers, Channels, Messages, Invites, Users,
                         Friends, DirectMessages, Upload, Calls, Admin
  Hubs/ChatHub.cs        messages, typing, DMs, voice/presence signaling,
                         private call signaling (ring/accept/decline/end)
  Auth/                  JWT token issuing/validation
  Site/                  Public landing page (served at the app's root URL);
                         admin/ is the developer dashboard (see Features)
Client/                  WPF desktop app
  Views/                 LoginWindow, RegisterWindow, MainWindow — hosts most
                         secondary UI as in-window pages via a PageHost/
                         ModalOverlay pattern (SettingsPage, BanListPage,
                         ModerationLogPage, CallHistoryPage,
                         PinnedMessagesPage, MessageSearchPage,
                         EditPermissionsPage) instead of separate popup
                         windows. Remaining popups are InvitesWindow,
                         CallWindow (a non-modal call HUD),
                         ScreenShareViewerWindow, ToastNotificationWindow,
                         and UpdateGateWindow (pre-login update check).
                         Shared controls: AvatarView, VoiceSettingsPanel
  Services/              ApiService (REST), SignalRService (real-time),
                         VoiceService + MicCaptureSource/ScreenCaptureSource/
                         ScreenAudioCaptureSource (LiveKit audio/video,
                         noise suppression, screen share), NoiseSuppressionProcessor
                         (RNNoise/NSNet2 + GPU backend + Silero VAD pre-roll
                         gate), GpuDeviceService, E2eeService, IdleDetector,
                         SelfUpdateService
  Models/                Client-side DTOs
  installer/             Inno Setup script for the Windows installer
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

## Tech stack

- **Server**: ASP.NET Core 8, EF Core + Npgsql (Postgres), SignalR, JWT bearer
  auth, BCrypt, Serilog (console + Postgres sink)
- **Client**: WPF (.NET 8), WPF-UI (Fluent Design), NAudio (device/loopback
  capture), LiveKit's .NET client SDK, Windows.Graphics.Capture (screen share)
- **Voice/video**: self-hosted LiveKit SFU; RNNoise / NSNet2 (ONNX Runtime,
  optionally DirectML GPU-accelerated) for noise suppression, with a
  Silero VAD (ONNX Runtime) pre-gate
- **Encryption**: ECDH (P-256) + HKDF + AES-256-GCM, entirely client-side
- **Deployment**: server on [Railway](https://railway.app/) with a managed
  Postgres add-on; client installer/ZIP hosted as GitHub Releases

## Contributing

This started as a personal project, so there's no formal contribution process
yet — issues and PRs are welcome regardless.
