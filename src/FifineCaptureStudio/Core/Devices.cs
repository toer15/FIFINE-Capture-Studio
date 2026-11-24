using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FifineCaptureStudio.Core;

public sealed record DShowDevice(string Id, string Name, string DevicePath, bool IsCaptureCard)
{
    public override string ToString() => Name;
}

public sealed record DisplayMode(int Width, int Height, int RefreshHz);

public sealed record DisplayInfo(int Index, string DeviceName, string Name, int X, int Y, int Width, int Height, int RefreshHz, IReadOnlyList<DisplayMode> Modes)
{
    public override string ToString() => Name;
}

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    public override string ToString() => Title;
}

/// <summary>
/// Real hardware enumeration. Video devices come from the DirectShow capture
/// filter categories in the registry (the same list FFmpeg's dshow input uses),
/// displays from the Win32 display APIs, and windows from the top-level window list.
/// </summary>
public static class Devices
{
    // DirectShow filter categories
    private static readonly Guid VideoInputDevice =
        new("860BB310-5D01-11D0-BD3B-00A0C911CE86");

    private static readonly Guid AudioInputDevice =
        new("33D9A762-90C8-11D0-BD43-00A0C911CE86");

    private static readonly string[] CaptureCardMarkers =
    {
        "fifine", "ampligame", "elgato", "avermedia", "cam link", "camlink", "capture", "hdmi",
        "magewell", "razer ripsaw", "evga xt", "cloneralliance", "ezcap", "live gamer", "usb video"
    };

    public static IReadOnlyList<DShowDevice> EnumerateVideoDevices()
    {
        var list = new List<DShowDevice>();
        foreach (var entry in EnumerateFilterCategory(VideoInputDevice))
        {
            list.Add(new DShowDevice(
                entry.Id,
                entry.Name,
                entry.Path,
                CaptureCardMarkers.Any(m => entry.Name.Contains(m, StringComparison.OrdinalIgnoreCase))));
        }

        // Stable order: likely capture cards first, then alphabetical.
        return list
            .OrderByDescending(d => d.IsCaptureCard)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<DShowDevice> EnumerateAudioDevices()
    {
        var list = new List<DShowDevice>();
        foreach (var entry in EnumerateFilterCategory(AudioInputDevice))
        {
            list.Add(new DShowDevice(
                entry.Id,
                entry.Name,
                entry.Path,
                CaptureCardMarkers.Any(m => entry.Name.Contains(m, StringComparison.OrdinalIgnoreCase))));
        }

        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<(string Id, string Name, string Path)> EnumerateFilterCategory(Guid category)
    {
        var results = new List<(string, string, string)>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cat = baseKey.OpenSubKey($@"SOFTWARE\Classes\CLSID\{{{category:D}}}\Instance");
            if (cat is null)
            {
                return results;
            }

            foreach (var id in cat.GetSubKeyNames())
            {
                try
                {
                    using var inst = cat.OpenSubKey(id);
                    if (inst is null)
                    {
                        continue;
                    }

                    var name = inst.GetValue("FriendlyName") as string;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var path = inst.GetValue("DevicePath") as string ?? string.Empty;
                    results.Add((id, name.Trim(), path));
                }
                catch
                {
                    // skip unreadable filters
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Device enumeration failed", ex);
        }

        return results;
    }

    public static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var displays = new List<DisplayInfo>();
        var index = 0;

        try
        {
            for (uint i = 0; ; i++)
            {
                var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref device, 0))
                {
                    break;
                }

                if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                {
                    continue;
                }

                var modes = new List<DisplayMode>();
                for (var modeIndex = 0; ; modeIndex++)
                {
                    var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    if (!EnumDisplaySettings(device.DeviceName, modeIndex, ref dm))
                    {
                        break;
                    }

                    if (dm.dmBitsPerPel >= 24 && dm.dmPelsWidth >= 640)
                    {
                        modes.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
                    }
                }

                var current = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                var hasCurrent = EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref current);

                var width = hasCurrent ? current.dmPelsWidth : modes.FirstOrDefault()?.Width ?? 1920;
                var height = hasCurrent ? current.dmPelsHeight : modes.FirstOrDefault()?.Height ?? 1080;
                var hz = hasCurrent ? current.dmDisplayFrequency : modes.FirstOrDefault()?.RefreshHz ?? 60;

                var x = hasCurrent ? current.dmPositionX : 0;
                var y = hasCurrent ? current.dmPositionY : 0;

                displays.Add(new DisplayInfo(
                    index,
                    device.DeviceName,
                    string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceName : device.DeviceString,
                    x,
                    y,
                    width,
                    height,
                    hz,
                    modes
                        .GroupBy(m => (m.Width, m.Height, m.RefreshHz))
                        .Select(g => g.Key)
                        .Select(k => new DisplayMode(k.Width, k.Height, k.RefreshHz))
                        .OrderByDescending(m => (long)m.Width * m.Height)
                        .ThenByDescending(m => m.RefreshHz)
                        .ToList()));

                index++;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Display enumeration failed", ex);
        }

        if (displays.Count == 0)
        {
            // Fall back to primary screen metrics so the UI is never empty.
            displays.Add(new DisplayInfo(0, "DISPLAY1", "Primary display", 0, 0, 1920, 1080, 60,
                new[] { new DisplayMode(1920, 1080, 60) }));
        }

        return displays;
    }

    public static IReadOnlyList<WindowInfo> EnumerateCapturableWindows()
    {
        var list = new List<WindowInfo>();
        var ownPid = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || GetWindowTextLength(hWnd) == 0)
            {
                return true;
            }

            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == ownPid)
            {
                return true;
            }

            string processName = string.Empty;
            try
            {
                processName = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                // process may have exited
            }

            if (processName is "explorer" or "ShellExperienceHost" or "StartMenuExperienceHost")
            {
                return true;
            }

            var rect = new RECT();
            if (GetWindowRect(hWnd, ref rect) && (rect.Right - rect.Left) < 200)
            {
                return true;
            }

            list.Add(new WindowInfo(hWnd, title, processName));
            return true;
        }, IntPtr.Zero);

        return list
            .GroupBy(w => w.Title)
            .Select(g => g.First())
            .OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, ref RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
