using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.FrameGeneration;
using FifineCaptureStudio.Core.Monitoring;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.Direct3D11.Resource;

namespace FifineCaptureStudio.Rendering;

/// <summary>
/// Native fullscreen presenter. It owns a D3D11 device, a DXGI flip-model swapchain and a render
/// thread, so gameplay presentation is independent of WPF's dispatcher and composition cadence.
///
/// Two input paths share the same bounded queue:
///   * CPU BGRA frames from the existing RIFE pipeline / self-test (compatibility path).
///   * D3D11 shared textures produced by the enhancement engine (no CPU readback).
///
/// Presentation is paced with the swapchain's frame-latency waitable object and a max frame latency
/// of one. Vsync is the default; tearing is only used when explicitly requested through the
/// FIFINE_PRESENT_ALLOW_TEARING=1 environment variable and accepted by the driver.
/// </summary>
public sealed class NativeFramePresenter : IDisposable
{
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmSize = 0x0005;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmDisplayChange = 0x007E;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTopMost = new(-1);

    private readonly PresentationQueue _frames;
    private readonly ConcurrentQueue<PresentationFrame> _droppedGpu = new();
    private readonly object _metricsGate = new();
    private readonly List<double> _intervalSamples = new(240);
    private readonly List<double> _latencySamples = new(240);
    private readonly Action<bool>? _bypassChanged;
    private readonly Action? _closed;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly bool _requestedTearing;

    private Thread? _thread;
    private IntPtr _hwnd;
    private volatile bool _stop;
    private volatile bool _bypass;
    private volatile bool _displayDirty;
    private volatile bool _swapDirty;
    private string? _fatal;

    private Device? _device;
    private DeviceContext? _context;
    private Factory2? _factory;
    private SwapChain1? _swapChain;
    private SwapChain2? _swapChain2;
    private Texture2D? _backBuffer;
    private Texture2D? _upload;
    private IntPtr _waitable;
    private SwapChainFlags _swapFlags;
    private bool _useTearing;
    private int _swapWidth;
    private int _swapHeight;

    private readonly Dictionary<IntPtr, SharedInput> _sharedInputs = new();
    private int _displayRefresh;
    private string _displayName = "Display";
    private long _lastDisplayPoll;
    private long _lastPresentTicks;
    private long _presented;
    private long _missed;
    private long _lateDrops;
    private long _bypassedFrames;
    private long _presentedSequence;
    private long _presentWindowFrames;
    private long _presentWindowStart;
    private double _presentFps;
    private double _intervalMs;
    private double _latenessMs;
    private double _queueAgeMs;
    private int _lastCpuWidth;
    private int _lastCpuHeight;
    private volatile FpsOverlaySnapshot? _overlay;

    private readonly int _bufferWidth;
    private readonly int _bufferHeight;

    public NativeFramePresenter(
        int bufferWidth,
        int bufferHeight,
        Action? closed = null,
        Action<bool>? bypassChanged = null,
        bool? allowTearing = null)
    {
        _bufferWidth = Math.Max(2, bufferWidth);
        _bufferHeight = Math.Max(2, bufferHeight);
        _frames = new PresentationQueue(4, OnFrameEvicted);
        _closed = closed;
        _bypassChanged = bypassChanged;
        _requestedTearing = allowTearing
            ?? string.Equals(Environment.GetEnvironmentVariable("FIFINE_PRESENT_ALLOW_TEARING"), "1", StringComparison.Ordinal);
        _presentWindowStart = Stopwatch.GetTimestamp();
    }

    public int DisplayRefreshHz => _displayRefresh;
    public string DisplayName => _displayName;
    public bool IsOpen => _thread is { IsAlive: true };
    public string? Error => _fatal;
    public bool Bypass => _bypass;

    public string PresentMode => _useTearing
        ? "vsync off · tearing accepted"
        : "vsync on · flip discard";

    public bool WaitableObjectActive => _waitable != IntPtr.Zero;
    public string SwapChainMode => _swapChain is null ? "not created" : "DXGI flip discard · 2 buffers";

