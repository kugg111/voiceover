# Voiceover

A Discord-style chat app in C#: an ASP.NET Core server (REST API + SignalR)
and a WPF desktop client. Supports servers, roles, invites, text channels,
direct messages, file attachments, and mesh WebRTC voice chat.

## Prerequisites

- .NET 8 SDK: https://dotnet.microsoft.com/download
- Visual Studio 2022 (recommended, for the WPF designer) or VS Code + C# Dev Kit
- Windows 10/11 (the client uses Windows-only audio APIs for voice chat)

## Project layout

```
Voiceover.sln
Server/              ASP.NET Core Web API + SignalR
  Models/            EF Core entities (User, GuildServer, Channel, Message,
                      Membership, Invite, DirectMessage)
  Data/              AppDbContext
  Services/          PermissionService (role checks)
  Controllers/       Auth, Servers, Channels, Messages, Invites, Users,
                      DirectMessages, Upload
  Hubs/              ChatHub - messages, typing, DMs, voice presence + WebRTC signaling
  Auth/              JWT token issuing/validation
Client/              WPF desktop app
  Views/             LoginWindow, MainWindow, MembersWindow, DirectMessageWindow
  Services/          ApiService (REST), SignalRService (real-time), VoiceService (WebRTC audio)
  Models/            Client-side DTOs
```

## Running it

1. Open `Voiceover.sln` in Visual Studio, or work from the CLI.
2. **Restore packages**: `dotnet restore` at the solution root.
3. **Run the server**:
   ```
   cd Server
   dotnet run
   ```
   Starts on `http://localhost:5220`, auto-creates a SQLite DB
   (`voiceover.db`) on first run, and serves uploaded files from `/uploads`.
4. **Run the client** (separate terminal, or set as startup project in VS):
   ```
   cd Client
   dotnet run
   ```
5. Register, create a server, invite a friend (or run a second `dotnet run`
   and register a second account to test locally).

## What's implemented

- **Auth**: register/login with JWT, hashed passwords (BCrypt)
- **Servers & channels**: create servers, text/voice channels
- **Roles & permissions**: Owner/Moderator/Member; only owners/mods can create
  channels, kick members, delete others' messages; only the owner can change roles
- **Invites**: generate shareable codes (with optional expiry/use limits), join via code
- **Members panel**: view members, kick, generate invites
- **Real-time text chat**: SignalR-backed messaging, typing indicators, persisted history
- **Direct messages**: search users by username, 1:1 real-time DMs
- **File/image attachments**: upload (15 MB cap, allow-listed extensions) and
  share in channel messages
- **Voice chat**: mesh WebRTC audio via SIPSorcery - each pair of participants
  in a voice channel negotiates a direct peer connection, with SDP/ICE
  relayed through the SignalR hub. Works well for small voice channels; a
  large channel would need a media server (SFU) instead of mesh, which is a
  bigger project.
- **Client reconnection handling**: SignalR auto-reconnect with a status
  banner ("Reconnecting...", "Disconnected") in the message header

## Important note on the voice feature

The voice code (`Client/Services/VoiceService.cs`) is written against the
current SIPSorcery + SIPSorceryMedia.Windows APIs (verified via their GitHub
examples), but **it hasn't been compiled or run** - this sandbox has no .NET
SDK. A few things to know:
- **Package versions are pinned to the 8.0.x line** (`Client.csproj`), since
  the newer 10.x releases require the .NET 10 SDK. If you have .NET 10
  installed and want the latest packages, bump both `SIPSorcery` and
  `SIPSorceryMedia.Windows` to `10.0.x` and change the `TargetFramework` in
  `Client.csproj` to `net10.0-windows10.0.17763.0`.
- If you hit build errors in `VoiceService.cs`, they'll most likely be around
  `WindowsAudioEndPoint.StartAudio()` / `.CloseAudio()` - some versions split
  this into `StartAudioSink()` / `StartAudioSource()` instead. IntelliSense on
  the object will show you what's actually available.

Everything else (server, REST API, text chat, DMs, invites, uploads) follows
stable, long-settled ASP.NET Core / SignalR APIs and is lower-risk.

## Natural next steps

- EF Core migrations instead of `EnsureCreated()` once the schema settles:
  `dotnet ef migrations add InitialCreate` (run locally with the EF CLI tool installed)
- Push-to-talk / mute toggle in the voice UI (currently always-on mic)
- Message editing, reactions, read receipts
- A media server (SFU) if you want voice channels with many simultaneous speakers
- Avatars, rich presence, server icons

## Config

- Server port/JWT key: `Server/appsettings.json`
- Client's server URL: `Client/App.xaml.cs` (`ApiBaseUrl`, `HubUrl` constants)
