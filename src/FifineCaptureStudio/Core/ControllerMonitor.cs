using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FifineCaptureStudio.Core;

public enum ControllerKind
{
    None,
    Xbox,
    DualSense,
    DualShock
}

public sealed class ControllerState
{
    public bool Connected { get; set; }
    public string Name { get; set; } = "No controller";
    public string Transport { get; set; } = string.Empty;
    public ControllerKind Kind { get; set; }

    /// <summary>Sticks normalised to -1..1 with Y up.</summary>
    public float LeftX { get; set; }
    public float LeftY { get; set; }
    public float RightX { get; set; }
    public float RightY { get; set; }

    /// <summary>Triggers normalised to 0..1.</summary>
    public float LeftTrigger { get; set; }
    public float RightTrigger { get; set; }

    public bool A { get; set; }
    public bool B { get; set; }
    public bool X { get; set; }
    public bool Y { get; set; }
    public bool Up { get; set; }
    public bool Down { get; set; }
    public bool Left { get; set; }
    public bool Right { get; set; }
    public bool LeftBumper { get; set; }
    public bool RightBumper { get; set; }
    public bool LeftStickButton { get; set; }
    public bool RightStickButton { get; set; }
    public bool Start { get; set; }
    public bool Back { get; set; }
    public bool Guide { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.MinValue;
    public double PollRateHz { get; set; }

    public ControllerState Clone() => (ControllerState)MemberwiseClone();
}

/// <summary>
/// Reads controllers that Windows exposes. Xbox pads go through XInput; Sony pads
/// (DualSense / DualShock 4) are parsed from their raw HID input reports. Input is
/// only observed - it is never injected or routed through the capture pipeline, so
/// monitoring cannot add latency to gameplay.
/// </summary>
public sealed class ControllerMonitor : IDisposable
{
    private static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private readonly object _gate = new();
    private readonly ControllerState _state = new();
    private Timer? _xinputTimer;
    private Thread? _hidThread;
    private volatile bool _running;
    private IntPtr _hidHandle = IntPtr.Zero;
    private ControllerKind _hidKind = ControllerKind.None;
    private string _hidName = string.Empty;
    private string _hidTransport = string.Empty;
    private long _pollCount;
    private DateTime _pollWindowStart = DateTime.UtcNow;
    private DateTime _lastHidPacket = DateTime.MinValue;
    private DateTime _lastXInputPacket = DateTime.MinValue;

    public event Action<ControllerState>? StateChanged;
    public ControllerState Current
    {
        get
        {
            lock (_gate)
            {
                return _state.Clone();
            }
        }
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _pollWindowStart = DateTime.UtcNow;
        _xinputTimer = new Timer(_ => PollXInput(), null, 0, 8);

        _hidThread = new Thread(HidLoop)
        {
            IsBackground = true,
            Name = "FIFINE.ControllerHid"
        };
        _hidThread.Start();
        Log.Info("Controller monitor started.");
    }

    public void Stop()
    {
        _running = false;
        _xinputTimer?.Dispose();
        _xinputTimer = null;

        var handle = _hidHandle;
        _hidHandle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            try { CloseHandle(handle); } catch { }
        }

        try { _hidThread?.Join(600); } catch { }
        _hidThread = null;
    }

