using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.FrameGeneration;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;

namespace FifineCaptureStudio.Rendering;

/// <summary>
/// Borderless fullscreen presenter with its own D3D11 flip-model swapchain and render thread.
/// WPF never touches a gameplay frame: the main window keeps the controls, while frames are copied
/// into a native texture and presented at the display's own refresh rate (with generated frames in
/// the correct order when frame generation is active).
/// </summary>
public sealed class NativeFramePresenter : IDisposable
{
    private readonly ConcurrentQueue<ScheduledFrame> _frames = new();
    private readonly object _windowGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action<bool>? _bypassChanged;
    private readonly Action? _closed;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private IntPtr _hwnd;
    private volatile bool _stop;
    private volatile bool _bypass;
    private string? _fatal;
    private long _presented;
    private long _missed;
    private long _bypassedFrames;
    private double _presentFps;
    private long _presentWindowFrames;
    private long _presentWindowStart;
    private double _queueAgeMs;
    private double _latenessMs;
    private int _lastWidth;
    private int _lastHeight;
    private int _displayRefresh;
    private string _displayName = "Display";
    private readonly int _bufferWidth;
    private readonly int _bufferHeight;

    public NativeFramePresenter(int bufferWidth, int bufferHeight, Action? closed = null, Action<bool>? bypassChanged = null)
    {
        _bufferWidth = bufferWidth;
        _bufferHeight = bufferHeight;
        _closed = closed;
        _bypassChanged = bypassChanged;
    }

    public int DisplayRefreshHz => _displayRefresh;
    public string DisplayName => _displayName;
    public bool IsOpen => _thread is { IsAlive: true };
    public string? Error => _fatal;
    public long PresentedFrames => Interlocked.Read(ref _presented);
    public long MissedDeadlines => Interlocked.Read(ref _missed);
    public double PresentedFps => _presentFps;
    public double QueueAgeMs => _queueAgeMs;
    public double LatenessMs => _latenessMs;
    public bool Bypass => _bypass;

    public bool Start(IntPtr owner)
    {
        _thread = new Thread(() => Run(owner))
        {
            IsBackground = true,
            Name = "FIFINE.FramePresenter"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(8));
        return _fatal is null && _hwnd != IntPtr.Zero;
    }

    public void Enqueue(ScheduledFrame frame) => _frames.Enqueue(frame);