    public long SubmittedFrames => _frames.SubmittedFrames;
    public long PresentedFrames => Interlocked.Read(ref _presented);
    public long QueueDroppedFrames => _frames.DroppedFrames;
    public long LateDroppedFrames => Interlocked.Read(ref _lateDrops);
    public long BypassedFrames => Interlocked.Read(ref _bypassedFrames);
    public long DroppedFrames => QueueDroppedFrames + LateDroppedFrames + BypassedFrames;
    public long MissedDeadlines => Interlocked.Read(ref _missed);
    public long PresentedSequence => Interlocked.Read(ref _presentedSequence);
    public double PresentedFps => _presentFps;
    public double AveragePresentIntervalMs => _intervalMs;
    public double AveragePresentationLatencyMs => _latenessMs;
    public double LatenessMs => _latenessMs;
    public double AverageQueueAgeMs => _queueAgeMs;

    public double P95PresentIntervalMs
    {
        get { lock (_metricsGate) { return Percentile(_intervalSamples, 0.95); } }
    }

    public double P95PresentationLatencyMs
    {
        get { lock (_metricsGate) { return Percentile(_latencySamples, 0.95); } }
    }

    public bool Start(IntPtr owner)
    {
        if (_thread is not null)
        {
            return IsOpen && _fatal is null;
        }

        _thread = new Thread(() => Run(owner))
        {
            IsBackground = true,
            Name = "FIFINE.NativePresenter"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(8));
        return _fatal is null && _hwnd != IntPtr.Zero && _swapChain is not null;
    }

    public void Enqueue(ScheduledFrame frame) => _frames.Enqueue(PresentationFrame.FromCpu(frame));

    public void EnqueueGpu(GpuScheduledFrame frame) => _frames.Enqueue(PresentationFrame.FromGpu(frame));

    public void UpdateOverlay(FpsOverlaySnapshot snapshot) => _overlay = snapshot;

