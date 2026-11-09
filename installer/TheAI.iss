; Inno Setup script for TheAI.
; Build: scripts\build-installer.ps1 (publishes, signs when configured, then calls ISCC).
; Needs Inno Setup 6+ (https://jrsoftware.org/isdl.php).

#define MyAppName "TheAI"
#ifndef MyAppVersion
  #define MyAppVersion "1.7.0"
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
Name: "id"; MessagesFile: "compiler:Default.isl,Indonesian.isl"

[CustomMessages]
en.DesktopIcon=Create a desktop icon
id.DesktopIcon=Buat ikon desktop
en.StartAtLogin=Start TheAI when I sign in
id.StartAtLogin=Jalankan TheAI saat login
en.Extras=Extras:
id.Extras=Tambahan:
en.LaunchNow=Start TheAI now
id.LaunchNow=Jalankan TheAI sekarang
en.RemoveData=Also delete your TheAI settings, conversations and history on this PC?%n%nChoose No to keep them for a later reinstall. Saved API keys stay in Windows Credential Manager either way.
id.RemoveData=Hapus juga pengaturan, percakapan, dan riwayat TheAI di PC ini?%n%nPilih No untuk menyimpannya kalau nanti dipasang lagi. API key yang tersimpan tetap ada di Windows Credential Manager.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Extras}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartAtLogin}"; GroupDescription: "{cm:Extras}"

[InstallDelete]
; What CodexBar 1.x left behind (its data in %LOCALAPPDATA%\CodexBar moves over on first start).
Type: filesandordirs; Name: "{localappdata}\Programs\CodexBar"
Type: files; Name: "{userprograms}\CodexBar.lnk"
Type: files; Name: "{userdesktop}\CodexBar.lnk"

[Files]
Source: "..\artifacts\release\win-x64\TheAI.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\release\win-x64\Fonts\OFL.txt"; DestDir: "{app}\Fonts"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\artifacts\release\win-x64\Licenses\Lucide-LICENSE.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CodexBar"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "TheAI"; ValueData: """{app}\{#MyAppExeName}"""; \
    Tasks: startup; Check: not IsInAppUpdate
; Whoever set it, the installer's checkbox or TheAI's own switch, uninstalling removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TheAI"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchNow}"; \
    Flags: nowait postinstall skipifsilent
; An update from inside TheAI (silent, /update=1): start the new version when it's done.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent; Check: IsInAppUpdate

[Code]
function IsInAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;

// Uninstalling asks whether to remove the data folder too (kept by default, and always kept when
// uninstalling silently).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    if MsgBox(ExpandConstant('{cm:RemoveData}'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\TheAI'), True, True, True);
  end;
end;
