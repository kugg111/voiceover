# Voiceover

A Discord-style chat app: an ASP.NET Core server (REST API + SignalR) and a
WPF desktop client. Servers, roles, invites, text channels, direct messages,
friends, file attachments, and voice chat with real noise suppression and
online/away/offline presence.

**Live app:** [voiceover-app.hu](https://www.voiceover-app.hu/) (or the
[Railway subdomain](https://voiceover-production-c32a.up.railway.app/)) —
download the Windows installer or a portable ZIP from the landing page.

## Features

- **Auth**: register/login with JWT, passwords hashed with BCrypt
- **Servers & channels**: create servers, text and voice channels, drag-free
  reordering by position
- **Roles & permissions**: Owner/Moderator/Member — mods can create channels,
  kick members, delete others' messages; only the owner can change roles
- **Invites**: any member can generate a shareable invite code; a popup
  lists every active invite with one-click copy
- **Real-time text chat**: SignalR-backed messaging, typing indicators,
  persisted history, numeric unread badges
- **Direct messages & friends**: search users by username, send/accept
  friend requests, 1:1 real-time DMs
- **File/image attachments**: upload (8 MB cap, allow-listed extensions)
  and share in channel messages or DMs
- **Voice chat**: routed through a self-hosted [LiveKit](https://livekit.io/)
  SFU (not a peer mesh — scales to larger channels without an N² connection
  blowup). Includes mute, deafen, per-user volume, push-to-talk / push-to-mute
  (keyboard or mouse button), and a voice-activity mode
- **Noise suppression**: three selectable engines — WebRTC's Audio Processing
  Module, RNNoise, and [DeepFilterNet3](https://github.com/Rikorose/DeepFilterNet)
  (with an adjustable attenuation limit) — all real native denoisers, not a
  hand-rolled volume gate
- **Presence**: Online / Away / Offline status, Away triggered by real system
  idle detection, suppressed while you're in a voice call
- **Avatars & server icons**, toast/sound notifications gated on window focus
- **Self-updating client**: checks for new releases and installs them in the
  background, no manual download needed after the first install
- **Fluent Design UI** ([WPF-UI](https://github.com/lepoco/wpfui)) with a
  Discord-inspired dark theme

## Architecture

```
Client (WPF, Windows)  <-- REST + SignalR -->  Server (ASP.NET Core)  <--> Postgres
        |                                              |
        `---------------- LiveKit SFU <---- join tokens minted by Server
```

The server never touches voice media itself — it only authenticates users,
persists app data, and mints short-lived LiveKit join tokens; audio flows
directly between clients and the LiveKit deployment.

## Project layout

```
Voiceover.sln
Server/                 ASP.NET Core Web API + SignalR
  Models/               EF Core entities (User, GuildServer, Channel, Message,
                         Membership, Invite, DirectMessage, Friendship)
  Data/                 AppDbContext + migrations
  Services/             PermissionService, PresenceService, VoicePresenceService,
                         LiveKitTokenService
  Controllers/          Auth, Servers, Channels, Messages, Invites, Users,
                         Friends, DirectMessages, Upload
  Hubs/ChatHub.cs        messages, typing, DMs, voice/presence signaling
  Auth/                  JWT token issuing/validation
  Site/                  Public landing page (served at the app's root URL)
Client/                  WPF desktop app
  Views/                 LoginWindow, RegisterWindow, MainWindow, SettingsWindow,
                         InvitesWindow, and shared controls (AvatarView, dialogs)
  Services/              ApiService (REST), SignalRService (real-time),
                         VoiceService + MicCaptureSource (LiveKit audio,
                         noise suppression), IdleDetector, SelfUpdateService
  Models/                Client-side DTOs
  installer/             Inno Setup script for the Windows installer
```

## Running it locally

1. **.NET 8 SDK**, Windows 10/11 (the client uses Windows-only audio APIs).
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
   Everything else works without this — only actually joining a voice channel
   needs it, and fails with a clear error if it's missing.
4. **Run the server**: `dotnet run` from `Server/` — applies EF Core
   migrations automatically on startup, listens on `http://localhost:5220`.
5. **Run the client**: `dotnet run` from `Client/` (or set as the startup
   project in Visual Studio). Point it at your local server by editing the
   `ApiBaseUrl`/`HubUrl` constants in `Client/App.xaml.cs`.
6. Register an account, create a server, and invite a friend — or run a
   second client instance and register a second account to test locally.

## Tech stack

- **Server**: ASP.NET Core 8, EF Core + Npgsql (Postgres), SignalR, JWT bearer
  auth, BCrypt
- **Client**: WPF (.NET 8), WPF-UI (Fluent Design), NAudio (device capture),
  LiveKit's .NET client SDK
- **Voice**: self-hosted LiveKit SFU; WebRTC APM / RNNoise / DeepFilterNet3
  for noise suppression (the last driven through a small LADSPA host, since
  there's no native .NET package for it)
- **Deployment**: server on [Railway](https://railway.app/) with a managed
  Postgres add-on; client installer/ZIP hosted as GitHub Releases

## Contributing

This started as a personal project, so there's no formal contribution process
yet — issues and PRs are welcome regardless.