    public void Stop()
    {
        _stop = true;
        var handle = _hwnd;
        if (handle != IntPtr.Zero)
        {
            PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        try { _thread?.Join(2500); } catch { }
    }

    private void Run(IntPtr owner)
    {
        Texture2D? upload = null;
        try
        {
            _displayRefresh = DisplayRefresh.Get(owner, out _displayName);
            _lastDisplayPoll = Stopwatch.GetTimestamp();
            var bounds = DisplayRefresh.Bounds(owner);

            var className = "FIFINE_NativePresenter_" + Guid.NewGuid().ToString("N")[..8];
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
                0,
                className,
                "FIFINE Capture Studio — Native Presenter",
                0x80000000 /* WS_POPUP */ | 0x10000000 /* WS_VISIBLE */,
                bounds.Left,
                bounds.Top,
                Math.Max(1, bounds.Right - bounds.Left),
                Math.Max(1, bounds.Bottom - bounds.Top),
                IntPtr.Zero,
                IntPtr.Zero,
                wndClass.hInstance,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _fatal = "Could not create the fullscreen window.";
                return;
            }

            ShowWindow(_hwnd, 5 /* SW_SHOW */);
            SetForegroundWindow(_hwnd);

            _device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            _context = _device.ImmediateContext;
            _factory = new Factory2();

            EnsureSwapChain(_bufferWidth, _bufferHeight);
            _ready.Set();

            while (!_stop && IsWindow(_hwnd))
            {
                PumpMessages();
                DrainDroppedGpuFrames();
                PollDisplayChange();

                if (_swapDirty)
                {
                    _swapDirty = false;
                    RecreateSwapChain();
                }

                if (!_frames.TryPeek(out var peeked) || peeked is null)
                {
                    Thread.Sleep(1);
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                var dueInMs = peeked.DueMs(now);
                if (dueInMs > 0.05)
                {
                    SleepUntil(dueInMs);
                    continue;
                }

                if (!_frames.TryDequeue(out var dequeued) || dequeued is null)
                {
                    continue;
                }

                if (_bypass && dequeued.Generated)
                {
                    ReleaseGpuLease(dequeued);
                    Interlocked.Increment(ref _bypassedFrames);
                    continue;
                }

                // A frame this late cannot improve cadence; discard it rather than accumulating lag.
                var lateMs = Math.Max(0, -dueInMs);
                if (lateMs > 120)
                {
                    ReleaseGpuLease(dequeued);
                    Interlocked.Increment(ref _lateDrops);
                    continue;
                }

                var presentedAt = PresentFrame(dequeued);
                if (presentedAt == 0)
                {
                    Interlocked.Increment(ref _lateDrops);
                    continue;
                }

                RecordPresent(dequeued, presentedAt, lateMs);
            }
        }
        catch (Exception ex)
        {
            _fatal = ex.Message;
            Log.Error("Native presenter failed", ex);
        }
        finally
        {
            _ready.Set();
            DrainQueuedGpuFrames();
            upload?.Dispose();
            DisposeSharedInputs();
            DisposeSwapChain();
            _factory?.Dispose();
            _factory = null;
            _context = null;
            _device?.Dispose();
            _device = null;

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            WriteSessionReport();
            try { _closed?.Invoke(); } catch (Exception ex) { Log.Debug("Presenter close callback: " + ex.Message); }
        }
    }

    /// <summary>
    /// Presents one frame. Returns the Stopwatch timestamp immediately after Present, or 0 when the
    /// frame could not be presented (texture race, device error or unsupported resource).
    /// </summary>
    private long PresentFrame(PresentationFrame frame)
    {
        if (_device is null || _context is null || _swapChain is null)
        {
            return 0;
        }

        if (frame.IsGpu)
        {
            return PresentGpu(frame);
        }

        return PresentCpu(frame);
    }

    private long PresentGpu(PresentationFrame frame)
    {
        var input = GetSharedInput(frame.SharedTextureHandle, frame.Width, frame.Height);
        if (input is null)
        {
            return 0;
        }

        try
        {
            EnsureSwapChain(frame.Width, frame.Height);
        }
        catch
        {
            // The producer has already published key 1; hand the surface back before propagating
            // the swapchain failure so it cannot strand a shared surface.
            ReleaseGpuInput(input);
            throw;
        }

        if (_swapChain is null || _backBuffer is null || _context is null)
        {
            ReleaseGpuInput(input);
            return 0;
        }

        WaitForFrameLatency();
        var acquired = false;
        try
        {
            acquired = input.Mutex.Acquire(1, 16).Success;
        }
        catch (Exception ex)
        {
            Log.Debug("Shared texture acquire failed: " + ex.Message);
        }

        if (!acquired)
        {
            return 0;
        }

        try
        {
            _context.CopyResource(input.Texture, _backBuffer);
            // Keep the producer out until the copy has reached the GPU command stream. ReleaseSync
            // itself is the cross-device hand-off point; Flush makes the ordering explicit.
            _context.Flush();
        }
        finally
        {
            try { input.Mutex.Release(0); } catch { }
        }

        return PresentSwapChain();
    }

    private long PresentCpu(PresentationFrame frame)
    {
        if (frame.Bgra is null || _device is null || _context is null || _swapChain is null)
        {
            return 0;
        }

        var overlay = _overlay;
        if (overlay is { Visible: true })
        {
            FpsOverlayCompositor.Draw(frame.Bgra, frame.Width, frame.Height, overlay);
        }

        EnsureSwapChain(frame.Width, frame.Height);
        if (_swapChain is null || _backBuffer is null)
        {
            return 0;
        }

        if (_upload is null || _lastCpuWidth != frame.Width || _lastCpuHeight != frame.Height)
        {
            _upload?.Dispose();
            _upload = new Texture2D(_device, new Texture2DDescription
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
            _lastCpuWidth = frame.Width;
            _lastCpuHeight = frame.Height;
        }

        var dataBox = _context.MapSubresource(_upload, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = frame.Width * 4;
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Bgra, y * rowBytes, dataBox.DataPointer + (y * dataBox.RowPitch), rowBytes);
            }
        }
        finally
        {
            _context.UnmapSubresource(_upload, 0);
        }

        WaitForFrameLatency();
        _context.CopyResource(_upload, _backBuffer);
        return PresentSwapChain();
    }

    private long PresentSwapChain()
    {
        if (_swapChain is null)
        {
            return 0;
        }

        var interval = _useTearing ? 0 : 1;
        var flags = _useTearing ? PresentFlags.AllowTearing : PresentFlags.None;
        _swapChain.Present(interval, flags);
        return Stopwatch.GetTimestamp();
    }

