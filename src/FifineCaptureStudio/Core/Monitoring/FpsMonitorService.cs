using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace FifineCaptureStudio.Core.Monitoring;

/// <summary>
/// Owns both measurement paths. PC games use a persistent PresentMon capture selected by the user;
/// console/capture-card sources use the visual cadence estimator. Capture FPS is never substituted for
/// game FPS, and a missing measurement is reported as unavailable.
/// </summary>
public sealed class FpsMonitorService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly VisualCadenceEstimator _cadence = new();
    private readonly byte[] _sample = new byte[VisualCadenceEstimator.Width * VisualCadenceEstimator.Height * 4];
    private readonly object _sampleGate = new();
    private readonly object _processGate = new();
    private PresentMonSource? _presentMon;
    private IReadOnlyList<GameProcessInfo> _processes = Array.Empty<GameProcessInfo>();
    private long _lastCaptureTicks;
    private string _pcStatus = "No PC game selected";
    private int _pcProcessId;
    private string _pcProcessName = string.Empty;

    public FpsMonitorService(SettingsService settings)
    {
        _settings = settings;
        _pcProcessId = settings.Current.FpsMonitor.GameProcessId;
        _pcProcessName = settings.Current.FpsMonitor.GameProcessName;
    }

    public VisualCadenceEstimator Cadence => _cadence;
    public int SelectedProcessId => _pcProcessId;
    public string SelectedProcessName => _pcProcessName;
    public string PcStatus { get { lock (_processGate) return _pcStatus; } }
    public bool PresentMonInstalled => PresentMonTool.IsInstalled;

    public IReadOnlyList<GameProcessInfo> Processes
    {
        get { lock (_processGate) return _processes; }
    }

    /// <summary>Refresh the list of windowed processes the user can choose from. Never includes FIFINE.</summary>
    public IReadOnlyList<GameProcessInfo> RefreshProcesses()
    {
        var current = Process.GetCurrentProcess().Id;
        var list = new List<GameProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == current || process.MainWindowHandle == IntPtr.Zero) continue;
                var title = process.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new GameProcessInfo(process.Id, process.ProcessName, title.Trim()));
            }
            catch
            {
                // Processes can exit or deny access while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        var ordered = list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToArray();
        lock (_processGate) _processes = ordered;
        return ordered;
    }

    /// <summary>Starts (or restarts) PresentMon for the selected process. No elevation is requested.</summary>
    public bool StartPcMonitoring(int processId, string processName)
    {
        StopPcMonitoring();
        lock (_processGate)
        {
            _pcProcessId = processId;
            _pcProcessName = processName;
            _settings.Current.FpsMonitor.GameProcessId = processId;
            _settings.Current.FpsMonitor.GameProcessName = processName;
            _settings.SaveSoon();
        }

        if (!PresentMonTool.IsInstalled)
        {
            lock (_processGate) _pcStatus = "PresentMon is not installed.";
            return false;
        }

        var source = new PresentMonSource();
        var (ok, message) = source.Start(processId, processName);
        lock (_processGate)
        {
            _presentMon = source;
            _pcStatus = message;
        }

        return ok;
    }

    public void StopPcMonitoring()
    {
        PresentMonSource? source;
        lock (_processGate)
        {
            source = _presentMon;
            _presentMon = null;
            if (source is not null) _pcStatus = "PC monitoring stopped.";
        }

        source?.Dispose();
    }

    public void ObserveCaptureFrame(BitmapSource image, long decodedTicks)
    {
        lock (_sampleGate)
        {
            if (!FrameSignatureSampler.TrySample(image, _sample, out _, out _)) return;
            _cadence.Observe(decodedTicks, _sample);
            _lastCaptureTicks = Stopwatch.GetTimestamp();
        }
    }

    public FpsReading GetReading(PlatformKind platform, double streamFps, bool captureActive)
    {
        var cadence = _cadence.Current;
        var captureAge = _lastCaptureTicks == 0
            ? double.MaxValue
            : (Stopwatch.GetTimestamp() - _lastCaptureTicks) / (double)Stopwatch.Frequency;
        var captureFresh = captureActive && captureAge < 2.0;

        if (platform == PlatformKind.PC)
        {
            PresentMonSource? source;
            string status;
            int pid;
            string name;
            lock (_processGate)
            {
                source = _presentMon;
                status = _pcStatus;
                pid = _pcProcessId;
                name = _pcProcessName;
            }

            if (source is not null)
            {
                var pc = source.Snapshot();
                if (pc.Available)
                {
                    return new FpsReading(true, FpsMeasurementSource.PresentMon,
                        $"PresentMon · {name} (PID {pid})", pc.Fps, 1.0, false, false, false,
                        pc.Detail, pc.FrameTimeMs, pc.PresentedFps, pc.DisplayedFps);
                }

                if (!string.IsNullOrEmpty(name))
                {
                    return FpsReading.Unavailable(pc.Detail);
                }
            }

            // PC game FPS is a process-level measurement, never a capture-device or visual estimate.
            // If PresentMon cannot provide it, the honest answer is unavailable.
            return FpsReading.Unavailable(pid > 0 && !string.IsNullOrEmpty(name)
                ? status
                : "Select the PC game process to measure its own FPS. Capture-stream FPS is shown separately.");
        }

        if (!captureFresh)
        {
            return FpsReading.Unavailable(captureActive
                ? "No recent decoded frames."
                : "Start a capture or select a PC game to measure FPS.");
        }

        if (!cadence.Available)
        {
            return FpsReading.Unavailable(cadence.Detail);
        }

        var confidence = Math.Round(cadence.Confidence * 100);
        var label = platform == PlatformKind.PS5 ? "PS5 capture estimate" : "Xbox capture estimate";
        return new FpsReading(true, FpsMeasurementSource.VisualEstimate, label, cadence.Fps,
            cadence.Confidence, true, cadence.IsStreamLimited, cadence.IsStatic,
            cadence.Detail + " · " + confidence + "% confidence",
            cadence.Fps > 0 ? 1000.0 / cadence.Fps : 0, 0, 0);
    }

    public void Dispose()
    {
        StopPcMonitoring();
    }
}
