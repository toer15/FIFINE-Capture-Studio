param(
    [string]$Out = "shot.png",
    [string]$Title = "FIFINE Capture Studio"
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinShot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@

$hwnd = [WinShot]::FindWindow($null, $Title)
if ($hwnd -eq [IntPtr]::Zero) {
    $proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*FIFINE*" } | Select-Object -First 1
    if ($proc) { $hwnd = $proc.MainWindowHandle }
}
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Error "Window not found: $Title"
    exit 1
}

[WinShot]::ShowWindow($hwnd, 9) | Out-Null
[WinShot]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 700

$rect = New-Object WinShot+RECT
[WinShot]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) {
    Write-Error "Invalid window rect"
    exit 1
}

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [WinShot]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

$path = Join-Path (Get-Location) $Out
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $path ($w x $h) printwindow=$ok"
