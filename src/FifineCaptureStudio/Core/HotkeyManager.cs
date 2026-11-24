using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FifineCaptureStudio.Core;

/// <summary>Registers real global hotkeys (Win32 RegisterHotKey) for capture actions.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly Dictionary<int, string> _bindings = new();
    private HwndSource? _source;
    private IntPtr _handle = IntPtr.Zero;
    private int _nextId = 0x4A00;

    public event Action<string>? Pressed;
    public IReadOnlyList<string> Failures => _failures;
    private readonly List<string> _failures = new();

    public void Attach(Window window)
    {
        if (_handle != IntPtr.Zero)
        {
            return;
        }

        void Hook()
        {
            _handle = new WindowInteropHelper(window).Handle;
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WndProc);
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Hook();
        }
        else
        {
            window.SourceInitialized += (_, _) => Hook();
        }
    }

    public void RegisterAll(HotkeySettings settings)
    {
        Clear();
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        Add("record.toggle", settings.ToggleRecording);
        Add("record.pause", settings.PauseRecording);
        Add("replay.save", settings.SaveReplay);
        Add("mic.mute", settings.MuteMicrophone);
        Add("preview.toggle", settings.TogglePreview);
    }

    private void Add(string action, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return;
        }

        if (!TryParse(gesture, out var modifiers, out var key))
        {
            _failures.Add($"{gesture} (unrecognised)");
            return;
        }

        var id = _nextId++;
        if (RegisterHotKey(_handle, id, modifiers | ModNoRepeat, key))
        {
            _bindings[id] = action;
        }
        else
        {
            _failures.Add(gesture);
            Log.Warn($"Hotkey {gesture} could not be registered (already in use).");
        }
    }

    public void Clear()
    {
        foreach (var id in _bindings.Keys)
        {
            try { UnregisterHotKey(_handle, id); } catch { }
        }

        _bindings.Clear();
        _failures.Clear();
        _nextId = 0x4A00;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _bindings.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try
            {
                Pressed?.Invoke(action);
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey handler failed", ex);
            }
        }

        return IntPtr.Zero;
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? keyName = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    keyName = part;
                    break;
            }
        }

        if (keyName is null)
        {
            return false;
        }

        if (!Enum.TryParse<Key>(NormaliseKeyName(keyName), true, out var key))
        {
            return false;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return false;
        }

        virtualKey = (uint)vk;
        return true;
    }

    private static string NormaliseKeyName(string name) => name.ToUpperInvariant() switch
    {
        "SPACE" => "Space",
        "ENTER" or "RETURN" => "Return",
        "ESC" or "ESCAPE" => "Escape",
        "TAB" => "Tab",
        "PRTSC" or "PRINTSCREEN" => "PrintScreen",
        "BACKSPACE" => "Back",
        "INS" or "INSERT" => "Insert",
        "DEL" or "DELETE" => "Delete",
        "HOME" => "Home",
        "END" => "End",
        "PGUP" => "PageUp",
        "PGDN" => "PageDown",
        _ => name.Length == 1 ? name.ToUpperInvariant() : name.ToUpperInvariant() switch
        {
            var s when s.StartsWith("F") && int.TryParse(s[1..], out var n) && n is >= 1 and <= 24 => "F" + n,
            _ => name
        }
    };

    public void Dispose()
    {
        Clear();
        try { _source?.RemoveHook(WndProc); } catch { }
        _source = null;
        _handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
