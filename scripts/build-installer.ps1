# Build installer CodexBar: publish self-contained, (opsional) tandatangani, lalu kompilasi installer Inno Setup.
#
# Code signing (opsional, sangat disarankan untuk distribusi):
#   Set env var CODEXBAR_PFX dan CODEXBAR_PFX_PASSWORD untuk menandatangani exe + installer
#   memakai signtool (bagian dari Windows SDK). Tanpa itu, build tetap jalan namun
#   Windows SmartScreen akan menampilkan peringatan "Unknown publisher".
#
#   $env:CODEXBAR_PFX = "C:\path\ke\sertifikat.pfx"
#   $env:CODEXBAR_PFX_PASSWORD = "rahasia"
#   .\scripts\build-installer.ps1

param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [switch]$AllowPublishOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Find-Tool([string[]]$candidates, [string]$onPath) {
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command $onPath -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Invoke-Sign([string]$file) {
    if (-not $env:CODEXBAR_PFX) { return }
    if (-not (Test-Path -LiteralPath $env:CODEXBAR_PFX)) {
        throw "CODEXBAR_PFX tidak ditemukan: $($env:CODEXBAR_PFX)"
    }
    if (-not $env:CODEXBAR_PFX_PASSWORD) {
        throw "CODEXBAR_PFX_PASSWORD wajib diisi saat CODEXBAR_PFX digunakan."
    }
    $signtool = Find-Tool @() "signtool"
    if (-not $signtool) {
        $found = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "x64" } | Select-Object -First 1
        if ($found) { $signtool = $found.FullName }
    }
    if (-not $signtool) { throw "signtool tidak ditemukan; signing tidak boleh dilewati saat CODEXBAR_PFX diatur." }
    Write-Host "Menandatangani $file"
    & $signtool sign /fd SHA256 /f $env:CODEXBAR_PFX /p $env:CODEXBAR_PFX_PASSWORD `
        /tr https://timestamp.digicert.com /td SHA256 $file
    if ($LASTEXITCODE -ne 0) { throw "signtool gagal untuk $file" }
    & $signtool verify /pa /v $file
    if ($LASTEXITCODE -ne 0) { throw "Verifikasi tanda tangan gagal untuk $file" }
}

# 1) Publish
& (Join-Path $PSScriptRoot "publish.ps1") -Runtime $Runtime
$exe = Join-Path $repoRoot "artifacts\$Runtime\CodexBar.App.exe"
if (-not (Test-Path $exe)) { throw "Publish tidak menghasilkan $exe" }

# 2) Sign exe (opsional)
Invoke-Sign $exe

# 3) Kompilasi installer dgn Inno Setup (ISCC)
$isccCandidates = @(
    $env:CODEXBAR_ISCC,
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$iscc = Find-Tool $isccCandidates "ISCC"

if (-not $iscc) {
    if ($AllowPublishOnly) {
        Write-Warning "Inno Setup tidak ditemukan; hanya exe self-contained yang dibuat: $exe"
        return
    }
    throw "Inno Setup (ISCC.exe) tidak ditemukan. Pasang Inno Setup 6 atau gunakan -AllowPublishOnly."
}

$projectXml = [xml](Get-Content (Join-Path $repoRoot "src\CodexBar.App\CodexBar.App.csproj"))
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Versi aplikasi tidak ditemukan di CodexBar.App.csproj." }

& $iscc "/DMyAppVersion=$version" (Join-Path $repoRoot "installer\CodexBar.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC gagal dengan exit code $LASTEXITCODE." }

# 4) Sign installer (opsional)
$setup = Get-ChildItem (Join-Path $repoRoot "artifacts\installer") -Filter "CodexBar-Setup-*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) {
    Invoke-Sign $setup.FullName
    Write-Host "Installer siap: $($setup.FullName)"
}
