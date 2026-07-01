param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $repositoryRoot
$dotnet = $env:CODEXBAR_DOTNET

if ([string]::IsNullOrWhiteSpace($dotnet)) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
}

if (-not $dotnet) {
    $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
}

if (-not (Test-Path $dotnet)) {
    throw "dotnet SDK tidak ditemukan. Instal .NET 10 SDK terlebih dahulu."
}

$project = Join-Path $repositoryRoot "src\CodexBar.App\CodexBar.App.csproj"
$output = Join-Path $repositoryRoot "artifacts\$Runtime"

& $dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    --output $output

if ($LASTEXITCODE -ne 0) {
    throw "Publish gagal dengan exit code $LASTEXITCODE."
}

Write-Host "Build tersedia di $output"