    public void Stop()
    {
        _stop = true;
        var handle = _hwnd;
        if (handle != IntPtr.Zero)
        {
            PostMessage(handle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }

        try { _thread?.Join(1500); } catch { }
    }

    private void Run(IntPtr owner)
    {
        Device? device = null;
        SwapChain1? swapChain = null;
        Texture2D? backBuffer = null;
        Texture2D? upload = null;
        try
        {
            _displayRefresh = DisplayRefresh.Get(owner, out _displayName);
            var bounds = DisplayRefresh.Bounds(owner);

            var className = "FIFINE_NativeFramePresenter_" + Guid.NewGuid().ToString("N")[..8];
            _wndProc = WndProc;
            var wndClass = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, 32512 /* IDC_ARROW */),
                lpszClassName = className
            };
            RegisterClassEx(ref wndClass);

            _hwnd = CreateWindowEx(
                0, className, "FIFINE Capture Studio — Frame Generation",
                0x80000000 /* WS_POPUP */ | 0x10000000 /* WS_VISIBLE */,
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _fatal = "Could not create the fullscreen window.";
                return;
            }

            ShowWindow(_hwnd, 5);
            SetForegroundWindow(_hwnd);

            device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using var factory = new Factory2();

            var description = new SwapChainDescription1
            {
                // The swapchain stays at the internal video size and DXGI stretches it to the
                // borderless window, so CopyResource never has to rescale.
                Width = _bufferWidth,
                Height = _bufferHeight,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                Usage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None
            };

            swapChain = new SwapChain1(factory, device, _hwnd, ref description);
            backBuffer = Resource.FromSwapChain<Texture2D>(swapChain, 0);
            _ready.Set();

            while (!_stop && IsWindow(_hwnd))
            {
                PumpMessages();
                if (!_frames.TryPeek(out var frame))
                {
                    Thread.Sleep(1);
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                var dueInMs = (frame.DueTicks - now) * 1000.0 / Stopwatch.Frequency;
                if (dueInMs > 0.05)
                {
                    Thread.Sleep((int)Math.Clamp(dueInMs, 1, 4));
                    continue;
                }

                if (!_frames.TryDequeue(out frame))
                {
                    continue;
                }

                // Holding Tab must restore the source immediately; a generated frame that was
                // already queued when bypass began is dropped rather than shown.
                if (_bypass && frame.Generated)
                {
                    Interlocked.Increment(ref _bypassedFrames);
                    continue;
                }

                // A frame that is already far past its slot would only add lag: skip it.
                var lateMs = -dueInMs;
                if (lateMs > 120)
                {
                    continue;
                }

                if (_lastWidth != frame.Width || _lastHeight != frame.Height || upload is null)
                {
                    upload?.Dispose();
                    upload = new Texture2D(device, new Texture2DDescription
                    {
                        Width = frame.Width,
                        Height = frame.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Dynamic,
                        BindFlags = BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.Write,
                        OptionFlags = ResourceOptionFlags.None
                    });
                    _lastWidth = frame.Width;
                    _lastHeight = frame.Height;
                }

                var dataBox = device.ImmediateContext.MapSubresource(upload, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    var rowBytes = frame.Width * 4;
                    for (var y = 0; y < frame.Height; y++)
                    {
                        Marshal.Copy(frame.Bgra, y * rowBytes, dataBox.DataPointer + y * dataBox.RowPitch, rowBytes);
                    }
                }
                finally
                {
                    device.ImmediateContext.UnmapSubresource(upload, 0);
                }

                device.ImmediateContext.CopyResource(upload, backBuffer);
                swapChain.Present(1, PresentFlags.None);

                Interlocked.Increment(ref _presented);
                _presentWindowFrames++;
                _queueAgeMs = Ewma(_queueAgeMs, lateMs + Math.Max(0, dueInMs));
                _latenessMs = Ewma(_latenessMs, Math.Max(0, lateMs));
                if (lateMs > 8)
                {
                    Interlocked.Increment(ref _missed);
                }

                var now2 = Stopwatch.GetTimestamp();
                var window = (now2 - _presentWindowStart) * 1000.0 / Stopwatch.Frequency;
                if (window >= 500)
                {
                    _presentFps = _presentWindowFrames * 1000.0 / window;
                    _presentWindowFrames = 0;
                    _presentWindowStart = now2;
                }
            }
        }
        catch (Exception ex)
        {
            _fatal = ex.Message;
            Log.Error("Native frame presenter failed", ex);
        }
        finally
        {
            _ready.Set();
            upload?.Dispose();
            backBuffer?.Dispose();
            swapChain?.Dispose();
            device?.Dispose();
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            WriteSessionReport();
            _closed?.Invoke();
        }
    }

    private void WriteSessionReport()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Root, "fg-presenter.txt"),
                "FIFINE native presenter session\n" +
                "display       = " + _displayRefresh + " Hz (" + _displayName + ")\n" +
                "buffer        = " + _bufferWidth + "x" + _bufferHeight + "\n" +
                "presented     = " + PresentedFrames + "\n" +
                "missed >8ms   = " + MissedDeadlines + "\n" +
                "cadence       = " + _presentFps.ToString("0.0") + " fps (0.5 s window)\n" +
                "queue age     = " + _queueAgeMs.ToString("0.0") + " ms\n" +
                "lateness      = " + _latenessMs.ToString("0.0") + " ms\n" +
                "bypass drops  = " + Interlocked.Read(ref _bypassedFrames) + "\n" +
                "error         = " + (_fatal ?? "none") + "\n");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private void PumpMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case 0x0100: // WM_KEYDOWN
                var key = wParam.ToInt32();
                if (key is 0x1B /* Esc */ or 0x7A /* F11 */)
                {
                    _stop = true;
                    PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
                }

                if (key == 0x09 /* Tab */ && !_bypass)
                {
                    _bypass = true;
                    _bypassChanged?.Invoke(true);
                }

                return IntPtr.Zero;

            case 0x0101: // WM_KEYUP
                if (wParam.ToInt32() == 0x09 && _bypass)
                {
                    _bypass = false;
                    _bypassChanged?.Invoke(false);
                }

                return IntPtr.Zero;

            case 0x0010: // WM_CLOSE
                _stop = true;
                DestroyWindow(hwnd);
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;

            case 0x0002: // WM_DESTROY
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    private static double Ewma(double current, double sample) =>
        current <= 0 ? sample : (current * 0.9) + (sample * 0.1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate _wndProc = null!;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int cursor);
}
