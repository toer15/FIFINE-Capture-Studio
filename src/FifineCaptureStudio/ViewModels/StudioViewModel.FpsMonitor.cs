using System.Collections.ObjectModel;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Monitoring;
using FifineCaptureStudio.Rendering;

namespace FifineCaptureStudio.ViewModels;

public sealed partial class StudioViewModel
{
    private readonly ObservableCollection<GameProcessInfo> _fpsProcesses = new();
    private GameProcessInfo? _selectedPcGameProcess;
    private bool _presentMonBusy;
    private string _presentMonProgress = string.Empty;

    public IReadOnlyList<GameProcessInfo> PcGameProcesses => _fpsProcesses;
    public IReadOnlyList<OverlayCorner> OverlayCornerOptions { get; } = Enum.GetValues<OverlayCorner>();

    public string PresentMonActionText => PresentMonTool.IsInstalled
        ? "Reinstall PresentMon"
        : "Download PresentMon (1 MB)";

    public string PresentMonStatusText => PresentMonTool.IsInstalled
        ? "PresentMon " + PresentMonTool.Version + " ready"
        : "PresentMon not installed";

    public bool PresentMonBusy => _presentMonBusy;
    public string PresentMonProgress => _presentMonProgress;
    public string PcMonitorStatusText => _services.FpsMonitor.PcStatus;
    public bool PresentMonInstalled => PresentMonTool.IsInstalled;

    public GameProcessInfo? SelectedPcGameProcess
    {
        get => _selectedPcGameProcess;
        set
        {
            if (ReferenceEquals(_selectedPcGameProcess, value)) return;
            _selectedPcGameProcess = value;
            Raise();
            if (value is not null)
            {
                if (Config.Capture.Platform == PlatformKind.PC)
                {
                    _services.FpsMonitor.StartPcMonitoring(value.Id, value.Name);
                }

                Notify(PresentMonTool.IsInstalled
                    ? $"Selected {value.Name} for PC FPS measurement."
                    : "PresentMon is not installed. Download it from FPS monitoring to measure PC games.", true);
            }
            else
            {
                _services.FpsMonitor.StopPcMonitoring();
            }

            RaiseFpsMonitorStats();
        }
    }

    public bool FpsOverlayShown => FpsOverlayVisible && !ShowNoSignal;

    public bool FpsOverlayVisible
    {
        get => Config.FpsMonitor.OverlayVisible;
        set
        {
            if (Config.FpsMonitor.OverlayVisible == value) return;
            Config.FpsMonitor.OverlayVisible = value;
            _services.Settings.SaveSoon();
            Raise();
            RaiseFpsOverlay();
        }
    }

    public OverlayCorner FpsOverlayCorner
    {
        get => Config.FpsMonitor.OverlayCorner;
        set
        {
            if (Config.FpsMonitor.OverlayCorner == value) return;
            Config.FpsMonitor.OverlayCorner = value;
            _services.Settings.SaveSoon();
            Raise();
            RaiseFpsOverlay();
        }
    }

    public double FpsOverlayScale
    {
        get => Config.FpsMonitor.OverlayScale;
        set
        {
            var clamped = Math.Clamp(value, 0.75, 1.5);
            if (Math.Abs(Config.FpsMonitor.OverlayScale - clamped) < 0.001) return;
            Config.FpsMonitor.OverlayScale = clamped;
            _services.Settings.SaveSoon();
            Raise();
            RaiseFpsOverlay();
        }
    }

    // ------------------------------------------------------------------ overlay values

    private FpsReading CurrentGameReading =>
        _services.FpsMonitor.GetReading(Config.Capture.Platform, _services.Session.MeasuredFps, IsPreviewing);

    public string GameFpsDisplay
    {
        get
        {
            var reading = CurrentGameReading;
            if (!reading.Available || reading.Fps is null) return "N/A";
            return reading.IsEstimate ? $"≈{reading.Fps:0}" : $"{reading.Fps:0.0}";
        }
    }

