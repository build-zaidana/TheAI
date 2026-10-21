; Inno Setup script for TheAI.
; Build: scripts\build-installer.ps1 (publishes, signs when configured, then calls ISCC).
; Needs Inno Setup 6+ (https://jrsoftware.org/isdl.php).

#define MyAppName "TheAI"
#ifndef MyAppVersion
  #define MyAppVersion "1.5.0"
#endif
#define MyAppPublisher "build-zaidana"
#define MyAppURL "https://build-zaidana.github.io/theai-app/"
#define MyAppExeName "TheAI.exe"

[Setup]
; Same AppId as CodexBar: installing TheAI upgrades an existing CodexBar instead of adding a second app.
AppId={{B6D7B6F2-9A3D-4C2E-9E7E-1C0A2B3C4D5E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://github.com/build-zaidana/theai-app/issues
AppUpdatesURL=https://github.com/build-zaidana/theai-app/releases
; Per user: no admin rights, no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\TheAI
; The old CodexBar folder is not reused: TheAI gets its own.
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\CodexBar.App\codexbar.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=TheAI-Setup-{#MyAppVersion}
; The bundle is published uncompressed (less memory at run time); the installer compresses it.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 and later.
MinVersion=10.0.17763
AppMutex=Local\TheAI.SingleInstance,Local\CodexBar.SingleInstance
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "id"; MessagesFile: "compiler:Languages\Indonesian.isl"

[CustomMessages]
en.DesktopIcon=Create a desktop icon
id.DesktopIcon=Buat ikon desktop
en.StartAtLogin=Start TheAI when I sign in
id.StartAtLogin=Jalankan TheAI saat login
en.Extras=Extras:
id.Extras=Tambahan:
en.LaunchNow=Start TheAI now
id.LaunchNow=Jalankan TheAI sekarang

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Extras}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartAtLogin}"; GroupDescription: "{cm:Extras}"

[InstallDelete]
; What CodexBar 1.x left behind (its data in %LOCALAPPDATA%\CodexBar moves over on first start).
Type: files; Name: "{localappdata}\Programs\CodexBar\CodexBar.App.exe"
Type: dirifempty; Name: "{localappdata}\Programs\CodexBar"
Type: files; Name: "{userprograms}\CodexBar.lnk"
Type: files; Name: "{userdesktop}\CodexBar.lnk"

[Files]
Source: "..\artifacts\win-x64\TheAI.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\win-x64\Fonts\OFL.txt"; DestDir: "{app}\Fonts"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CodexBar"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "TheAI"; ValueData: """{app}\{#MyAppExeName}"""; \
    Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchNow}"; \
    Flags: nowait postinstall skipifsilent
; An update from inside TheAI runs silently: start the new version when it's done.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent
