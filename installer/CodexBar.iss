; Inno Setup script untuk CodexBar.
; Build: scripts\build-installer.ps1 (mem-publish lalu memanggil ISCC).
; Butuh Inno Setup 6+ (https://jrsoftware.org/isdl.php).

#define MyAppName "CodexBar"
#ifndef MyAppVersion
  #define MyAppVersion "1.3.0"
#endif
#define MyAppPublisher "CodexBar Community"
#define MyAppExeName "CodexBar.App.exe"

[Setup]
; AppId unik — jangan diubah antar versi (dipakai untuk upgrade/uninstall).
AppId={{B6D7B6F2-9A3D-4C2E-9E7E-1C0A2B3C4D5E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Pasang per-user agar tidak butuh hak admin (mengurangi friksi & prompt UAC).
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\CodexBar
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\CodexBar.App\codexbar.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=CodexBar-Setup-{#MyAppVersion}
; Single-file .NET sudah dikompresi saat publish. ZIP menghindari kompresi ulang
; LZMA yang sangat lambat dengan penghematan ukuran yang nyaris tidak ada.
Compression=zip
SolidCompression=no
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
AppMutex=Local\CodexBar.SingleInstance

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Buat ikon desktop"; GroupDescription: "Tambahan:"
Name: "startup"; Description: "Jalankan CodexBar saat login"; GroupDescription: "Tambahan:"

[Files]
Source: "..\artifacts\win-x64\CodexBar.App.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "CodexBar"; ValueData: """{app}\{#MyAppExeName}"""; \
    Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Jalankan {#MyAppName} sekarang"; \
    Flags: nowait postinstall skipifsilent
