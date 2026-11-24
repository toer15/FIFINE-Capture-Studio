param([string]$Out = "hover.png")

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class HoverShot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@

$proc = Get-Process FifineCaptureStudio -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "FIFINE Capture Studio is not running"; exit 1 }

$hwnd = $proc.MainWindowHandle
[HoverShot]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 400

$r = New-Object HoverShot+RECT
[HoverShot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top

# Aim at the middle of the preview surface (about 45% across, 40% down the window).
$px = $r.Left + [int]($w * 0.45)
$py = $r.Top + [int]($h * 0.40)
[HoverShot]::SetCursorPos($px, $py) | Out-Null
Start-Sleep -Milliseconds 150
[HoverShot]::SetCursorPos($px + 18, $py + 9) | Out-Null
Start-Sleep -Milliseconds 400

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [HoverShot]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

$path = Join-Path (Get-Location) $Out
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $path ($w x $h) at $px,$py printwindow=$ok"