    private void PollXInput()
    {
        if (!_running)
        {
            return;
        }

        try
        {
            for (uint index = 0; index < 4; index++)
            {
                var result = XInput.GetState(index, out var pad);
                if (result != 0)
                {
                    continue;
                }

                _lastXInputPacket = DateTime.UtcNow;
                BumpPollCount();

                var gp = pad.Gamepad;
                var leftDead = gp.sThumbLX / 32768f;
                var leftDeadY = gp.sThumbLY / 32768f;
                var rightDead = gp.sThumbRX / 32768f;
                var rightDeadY = gp.sThumbRY / 32768f;

                lock (_gate)
                {
                    // A live HID Sony pad takes precedence; XInput otherwise.
                    if (_hidKind != ControllerKind.None && (DateTime.UtcNow - _lastHidPacket).TotalSeconds < 4)
                    {
                        return;
                    }

                    _state.Connected = true;
                    _state.Kind = ControllerKind.Xbox;
                    _state.Name = "Xbox Controller";
                    _state.Transport = "XInput";
                    _state.LeftX = ApplyDeadzone(leftDead);
                    _state.LeftY = ApplyDeadzone(leftDeadY);
                    _state.RightX = ApplyDeadzone(rightDead);
                    _state.RightY = ApplyDeadzone(rightDeadY);
                    _state.LeftTrigger = gp.bLeftTrigger / 255f;
                    _state.RightTrigger = gp.bRightTrigger / 255f;
                    _state.A = (gp.wButtons & 0x1000) != 0;
                    _state.B = (gp.wButtons & 0x2000) != 0;
                    _state.X = (gp.wButtons & 0x4000) != 0;
                    _state.Y = (gp.wButtons & 0x8000) != 0;
                    _state.Up = (gp.wButtons & 0x0001) != 0;
                    _state.Down = (gp.wButtons & 0x0002) != 0;
                    _state.Left = (gp.wButtons & 0x0004) != 0;
                    _state.Right = (gp.wButtons & 0x0008) != 0;
                    _state.Start = (gp.wButtons & 0x0010) != 0;
                    _state.Back = (gp.wButtons & 0x0020) != 0;
                    _state.LeftStickButton = (gp.wButtons & 0x0040) != 0;
                    _state.RightStickButton = (gp.wButtons & 0x0080) != 0;
                    _state.LeftBumper = (gp.wButtons & 0x0100) != 0;
                    _state.RightBumper = (gp.wButtons & 0x0200) != 0;
                    _state.Guide = (gp.wButtons & 0x0400) != 0;
                    _state.UpdatedAt = DateTime.UtcNow;
                    _state.PollRateHz = CurrentPollRate();
                }

                return;
            }

            lock (_gate)
            {
                if (_hidKind == ControllerKind.None || (DateTime.UtcNow - _lastHidPacket).TotalSeconds > 4)
                {
                    if (_state.Connected && _state.Kind == ControllerKind.Xbox)
                    {
                        _state.Connected = false;
                        _state.Name = "No controller";
                        _state.Kind = ControllerKind.None;
                        StateChanged?.Invoke(_state.Clone());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("XInput poll failed: " + ex.Message);
        }
    }

    private static float ApplyDeadzone(float value)
    {
        const float dead = 0.08f;
        var magnitude = Math.Abs(value);
        if (magnitude < dead)
        {
            return 0;
        }

        var scaled = (magnitude - dead) / (1 - dead);
        return Math.Sign(value) * scaled;
    }

    private void BumpPollCount() => Interlocked.Increment(ref _pollCount);

    private double CurrentPollRate()
    {
        var elapsed = (DateTime.UtcNow - _pollWindowStart).TotalSeconds;
        if (elapsed < 1)
        {
            return 0;
        }

        var rate = Interlocked.Exchange(ref _pollCount, 0) / elapsed;
        _pollWindowStart = DateTime.UtcNow;
        return Math.Round(rate, 1);
    }

    private void HidLoop()
    {
        while (_running)
        {
            try
            {
                if (_hidHandle == IntPtr.Zero)
                {
                    if (!TryOpenSonyController())
                    {
                        Thread.Sleep(1500);
                        continue;
                    }
                }

                var buffer = new byte[128];
                if (!ReadFile(_hidHandle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) || read == 0)
                {
                    // Device disconnected or read error: drop the handle and rescan.
                    CloseHid();
                    Thread.Sleep(500);
                    continue;
                }

                ParseSonyReport(buffer, (int)read);
            }
            catch
            {
                CloseHid();
                Thread.Sleep(500);
            }
        }
    }

    private void CloseHid()
    {
        var h = _hidHandle;
        _hidHandle = IntPtr.Zero;
        if (h != IntPtr.Zero)
        {
            try { CloseHandle(h); } catch { }
        }

        _hidKind = ControllerKind.None;
        _hidName = string.Empty;

        lock (_gate)
        {
            if (_state.Kind is ControllerKind.DualSense or ControllerKind.DualShock)
            {
                _state.Connected = false;
                _state.Kind = ControllerKind.None;
                _state.Name = "No controller";
                StateChanged?.Invoke(_state.Clone());
            }
        }
    }

    private bool TryOpenSonyController()
    {
        var hidGuid = HidInterface;
        var detail = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
        if (detail == IntPtr.Zero || detail == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(detail, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                var path = GetInterfacePath(detail, ref ifData);
                if (path is null)
                {
                    continue;
                }

                var handle = CreateFile(path, 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                    {
                        continue;
                    }
                }

                var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(handle, ref attrs))
                {
                    CloseHandle(handle);
                    continue;
                }

                if (attrs.VendorID != 0x054C)
                {
                    CloseHandle(handle);
                    continue;
                }

                _hidKind = attrs.ProductID switch
                {
                    0x0CE6 => ControllerKind.DualSense,
                    0x05C4 or 0x09CC or 0x0843 => ControllerKind.DualShock,
                    _ => ControllerKind.None
                };

                if (_hidKind == ControllerKind.None)
                {
                    CloseHandle(handle);
                    continue;
                }

                _hidName = _hidKind == ControllerKind.DualSense ? "DualSense Wireless Controller" : "DualShock 4";
                _hidTransport = path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ? "Bluetooth" : "USB";
                _hidHandle = handle;
                Log.Info($"Sony controller opened: {_hidName} ({_hidTransport})");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("HID scan failed: " + ex.Message);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(detail);
        }

        return false;
    }

    private static string? GetInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData)
    {
        var size = 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, 0, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, buffer, size, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ParseSonyReport(byte[] report, int length)
    {
        if (length < 10)
        {
            return;
        }

        var id = report[0];
        var isDualSense = _hidKind == ControllerKind.DualSense;

        int baseIndex;
        if (isDualSense)
        {
            if (id == 0x31) baseIndex = 2;
            else if (id == 0x01) baseIndex = 1;
            else return;
        }
        else
        {
            if (id == 0x11) baseIndex = 3;
            else if (id == 0x01) baseIndex = 1;
            else return;
        }

        if (length < baseIndex + 9)
        {
            return;
        }

        var lx = report[baseIndex];
        var ly = report[baseIndex + 1];
        var rx = report[baseIndex + 2];
        var ry = report[baseIndex + 3];

        byte buttons1, buttons2, buttons3 = 0;
        byte leftTrigger, rightTrigger;

        if (isDualSense)
        {
            leftTrigger = report[baseIndex + 4];
            rightTrigger = report[baseIndex + 5];
            buttons1 = report[baseIndex + 7];
            buttons2 = report[baseIndex + 8];
            if (length > baseIndex + 9) buttons3 = report[baseIndex + 9];
        }
        else
        {
            buttons1 = report[baseIndex + 4];
            buttons2 = report[baseIndex + 5];
            leftTrigger = report[baseIndex + 7];
            rightTrigger = report[baseIndex + 8];
            if (length > baseIndex + 9) buttons3 = report[baseIndex + 9];
        }

        var hat = buttons1 & 0x0F;
        var up = hat is 7 or 0 or 1;
        var right = hat is 1 or 2 or 3;
        var down = hat is 3 or 4 or 5;
        var left = hat is 5 or 6 or 7;

        _lastHidPacket = DateTime.UtcNow;
        BumpPollCount();

        lock (_gate)
        {
            _state.Connected = true;
            _state.Kind = _hidKind;
            _state.Name = _hidName;
            _state.Transport = _hidTransport;
            _state.LeftX = ApplyDeadzone((lx - 128) / 127f);
            _state.LeftY = ApplyDeadzone((128 - ly) / 127f);
            _state.RightX = ApplyDeadzone((rx - 128) / 127f);
            _state.RightY = ApplyDeadzone((128 - ry) / 127f);
            _state.LeftTrigger = leftTrigger / 255f;
            _state.RightTrigger = rightTrigger / 255f;
            _state.Y = (buttons1 & 0x80) != 0;
            _state.B = (buttons1 & 0x40) != 0;
            _state.A = (buttons1 & 0x20) != 0;
            _state.X = (buttons1 & 0x10) != 0;
            _state.LeftBumper = (buttons2 & 0x01) != 0;
            _state.RightBumper = (buttons2 & 0x02) != 0;
            _state.Back = (buttons2 & 0x10) != 0;
            _state.Start = (buttons2 & 0x20) != 0;
            _state.LeftStickButton = (buttons2 & 0x40) != 0;
            _state.RightStickButton = (buttons2 & 0x80) != 0;
            _state.Up = up;
            _state.Down = down;
            _state.Left = left;
            _state.Right = right;
            _state.Guide = isDualSense ? (buttons3 & 0x01) != 0 : (buttons3 & 0x01) != 0;
            _state.UpdatedAt = DateTime.UtcNow;
            _state.PollRateHz = CurrentPollRate();
        }

        StateChanged?.Invoke(Current);
    }

    public void Dispose() => Stop();

    // ---------------- XInput ----------------

    private static class XInput
    {
        private delegate uint XInputGetStateDelegate(uint index, out XINPUT_STATE state);
        private static readonly XInputGetStateDelegate? GetStateFn = Load();

        private static XInputGetStateDelegate? Load()
        {
            foreach (var name in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
            {
                try
                {
                    if (NativeLibrary.TryLoad(name, out var lib)
                        && NativeLibrary.TryGetExport(lib, "XInputGetState", out var export))
                    {
                        Log.Info("XInput loaded from " + name);
                        return Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(export);
                    }
                }
                catch
                {
                    // try next
                }
            }

            Log.Warn("XInput not available; Xbox controller monitoring disabled.");
            return null;
        }

        public static uint GetState(uint index, out XINPUT_STATE state)
        {
            if (GetStateFn is null)
            {
                state = default;
                return 1167; // ERROR_DEVICE_NOT_CONNECTED
            }

            try
            {
                return GetStateFn(index, out state);
            }
            catch
            {
                state = default;
                return 1167;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    // ---------------- HID / setupapi ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern void SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr hidDevice, ref HIDD_ATTRIBUTES attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