    private void RecordPresent(PresentationFrame frame, long presentedAt, double lateAtDequeueMs)
    {
        var frequency = (double)Stopwatch.Frequency;
        var latency = Math.Max(0, (presentedAt - frame.DueTicks) * 1000.0 / frequency);
        var queueAge = frame.QueueAgeMs(presentedAt);
        var budget = _displayRefresh > 0 ? 1000.0 / _displayRefresh : 16.6667;

        lock (_metricsGate)
        {
            if (_lastPresentTicks > 0)
            {
                var interval = (presentedAt - _lastPresentTicks) * 1000.0 / frequency;
                if (interval > 0 && interval < 2000)
                {
                    AddSample(_intervalSamples, interval);
                }
            }

            _lastPresentTicks = presentedAt;
            _latenessMs = Ewma(_latenessMs, latency);
            _queueAgeMs = Ewma(_queueAgeMs, Math.Max(0, queueAge));
            AddSample(_latencySamples, latency);
            Interlocked.Increment(ref _presented);
            Interlocked.Exchange(ref _presentedSequence, frame.SourceSequence);
            _presentWindowFrames++;

            var window = (presentedAt - _presentWindowStart) * 1000.0 / frequency;
            if (window >= 500)
            {
                _presentFps = _presentWindowFrames * 1000.0 / window;
                _presentWindowFrames = 0;
                _presentWindowStart = presentedAt;
            }

            if (_intervalSamples.Count > 0)
            {
                _intervalMs = _intervalSamples.Average();
            }
        }

        // A frame that was already late before Present reached the swapchain missed its source
        // deadline. The post-Present timestamp also includes the intentional vsync wait, so using
        // it here would falsely count normal refresh quantisation as a missed deadline.
        if (lateAtDequeueMs > budget)
        {
            Interlocked.Increment(ref _missed);
        }
    }

    private void EnsureSwapChain(int width, int height)
    {
        width = Math.Max(2, width);
        height = Math.Max(2, height);
        if (_swapChain is null)
        {
            CreateSwapChain(width, height);
            return;
        }

        if (_swapWidth == width && _swapHeight == height)
        {
            return;
        }

        ResizeSwapChain(width, height);
    }

    private void RecreateSwapChain()
    {
        var width = _swapWidth > 0 ? _swapWidth : _bufferWidth;
        var height = _swapHeight > 0 ? _swapHeight : _bufferHeight;
        if (_swapChain is null)
        {
            CreateSwapChain(width, height);
            return;
        }

        ResizeSwapChain(width, height);
    }

    private void ResizeSwapChain(int width, int height)
    {
        if (_swapChain is null)
        {
            CreateSwapChain(width, height);
            return;
        }

        // Recreating a swapchain for an HWND that has already presented can return
        // DXGI_ERROR_INVALID_CALL on some drivers. Resizing the existing flip chain is the supported
        // path and keeps the waitable object/latency configuration intact.
        _backBuffer?.Dispose();
        _backBuffer = null;
        _upload?.Dispose();
        _upload = null;
        _lastCpuWidth = 0;
        _lastCpuHeight = 0;

        _swapChain.ResizeBuffers(2, width, height, Format.B8G8R8A8_UNorm, _swapFlags);
        _backBuffer = Resource.FromSwapChain<Texture2D>(_swapChain, 0);
        _swapWidth = width;
        _swapHeight = height;
    }