    public string GameFpsQualifier
    {
        get
        {
            var reading = CurrentGameReading;
            if (!reading.Available) return "UNAVAILABLE";
            if (!reading.IsEstimate) return "MEASURED";
            var text = $"EST {reading.Confidence * 100:0}%";
            if (reading.IsStreamLimited) text += " · STREAM-LIMITED";
            else if (reading.IsStatic) text += " · STATIC";
            return text;
        }
    }

    public string FrameGenOutputDisplay
    {
        get
        {
            if (!FrameGenerationActive) return "—";
            var total = _services.FrameGeneration.SourceFps + _services.FrameGeneration.GeneratedFps;
            return total > 0 ? $"{total:0}" : "—";
        }
    }

    /// <summary>The only multiplier the interpolator implements; settings normalise it to 2.</summary>
    public string FrameGenMultiplierDisplay => $"{Math.Max(2, Config.FrameGeneration.Multiplier)}×";

    public bool FrameGenBadgeVisible => FrameGenerationEnabled;

    public bool FrameGenOutputReady => FrameGenerationActive &&
        (_services.FrameGeneration.SourceFps + _services.FrameGeneration.GeneratedFps) > 0;

    public string FrameGenFpsDisplay => FrameGenOutputReady ? FrameGenOutputDisplay : "READY";

    public string FrameGenOutputSuffix => FrameGenOutputReady ? "FPS" : string.Empty;

    /// <summary>Game FPS followed by the generated output only while frame generation is enabled.</summary>
    public string LiveFpsLabel => FrameGenerationEnabled
        ? $"{GameFpsDisplay} FPS · {FrameGenMultiplierDisplay} {FrameGenFpsDisplay}"
        : $"{GameFpsDisplay} FPS";

    public string GeneratedFpsDisplay => FrameGenerationActive && _services.FrameGeneration.GeneratedFps > 0
        ? $"{_services.FrameGeneration.GeneratedFps:0}"
        : "—";

    public string PresentedFpsDisplay
    {
        get
        {
            var presenter = _nativePresenter;
            return presenter is null ? "—" : $"{presenter.PresentedFps:0}";
        }
    }

    public string InputFpsDisplay => _services.Session.MeasuredFps > 0
        ? $"{_services.Session.MeasuredFps:0.0}"
        : "—";

    public string GameFpsDetailText
    {
        get
        {
            var reading = CurrentGameReading;
            return reading.Available
                ? $"{reading.SourceLabel} · {reading.Detail}"
                : reading.Detail;
        }
    }

    public string FpsMonitorSourceText => CurrentGameReading.SourceLabel;
    public string FpsMonitorProcessText => _services.FpsMonitor.SelectedProcessId > 0
        ? $"{_services.FpsMonitor.SelectedProcessName} (PID {_services.FpsMonitor.SelectedProcessId})"
        : "No PC game selected";
    public string FpsMonitorConfidenceText
    {
        get
        {
            var reading = CurrentGameReading;
            return !reading.Available ? "—"
                : reading.IsEstimate ? $"{reading.Confidence * 100:0}%" : "Direct measurement";
        }
    }

    public string FpsMonitorFrameTimeText => CurrentGameReading.Available && CurrentGameReading.FrameTimeMs > 0
        ? $"{CurrentGameReading.FrameTimeMs:0.00} ms"
        : "—";

    public string FpsMonitorDisplayedText => CurrentGameReading is { Available: true, DisplayedFps: > 0 }
        ? $"{CurrentGameReading.DisplayedFps:0.0} fps"
        : "—";

    public string FpsMonitorQueueAgeText => FrameGenerationActive
        ? $"{_services.FrameGeneration.QueueAgeMs:0.0} ms"
        : "—";

