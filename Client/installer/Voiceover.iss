; Inno Setup script for the Voiceover client installer.
; Compile with: ISCC.exe Voiceover.iss
; Expects the self-contained publish output (dotnet publish, see REDEPLOY.txt)
; to already exist at ..\..\publish relative to this file.

#define MyAppName "Voiceover"
; Must move together with Client.csproj's <Version> and Server/Site/downloads/
; version.json on every release, see REDEPLOY.txt.
#define MyAppVersion "1.0.19"
#define MyAppPublisher "Voiceover"
#define MyAppExeName "Client.exe"
#define MyAppURL "https://voiceover-production-c32a.up.railway.app/"

[Setup]
AppId={{7E4A9B5C-2D1F-4A6E-9C3B-8F5D6A2E1B4C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
; Installs to the current user's profile (no admin/UAC prompt needed) -
; this is a small friends app, not something that needs a machine-wide
; install, and skipping the UAC prompt keeps the install flow simple.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\installer-output
OutputBaseFilename=Voiceover-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
