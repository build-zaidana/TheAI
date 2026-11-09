# Publish a TheAI release to the public repository (README, website, release files only - the
# source stays in this private repository).
#
#   1. Builds the installer (scripts\build-installer.ps1; signs it when THEAI_* signing variables are set)
#      and its .sha256, and checks the file still matches it.
#   2. Creates github.com/<Repo> as a public repository the first time, and turns on GitHub Pages
#      from its /docs folder (the website).
#   3. Copies publish\theai-app into it, points its download buttons straight at this version's
#      installer, and pushes.
#   4. Creates the release v<version> with the CHANGELOG section and the SHA-256 as notes, and uploads
#      the installer, its .sha256 and the portable zip.
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
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Windows PowerShell turns a program's stderr into a terminating error under "Stop", even when it
# is redirected, so "repo not found" would end the script. These look at the exit code instead.
function Test-Native([scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command *> $null
        return $LASTEXITCODE -eq 0
    }
    finally { $ErrorActionPreference = $previous }
}

function Invoke-Native([string]$Failure, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command
        if ($LASTEXITCODE -ne 0) { throw $Failure }
    }
    finally { $ErrorActionPreference = $previous }
}

$project = [xml](Get-Content (Join-Path $root "src\CodexBar.App\CodexBar.App.csproj"))
$version = [string]$project.Project.PropertyGroup.Version
$version = $version.Trim()
if (-not $version) { throw "No <Version> in CodexBar.App.csproj." }
$tag = "v$version"

# Before the long build: what would stop this publish anyway.
if ($Confirm) {
    if (-not (Test-Native { & $gh auth status })) { throw "The GitHub CLI isn't signed in. Run: gh auth login --web --git-protocol https" }
    if (Test-Native { & $gh release view $tag --repo $Repo }) { throw "$tag already exists in $Repo. Bump <Version> first." }
}

# Release notes: this version's section of the CHANGELOG.
$changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw -Encoding UTF8
$match = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($version))\b.*?(?=^## |\z)")
if (-not $match.Success) { throw "CHANGELOG.md has no '## $version' section." }
$notes = ($match.Value -split "`n", 2)[1].Trim()

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build-installer.ps1")
}

$setupName = "TheAI-Setup-$version.exe"
$setup = Join-Path $root "artifacts\installer\$setupName"
$checksum = "$setup.sha256"
$portable = Join-Path $root "artifacts\installer\TheAI-$version-portable.zip"
foreach ($file in $setup, $checksum, $portable) {
    if (-not (Test-Path $file)) { throw "Missing $file (build the installer first)." }
}

# The file people download has to be the one the checksum describes, and built from this code.
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not (Get-Content $checksum -Raw).ToLowerInvariant().StartsWith($hash)) { throw "$setupName doesn't match its .sha256. Build the installer again." }
if ($SkipBuild) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $lastCode = & git -C $root log -1 --format=%ct -- src installer
    $ErrorActionPreference = $previous
    $built = [DateTimeOffset]::new((Get-Item $setup).LastWriteTimeUtc).ToUnixTimeSeconds()
    if ($lastCode -and [long]$lastCode -gt $built) { throw "$setupName is older than the last code change. Run without -SkipBuild." }
}

$owner, $name = $Repo.Split('/')
$site = "https://$owner.github.io/$name/"
$direct = "https://github.com/$Repo/releases/download/$tag/$setupName"
$size = [math]::Round((Get-Item $setup).Length / 1MB)
$notes = "$notes`n`n---`n`n**Install:** download ``$setupName``. It installs for your user only. If Windows shows ""Windows protected your PC"", choose **More info, Run anyway**.`n`n**SHA-256** of ``$setupName``:`n`n``````text`n$hash`n```````n"
$notesFile = Join-Path $env:TEMP "theai-release-notes-$version.md"
[System.IO.File]::WriteAllText($notesFile, $notes, $utf8)

Write-Host ""
Write-Host "Release $tag -> https://github.com/$Repo"
Write-Host "  installer : $setup ($size MB)"
Write-Host "  sha256    : $hash"
Write-Host "  portable  : $portable"
Write-Host "  download  : $direct"
Write-Host "  notes     : $notesFile"
if (-not $Confirm) {
    Write-Host ""
    Write-Host "Dry run. Run again with -Confirm to publish."
    return
}

# 1) The public repository (once) and its website.
if (-not (Test-Native { & $gh repo view $Repo })) {
    Invoke-Native "Couldn't create $Repo." { & $gh repo create $Repo --public --description "TheAI - a notch for Windows: AI agents, builds, music and your day at the top of the screen." --homepage $site }
}
Invoke-Native "Couldn't set git up with the GitHub CLI." { & $gh auth setup-git }

# 2) Its content: README, license, website, issue templates.
$work = Join-Path $env:TEMP ("theai-app-" + [Guid]::NewGuid().ToString("N"))
Invoke-Native "git clone failed." { & git clone "https://github.com/$Repo.git" $work }
Push-Location $work
try {
    Get-ChildItem -Force | Where-Object Name -ne ".git" | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $root "publish\theai-app\*") -Destination $work -Recurse -Force
    Copy-Item -Path (Join-Path $root "publish\theai-app\.github") -Destination $work -Recurse -Force
    New-Item -ItemType File -Force (Join-Path $work "docs\.nojekyll") | Out-Null

    # The buttons go straight to this version's installer, so a click starts the download.
    $page = Join-Path $work "docs\index.html"
    $html = [System.IO.File]::ReadAllText($page)
    $buttons = [regex]::Matches($html, 'href="[^"]+/releases/latest" data-download').Count
    if ($buttons -lt 1) { throw "No download buttons found in the website." }
    $html = [regex]::Replace($html, 'href="[^"]+/releases/latest" data-download', "href=""$direct"" data-download")
    $html = $html.Replace("<!--release-->", " &middot; $tag &middot; $size MB")
    [System.IO.File]::WriteAllText($page, $html, $utf8)
    $readme = Join-Path $work "README.md"
    $text = [System.IO.File]::ReadAllText($readme)
    $text = [regex]::Replace($text, '\[Download for Windows\]\([^)]+\)', "[Download for Windows]($direct)")
    [System.IO.File]::WriteAllText($readme, $text, $utf8)

    Invoke-Native "git add failed." { & git add -A }
    if (-not (Test-Native { & git diff --cached --quiet })) {
        Invoke-Native "git commit failed." { & git commit -m "TheAI $version" }
        Invoke-Native "git branch failed." { & git branch -M main }
        Invoke-Native "git push failed." { & git push -u origin main }
    }
}
finally {
    Pop-Location
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

# GitHub Pages from main:/docs (when it's already on, turning it on just fails).
Test-Native { & $gh api -X POST "repos/$Repo/pages" -f "source[branch]=main" -f "source[path]=/docs" } | Out-Null
if (-not (Test-Native { & $gh api "repos/$Repo/pages" })) {
    Write-Warning "GitHub Pages isn't on: the website won't be live. Turn it on in the repository's Settings > Pages (main, /docs)."
}

# 3) The release.
Invoke-Native "Creating the release failed." { & $gh release create $tag $setup $checksum $portable --repo $Repo --title "TheAI $version" --notes-file $notesFile }

Write-Host ""
Write-Host "Published: https://github.com/$Repo/releases/tag/$tag"
Write-Host "Website (a minute or two): $site"
