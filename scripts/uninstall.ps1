# Lepas CodexBar yang dipasang lewat scripts\install.ps1: hentikan aplikasinya, hapus folder
# program, shortcut Start Menu / desktop, dan jalan-saat-login. Pengaturan, riwayat, dan model
# suara di %LOCALAPPDATA%\CodexBar tetap disimpan, kecuali dengan -RemoveData.

param(
    [switch]$RemoveData,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\CodexBar")
)

$ErrorActionPreference = "Stop"

Get-Process CodexBar.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$run = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CodexBar" -ErrorAction SilentlyContinue
if ($run) {
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CodexBar"
    Write-Host "Jalan saat login: dimatikan"
}

foreach ($folder in @([Environment]::GetFolderPath("Programs"), [Environment]::GetFolderPath("Desktop"))) {
    $link = Join-Path $folder "CodexBar.lnk"
    if (Test-Path -LiteralPath $link) {
        Remove-Item -LiteralPath $link -Force
        Write-Host "Shortcut dihapus: $link"
    }
}

$exe = Join-Path $InstallDir "CodexBar.App.exe"
if (Test-Path -LiteralPath $exe) {
    Remove-Item -LiteralPath $exe -Force
    if (-not (Get-ChildItem -LiteralPath $InstallDir -Force)) {
        Remove-Item -LiteralPath $InstallDir -Force
    }

    Write-Host "Dihapus: $InstallDir"
}

if ($RemoveData) {
    $data = Join-Path $env:LOCALAPPDATA "CodexBar"
    if (Test-Path -LiteralPath $data) {
        Remove-Item -LiteralPath $data -Recurse -Force
        Write-Host "Data pengguna dihapus: $data"
    }
}