    private void CreateSwapChain(int width, int height)
    {
        if (_device is null || _factory is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var attempts = new List<SwapChainFlags>();
        var waitable = SwapChainFlags.FrameLatencyWaitAbleObject;
        if (_requestedTearing)
        {
            attempts.Add(waitable | SwapChainFlags.AllowTearing);
        }

        attempts.Add(waitable);
        attempts.Add(SwapChainFlags.None);

        Exception? lastError = null;
        foreach (var flags in attempts.Distinct())
        {
            try
            {
                var description = new SwapChainDescription1
                {
                    // The backbuffer follows the processed frame size. DXGI stretches the
                    // borderless window to the monitor, so no CPU rescale and no extra copy.
                    Width = width,
                    Height = height,
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = flags
                };

                var swapChain = new SwapChain1(_factory, _device, _hwnd, ref description);
                var swapChain2 = swapChain.QueryInterface<SwapChain2>();
                swapChain2.MaximumFrameLatency = 1;

                var backBuffer = Resource.FromSwapChain<Texture2D>(swapChain, 0);
                var waitableHandle = flags.HasFlag(SwapChainFlags.FrameLatencyWaitAbleObject)
                    ? swapChain2.FrameLatencyWaitableObject
                    : IntPtr.Zero;

                _swapChain = swapChain;
                _swapChain2 = swapChain2;
                _backBuffer = backBuffer;
                _waitable = waitableHandle;
                _swapFlags = flags;
                _useTearing = flags.HasFlag(SwapChainFlags.AllowTearing);
                _swapWidth = width;
                _swapHeight = height;
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                DisposeSwapChain();
            }
        }

        var detail = lastError is null ? "no attempt ran" : lastError.GetType().Name + ": " + lastError.Message;
        throw new InvalidOperationException("Could not create a DXGI flip-model swapchain (" + detail + ")", lastError);
    }

    private void WaitForFrameLatency()
    {
        if (_waitable != IntPtr.Zero)
        {
            // The waitable object is signalled when the previous frame's latency budget is free.
            // A timeout means the compositor is stalled; still attempt the present so the user can
            // see the frame rather than freezing indefinitely.
            WaitForSingleObject(_waitable, 16);
        }
    }

    private void OnFrameEvicted(PresentationFrame frame)
    {
        if (frame.IsGpu)
        {
            _droppedGpu.Enqueue(frame);
        }
    }

    private void DrainDroppedGpuFrames()
    {
        while (_droppedGpu.TryDequeue(out var frame))
        {
            ReleaseGpuLease(frame);
        }
    }

    private void DrainQueuedGpuFrames()
    {
        while (_frames.TryDequeue(out var frame) && frame is not null)
        {
            ReleaseGpuLease(frame);
        }

        DrainDroppedGpuFrames();
    }

    private void ReleaseGpuLease(PresentationFrame frame)
    {
        if (!frame.IsGpu)
        {
            return;
        }

        var input = GetSharedInput(frame.SharedTextureHandle, frame.Width, frame.Height);
        if (input is not null)
        {
            ReleaseGpuInput(input);
        }
    }

    private static void ReleaseGpuInput(SharedInput input)
    {
        try
        {
            if (input.Mutex.Acquire(1, 1).Success)
            {
                input.Mutex.Release(0);
            }
        }
        catch
        {
            // The producer may already have reclaimed the surface or the device may be gone.
        }
    }

    private SharedInput? GetSharedInput(IntPtr handle, int width, int height)
    {
        if (_device is null || handle == IntPtr.Zero)
        {
            return null;
        }

        if (_sharedInputs.TryGetValue(handle, out var existing))
        {
            return existing;
        }

        try
        {
            var texture = _device.OpenSharedResource<Texture2D>(handle);
            var mutex = texture.QueryInterface<KeyedMutex>();
            var input = new SharedInput(texture, mutex, width, height);
            _sharedInputs[handle] = input;
            return input;
        }
        catch (Exception ex)
        {
            Log.Debug("Could not open enhanced shared texture: " + ex.Message);
            return null;
        }
    }

    private void DisposeSharedInputs()
    {
        foreach (var input in _sharedInputs.Values)
        {
            input.Dispose();
        }

        _sharedInputs.Clear();
    }

    private void DisposeSwapChain()
    {
        _backBuffer?.Dispose();
        _backBuffer = null;
        _upload?.Dispose();
        _upload = null;
        _lastCpuWidth = 0;
        _lastCpuHeight = 0;

        if (_waitable != IntPtr.Zero)
        {
            CloseHandle(_waitable);
            _waitable = IntPtr.Zero;
        }

        _swapChain2?.Dispose();
        _swapChain2 = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _swapWidth = 0;
        _swapHeight = 0;
        _swapFlags = SwapChainFlags.None;
        _useTearing = false;
    }

    private void PollDisplayChange()
    {
        // WM_DISPLAYCHANGE already marked the window dirty; apply it on the next render iteration
        // instead of waiting for the two-second safety poll.
        if (_displayDirty)
        {
            ApplyDisplayChange();
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastDisplayPoll != 0 && (now - _lastDisplayPoll) < Stopwatch.Frequency * 2)
        {
            return;
        }

        _lastDisplayPoll = now;
        var refresh = DisplayRefresh.Get(_hwnd, out var name);
        if (refresh != _displayRefresh)
        {
            _displayRefresh = refresh;
            _displayName = name;
            _displayDirty = true;
        }

        ApplyDisplayChange();
    }

    private void ApplyDisplayChange()
    {
        if (!_displayDirty || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _displayDirty = false;
        var bounds = DisplayRefresh.Bounds(_hwnd);
        SetWindowPos(
            _hwnd,
            HwndTopMost,
            bounds.Left,
            bounds.Top,
            Math.Max(1, bounds.Right - bounds.Left),
            Math.Max(1, bounds.Bottom - bounds.Top),
            SwpShowWindow | SwpNoActivate | SwpFrameChanged);

        _swapDirty = true;
    }

    private void WriteSessionReport()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Root, "fg-presenter.txt"),
                "FIFINE native presenter session\n" +
                "presentation  = DXGI flip-model, 2 buffers, FlipDiscard\n" +
                "present mode  = " + PresentMode + "\n" +
                "waitable      = " + (WaitableObjectActive ? "yes, max frame latency 1" : "not available") + "\n" +
                "display       = " + _displayRefresh + " Hz (" + _displayName + ")\n" +
                "buffer        = " + _swapWidth + "x" + _swapHeight + "\n" +
                "submitted     = " + SubmittedFrames + "\n" +
                "presented     = " + PresentedFrames + "\n" +
                "queue drops   = " + QueueDroppedFrames + "\n" +
                "late drops    = " + LateDroppedFrames + "\n" +
                "bypass drops  = " + BypassedFrames + "\n" +
                "dropped total = " + DroppedFrames + "\n" +
                "missed budget = " + MissedDeadlines + "\n" +
                "cadence       = " + _presentFps.ToString("0.0") + " fps (0.5 s window)\n" +
                "interval      = " + _intervalMs.ToString("0.00") + " ms avg / " + P95PresentIntervalMs.ToString("0.00") + " ms p95\n" +
                "latency       = " + _latenessMs.ToString("0.00") + " ms avg / " + P95PresentationLatencyMs.ToString("0.00") + " ms p95\n" +
                "queue age     = " + _queueAgeMs.ToString("0.00") + " ms\n" +
                "last sequence = " + PresentedSequence + "\n" +
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

    private static void SleepUntil(double dueInMs)
    {
        if (dueInMs > 1.5)
        {
            Thread.Sleep((int)Math.Clamp(dueInMs - 0.3, 1, 4));
        }
        else
        {
            Thread.SpinWait(80);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmKeyDown:
                var key = wParam.ToInt32();
                if (key is 0x1B /* Esc */ or 0x7A /* F11 */)
                {
                    _stop = true;
                    PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                    return IntPtr.Zero;
                }

                if (key == 0x09 /* Tab */ && !_bypass)
                {
                    _bypass = true;
                    _bypassChanged?.Invoke(true);
                }

                return IntPtr.Zero;

            case WmKeyUp:
                if (wParam.ToInt32() == 0x09 && _bypass)
                {
                    _bypass = false;
                    _bypassChanged?.Invoke(false);
                }

                return IntPtr.Zero;

            case WmClose:
                _stop = true;
                DestroyWindow(hwnd);
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;

            case WmDisplayChange:
                _displayDirty = true;
                return IntPtr.Zero;

            case WmSize:
                _swapDirty = true;
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    private static void AddSample(List<double> samples, double value)
    {
        samples.Add(value);
        if (samples.Count > 240)
        {
            samples.RemoveRange(0, samples.Count - 240);
        }
    }

    private static double Percentile(List<double> samples, double fraction)
    {
        if (samples.Count < 8)
        {
            return 0;
        }

        var copy = samples.ToArray();
        Array.Sort(copy);
        var index = (int)Math.Round((copy.Length - 1) * fraction);
        return copy[Math.Clamp(index, 0, copy.Length - 1)];
    }

    private static double Ewma(double current, double sample) =>
        current <= 0 ? sample : (current * 0.9) + (sample * 0.1);

    private sealed class SharedInput : IDisposable
    {
        public SharedInput(Texture2D texture, KeyedMutex mutex, int width, int height)
        {
            Texture = texture;
            Mutex = mutex;
            Width = width;
            Height = height;
        }

        public Texture2D Texture { get; }
        public KeyedMutex Mutex { get; }
        public int Width { get; }
        public int Height { get; }

        public void Dispose()
        {
            Mutex.Dispose();
            Texture.Dispose();
        }
    }

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
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
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
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
