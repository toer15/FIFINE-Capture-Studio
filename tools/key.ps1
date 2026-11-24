param([string]$Keys = "{ESC}", [string]$TitleLike = "Preview")

Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class KeySend {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    public static IntPtr Find(uint pid, string like) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            var sb = new StringBuilder(300); GetWindowText(h, sb, 300);
            if (p == pid && IsWindowVisible(h) && sb.ToString().Contains(like)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$proc = Get-Process FifineCaptureStudio -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "FIFINE Capture Studio is not running"; exit 1 }

$hwnd = [KeySend]::Find([uint32]$proc.Id, $TitleLike)
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "Window containing '$TitleLike' not found"; exit 1 }

[KeySend]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500
$fg = [KeySend]::GetForegroundWindow()
Write-Host "target=$hwnd foreground=$fg match=$($fg -eq $hwnd)"
[System.Windows.Forms.SendKeys]::SendWait($Keys)
