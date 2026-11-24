param([int]$X, [int]$Y, [string]$Out = "")

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeClick {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$proc = Get-Process | Where-Object { $_.MainWindowTitle -like "*FIFINE*" } | Select-Object -First 1
if (-not $proc) { throw "FIFINE window not found" }

$rect = New-Object NativeClick+RECT
[NativeClick]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null

# The app is DPI aware; use the real window rect and the capture scale (1600x920 shot).
$screenX = $rect.Left + $X
$screenY = $rect.Top + $Y

[NativeClick]::SetCursorPos($screenX, $screenY) | Out-Null
Start-Sleep -Milliseconds 150
[NativeClick]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
[NativeClick]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 1200

if ($Out) {
    & "$PSScriptRoot\screenshot.ps1" -Out $Out | Out-Null
    Write-Output "clicked $X,$Y -> $Out"
} else {
    Write-Output "clicked $X,$Y"
}