    public string FpsMonitorGeneratedText => FrameGenerationActive
        ? $"{_services.FrameGeneration.GeneratedFps:0.0} fps · source {_services.FrameGeneration.SourceFps:0.0}"
        : "—";
    public string FpsMonitorPresentedText => _nativePresenter is null
        ? "—"
        : $"{_nativePresenter.PresentedFps:0.0} fps";
    public string FpsMonitorInputText => InputFpsDisplay + (InputFpsDisplay == "—" ? string.Empty : " fps");
    public string FpsMonitorDroppedText
    {
        get
        {
            var late = _nativePresenter?.MissedDeadlines ?? 0;
            return $"{_services.FrameGeneration.DroppedFrames:N0} gen · {late:N0} late";
        }
    }
    public string FpsMonitorProcessingText => _services.FrameGeneration.AverageInferenceMs > 0
        ? $"{_services.FrameGeneration.AverageInferenceMs:0.00} ms avg / {_services.FrameGeneration.P95InferenceMs:0.00} ms p95"
        : "—";

    public string FpsMonitorRefreshText
    {
        get
        {
            if (_nativePresenter is { DisplayRefreshHz: > 0 } presenter)
            {
                return $"{presenter.DisplayRefreshHz} Hz · {presenter.DisplayName}";
            }

            var refresh = DisplayRefresh.Get(IntPtr.Zero, out var name);
            return $"{refresh} Hz · {name}";
        }
    }

    public string FpsMonitorPresentationBoundaryText => _nativePresenter is null
        ? "FIFINE's native presenter is not open; only the frame generator's processing rate is available."
        : "Presented counts successful Present calls on FIFINE's flip swapchain. Windows can still drop a " +
          "frame at composition; scanout-confirmed display timing is not available through DXGI.";

    public string FpsMonitorLimitText
    {
        get
        {
            var reading = CurrentGameReading;
            if (reading.IsStreamLimited)
                return "The input stream caps the measurement; a faster game cannot be distinguished.";
            if (reading.IsStatic)
                return "Static scene: showing the last stable estimate with reduced confidence.";
            if (reading.IsEstimate)
                return "Estimated from visual frame progression, not the console's internal render counter.";
            return reading.Available ? "Measured from the game's present events." : "No measurement is available.";
        }
    }

    // ------------------------------------------------------------------ commands

    public System.Windows.Input.ICommand RefreshPcProcessesCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand InstallPresentMonCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand ClearPcGameProcessCommand { get; private set; } = null!;

    private void InitializeFpsMonitor()
    {
        RefreshPcProcessesCommand = new AsyncRelayCommand(() => RefreshProcessesAsync(startSaved: true));
        InstallPresentMonCommand = new AsyncRelayCommand(InstallPresentMonAsync);
        ClearPcGameProcessCommand = new RelayCommand(() => SelectedPcGameProcess = null);
        _ = RefreshProcessesAsync(startSaved: true);
    }

