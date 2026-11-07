# Publish a TheAI release to the public repository (README, website, release files only - the
# source stays in this private repository).
#
#   1. Builds the installer (scripts\build-installer.ps1; signs it when THEAI_* signing variables are set)
#      and its .sha256.
#   2. Creates github.com/<Repo> as a public repository the first time, and turns on GitHub Pages
#      from its /docs folder (the website).
#   3. Copies publish\theai-app into it and pushes.
#   4. Creates the release v<version> with the CHANGELOG section as notes and uploads the
#      installer, its .sha256 and the portable zip.
#
# Needs: the GitHub CLI signed in (`gh auth login`), Inno Setup 6, git.
# Nothing is published without -Confirm; without it the script only shows what it would do.
#
#   .\scripts\publish-release.ps1            # dry run
#   .\scripts\publish-release.ps1 -Confirm   # publish

param(
    [string]$Repo = "build-zaidana/theai-app",
    [switch]$Confirm,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw "GitHub CLI not found. Install it: winget install GitHub.cli" }

$project = [xml](Get-Content (Join-Path $root "src\CodexBar.App\CodexBar.App.csproj"))
$version = [string]$project.Project.PropertyGroup.Version
$version = $version.Trim()
if (-not $version) { throw "No <Version> in CodexBar.App.csproj." }
$tag = "v$version"

# Release notes: this version's section of the CHANGELOG.
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw -Encoding UTF8
$match = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($version))\b.*?(?=^## |\z)")
if (-not $match.Success) { throw "CHANGELOG.md has no '## $version' section." }
$notes = ($match.Value -split "`n", 2)[1].Trim()
$notesFile = Join-Path $env:TEMP "theai-release-notes-$version.md"
Set-Content -Path $notesFile -Value $notes -Encoding utf8

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build-installer.ps1")
}

$setup = Join-Path $root "artifacts\installer\TheAI-Setup-$version.exe"
$checksum = "$setup.sha256"
$portable = Join-Path $root "artifacts\installer\TheAI-$version-portable.zip"
foreach ($file in $setup, $checksum, $portable) {
    if (-not (Test-Path $file)) { throw "Missing $file (build the installer first)." }
}

Write-Host ""
Write-Host "Release $tag -> https://github.com/$Repo"
Write-Host "  installer : $setup"
Write-Host "  sha256    : $(Get-Content $checksum)"
Write-Host "  portable  : $portable"
Write-Host "  notes     : $notesFile"
if (-not $Confirm) {
    Write-Host ""
    Write-Host "Dry run. Run again with -Confirm to publish."
    return
}

& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "The GitHub CLI isn't signed in. Run: gh auth login --web --git-protocol https" }

# 1) The public repository (once) and its website.
& $gh repo view $Repo *> $null
if ($LASTEXITCODE -ne 0) {
    & $gh repo create $Repo --public --description "TheAI - a notch for Windows: AI agents, builds, music and your day at the top of the screen." --homepage "https://$($Repo.Split('/')[0]).github.io/$($Repo.Split('/')[1])/"
    if ($LASTEXITCODE -ne 0) { throw "Couldn't create $Repo." }
}

# 2) Its content: README, license, website, issue templates.
$work = Join-Path $env:TEMP ("theai-app-" + [Guid]::NewGuid().ToString("N"))
& git clone "https://github.com/$Repo.git" $work
if ($LASTEXITCODE -ne 0) { throw "git clone failed." }
Push-Location $work
try {
    Get-ChildItem -Force | Where-Object Name -ne ".git" | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $root "publish\theai-app\*") -Destination $work -Recurse -Force
    Copy-Item -Path (Join-Path $root "publish\theai-app\.github") -Destination $work -Recurse -Force
    New-Item -ItemType File -Force (Join-Path $work "docs\.nojekyll") | Out-Null
    & git add -A
    & git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) {
        & git commit -m "TheAI $version"
        & git branch -M main
        & git push -u origin main
        if ($LASTEXITCODE -ne 0) { throw "git push failed." }
    }
}
finally {
    Pop-Location
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

# GitHub Pages from main:/docs (already on: the call just fails harmlessly).
& $gh api -X POST "repos/$Repo/pages" -f "source[branch]=main" -f "source[path]=/docs" *> $null

# 3) The release.
& $gh release view $tag --repo $Repo *> $null
if ($LASTEXITCODE -eq 0) { throw "$tag already exists in $Repo. Bump <Version> first." }
& $gh release create $tag $setup $checksum $portable --repo $Repo --title "TheAI $version" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw "Creating the release failed." }

Write-Host ""
Write-Host "Published: https://github.com/$Repo/releases/tag/$tag"
Write-Host "Website (a minute or two): https://$($Repo.Split('/')[0]).github.io/$($Repo.Split('/')[1])/"
