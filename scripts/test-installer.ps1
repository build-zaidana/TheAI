param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath
)

$ErrorActionPreference = "Stop"
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$testRoot = Join-Path $env:TEMP ("CodexBar-installer-smoke-" + [Guid]::NewGuid().ToString("N"))
$installDir = Join-Path $testRoot "app"
$logPath = Join-Path $testRoot "install.log"
$app = $null
$childPid = $null
$uninstaller = $null

New-Item -ItemType Directory -Force $testRoot | Out-Null

try {
    $install = Start-Process -FilePath $installer -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/TASKS=",
        "/DIR=$installDir",
        "/LOG=$logPath"
    ) -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "Installer gagal dengan exit code $($install.ExitCode). Log: $logPath"
    }

    $appPath = Join-Path $installDir "CodexBar.App.exe"
    if (-not (Test-Path -LiteralPath $appPath)) {
        throw "Installer tidak menghasilkan CodexBar.App.exe."
    }
    $uninstaller = Get-ChildItem -LiteralPath $installDir -Filter "unins*.exe" |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $uninstaller) {
        throw "Uninstaller tidak dibuat."
    }

    $app = Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 200
        if ($app.HasExited) {
            throw "Aplikasi hasil install berhenti saat startup dengan exit code $($app.ExitCode)."
        }
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($app.Id)" -ErrorAction SilentlyContinue |
            Where-Object Name -eq "codex.exe" | Select-Object -First 1
    } while (-not $child -and [DateTimeOffset]::UtcNow -lt $deadline)

    if (-not $child) {
        throw "Aplikasi berjalan, tetapi child codex.exe tidak muncul dalam 20 detik."
    }
    $childPid = [int]$child.ProcessId

    Stop-Process -Id $app.Id -Force
    $app.WaitForExit(5000) | Out-Null
    $childDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while (Get-Process -Id $childPid -ErrorAction SilentlyContinue) {
        if ([DateTimeOffset]::UtcNow -ge $childDeadline) {
            throw "Child codex.exe $childPid menjadi orphan setelah aplikasi dihentikan."
        }
        Start-Sleep -Milliseconds 100
    }
    $app = $null

    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART"
    ) -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "Uninstaller gagal dengan exit code $($uninstall.ExitCode)."
    }
    $uninstaller = $null

    if (Test-Path -LiteralPath $appPath) {
        throw "Executable masih tersisa setelah uninstall."
    }
    $registration = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" `
        -ErrorAction SilentlyContinue | Where-Object DisplayName -eq "CodexBar"
    if ($registration) {
        throw "Registrasi uninstall CodexBar masih tersisa."
    }

    Write-Output "PASS installer install=true launch=true child=$childPid orphan=false uninstall=true"
}
finally {
    if ($app -and -not $app.HasExited) {
        Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    }
    if ($uninstaller -and (Test-Path -LiteralPath $uninstaller)) {
        Start-Process -FilePath $uninstaller -ArgumentList @(
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"
        ) -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $expectedRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\CodexBar-installer-smoke-'
        if ($resolved.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
