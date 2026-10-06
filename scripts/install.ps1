# Pasang CodexBar untuk user ini tanpa installer: salin exe hasil publish ke
# %LOCALAPPDATA%\Programs\CodexBar (folder yang sama dengan installer Inno Setup), buat
# shortcut Start Menu, dan opsional shortcut desktop + jalankan saat login. Tidak butuh admin.
#
#   .\scripts\install.ps1                      # pasang dari artifacts\win-x64
#   .\scripts\install.ps1 -Publish             # publish dulu, lalu pasang
#   .\scripts\install.ps1 -LaunchAtLogin -Desktop
#
# Lepas lagi dengan .\scripts\uninstall.ps1.

param(
    [switch]$Publish,
    [switch]$LaunchAtLogin,
    [switch]$Desktop,
    [switch]$NoStartMenu,
    [switch]$NoLaunch,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\CodexBar")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exeName = "CodexBar.App.exe"
$source = Join-Path $repoRoot "artifacts\win-x64\$exeName"

if ($Publish) {
    & (Join-Path $PSScriptRoot "publish.ps1")
}

if (-not (Test-Path -LiteralPath $source)) {
    throw "Belum ada build di $source. Jalankan dulu .\scripts\publish.ps1 atau pakai -Publish."
}

# The running copy holds the exe (and the single-instance mutex): stop it first.
Get-Process CodexBar.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$target = Join-Path $InstallDir $exeName
Copy-Item -LiteralPath $source -Destination $target -Force
Write-Host "Terpasang: $target"

function New-Shortcut([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = $InstallDir
    $link.IconLocation = "$target,0"
    $link.Description = "CodexBar"
    $link.Save()
    Write-Host "Shortcut: $path"
}

if (-not $NoStartMenu) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath("Programs")) "CodexBar.lnk")
}

if ($Desktop) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath("Desktop")) "CodexBar.lnk")
}

if ($LaunchAtLogin) {
    # The same Run value the app writes itself, plus the setting, so the app keeps it on.
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CodexBar" -Value "`"$target`""
    $settingsPath = Join-Path $env:LOCALAPPDATA "CodexBar\settings.json"
    if (Test-Path -LiteralPath $settingsPath) {
        $json = Get-Content -LiteralPath $settingsPath -Raw
        $json = $json -replace '"launchAtLogin"\s*:\s*false', '"launchAtLogin": true'
        [IO.File]::WriteAllText($settingsPath, $json, (New-Object Text.UTF8Encoding($false)))
    }
    Write-Host "Jalan saat login: aktif"
}
else {
    # A Run value from an older copy (e.g. the build folder) now points at the installed one.
    $run = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CodexBar" -ErrorAction SilentlyContinue
    if ($run) {
        Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CodexBar" -Value "`"$target`""
    }
}

if (-not $NoLaunch) {
    Start-Process -FilePath $target -WorkingDirectory $InstallDir
    Write-Host "CodexBar dijalankan dari $InstallDir"
}
