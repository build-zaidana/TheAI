# Build installer TheAI: publish self-contained, (opsional) tandatangani, lalu kompilasi installer Inno Setup.
#
# Code signing (optional; strongly recommended for public releases). Without it the build still
# works, but Windows SmartScreen shows "Unknown publisher" when the installer is run.
# Switch it on with ONE of these (secrets stay in environment variables, never in the repo):
#
#   Azure Artifact Signing (formerly Trusted Signing; individuals only in the USA and Canada):
#     $env:THEAI_TRUSTED_SIGNING_DLIB     = "C:\path\Azure.CodeSigning.Dlib.dll"
#     $env:THEAI_TRUSTED_SIGNING_METADATA = "C:\path\metadata.json"   # Endpoint, CodeSigningAccountName, CertificateProfileName
#     (sign in first with `az login`; see https://learn.microsoft.com/azure/trusted-signing/)
#
#   A code signing certificate (.pfx):
#     $env:THEAI_PFX = "C:\path\certificate.pfx"; $env:THEAI_PFX_PASSWORD = "..."
#     (CODEXBAR_PFX / CODEXBAR_PFX_PASSWORD are still accepted)
#
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

function Find-SignTool {
    $signtool = Find-Tool @() "signtool"
    if (-not $signtool) {
        $found = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "x64" } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { $signtool = $found.FullName }
    }
    if (-not $signtool) { throw "signtool not found (Windows SDK); signing is configured, so it is not skipped." }
    return $signtool
}

function Invoke-Sign([string]$file) {
    $pfx = if ($env:THEAI_PFX) { $env:THEAI_PFX } else { $env:CODEXBAR_PFX }
    $pfxPassword = if ($env:THEAI_PFX_PASSWORD) { $env:THEAI_PFX_PASSWORD } else { $env:CODEXBAR_PFX_PASSWORD }
    $trusted = $env:THEAI_TRUSTED_SIGNING_DLIB -and $env:THEAI_TRUSTED_SIGNING_METADATA
    if (-not $trusted -and -not $pfx) {
        Write-Warning "Not signed: $file (set THEAI_TRUSTED_SIGNING_* or THEAI_PFX to sign)."
        return
    }

    $signtool = Find-SignTool
    Write-Host "Signing $file"
    if ($trusted) {
        foreach ($path in $env:THEAI_TRUSTED_SIGNING_DLIB, $env:THEAI_TRUSTED_SIGNING_METADATA) {
            if (-not (Test-Path -LiteralPath $path)) { throw "Not found: $path" }
        }
        & $signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 `
            /dlib $env:THEAI_TRUSTED_SIGNING_DLIB /dmdf $env:THEAI_TRUSTED_SIGNING_METADATA $file
    }
    else {
        if (-not (Test-Path -LiteralPath $pfx)) { throw "Certificate not found: $pfx" }
        if (-not $pfxPassword) { throw "THEAI_PFX_PASSWORD is required with THEAI_PFX." }
        & $signtool sign /fd SHA256 /f $pfx /p $pfxPassword /tr http://timestamp.digicert.com /td SHA256 $file
    }
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
    & $signtool verify /pa /v $file
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $file" }
}

# 1) Publish
& (Join-Path $PSScriptRoot "publish.ps1") -Runtime $Runtime
$exe = Join-Path $repoRoot "artifacts\release\$Runtime\TheAI.exe"
if (-not (Test-Path $exe)) { throw "Publish tidak menghasilkan $exe" }

# 2) Sign exe (opsional)
Invoke-Sign $exe

# 2b) The portable version: the same signed exe in a zip, for people who don't want an installer.
$projectXmlEarly = [xml](Get-Content (Join-Path $repoRoot "src\CodexBar.App\CodexBar.App.csproj"))
$portableVersion = ([string]$projectXmlEarly.Project.PropertyGroup.Version).Trim()
$portableDir = Join-Path $env:TEMP ("TheAI-portable-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force (Join-Path $portableDir "Fonts") | Out-Null
Copy-Item $exe $portableDir
$ofl = Join-Path $repoRoot "artifacts\release\$Runtime\Fonts\OFL.txt"
if (Test-Path $ofl) { Copy-Item $ofl (Join-Path $portableDir "Fonts") }
$lucide = Join-Path $repoRoot "artifacts\release\$Runtime\Licenses\Lucide-LICENSE.txt"
if (Test-Path $lucide) {
    New-Item -ItemType Directory -Force (Join-Path $portableDir "Licenses") | Out-Null
    Copy-Item $lucide (Join-Path $portableDir "Licenses")
}
@"
TheAI $portableVersion - portable

Run TheAI.exe. Nothing is installed: no Start menu entry, no uninstaller.
Settings and conversations are kept in %LOCALAPPDATA%\TheAI (delete that folder to remove them).
To start TheAI when you sign in, turn it on in TheAI's Settings.

https://build-zaidana.github.io/theai-app/
"@ | Set-Content (Join-Path $portableDir "README.txt") -Encoding ascii
New-Item -ItemType Directory -Force (Join-Path $repoRoot "artifacts\installer") | Out-Null
$zip = Join-Path $repoRoot "artifacts\installer\TheAI-$portableVersion-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $portableDir -Recurse -Force
Write-Host "Portable zip: $zip"

# 3) Kompilasi installer dgn Inno Setup (ISCC)
$isccCandidates = @(
    $env:THEAI_ISCC,
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

& $iscc "/DMyAppVersion=$version" (Join-Path $repoRoot "installer\TheAI.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC gagal dengan exit code $LASTEXITCODE." }

# 4) Sign installer (opsional)
$setup = Get-ChildItem (Join-Path $repoRoot "artifacts\installer") -Filter "TheAI-Setup-*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) {
    Invoke-Sign $setup.FullName
    $hash = (Get-FileHash -Algorithm SHA256 $setup.FullName).Hash.ToLowerInvariant()
    Set-Content -Path ($setup.FullName + ".sha256") -Value "$hash  $($setup.Name)" -Encoding ascii -NoNewline
    Write-Host "Installer ready: $($setup.FullName)"
    Write-Host "SHA-256: $hash (also in $($setup.Name).sha256 - upload both to the release)"
}
