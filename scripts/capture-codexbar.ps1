param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\codexbar-layout.png")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class CodexBarCaptureNative
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
}
"@

$process = Get-Process CodexBar.App -ErrorAction Stop | Select-Object -First 1
$window = [IntPtr]::Zero
$callback = [CodexBarCaptureNative+EnumWindowsProc]{
    param([IntPtr]$handle, [IntPtr]$state)
    [uint32]$processId = 0
    [void][CodexBarCaptureNative]::GetWindowThreadProcessId($handle, [ref]$processId)
    if ($processId -eq $process.Id) {
        $rect = New-Object CodexBarCaptureNative+Rect
        if ([CodexBarCaptureNative]::GetWindowRect($handle, [ref]$rect) -and
            ($rect.Right - $rect.Left) -ge 500 -and ($rect.Bottom - $rect.Top) -gt 40) {
            $script:window = $handle
            return $false
        }
    }
    return $true
}
[void][CodexBarCaptureNative]::EnumWindows($callback, [IntPtr]::Zero)
if ($window -eq [IntPtr]::Zero) { throw "Window CodexBar tidak ditemukan." }

[void][CodexBarCaptureNative]::ShowWindow($window, 5)
Start-Sleep -Milliseconds 300
$rect = New-Object CodexBarCaptureNative+Rect
[void][CodexBarCaptureNative]::GetWindowRect($window, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $hdc = $graphics.GetHdc()
    try {
        if (-not [CodexBarCaptureNative]::PrintWindow($window, $hdc, 2)) {
            throw "PrintWindow gagal."
        }
    }
    finally { $graphics.ReleaseHdc($hdc) }
    $directory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Force $directory | Out-Null
    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output $OutputPath
