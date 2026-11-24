param([string]$Out = "preview.png")

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class PrevShot {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    public static IntPtr FindPreview(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            var sb = new StringBuilder(300); GetWindowText(h, sb, 300);
            if (p == pid && IsWindowVisible(h) && sb.ToString().Contains("Preview")) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$proc = Get-Process FifineCaptureStudio -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "FIFINE Capture Studio is not running"; exit 1 }

$hwnd = [PrevShot]::FindPreview([uint32]$proc.Id)
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "Fullscreen preview window not found"; exit 1 }

[PrevShot]::SetForegroundWindow($hwnd) | Out-Null
$r = New-Object PrevShot+RECT
[PrevShot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int](($r.Top + $r.Bottom) / 2)

# Jiggle the cursor inside the preview so the hover overlay fades in, then grab it at once.
[PrevShot]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 220
[PrevShot]::SetCursorPos($cx + 12, $cy + 6) | Out-Null
Start-Sleep -Milliseconds 320

$bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [PrevShot]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

$path = Join-Path (Get-Location) $Out
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $path ($($r.Right - $r.Left) x $($r.Bottom - $r.Top)) printwindow=$ok"