    private async Task RefreshProcessesAsync(bool startSaved)
    {
        var list = await Task.Run(_services.FpsMonitor.RefreshProcesses);
        Dispatch(() =>
        {
            _fpsProcesses.Clear();
            foreach (var process in list) _fpsProcesses.Add(process);
            Raise(nameof(PcGameProcesses));

            var settings = Config.FpsMonitor;
            var saved = list.FirstOrDefault(p =>
                p.Id == settings.GameProcessId &&
                string.Equals(p.Name, settings.GameProcessName, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
            {
                _selectedPcGameProcess = saved;
                Raise(nameof(SelectedPcGameProcess));
                if (startSaved && Config.Capture.Platform == PlatformKind.PC)
                {
                    _services.FpsMonitor.StartPcMonitoring(saved.Id, saved.Name);
                }
            }
            else if (_selectedPcGameProcess is not null)
            {
                // The chosen game closed; do not keep a stale PID or its last value on screen.
                _selectedPcGameProcess = null;
                Raise(nameof(SelectedPcGameProcess));
                _services.FpsMonitor.StopPcMonitoring();
            }

            RaiseFpsMonitorStats();
        });
    }

    private async Task InstallPresentMonAsync()
    {
        if (_presentMonBusy) return;
        _presentMonBusy = true;
        _presentMonProgress = "0%";
        Raise(nameof(PresentMonBusy));
        Raise(nameof(PresentMonProgress));
        try
        {
            var progress = new Progress<double>(value => Dispatch(() =>
            {
                _presentMonProgress = (value * 100).ToString("0") + "%";
                Raise(nameof(PresentMonProgress));
            }));
            var (ok, message) = await PresentMonTool.InstallAsync(progress, CancellationToken.None);
            Notify(message, !ok);
            if (ok)
            {
                PresentMonTool.WriteLicense();
                var selected = _selectedPcGameProcess;
                if (selected is not null)
                {
                    _services.FpsMonitor.StartPcMonitoring(selected.Id, selected.Name);
                }
            }
        }
        finally
        {
            _presentMonBusy = false;
            _presentMonProgress = string.Empty;
            Raise(nameof(PresentMonBusy));
            Raise(nameof(PresentMonProgress));
            Raise(nameof(PresentMonActionText));
            Raise(nameof(PresentMonStatusText));
            Raise(nameof(PresentMonInstalled));
            RaiseFpsMonitorStats();
        }
    }

    public void SelectPlatformForFpsMonitor(PlatformKind platform)
    {
        if (platform != PlatformKind.PC)
        {
            _services.FpsMonitor.StopPcMonitoring();
        }
        else if (_selectedPcGameProcess is not null)
        {
            _services.FpsMonitor.StartPcMonitoring(_selectedPcGameProcess.Id, _selectedPcGameProcess.Name);
        }

        RaiseFpsMonitorStats();
    }

    public FpsOverlaySnapshot BuildFpsOverlaySnapshot() => new(
        FpsOverlayVisible,
        FpsOverlayCorner,
        FpsOverlayScale,
        GameFpsDisplay,
        FrameGenOutputDisplay,
        GeneratedFpsDisplay,
        PresentedFpsDisplay,
        InputFpsDisplay,
        GameFpsQualifier,
        FrameGenMultiplierDisplay,
        FrameGenBadgeVisible);

    public void RaiseFpsOverlay()
    {
        Raise(nameof(FpsOverlayVisible));
        Raise(nameof(FpsOverlayShown));
        Raise(nameof(FpsOverlayCorner));
        Raise(nameof(FpsOverlayScale));
        _nativePresenter?.UpdateOverlay(BuildFpsOverlaySnapshot());
    }

    public void RaiseFpsMonitorStats()
    {
        Raise(nameof(FpsOverlayShown));
        Raise(nameof(GameFpsDisplay));
        Raise(nameof(GameFpsQualifier));
        Raise(nameof(FrameGenOutputDisplay));
        Raise(nameof(FrameGenMultiplierDisplay));
        Raise(nameof(FrameGenBadgeVisible));
        Raise(nameof(FrameGenFpsDisplay));
        Raise(nameof(FrameGenOutputReady));
        Raise(nameof(FrameGenOutputSuffix));
        Raise(nameof(LiveFpsLabel));
        Raise(nameof(GeneratedFpsDisplay));
        Raise(nameof(PresentedFpsDisplay));
        Raise(nameof(InputFpsDisplay));
        Raise(nameof(GameFpsDetailText));
        Raise(nameof(FpsMonitorSourceText));
        Raise(nameof(FpsMonitorProcessText));
        Raise(nameof(FpsMonitorConfidenceText));
        Raise(nameof(FpsMonitorFrameTimeText));
        Raise(nameof(FpsMonitorDisplayedText));
        Raise(nameof(FpsMonitorQueueAgeText));
        Raise(nameof(FpsMonitorGeneratedText));
        Raise(nameof(FpsMonitorPresentedText));
        Raise(nameof(FpsMonitorInputText));
        Raise(nameof(FpsMonitorDroppedText));
        Raise(nameof(FpsMonitorProcessingText));
        Raise(nameof(FpsMonitorRefreshText));
        Raise(nameof(FpsMonitorLimitText));
        Raise(nameof(FpsMonitorPresentationBoundaryText));
        Raise(nameof(PcMonitorStatusText));
        Raise(nameof(PresentMonInstalled));
        Raise(nameof(PresentMonActionText));
        Raise(nameof(PresentMonStatusText));
        Raise(nameof(FpsChipLabel));
        Raise(nameof(FpsChipLive));
        Raise(nameof(OutputFpsLabel));
        _nativePresenter?.UpdateOverlay(BuildFpsOverlaySnapshot());
    }
}
