using System.Diagnostics;

namespace FifineCaptureStudio.Core.FrameGeneration;

/// <summary>
/// Owns the persistent RIFE engine and the numbers the UI shows. The engine is created once on a
/// worker thread and reused for the whole session; disabling frame generation keeps it loaded (so
/// re-enabling does not pay the DirectML compile cost again) until the app shuts down.
/// </summary>
public sealed class FrameGenerationService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private readonly List<double> _inference = new();
    private readonly List<double> _total = new();

    private RifeOnnxEngine? _engine;
    private Task<bool>? _loading;
    private string _status = "Off";
    private bool _ready;
    private long _lastVramQueryTicks;
    private double _vramMb;
    private long _generated;
    private long _sourceFrames;
    private long _sceneCuts;
    private long _dropped;
    private long _skipped;
    private long _presented;
    private long _missed;
    private double _queueAgeMs;
    private double _presentLatencyMs;
    private long _sourceFpsFrames;
    private double _sourceFps;
    private DateTime _sourceWindow = DateTime.UtcNow;
    private long _generatedFpsFrames;
    private double _generatedFps;
    private DateTime _generatedWindow = DateTime.UtcNow;

    public FrameGenerationService(SettingsService settings)
    {
        _settings = settings;
    }

    public FrameGenerationSettings Settings => _settings.Current.FrameGeneration;
    public bool Enabled => Settings.Enabled;
    public bool Ready => _ready;
    public bool Loading => _loading is { IsCompleted: false };
    public string Status => _status;
    public string? ModelPath { get; private set; }
    public string Provider => _engine?.Provider ?? "—";
    public bool GpuVerified => _engine?.GpuVerified ?? false;
    public string PlacementReport => _engine?.PlacementReport ?? "not checked";
    public double LastInferenceMs => _engine?.LastInferenceMs ?? 0;
    public double AverageInferenceMs => Average(_inference);
    public double P50InferenceMs => Percentile(_inference, 0.50);
    public double P95InferenceMs => Percentile(_inference, 0.95);
    public double AverageTotalMs => Average(_total);
    public double VramMb => _vramMb;
    public long GeneratedFrames => Interlocked.Read(ref _generated);

    public long SkippedInterpolations => Interlocked.Read(ref _skipped);
    public long SourceFrames => Interlocked.Read(ref _sourceFrames);
    public long SceneCuts => Interlocked.Read(ref _sceneCuts);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long PresentedFrames => Interlocked.Read(ref _presented);
    public long MissedDeadlines => Interlocked.Read(ref _missed);
    public double QueueAgeMs => _queueAgeMs;
    public double PresentLatencyMs => _presentLatencyMs;
    public double SourceFps => _sourceFps;
    public double GeneratedFps => _generatedFps;
    public bool ModelInstalled => RifeModel.IsInstalled();
    public string ModelDescription => RifeModel.Describe();
    public (int Width, int Height) InternalSize => Settings.InternalSize;

    /// <summary>Loads the model if needed. Never called from the UI thread directly.</summary>
    public Task<bool> EnsureReadyAsync()
    {
        lock (_gate)
        {
            if (_ready && _engine is not null)
            {
                return Task.FromResult(true);
            }

            if (_loading is { IsCompleted: false })
            {
                return _loading;
            }

            _status = "Loading RIFE…";
            _loading = Task.Run(Load);
            return _loading;
        }
    }

    private bool Load()
    {
        try
        {
            var path = RifeModel.ResolvePath();
            if (path is null)
            {
                _status = "Model not installed";
                return false;
            }

            if (!RifeModel.Verify(path, out var verification))
            {
                _status = verification;
                return false;
            }

            ModelPath = path;
            RifeModel.WriteLicenseFile();
            var (width, height) = Settings.InternalSize;
            var engine = new RifeOnnxEngine(path, width, height);
            if (!engine.GpuVerified)
            {
                Log.Warn("RIFE placement: " + engine.PlacementReport);
            }

            lock (_gate)
            {
                _engine = engine;
                _ready = true;
            }

            _status = engine.GpuVerified
                ? $"{RifeModel.DisplayName} · DirectML · {width} × {height}"
                : $"{RifeModel.DisplayName} · loaded ({engine.PlacementReport})";
            Log.Info("Frame generation ready: " + _status);
            return true;
        }
        catch (Exception ex)
        {
            _status = "RIFE failed: " + ex.Message;
            Log.Error("Could not start RIFE", ex);
            return false;
        }
    }

    public RifeOnnxEngine? Engine
    {
        get
        {
            lock (_gate)
            {
                return _engine;
            }
        }
    }

    public void RecordInference(double inferenceMs, double totalMs)
    {
        lock (_inference)
        {
            Add(_inference, inferenceMs);
            Add(_total, totalMs);
        }

        Interlocked.Increment(ref _generated);
        // DXGI memory queries are cheap but not free; once a second is enough for a status readout.
        var nowTicks = Stopwatch.GetTimestamp();
        if (nowTicks - _lastVramQueryTicks > Stopwatch.Frequency)
        {
            _vramMb = _engine?.QueryVramUsedMb() ?? 0;
            _lastVramQueryTicks = nowTicks;
        }
        _generatedFpsFrames++;
        var generatedWindow = (DateTime.UtcNow - _generatedWindow).TotalSeconds;
        if (generatedWindow >= 0.5)
        {
            _generatedFps = Math.Round(_generatedFpsFrames / generatedWindow, 1);
            _generatedFpsFrames = 0;
            _generatedWindow = DateTime.UtcNow;
        }
    }

    public void NoteSourceFrame()
    {
        Interlocked.Increment(ref _sourceFrames);
        _sourceFpsFrames++;
        var elapsed = (DateTime.UtcNow - _sourceWindow).TotalSeconds;
        if (elapsed >= 0.5)
        {
            _sourceFps = Math.Round(_sourceFpsFrames / elapsed, 1);
            _sourceFpsFrames = 0;
            _sourceWindow = DateTime.UtcNow;
        }
    }

    public void NoteQueueAge(double milliseconds) => _queueAgeMs = Ewma(_queueAgeMs, milliseconds);
    public void NoteSceneCut() => Interlocked.Increment(ref _sceneCuts);
    public void NoteDropped() => Interlocked.Increment(ref _dropped);
    public void NoteSkipped() => Interlocked.Increment(ref _skipped);
    public void NotePresented(long queueAgeTicks, long presentLatencyTicks, bool onTime)
    {
        Interlocked.Increment(ref _presented);
        if (!onTime)
        {
            Interlocked.Increment(ref _missed);
        }

        var frequency = (double)Stopwatch.Frequency;
        _queueAgeMs = Ewma(_queueAgeMs, queueAgeTicks * 1000.0 / frequency);
        _presentLatencyMs = Ewma(_presentLatencyMs, presentLatencyTicks * 1000.0 / frequency);
    }

    public void SetStatus(string status) => _status = status;

    public void Reload()
    {
        // Internal size depends on the quality preset; a loaded engine keeps its shape until reload.
        var engine = Engine;
        if (engine is null)
        {
            return;
        }

        var (width, height) = Settings.InternalSize;
        if (engine.Width != width || engine.Height != height)
        {
            ResetEngine();
        }
    }

    public void ResetEngine()
    {
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
            _ready = false;
            _loading = null;
            _status = "Model changed — reload on next use";
        }
    }

    private static void Add(List<double> values, double value)
    {
        values.Add(value);
        if (values.Count > 240)
        {
            values.RemoveRange(0, values.Count - 240);
        }
    }

    private static double Average(List<double> values)
    {
        lock (values)
        {
            return values.Count == 0 ? 0 : values.Average();
        }
    }

    private static double Percentile(List<double> values, double fraction)
    {
        lock (values)
        {
            if (values.Count < 8) return 0;
            var copy = values.ToArray();
            Array.Sort(copy);
            return copy[Math.Clamp((int)Math.Round((copy.Length - 1) * fraction), 0, copy.Length - 1)];
        }
    }

    private static double Ewma(double current, double sample) =>
        current <= 0 ? sample : (current * 0.9) + (sample * 0.1);

    public void Dispose()
    {
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
            _ready = false;
        }
    }
}
