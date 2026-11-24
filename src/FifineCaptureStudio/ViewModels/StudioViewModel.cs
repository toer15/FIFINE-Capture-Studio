using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FifineCaptureStudio.Capture;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.ViewModels;

public sealed record SourceKindOption(CaptureSourceKind Kind, string Label)
{
    public override string ToString() => Label;
}

public sealed record ResolutionOption(string Label, int Width, int Height, int MaxFps)
{
    public override string ToString() => Label;
}

public sealed record FpsOption(int Fps, string Label)
{
    public override string ToString() => Label;
}

public sealed record CodecOption(VideoCodecKind Kind, string Label, bool Available)
{
    public override string ToString() => Label;
}

public sealed record ContainerOption(ContainerKind Kind, string Label)
{
    public override string ToString() => Label;
}

public sealed record EncoderOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record BitrateOption(int Kbps, string Label)
{
    public override string ToString() => Label;
}

public sealed record RateControlOption(RateControlMode Mode, string Label)
{
    public override string ToString() => Label;
}

public sealed class Toast
{
    public Toast(string title, string message, bool isError)
    {
        Title = title;
        Message = message;
        IsError = isError;
        CreatedAt = DateTime.Now;
    }

    public string Title { get; }
    public string Message { get; }
    public bool IsError { get; }
    public DateTime CreatedAt { get; }
    public string Accent => IsError ? "#FFFF4968" : "#FF8275FF";
    public string Glyph => IsError ? "\uE7BA" : "\uE930";
}

public sealed record EncoderRow(string DisplayName, string Vendor, bool Hardware, bool Verified)
{
    public string StatusText => Verified ? "ready" : "listed only";
}

public sealed record NavItem(string Title, string IconKey, string Page)
{
    public override string ToString() => Title;
}

public sealed partial class StudioViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _sessionClock = Stopwatch.StartNew();
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _gpuHistory = new();
    private readonly List<double> _fpsHistory = new();
    private readonly List<double> _latencyHistory = new();
    private readonly List<double> _ramHistory = new();

    private int _framePending;
    private BitmapSource? _previewImage;
    private bool _hasPreview;
    private ControllerState _controller = new();
    private SystemSnapshot _system = new(0, -1, -1, 0, 16, 0, 0, DateTime.Now);
    private string _currentPage = "dashboard";
    private bool _isBusy;
    private string _busyLabel = string.Empty;
    private string _wizardTestResult = string.Empty;
    private bool _isInstallingEngine;
    private string _engineProgress = string.Empty;
    private string _recordingTimeText = "00:00:00";
    private string _diskFreeText = "—";
    private string _statusText = "Starting…";
    private DateTime _lastToast = DateTime.MinValue;
    private string? _lastPreviewError;

    public StudioViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Restore the desktop state the user left behind, before anything binds to it.
        var ui = services.Settings.Current.Ui;
        _currentPage = NormalizePage(ui.LastPage);
        _previewScale = ui.PreviewScale;
        _keepAspect = ui.KeepAspect;
        _minimalPreview = ui.MinimalPreview;
        _settingsCategory = string.IsNullOrWhiteSpace(ui.SettingsCategory) ? "General" : ui.SettingsCategory;

        VideoDevices = new ObservableCollection<DShowDevice>();
        Displays = new ObservableCollection<DisplayInfo>();
        Windows = new ObservableCollection<WindowInfo>();
        ResolutionOptions = new ObservableCollection<ResolutionOption>();
        FpsOptions = new ObservableCollection<FpsOption>();
        Toasts = new ObservableCollection<Toast>();
        InitializeLibrary();

        SourceKinds = new ObservableCollection<SourceKindOption>
        {
            new(CaptureSourceKind.CaptureCard, "HDMI capture card"),
            new(CaptureSourceKind.ConsoleStream, "Console Remote Play stream"),
            new(CaptureSourceKind.Display, "Desktop display"),
            new(CaptureSourceKind.Window, "Game window"),
            new(CaptureSourceKind.TestPattern, "Test pattern")
        };

        Containers = new ObservableCollection<ContainerOption>
        {
            new(ContainerKind.Mp4, "MP4"),
            new(ContainerKind.Mkv, "MKV")
        };

        RateControls = new ObservableCollection<RateControlOption>
        {
            new(RateControlMode.Preset, "Auto (recommended)"),
            new(RateControlMode.Custom, "Custom bitrate"),
            new(RateControlMode.Quality, "Quality based (constant quality)")
        };

        BitrateOptions = new ObservableCollection<BitrateOption>
        {
            new(12000, "12 Mbps"),
            new(16000, "16 Mbps"),
            new(20000, "20 Mbps"),
            new(30000, "30 Mbps"),
            new(40000, "40 Mbps"),
            new(60000, "60 Mbps"),
            new(80000, "80 Mbps")
        };

        ReplayDurations = new ObservableCollection<FpsOption>
        {
            new(15, "15s"),
            new(30, "30s"),
            new(60, "60s"),
            new(120, "2m"),
            new(300, "5m")
        };

        EncoderOptions = new ObservableCollection<EncoderOption> { new("Auto", "Auto (hardware first)") };
        Codecs = new ObservableCollection<CodecOption>();

        NavigateCommand = new RelayCommand(p => CurrentPage = p as string ?? "dashboard");
        ToggleFullscreenCommand = new RelayCommand(() => FullScreenRequested?.Invoke());
        SelectPlatformCommand = new AsyncRelayCommand(p => SelectPlatformAsync(p as string));
        SetSourceKindCommand = new AsyncRelayCommand(p => SetSourceKindAsync(p as string));
        StartCaptureCommand = new AsyncRelayCommand(StartCaptureAsync);
        StopCaptureCommand = new AsyncRelayCommand(StopCaptureAsync);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        ToggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync);
        PauseResumeCommand = new AsyncRelayCommand(PauseResumeAsync);
        SaveReplayCommand = new AsyncRelayCommand(SaveReplayAsync);
        ToggleReplayBufferCommand = new AsyncRelayCommand(ToggleReplayBufferAsync);
        ToggleMuteMicCommand = new RelayCommand(ToggleMuteMic);
        InstallEngineCommand = new AsyncRelayCommand(InstallEngineAsync);
        OpenRecordingsFolderCommand = new RelayCommand(OpenRecordingsFolder);
        SaveSettingsCommand = new RelayCommand(() =>
        {
            var result = _services.Settings.TrySave();
            if (result.Success)
            {
                Notify("Settings saved", false);
            }
            // A failed save is surfaced by the SaveFailed subscription below, including the cause.
        });
        ResetLowLatencyCommand = new RelayCommand(ResetLowLatency);
        ResetFolderCommand = new RelayCommand(() => RecordingFolder = AppPaths.DefaultRecordingsDir);
        RestartAudioCommand = new AsyncRelayCommand(RestartAudioAsync);

        WizardNextCommand = new AsyncRelayCommand(WizardNextAsync);
        WizardBackCommand = new RelayCommand(() => { if (WizardStep > 1) WizardStep--; });
        RunCaptureTestCommand = new AsyncRelayCommand(RunCaptureTestAsync);
        FinishWizardCommand = new AsyncRelayCommand(FinishWizardAsync);
        InitializeEnhancement();
        InitializeFrameGeneration();
        InitializeFpsMonitor();

        // Onboarding is an overlay that must not be visible until settings say it is due.
        // The default state is hidden, so a slow or failed start-up can never leave it on screen.
        ShowWizard = ShouldShowOnboarding();

        _services.Session.StateChanged += () => Dispatch(RaiseCaptureState);
        _services.Session.FrameArrived += OnFrameArrived;
        _services.Session.Notice += (message, error) => Dispatch(() => Notify(message, error));
        _services.Performance.Sampled += OnSystemSampled;
        _services.Controllers.StateChanged += state => _controller = state;
        _services.Settings.Saved += _ => Dispatch(RaiseAllSettings);
        _services.Settings.SaveFailed += message => Dispatch(() => Notify(message, true));
        _services.Encoders.VerificationChanged += () => Dispatch(RefreshEncoderRows);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _uiTimer.Tick += (_, _) => Tick();
        System.Windows.Media.CompositionTarget.Rendering += OnCompositionRendering;
    }

    // ------------------------------------------------------------------ state

    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            value = NormalizePage(value);
            if (!Set(ref _currentPage, value))
            {
                return;
            }

            Config.Ui.LastPage = value;
            _services.Settings.SaveSoon();
            Raise(nameof(PageTitle));
            Raise(nameof(PageSubtitle));
            if (value == "library")
            {
                _ = RefreshLibraryAsync();
            }
        }
    }

    public AppSettings Config => _services.Settings.Current;

    public IReadOnlyList<NavItem> NavItems { get; } = new List<NavItem>
    {
        new("Dashboard", "IconHome", "dashboard"),
        new("Live Capture", "IconMonitor", "capture"),
        new("Recording Studio", "IconRecord", "recording"),
        new("Audio Studio", "IconWave", "audio"),
        new("Gaming Sources", "IconPad", "sources"),
        new("Gaming Enhancement", "IconSparkle", "enhance"),
        new("Performance Monitor", "IconChart", "performance"),
        new("Recordings Library", "IconLibrary", "library"),
        new("Settings", "IconSettings", "settings")
    };

    public bool ShowPlatformGuide => true;

    private double _previewScale = 1.0;
    private bool _keepAspect = true;

    public double PreviewScale
    {
        get => _previewScale;
        set
        {
            var clamped = Math.Clamp(value, 0.3, 1.0);
            if (!Set(ref _previewScale, clamped))
            {
                return;
            }

            Config.Ui.PreviewScale = clamped;
            _services.Settings.SaveSoon();
            Raise(nameof(PreviewScaleLabel));
        }
    }

    public bool KeepAspect
    {
        get => _keepAspect;
        set
        {
            if (!Set(ref _keepAspect, value))
            {
                return;
            }

            Config.Ui.KeepAspect = value;
            _services.Settings.SaveSoon();
        }
    }

    public IReadOnlyList<string> PreviewScales { get; } = new[] { "50%", "75%", "100%" };

    private bool _minimalPreview;

    public bool MinimalPreview
    {
        get => _minimalPreview;
        set
        {
            if (!Set(ref _minimalPreview, value))
            {
                return;
            }

            Config.Ui.MinimalPreview = value;
            _services.Settings.SaveSoon();
        }
    }

    /// <summary>Any unknown page name (older file, newer build, typo) falls back to the dashboard.</summary>
    private static string NormalizePage(string? page)
    {
        var candidate = (page ?? string.Empty).Trim().ToLowerInvariant();
        return AppSettings.KnownPages.Contains(candidate, StringComparer.Ordinal) ? candidate : "dashboard";
    }

    public IReadOnlyList<string> SettingsCategories { get; } = new[]
    {
        "General", "Capture", "Recording", "Audio", "Performance", "Gaming", "Updates", "Shortcuts", "Engine"
    };

    private string _settingsCategory = "General";

    public string SettingsCategory
    {
        get => _settingsCategory;
        set
        {
            if (!Set(ref _settingsCategory, value))
            {
                return;
            }

            Config.Ui.SettingsCategory = value;
            _services.Settings.SaveSoon();
        }
    }

    public ObservableCollection<EncoderRow> AvailableEncoders { get; } = new();

    public ICommand ResetFolderCommand { get; private set; } = null!;

    public string PreviewScaleLabel
    {
        get => $"{PreviewScale * 100:0}%";
        set
        {
            if (double.TryParse(value?.Replace("%", ""), out var percent))
            {
                PreviewScale = percent / 100.0;
                Raise();
            }
        }
    }
    public AppServices Services => _services;

    public string AppVersion => "v" + Log.Version();
    public string PageTitle => CurrentPage switch
    {
        "dashboard" => "Dashboard",
        "capture" => "Live Capture",
        "recording" => "Recording Studio",
        "audio" => "Audio Studio",
        "sources" => "Gaming Sources",
        "enhance" => "Gaming Enhancement",
        "performance" => "Performance Monitor",
        "library" => "Recordings Library",
        "settings" => "Settings",
        _ => "Dashboard"
    };

    public string PageSubtitle => CurrentPage switch
    {
        "dashboard" => "Your complete gaming capture command centre",
        "capture" => "Low-latency preview, scaling and signal information",
        "recording" => "Encoder, container and destination control",
        "audio" => "Capture, mix and monitor every audio source",
        "sources" => "PC, PlayStation 5 and Xbox capture setups",
        "enhance" => "Real-time GPU image enhancement, super resolution and colour",
        "performance" => "Measured system and pipeline load",
        "library" => "Every clip saved by this studio",
        "settings" => "Preferences, engine and shortcuts",
        _ => string.Empty
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string BusyLabel
    {
        get => _busyLabel;
        private set => Set(ref _busyLabel, value);
    }

    // ------------------------------------------------------------------ capture sources

    public ObservableCollection<DShowDevice> VideoDevices { get; }
    public ObservableCollection<DisplayInfo> Displays { get; }
    public ObservableCollection<WindowInfo> Windows { get; }
    public ObservableCollection<ResolutionOption> ResolutionOptions { get; }
    public ObservableCollection<FpsOption> FpsOptions { get; }
    public ObservableCollection<SourceKindOption> SourceKinds { get; }

    public SourceKindOption? SelectedSourceKind
    {
        get => SourceKinds.FirstOrDefault(k => k.Kind == Config.Capture.SourceKind) ?? SourceKinds[0];
        set
        {
            if (value is null || value.Kind == Config.Capture.SourceKind)
            {
                return;
            }

            Config.Capture.SourceKind = value.Kind;
            _services.Settings.Save();
            Raise();
            Raise(nameof(SourceKindHint));
            Raise(nameof(DeviceCardTitle));
            Raise(nameof(DeviceCardSubtitle));
            _ = RebuildSourceOptionsAsync(restart: true);
        }
    }

    public DShowDevice? SelectedVideoDevice
    {
        get => VideoDevices.FirstOrDefault(d => d.Name == Config.Capture.VideoDeviceName);
        set
        {
            if (value is null || value.Name == Config.Capture.VideoDeviceName)
            {
                return;
            }

            Config.Capture.VideoDeviceName = value.Name;
            Config.Capture.VideoDeviceId = value.Id;
            Config.Capture.SourceKind = CaptureSourceKind.CaptureCard;
            _services.Settings.Save();
            Raise();
            Raise(nameof(SelectedSourceKind));
            Raise(nameof(DeviceCardTitle));
            Raise(nameof(DeviceCardSubtitle));
            Raise(nameof(DeviceConnected));
            _ = RebuildSourceOptionsAsync(restart: true);
        }
    }

    public DisplayInfo? SelectedDisplay
    {
        get => Displays.FirstOrDefault(d => d.Index == Config.Capture.DisplayIndex) ?? Displays.FirstOrDefault();
        set
        {
            if (value is null)
            {
                return;
            }

            Config.Capture.DisplayIndex = value.Index;
            Config.Capture.SourceKind = CaptureSourceKind.Display;
            _services.Settings.Save();
            Raise();
            Raise(nameof(SelectedSourceKind));
            Raise(nameof(DeviceCardTitle));
            _ = RebuildSourceOptionsAsync(restart: true);
        }
    }

    public WindowInfo? SelectedWindow
    {
        get => Windows.FirstOrDefault(w => w.Title == Config.Capture.WindowTitle);
        set
        {
            if (value is null)
            {
                return;
            }

            Config.Capture.WindowTitle = value.Title;
            Config.Capture.SourceKind = CaptureSourceKind.Window;
            _services.Settings.Save();
            Raise();
            Raise(nameof(SelectedSourceKind));
            Raise(nameof(DeviceCardTitle));
            _ = RebuildSourceOptionsAsync(restart: true);
        }
    }

    public string InputFormatsLabel => "Auto · MJPG · YUY2";

    public ResolutionOption? SelectedResolution
    {
        get => ResolutionOptions.FirstOrDefault(r =>
                   r.Width == Config.Capture.ResolutionWidth && r.Height == Config.Capture.ResolutionHeight)
               ?? ResolutionOptions.FirstOrDefault();
        set
        {
            if (value is null)
            {
                return;
            }

            Config.Capture.ResolutionWidth = value.Width;
            Config.Capture.ResolutionHeight = value.Height;
            Config.Recording.Width = value.Width;
            Config.Recording.Height = value.Height;
            if (Config.Capture.Fps > value.MaxFps && value.MaxFps > 0)
            {
                Config.Capture.Fps = value.MaxFps;
                Config.Recording.Fps = value.MaxFps;
            }

            _services.Settings.Save();
            Raise();
            RebuildFpsOptions();
            Raise(nameof(ResolutionLabel));
            Raise(nameof(OutputResolutionLabel));
            _ = RestartIfRunningAsync();
        }
    }

    public FpsOption? SelectedFps
    {
        get => FpsOptions.FirstOrDefault(f => f.Fps == Config.Capture.Fps) ?? FpsOptions.FirstOrDefault();
        set
        {
            if (value is null)
            {
                return;
            }

            Config.Capture.Fps = value.Fps;
            Config.Recording.Fps = value.Fps;
            _services.Settings.Save();
            Raise();
            Raise(nameof(FpsChipLabel));
            _ = RestartIfRunningAsync();
        }
    }

    public bool HdrEnabled
    {
        get => Config.Capture.HdrEnabled;
        set
        {
            Config.Capture.HdrEnabled = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public bool IsHdrAvailable => false; // no HDR path in the current capture chain
    public string HdrHint => "HDR is not available on the current capture path. The MJPEG preview and the H.264/H.265 encoders run in 8-bit SDR.";

    public bool IsSourceCaptureCard => Config.Capture.SourceKind == CaptureSourceKind.CaptureCard;
    public bool IsSourceDisplay => Config.Capture.SourceKind == CaptureSourceKind.Display;
    public bool IsSourceWindow => Config.Capture.SourceKind == CaptureSourceKind.Window;
    public bool IsSourceTest => Config.Capture.SourceKind == CaptureSourceKind.TestPattern;
    public bool IsSourceDropdown => IsSourceDisplay || IsSourceWindow;

    public ICommand ToggleFullscreenCommand { get; private set; } = null!;

    public string SourceKindHint => Config.Capture.SourceKind switch
    {
        CaptureSourceKind.CaptureCard => "HDMI capture card via DirectShow. The card's own driver reports which modes are available.",
        CaptureSourceKind.ConsoleStream => "PlayStation Remote Play through the Remote Play engine: compressed console video is decoded by the same FFmpeg pipeline used everywhere else.",
        CaptureSourceKind.Display => "Captures a Windows display with GDI desktop capture. Useful for PC gameplay in windowed or borderless mode.",
        CaptureSourceKind.Window => "Captures a single game window. PC games in fullscreen exclusive mode may need borderless instead.",
        _ => "Generated test pattern — a real source used to verify preview and recording without hardware."
    };

    public string DeviceCardTitle => Config.Capture.SourceKind switch
    {
        CaptureSourceKind.CaptureCard => string.IsNullOrWhiteSpace(Config.Capture.VideoDeviceName)
            ? "No capture device"
            : Config.Capture.VideoDeviceName,
        CaptureSourceKind.Display => SelectedDisplay is null
            ? "No display"
            : $"Display {SelectedDisplay.Index + 1} · {SelectedDisplay.Name}",
        CaptureSourceKind.Window => string.IsNullOrWhiteSpace(Config.Capture.WindowTitle)
            ? "No game window selected"
            : Config.Capture.WindowTitle,
        _ => "Test pattern (verification source)"
    };

    public string DeviceCardSubtitle => Config.Capture.SourceKind switch
    {
        CaptureSourceKind.CaptureCard => string.IsNullOrWhiteSpace(Config.Capture.VideoDeviceName)
            ? "Connect a UVC / DirectShow capture card"
            : "HDMI capture · DirectShow",
        CaptureSourceKind.Display => "Windows desktop capture",
        CaptureSourceKind.Window => "Single window capture",
        _ => "Generated by FFmpeg — proves the pipeline without hardware"
    };

    public bool DeviceConnected => Config.Capture.SourceKind switch
    {
        CaptureSourceKind.CaptureCard => !string.IsNullOrWhiteSpace(Config.Capture.VideoDeviceName)
                                          && VideoDevices.Any(d => d.Name == Config.Capture.VideoDeviceName),
        CaptureSourceKind.Display => SelectedDisplay is not null,
        CaptureSourceKind.Window => !string.IsNullOrWhiteSpace(Config.Capture.WindowTitle),
        _ => true
    };

    public string ConnectionText => DeviceConnected ? "Connected" : "Not connected";
    public string DeviceConnectionText => _services.Session.IsPreviewing ? "Signal OK" : ConnectionText;

    public string InputResolutionLabel
    {
        get
        {
            var w = Config.Capture.ResolutionWidth;
            var h = Config.Capture.ResolutionHeight;
            return $"{w}×{h}";
        }
    }

    public string OutputResolutionLabel => Config.Recording.Width > 0 && Config.Recording.Height > 0
        ? $"{Config.Recording.Width}×{Config.Recording.Height}"
        : InputResolutionLabel;

    /// <summary>Live FPS shown next to Output in the app status bar (not over the game picture).</summary>
    public string OutputFpsLabel
    {
        get
        {
            var measured = _services.Session.MeasuredFps;
            var fps = measured > 0 ? measured : Config.Capture.Fps;
            return measured > 0 ? $"{fps:0.#} FPS" : $"{fps} FPS (set)";
        }
    }
    public string FpsChipLabel => $"{Config.Capture.Fps} FPS";
    public string ResolutionLabel => $"{Config.Capture.ResolutionWidth} × {Config.Capture.ResolutionHeight}";
    public string PlatformChipLabel => Config.Capture.Platform switch
    {
        PlatformKind.PS5 => "PlayStation 5",
        PlatformKind.Xbox => "Xbox",
        _ => "PC"
    };

    public string CaptureFormatSummary { get; private set; } = "Not probed yet.";
    public string CaptureLatencyNote { get; private set; } = string.Empty;

    // ------------------------------------------------------------------ platform

    public bool IsPlatformPS5 => Config.Capture.Platform == PlatformKind.PS5;
    public bool IsPlatformXbox => Config.Capture.Platform == PlatformKind.Xbox;
    public bool IsPlatformPC => Config.Capture.Platform == PlatformKind.PC;

    public string PlatformTitle => Config.Capture.Platform switch
    {
        PlatformKind.PS5 => "PlayStation 5",
        PlatformKind.Xbox => "Xbox",
        _ => "PC Gaming"
    };

    public string PlatformConnectionGuide => Config.Capture.Platform switch
    {
        PlatformKind.PS5 =>
            "PS5 HDMI OUT  →  capture card HDMI IN\n" +
            "Capture card HDMI OUT  →  gaming monitor (passthrough)\n" +
            "Capture card USB  →  this PC",
        PlatformKind.Xbox =>
            "Xbox HDMI OUT  →  capture card HDMI IN\n" +
            "Capture card HDMI OUT  →  TV or monitor (passthrough)\n" +
            "Capture card USB  →  this PC",
        _ =>
            "PC graphics output  →  capture card HDMI IN (external capture)\n" +
            "or capture the PC display directly with desktop / window capture\n" +
            "Controller stays connected to the PC"
    };

    public string PlatformHdcpNote => Config.Capture.Platform == PlatformKind.PC
        ? "Protected content on Windows (DRM) cannot be captured. Use borderless windowed mode when a game blocks capture."
        : "If the console reports HDCP-protected content the signal cannot be captured. Disable HDCP on the console (Settings → System → Enable HDCP: off). This app never bypasses HDCP.";

    public string PlatformCapabilityNote =>
        "Passthrough resolution is what the card sends to your monitor. USB capture resolution is what the card exposes to Windows — both are read from the driver.";

    // ------------------------------------------------------------------ live metrics

    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (Set(ref _previewImage, value))
            {
                Raise(nameof(HasPreviewFrame));
            }
        }
    }

    public bool HasPreviewFrame => _previewImage is not null && _services.Session.HasSignal;
    public bool ShowNoSignal => !_services.Session.HasSignal;

    public string CaptureStateText => _services.Session.StatusLine;
    public bool IsPreviewing => _services.Session.IsPreviewing;
    public bool IsRecording => _services.Session.IsRecording;
    public bool IsPaused => _services.Session.IsPaused;

    public string RecordingTimeText
    {
        get => _recordingTimeText;
        private set => Set(ref _recordingTimeText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string DroppedFramesText
    {
        get
        {
            var dropped = _services.Session.IsRecording
                ? _services.Session.RecordingDropped
                : _services.Session.PreviewGapCount;
            var frames = Math.Max(1, _services.Session.FramesDecoded) + dropped;
            var percent = dropped * 100.0 / frames;
            return $"{dropped} ({percent:0.0}%)";
        }
    }

    public string FpsText => _services.Session.MeasuredFps > 0
        ? $"{_services.Session.MeasuredFps:0.#}"
        : "—";

    public string FpsChipLive => _services.Session.MeasuredFps > 0
        ? $"FPS {_services.Session.MeasuredFps:0}"
        : $"FPS {Config.Capture.Fps}";

    public string LatencyText => _services.Session.PresentLatencyMs > 0
        ? $"{_services.Session.PresentLatencyMs:0.0} ms"
        : "—";

    public string EstimatedCaptureLatencyText => $"~{_services.Session.EstimatedCaptureLatencyMs:0} ms (est.)";

    public double PresentLatencyMs => _services.Session.PresentLatencyMs;
    public double FrameTimeMs => _services.Session.FrameTimeMs;
    public double JitterMs => _services.Session.JitterMs;
    public double DecodeMs => _services.Session.DecodeMs;

    public string AudioStatusText
    {
        get
        {
            var live = AudioChannels.Count(c => c.IsLive);
            return live == 0 ? "No audio" : live == AudioChannels.Count ? "Audio OK" : $"{live}/{AudioChannels.Count} audio";
        }
    }

    public int CpuPercent => (int)Math.Round(Math.Max(0, _system.CpuPercent));
    public int GpuPercent => _system.GpuPercent < 0 ? -1 : (int)Math.Round(_system.GpuPercent);
    public int GpuEncodePercent => _system.GpuEncodePercent < 0 ? -1 : (int)Math.Round(_system.GpuEncodePercent);
    public int RamPercent => _system.RamTotalGb <= 0 ? 0 : (int)Math.Round(_system.RamUsedGb * 100 / _system.RamTotalGb);
    public string RamText => $"{_system.RamUsedGb:0.0} / {_system.RamTotalGb:0.0} GB";
    public string CpuName => _services.Performance.CpuName;
    public string GpuName => _services.Performance.GpuName;

    public string GpuPercentText => _system.GpuPercent < 0 ? "n/a" : $"{GpuPercent}%";
    public string CpuPercentText => $"{CpuPercent}%";
    public string RamPercentText => $"{RamPercent}%";

    public string DiskFreeText
    {
        get => _diskFreeText;
        private set => Set(ref _diskFreeText, value);
    }

    public string RecordingSizeText => FormatBytes(_services.Session.RecordingBytes);
    public string RecordingBitrateText => _services.Session.RecordingBitrateMbps > 0
        ? $"{_services.Session.RecordingBitrateMbps:0.0} Mbps"
        : "—";

    public string RecordingEncoderText => _services.Session.ActiveEncoder ?? "—";
    public string RecordingFpsText => _services.Session.RecordingFps > 0
        ? $"{_services.Session.RecordingFps:0}"
        : "—";

    public string EncoderUtilizationText => GpuEncodePercent < 0 ? "n/a" : $"{GpuEncodePercent}%";

    public string SystemGrade
    {
        get
        {
            if (_system.CpuPercent > 88 || (_system.GpuPercent > 94))
            {
                return "Overloaded";
            }

            if (_system.CpuPercent > 70 || _system.GpuPercent > 85)
            {
                return "Busy";
            }

            return _system.CpuPercent < 35 ? "Excellent" : "Good";
        }
    }

    public bool SystemWarningVisible => SystemGrade is "Busy" or "Overloaded";
    public string SystemWarning => SystemGrade switch
    {
        "Overloaded" => "The system is saturated. Recording may drop frames — reduce resolution, FPS or bitrate.",
        "Busy" => "High system load. Frame pacing is being prioritised over maximum FPS.",
        _ => string.Empty
    };

    public IReadOnlyList<double> CpuHistory => _cpuHistory;
    public IReadOnlyList<double> GpuHistory => _gpuHistory;
    public IReadOnlyList<double> FpsHistory => _fpsHistory;
    public IReadOnlyList<double> LatencyHistory => _latencyHistory;
    public IReadOnlyList<double> RamHistory => _ramHistory;

    // ------------------------------------------------------------------ controller

    public bool ControllerConnected => _controller.Connected;
    public string ControllerName => _controller.Connected ? _controller.Name : "No controller detected";
    public string ControllerTransport => _controller.Transport;
    public string ControllerKindText => _controller.Kind switch
    {
        ControllerKind.DualSense => "DualSense (HID)",
        ControllerKind.DualShock => "DualShock 4 (HID)",
        ControllerKind.Xbox => "XInput",
        _ => "—"
    };

    public double StickLeftX => _controller.LeftX;
    public double StickLeftY => _controller.LeftY;
    public double StickRightX => _controller.RightX;
    public double StickRightY => _controller.RightY;
    public double TriggerLeft => _controller.LeftTrigger;
    public double TriggerRight => _controller.RightTrigger;
    public bool ButtonA => _controller.A;
    public bool ButtonB => _controller.B;
    public bool ButtonX => _controller.X;
    public bool ButtonY => _controller.Y;
    public bool BumperLeft => _controller.LeftBumper;
    public bool BumperRight => _controller.RightBumper;
    public string PollRateText => _controller.PollRateHz > 0 ? $"{_controller.PollRateHz:0} Hz" : "—";
    public string StickLeftReadout => $"({_controller.LeftX:0.00}, {_controller.LeftY:0.00})";
    public string StickRightReadout => $"({_controller.RightX:0.00}, {_controller.RightY:0.00})";
    public string TriggerReadout => $"L2 {_controller.LeftTrigger * 100:0}% · R2 {_controller.RightTrigger * 100:0}%";
    public string ControllerHint => Config.Capture.Platform == PlatformKind.PC
        ? "Windows exposes controllers through XInput and HID. Monitoring only observes input — it is never routed through the capture pipeline."
        : "Console controllers stay paired to the console. HDMI carries video and audio only, so button events are not available here.";

    // ------------------------------------------------------------------ toasts

    public ObservableCollection<Toast> Toasts { get; }

    public void Notify(string message, bool isError)
    {
        if (!Config.General.Notifications && !isError)
        {
            return;
        }

        // Collapse repeats: a periodic rescan should not stack identical toasts.
        if (Toasts.Any(t => t.Message == message && (DateTime.Now - t.CreatedAt).TotalSeconds < 8))
        {
            return;
        }

        var toast = new Toast(isError ? "Something needs attention" : "FIFINE Capture Studio", message, isError);
        Toasts.Insert(0, toast);
        _lastToast = DateTime.Now;
        while (Toasts.Count > 3)
        {
            Toasts.RemoveAt(Toasts.Count - 1);
        }

        Log.Info((isError ? "NOTICE: " : "INFO: ") + message);
    }

    // ------------------------------------------------------------------ commands

    public ICommand NavigateCommand { get; }
    public ICommand SelectPlatformCommand { get; }
    public ICommand SetSourceKindCommand { get; }
    public ICommand StartCaptureCommand { get; }
    public ICommand StopCaptureCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand ToggleRecordingCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand SaveReplayCommand { get; }
    public ICommand ToggleReplayBufferCommand { get; }
    public ICommand ToggleMuteMicCommand { get; }
    public ICommand InstallEngineCommand { get; }
    public ICommand OpenRecordingsFolderCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetLowLatencyCommand { get; }
    public ICommand RestartAudioCommand { get; }
    public ICommand WizardNextCommand { get; }
    public ICommand WizardBackCommand { get; }
    public ICommand RunCaptureTestCommand { get; }
    public ICommand FinishWizardCommand { get; }

    // ------------------------------------------------------------------ lifecycle

    public async Task InitializeAsync()
    {
        if (!_services.Ffmpeg.IsAvailable)
        {
            StatusText = "FFmpeg engine missing";
            Notify("The capture engine (FFmpeg) is not installed. Open Settings and install it to start capturing.", true);
        }

        RefreshDevices();
        await RefreshEncoderOptionsAsync();
        await RebuildSourceOptionsAsync(restart: false);

        if (_services.Audio.IsRunning == false)
        {
            await _services.Audio.StartAsync();
        }

        // Channels are created by the engine, so the mixer list is built afterwards.
        LoadAudioSettings();

        StartHotkeys();

        ShowWizard = ShouldShowOnboarding();
        StatusText = _services.Ffmpeg.IsAvailable ? "Capture ready" : "Engine missing";

        if (AutoStartSource())
        {
            await StartCaptureAsync();
        }

        _services.Controllers.StateChanged += ForwardLocalController;
        _uiTimer.Start();
        RaiseAllSettings();
        InitializeUpdates();
    }

    private bool AutoStartSource()
    {
        if (!_services.Ffmpeg.IsAvailable)
        {
            return false;
        }

        return Config.Capture.SourceKind switch
        {
            CaptureSourceKind.CaptureCard => !string.IsNullOrWhiteSpace(Config.Capture.VideoDeviceName)
                                             && VideoDevices.Any(d => d.Name == Config.Capture.VideoDeviceName),
            CaptureSourceKind.Display => SelectedDisplay is not null,
            CaptureSourceKind.Window => !string.IsNullOrWhiteSpace(Config.Capture.WindowTitle),
            _ => true
        };
    }

    private async Task StartCaptureAsync()
    {
        if (!_services.Ffmpeg.IsAvailable)
        {
            Notify("Install the capture engine first (Settings → Capture engine).", true);
            CurrentPage = "settings";
            return;
        }

        IsBusy = true;
        BusyLabel = "Connecting to capture device…";
        try
        {
            if (!_services.Audio.IsRunning)
            {
                await _services.Audio.StartAsync();
            }

            var started = await _services.Session.StartPreviewAsync();
            StatusText = started ? "Capture ready" : "Capture failed";
            if (!started && _services.Session.LastError is { } error)
            {
                Notify(error, true);
            }
        }
        finally
        {
            IsBusy = false;
            RaiseCaptureState();
        }
    }

    private async Task StopCaptureAsync()
    {
        IsBusy = true;
        BusyLabel = "Stopping capture…";
        try
        {
            await _services.Session.StopAsync();
            StatusText = "Capture stopped";
        }
        finally
        {
            IsBusy = false;
            RaiseCaptureState();
        }
    }

    private async Task RefreshDevicesAsync()
    {
        IsBusy = true;
        BusyLabel = "Scanning devices…";
        try
        {
            _services.Ffmpeg.Locate();
            _services.RefreshDevices();
            RefreshDevices();
            await RefreshEncoderOptionsAsync();
            await RebuildSourceOptionsAsync(restart: _services.Session.IsPreviewing);
            Notify($"Device scan complete · {VideoDevices.Count} video inputs", false);
        }
        finally
        {
            IsBusy = false;
            RaiseCaptureState();
        }
    }

    public void RefreshDevices()
    {
        VideoDevices.Clear();
        foreach (var device in _services.VideoDevices)
        {
            VideoDevices.Add(device);
        }

        Displays.Clear();
        foreach (var display in _services.Displays)
        {
            Displays.Add(display);
        }

        Windows.Clear();
        foreach (var window in _services.Windows)
        {
            Windows.Add(window);
        }

        if (string.IsNullOrEmpty(Config.Capture.VideoDeviceName))
        {
            var card = VideoDevices.FirstOrDefault(d => d.IsCaptureCard) ?? VideoDevices.FirstOrDefault();
            if (card is not null)
            {
                Config.Capture.VideoDeviceName = card.Name;
                Config.Capture.VideoDeviceId = card.Id;
            }
        }

        Raise(nameof(SelectedVideoDevice));
        Raise(nameof(SelectedDisplay));
        Raise(nameof(SelectedWindow));
        Raise(nameof(SelectedSourceKind));
        Raise(nameof(DeviceConnected));
        Raise(nameof(DeviceCardTitle));
        Raise(nameof(DeviceCardSubtitle));

        RefreshCaptureModes();
        RefreshPlatformDatabase();
    }

    private async Task RebuildSourceOptionsAsync(bool restart)
    {
        ResolutionOptions.Clear();
        var options = new List<ResolutionOption>();
        var fpsLimit = 60;
        string summary;

        switch (Config.Capture.SourceKind)
        {
            case CaptureSourceKind.CaptureCard:
            {
                var caps = await CapabilityProbe.ProbeVideoAsync(_services.Ffmpeg, Config.Capture.VideoDeviceName);
                if (caps.ProbeSucceeded)
                {
                    foreach (var mode in caps.DistinctResolutions)
                    {
                        options.Add(new ResolutionOption(
                            $"{mode.Width} × {mode.Height}" + ResolutionSuffix(mode.Width, mode.Height),
                            mode.Width,
                            mode.Height,
                            mode.Fps));
                    }

                    summary = $"Driver reports {caps.Modes.Count} modes · {string.Join(", ", caps.Codecs).ToUpperInvariant()} · max {caps.MaxFps} fps";
                }
                else
                {
                    summary = caps.Error ?? "No capture modes reported.";
                }

                break;
            }

            case CaptureSourceKind.Display:
            {
                var display = SelectedDisplay;
                if (display is not null)
                {
                    foreach (var mode in display.Modes
                                 .GroupBy(m => (m.Width, m.Height))
                                 .Select(g => g.OrderByDescending(m => m.RefreshHz).First())
                                 .OrderByDescending(m => (long)m.Width * m.Height))
                    {
                        options.Add(new ResolutionOption(
                            $"{mode.Width} × {mode.Height}" + ResolutionSuffix(mode.Width, mode.Height),
                            mode.Width,
                            mode.Height,
                            mode.RefreshHz));
                    }

                    fpsLimit = display.RefreshHz;
                    summary = $"Display mode {display.Width}×{display.Height} @ {display.RefreshHz} Hz · {display.Modes.Count} modes supported";
                }
                else
                {
                    summary = "No display detected.";
                }

                break;
            }

            case CaptureSourceKind.Window:
            {
                foreach (var (w, h) in new[] { (3840, 2160), (2560, 1440), (1920, 1080), (1600, 900) })
                {
                    options.Add(new ResolutionOption($"{w} × {h}" + ResolutionSuffix(w, h), w, h, 60));
                }

                summary = "Window capture follows the window size; select a resolution to scale the recording.";
                break;
            }

            default:
            {
                foreach (var (w, h) in new[] { (3840, 2160), (2560, 1440), (1920, 1080) })
                {
                    options.Add(new ResolutionOption($"{w} × {h}" + ResolutionSuffix(w, h), w, h, 120));
                }

                summary = "Generated source · 1080p / 1440p / 2160p at up to 120 fps";
                fpsLimit = 120;
                break;
            }
        }

        if (options.Count == 0)
        {
            options.Add(new ResolutionOption("No supported modes detected", 0, 0, 0));
        }

        foreach (var option in options)
        {
            ResolutionOptions.Add(option);
        }

        CaptureFormatSummary = summary;
        Raise(nameof(CaptureFormatSummary));

        if (Config.Capture.ResolutionWidth == 0 || options.All(o => o.Width != Config.Capture.ResolutionWidth))
        {
            var best = options.FirstOrDefault(o => o.Width >= 1920 && o.Height >= 1080 && o.Width > 0) ?? options[0];
            if (best.Width > 0)
            {
                Config.Capture.ResolutionWidth = best.Width;
                Config.Capture.ResolutionHeight = best.Height;

                // Only follow the source when the user has not chosen an output size.
                if (Config.Recording.Width == 0 || Config.Recording.Height == 0)
                {
                    Config.Recording.Width = best.Width;
                    Config.Recording.Height = best.Height;
                }
            }
        }

        RebuildFpsOptions(fpsLimit);
        RefreshQualityOptions();
        Raise(nameof(SelectedResolution));
        Raise(nameof(SelectedFps));
        Raise(nameof(ResolutionLabel));
        Raise(nameof(OutputFpsLabel));
        Raise(nameof(OutputResolutionLabel));
        if (restart)
        {
            await RestartIfRunningAsync();
        }
    }

    private static string ResolutionSuffix(int width, int height) => (width, height) switch
    {
        (3840, 2160) => " · 4K",
        (2560, 1440) => " · 2K",
        (1920, 1080) => " · Full HD",
        _ => string.Empty
    };

    private void RebuildFpsOptions(int maxFps = 0)
    {
        FpsOptions.Clear();
        var selected = ResolutionOptions.FirstOrDefault(r =>
            r.Width == Config.Capture.ResolutionWidth && r.Height == Config.Capture.ResolutionHeight);
        var cap = maxFps > 0 ? maxFps : selected?.MaxFps ?? 60;

        foreach (var fps in new[] { 30, 48, 50, 60, 120, 144, 240 })
        {
            if (fps <= cap)
            {
                FpsOptions.Add(new FpsOption(fps, $"{fps} FPS"));
            }
        }

        if (FpsOptions.Count == 0 && cap > 0)
        {
            FpsOptions.Add(new FpsOption(cap, $"{cap} FPS"));
        }

        var match = FpsOptions.FirstOrDefault(f => f.Fps == Config.Capture.Fps);
        if (match is null && FpsOptions.Count > 0)
        {
            Config.Capture.Fps = FpsOptions[^1].Fps;
            Config.Recording.Fps = Config.Capture.Fps;
        }

        Raise(nameof(SelectedFps));
        Raise(nameof(FpsChipLabel));
    }

    private async Task RestartIfRunningAsync()
    {
        if (!_services.Session.IsPreviewing || _services.Session.IsRecording)
        {
            return;
        }

        IsBusy = true;
        BusyLabel = "Applying capture settings…";
        try
        {
            await _services.Session.RestartPreviewAsync();
        }
        finally
        {
            IsBusy = false;
            RaiseCaptureState();
        }
    }

    private async Task SelectPlatformAsync(string? platform)
    {
        var kind = platform switch
        {
            "PS5" => PlatformKind.PS5,
            "Xbox" => PlatformKind.Xbox,
            _ => PlatformKind.PC
        };

        Config.Capture.Platform = kind;
        if (kind != PlatformKind.PC)
        {
            // Only fill in a capture card when the card path is already selected — never
            // throw away a test pattern, display or window selection the user made.
            var card = VideoDevices.FirstOrDefault(d => d.IsCaptureCard) ?? VideoDevices.FirstOrDefault();
            if (card is not null && string.IsNullOrWhiteSpace(Config.Capture.VideoDeviceName))
            {
                Config.Capture.VideoDeviceName = card.Name;
                Config.Capture.VideoDeviceId = card.Id;
            }
        }
        else if (Config.Capture.SourceKind == CaptureSourceKind.CaptureCard
                 && string.IsNullOrWhiteSpace(Config.Capture.VideoDeviceName))
        {
            Config.Capture.SourceKind = CaptureSourceKind.Display;
        }

        _services.Settings.Save();
        RaiseCaptureState();
        Raise(nameof(PlatformTitle));
        Raise(nameof(PlatformConnectionGuide));
        Raise(nameof(PlatformHdcpNote));
        Raise(nameof(SelectedSourceKind));
        Raise(nameof(DeviceConnected));
        Raise(nameof(ControllerHint));
        Raise(nameof(PairingPlatformParameter));
        Raise(nameof(PairingButtonLabel));
        SelectPlatformForFpsMonitor(kind);
        await RebuildSourceOptionsAsync(restart: _services.Session.IsPreviewing);
    }

    private async Task SetSourceKindAsync(string? kind)
    {
        var value = kind switch
        {
            "display" => CaptureSourceKind.Display,
            "window" => CaptureSourceKind.Window,
            "test" => CaptureSourceKind.TestPattern,
            _ => CaptureSourceKind.CaptureCard
        };

        Config.Capture.SourceKind = value;
        _services.Settings.Save();
        Raise(nameof(SelectedSourceKind));
        Raise(nameof(IsSourceCaptureCard));
        Raise(nameof(IsSourceDisplay));
        Raise(nameof(IsSourceWindow));
        Raise(nameof(IsSourceTest));
        Raise(nameof(IsSourceDropdown));
        Raise(nameof(SourceKindHint));
        Raise(nameof(DeviceCardTitle));
        Raise(nameof(DeviceCardSubtitle));
        Raise(nameof(DeviceConnected));
        await RebuildSourceOptionsAsync(restart: _services.Session.IsPreviewing);
    }

    // ------------------------------------------------------------------ recording

    public ObservableCollection<CodecOption> Codecs { get; }
    public ObservableCollection<ContainerOption> Containers { get; }
    public ObservableCollection<EncoderOption> EncoderOptions { get; }
    public ObservableCollection<BitrateOption> BitrateOptions { get; }
    public ObservableCollection<RateControlOption> RateControls { get; }
    public ObservableCollection<FpsOption> ReplayDurations { get; }

    public CodecOption? SelectedCodec
    {
        get => Codecs.FirstOrDefault(c => c.Kind == Config.Recording.Codec) ?? Codecs.FirstOrDefault();
        set
        {
            if (value is null || !value.Available || value.Kind == Config.Recording.Codec)
            {
                return;
            }

            Config.Recording.Codec = value.Kind;
            _services.Settings.Save();
            Raise();
            Raise(nameof(RecordingSummary));
        }
    }

    public ContainerOption? SelectedContainer
    {
        get => Containers.FirstOrDefault(c => c.Kind == Config.Recording.Container);
        set
        {
            if (value is null || value.Kind == Config.Recording.Container)
            {
                return;
            }

            Config.Recording.Container = value.Kind;
            _services.Settings.Save();
            Raise();
            Raise(nameof(RecordingSummary));
        }
    }

    public EncoderOption? SelectedEncoder
    {
        get => EncoderOptions.FirstOrDefault(e => e.Id == Config.Recording.Encoder) ?? EncoderOptions[0];
        set
        {
            if (value is null)
            {
                return;
            }

            Config.Recording.Encoder = value.Id;
            _services.Settings.Save();
            Raise();
            Raise(nameof(RecordingSummary));
        }
    }

    public RateControlOption? SelectedRateControl
    {
        get => RateControls.FirstOrDefault(r => r.Mode == Config.Recording.RateControl);
        set
        {
            if (value is null || value.Mode == Config.Recording.RateControl)
            {
                return;
            }

            Config.Recording.RateControl = value.Mode;
            _services.Settings.Save();
            Raise();
            Raise(nameof(IsCustomBitrate));
            Raise(nameof(BitrateSummary));
        }
    }

    public bool IsCustomBitrate => Config.Recording.RateControl != RateControlMode.Preset;

    public BitrateOption? SelectedBitrate
    {
        get => BitrateOptions.FirstOrDefault(b => b.Kbps == Config.Recording.BitrateKbps)
               ?? BitrateOptions.OrderBy(b => Math.Abs(b.Kbps - Config.Recording.BitrateKbps)).First();
        set
        {
            if (value is null || value.Kbps == Config.Recording.BitrateKbps)
            {
                return;
            }

            Config.Recording.BitrateKbps = value.Kbps;
            _services.Settings.Save();
            Raise();
            Raise(nameof(BitrateSummary));
        }
    }

    public string BitrateSummary
    {
        get
        {
            if (Config.Recording.RateControl == RateControlMode.Preset)
            {
                var estimate = CaptureSession.EstimateBitrate(
                    Config.Recording.Width, Config.Recording.Height, Config.Recording.Fps, Config.Recording.QualityPreset);
                return $"Auto · about {estimate / 1000.0:0.#} Mbps for {Config.Recording.Width}×{Config.Recording.Height} @ {Config.Recording.Fps} fps";
            }

            return Config.Recording.RateControl == RateControlMode.Quality
                ? $"Constant quality target · about {Config.Recording.BitrateKbps / 1000.0:0.#} Mbps ceiling"
                : $"{Config.Recording.BitrateKbps / 1000.0:0.#} Mbps target";
        }
    }

    public string RecordingSummary
    {
        get
        {
            var encoder = _services.Session.ActiveEncoder
                          ?? _services.Encoders.Resolve(Config.Recording.Codec, Config.Recording.UseHardwareEncoding, Config.Recording.Encoder)?.DisplayName
                          ?? "No encoder";
            return $"{encoder} · {Config.Recording.Container.ToString().ToUpperInvariant()} · {Config.Recording.Width}×{Config.Recording.Height} @ {Config.Recording.Fps} fps";
        }
    }

    public bool SeparateAudioTracks
    {
        get => Config.Recording.SeparateAudioTracks;
        set
        {
            Config.Recording.SeparateAudioTracks = value;
            _services.Settings.Save();
            Raise();
            Raise(nameof(RecordingSummary));
        }
    }

    public bool HardwareEncoding
    {
        get => Config.Recording.UseHardwareEncoding;
        set
        {
            Config.Recording.UseHardwareEncoding = value;
            _services.Settings.Save();
            Raise();
            Raise(nameof(RecordingSummary));
        }
    }

    public string RecordingFolder
    {
        get => Config.Recording.Directory;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            Config.Recording.Directory = value;
            _services.Performance.SetDrive(value);
            _services.Settings.Save();
            Raise();
            Raise(nameof(RecordingFolderShort));
        }
    }

    public string RecordingFolderShort
    {
        get
        {
            var path = Config.Recording.Directory;
            return path.Length > 42 ? "…" + path[^40..] : path;
        }
    }

    public bool ReplayEnabled
    {
        get => Config.Replay.Enabled;
        set
        {
            if (Config.Replay.Enabled == value)
            {
                return;
            }

            Config.Replay.Enabled = value;
            _services.Settings.Save();
            Raise();
            Raise(nameof(ReplaySummary));
            _ = ApplyReplaySettingAsync();
        }
    }

    public FpsOption? SelectedReplayDuration
    {
        get => ReplayDurations.FirstOrDefault(d => d.Fps == Config.Replay.DurationSeconds);
        set
        {
            if (value is null || value.Fps == Config.Replay.DurationSeconds)
            {
                return;
            }

            Config.Replay.DurationSeconds = value.Fps;
            _services.Settings.Save();
            Raise();
            Raise(nameof(ReplaySummary));
            if (Config.Replay.Enabled)
            {
                _ = ApplyReplaySettingAsync();
            }
        }
    }

    public string ReplaySummary => Config.Replay.Enabled
        ? $"Rolling buffer armed · last {FormatDuration(Config.Replay.DurationSeconds)} · saves as MP4"
        : "Instant replay is off. Arming it encodes a rolling buffer in the background.";

    public string ReplaySaveState => _services.Session.IsRecording
        ? "Paused while recording"
        : Config.Replay.Enabled ? "Ready to save" : "Buffer off";

    private async Task ApplyReplaySettingAsync()
    {
        if (!_services.Session.IsPreviewing || _services.Session.IsRecording)
        {
            return;
        }

        await _services.Session.RestartPreviewAsync();
        RaiseCaptureState();
    }

    private async Task ToggleReplayBufferAsync() => ReplayEnabled = !ReplayEnabled;

    private async Task ToggleRecordingAsync()
    {
        if (_services.Session.IsRecording)
        {
            IsBusy = true;
            BusyLabel = "Finalising recording…";
            var path = await _services.Session.StopRecordingAsync();
            IsBusy = false;
            RaiseCaptureState();
            if (path is not null)
            {
                Notify("Saved " + Path.GetFileName(path), false);
                await RefreshLibraryAsync();
            }

            return;
        }

        IsBusy = true;
        BusyLabel = "Starting encoder…";
        try
        {
            var result = await _services.Session.StartRecordingAsync();
            if (!result.Started)
            {
                Notify(result.Error ?? "Recording could not start.", true);
            }
        }
        finally
        {
            IsBusy = false;
            RaiseCaptureState();
        }
    }

    private async Task PauseResumeAsync()
    {
        if (!_services.Session.IsRecording)
        {
            return;
        }

        if (_services.Session.IsPaused)
        {
            await _services.Session.ResumeRecordingAsync();
        }
        else
        {
            await _services.Session.PauseRecordingAsync();
        }

        RaiseCaptureState();
    }

    private async Task SaveReplayAsync()
    {
        var path = await _services.Session.SaveReplayAsync(Config.Replay.DurationSeconds);
        if (path is not null)
        {
            await RefreshLibraryAsync();
        }
    }

    /// <summary>Rebuilds the encoder list from the catalog, newest verification results included.</summary>
    private void RefreshEncoderRows()
    {
        AvailableEncoders.Clear();
        foreach (var encoder in _services.Encoders.Available
                     .OrderByDescending(e => _services.Encoders.IsVerified(e.Id) == true)
                     .ThenByDescending(e => e.Hardware))
        {
            AvailableEncoders.Add(new EncoderRow(
                encoder.DisplayName,
                encoder.Vendor,
                encoder.Hardware,
                _services.Encoders.IsVerified(encoder.Id) == true));
        }

        Raise(nameof(EncoderDiagnostics));
        Raise(nameof(RecordingEncoderText));
        Raise(nameof(SelectedEncoder));
    }

    private async Task RefreshEncoderOptionsAsync()
    {
        await _services.Encoders.ScanAsync();

        EncoderOptions.Clear();
        EncoderOptions.Add(new EncoderOption("Auto", "Auto (hardware first)"));
        foreach (var encoder in _services.Encoders.Available.OrderBy(e => e.Hardware ? 0 : 1).ThenBy(e => e.DisplayName))
        {
            EncoderOptions.Add(new EncoderOption(encoder.Id, encoder.DisplayName + (encoder.Hardware ? " · hardware" : " · CPU")));
        }

        var previousCodec = Config.Recording.Codec;
        Codecs.Clear();
        foreach (var codec in new[] { VideoCodecKind.H264, VideoCodecKind.HEVC, VideoCodecKind.AV1 })
        {
            var available = _services.Encoders.Available.Any(e => e.Codec == codec);
            var label = codec switch
            {
                VideoCodecKind.H264 => "H.264 (AVC)",
                VideoCodecKind.HEVC => "H.265 (HEVC)",
                _ => "AV1"
            };
            Codecs.Add(new CodecOption(codec, available ? label : label + " · unavailable", available));
        }

        if (!Codecs.Any(c => c.Kind == previousCodec && c.Available))
        {
            var fallback = Codecs.FirstOrDefault(c => c.Available);
            if (fallback is not null)
            {
                Config.Recording.Codec = fallback.Kind;
            }
        }

        RefreshEncoderRows();
        Raise(nameof(SelectedCodec));
        Raise(nameof(SelectedEncoder));
        Raise(nameof(RecordingSummary));
    }

    private void ResetLowLatency()
    {
        Config.LowLatency.Mode = LowLatencyMode.UltraLow;
        Config.LowLatency.VideoBuffer = BufferMode.Low;
        Config.LowLatency.Sync = SyncMode.Automatic;
        Config.LowLatency.AudioBuffer = BufferMode.Automatic;
        _services.Settings.Save();
        RaiseAllSettings();
        Notify("Low-latency defaults restored.", false);
        _ = RestartIfRunningAsync();
    }

    private void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(Config.Recording.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Config.Recording.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("Could not open the folder: " + ex.Message, true);
        }
    }

    private void ToggleMuteMic()
    {
        var mic = AudioChannels.FirstOrDefault(c => c.Role == "mic");
        if (mic is null)
        {
            return;
        }

        mic.Muted = !mic.Muted;
        Notify(mic.Muted ? "Microphone muted" : "Microphone live", false);
    }

    // ------------------------------------------------------------------ engine install

    public bool IsEngineInstalled => _services.Ffmpeg.IsAvailable;
    public string EngineStatusText => _services.Ffmpeg.IsAvailable
        ? "Capture engine installed · " + (Config.General.Notifications ? "ready" : "ready")
        : "Capture engine not installed";

    public string EnginePathText => _services.Ffmpeg.FfmpegPath ?? "Not found";

    public string EncoderDiagnostics => AvailableEncoders.Count == 0
        ? "No encoder has been detected yet."
        : $"{AvailableEncoders.Count(e => e.Verified)} of {AvailableEncoders.Count} encoders passed a real encode test; failed hardware encoders are not offered.";

    public bool IsInstallingEngine
    {
        get => _isInstallingEngine;
        private set => Set(ref _isInstallingEngine, value);
    }

    public string EngineProgressText
    {
        get => _engineProgress;
        private set => Set(ref _engineProgress, value);
    }

    private async Task InstallEngineAsync()
    {
        if (IsInstallingEngine)
        {
            return;
        }

        IsInstallingEngine = true;
        EngineProgressText = "Preparing download…";
        var progress = new Progress<string>(p => EngineProgressText = p);
        try
        {
            var result = await _services.Ffmpeg.InstallAsync(progress, CancellationToken.None);
            if (result.Success)
            {
                Notify("Capture engine installed.", false);
                await RefreshEncoderOptionsAsync();
                Raise(nameof(IsEngineInstalled));
                Raise(nameof(EngineStatusText));
                Raise(nameof(EnginePathText));
                await RebuildSourceOptionsAsync(restart: false);
            }
            else
            {
                Notify("Engine installation failed: " + (result.Message ?? "unknown error"), true);
            }
        }
        finally
        {
            IsInstallingEngine = false;
            EngineProgressText = string.Empty;
        }
    }

    // ------------------------------------------------------------------ hotkeys

    public event Action? FullScreenRequested;

    private void StartHotkeys()
    {
        _services.Hotkeys.Pressed += action => Dispatch(() =>
        {
            switch (action)
            {
                case "record.toggle":
                    ToggleRecordingCommand.Execute(null);
                    break;
                case "record.pause":
                    PauseResumeCommand.Execute(null);
                    break;
                case "replay.save":
                    SaveReplayCommand.Execute(null);
                    break;
                case "mic.mute":
                    ToggleMuteMic();
                    break;
                case "preview.toggle":
                    FullScreenRequested?.Invoke();
                    break;
            }
        });
    }

    public void RegisterHotkeys()
    {
        _services.Hotkeys.RegisterAll(Config.Hotkeys);
        if (_services.Hotkeys.Failures.Count > 0)
        {
            Notify("Some hotkeys are already used by another app: " + string.Join(", ", _services.Hotkeys.Failures), true);
        }
    }

    // ------------------------------------------------------------------ wizard

    private int _wizardStep = 1;

    private bool _showWizard;

    public bool ShowWizard
    {
        get => _showWizard;
        private set => Set(ref _showWizard, value);
    }

    public int WizardStep
    {
        get => _wizardStep;
        private set
        {
            if (Set(ref _wizardStep, value))
            {
                RaiseWizard();
            }
        }
    }

    public string WizardTitle => WizardStep switch
    {
        1 => "Choose your gaming platform",
        2 => "Detect your capture hardware",
        3 => "Connect everything",
        4 => "Supported capture formats",
        5 => "Video and audio",
        6 => "Run a capture test",
        _ => "You're ready to capture"
    };

    public string WizardStepLabel => $"Step {WizardStep} of 7";

    public bool WizardCanGoBack => WizardStep > 1;
    public bool WizardIsLast => WizardStep == 7;
    public string WizardPrimaryLabel => WizardIsLast ? "Open the studio" : "Continue";

    public string WizardTestResult
    {
        get => _wizardTestResult;
        private set => Set(ref _wizardTestResult, value);
    }

    private void RaiseWizard()
    {
        Raise(nameof(WizardTitle));
        Raise(nameof(WizardStepLabel));
        Raise(nameof(WizardCanGoBack));
        Raise(nameof(WizardIsLast));
        Raise(nameof(WizardPrimaryLabel));
        Raise(nameof(WizardStep));
    }

    /// <summary>
    /// Onboarding runs once per Windows user and never again after it has been completed or
    /// dismissed. Only a manual "Setup wizard" click in Settings can bring it back.
    /// </summary>
    public bool ShouldShowOnboarding() => !Config.WizardCompleted;

    public void CloseWizard()
    {
        ShowWizard = false;
        Config.WizardCompleted = true;
        Config.General.ShowWizardOnStartup = false;
        _services.Settings.Save();
    }

    private async Task WizardNextAsync()
    {
        if (WizardStep >= 7)
        {
            await FinishWizardAsync();
            return;
        }

        WizardStep++;
        if (WizardStep == 2)
        {
            await RefreshDevicesAsync();
        }

        if (WizardStep == 4)
        {
            await RebuildSourceOptionsAsync(restart: false);
        }

        if (WizardStep == 3 || WizardStep == 5)
        {
            // Guide steps stay informational.
        }
    }

    private async Task RunCaptureTestAsync()
    {
        WizardTestResult = "Starting the pipeline…";
        if (!_services.Ffmpeg.IsAvailable)
        {
            WizardTestResult = "The capture engine is not installed.";
            return;
        }

        if (!_services.Session.IsPreviewing)
        {
            await StartCaptureAsync();
        }

        await Task.Delay(4000);
        var fps = _services.Session.MeasuredFps;
        var hasSignal = _services.Session.HasSignal;
        WizardTestResult = hasSignal
            ? $"Signal OK · {fps:0.#} fps delivered · preview latency {_services.Session.PresentLatencyMs:0.0} ms measured"
            : "No frames received. Check the HDMI input and that the console is powered on.";
    }

    private async Task FinishWizardAsync()
    {
        Config.WizardCompleted = true;
        CloseWizard();
        await StartCaptureAsync();
    }

    public void ReopenWizard()
    {
        WizardStep = 1;
        WizardTestResult = string.Empty;
        ShowWizard = true;
    }

    // ------------------------------------------------------------------ UI pump

    private void Tick()
    {
        if (Toasts.Count > 0 && (DateTime.Now - Toasts[0].CreatedAt).TotalSeconds > 7)
        {
            Toasts.RemoveAt(0);
        }

        var elapsed = _services.Session.IsRecording ? _services.Session.RecordingElapsed : TimeSpan.Zero;
        RecordingTimeText = elapsed.ToString(@"hh\:mm\:ss");

        _fpsHistory.Add(_services.Session.MeasuredFps);
        _latencyHistory.Add(_services.Session.PresentLatencyMs);
        _ramHistory.Add(RamPercent);
        Trim(_fpsHistory);
        Trim(_latencyHistory);
        Trim(_ramHistory);

        RaiseConsoleState();
        RaiseEnhancementStats();
        RaiseFrameGenerationStats();
        RaiseFpsMonitorStats();
        Raise(nameof(FpsText));
        Raise(nameof(FpsChipLive));
        Raise(nameof(OutputFpsLabel));
        Raise(nameof(LatencyText));
        Raise(nameof(DroppedFramesText));
        Raise(nameof(RecordingSizeText));
        Raise(nameof(RecordingBitrateText));
        Raise(nameof(RecordingFpsText));
        Raise(nameof(RecordingEncoderText));
        Raise(nameof(ReplaySaveState));
        Raise(nameof(HasPreviewFrame));
        Raise(nameof(ShowNoSignal));
        Raise(nameof(CaptureStateText));
        Raise(nameof(FpsHistory));
        Raise(nameof(LatencyHistory));
        Raise(nameof(RamHistory));
        Raise(nameof(StickLeftX));
        Raise(nameof(StickLeftY));
        Raise(nameof(StickRightX));
        Raise(nameof(StickRightY));
        Raise(nameof(TriggerLeft));
        Raise(nameof(TriggerRight));
        Raise(nameof(ButtonA));
        Raise(nameof(ButtonB));
        Raise(nameof(ButtonX));
        Raise(nameof(ButtonY));
        Raise(nameof(BumperLeft));
        Raise(nameof(BumperRight));
        Raise(nameof(PollRateText));
        Raise(nameof(StickLeftReadout));
        Raise(nameof(StickRightReadout));
        Raise(nameof(TriggerReadout));
        Raise(nameof(ControllerConnected));
        Raise(nameof(ControllerName));
        Raise(nameof(ControllerTransport));
        Raise(nameof(ControllerKindText));
        Raise(nameof(AudioStatusText));
        Raise(nameof(SystemWarningVisible));
        Raise(nameof(SystemWarning));

        if (IsRecording != _services.Session.IsRecording || IsPaused != _services.Session.IsPaused)
        {
            RaiseCaptureState();
        }

        if (DateTime.Now.Second % 3 == 0)
        {
            UpdateDisk();
        }
    }

    private static void Trim(List<double> list)
    {
        while (list.Count > 90)
        {
            list.RemoveAt(0);
        }
    }

    private void UpdateDisk()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Config.Recording.Directory) ?? "C:\\");
            DiskFreeText = $"{drive.AvailableFreeSpace / 1073741824.0:0.#} GB free";
        }
        catch
        {
            DiskFreeText = "—";
        }
    }

    private void OnSystemSampled(SystemSnapshot snapshot)
    {
        _system = snapshot;
        _cpuHistory.Add(snapshot.CpuPercent);
        _gpuHistory.Add(snapshot.GpuPercent < 0 ? 0 : snapshot.GpuPercent);
        Trim(_cpuHistory);
        Trim(_gpuHistory);

        Dispatch(() =>
        {
            Raise(nameof(CpuPercent));
            Raise(nameof(GpuPercent));
            Raise(nameof(GpuEncodePercent));
            Raise(nameof(RamPercent));
            Raise(nameof(RamText));
            Raise(nameof(CpuPercentText));
            Raise(nameof(GpuPercentText));
            Raise(nameof(RamPercentText));
            Raise(nameof(CpuHistory));
            Raise(nameof(GpuHistory));
            Raise(nameof(SystemGrade));
            Raise(nameof(SystemWarningVisible));
            Raise(nameof(SystemWarning));
            Raise(nameof(EncoderUtilizationText));
            UpdateDisk();
        });
    }

    private long _pendingPresentTicks;
    private long _pendingPresentSequence;
    private int _pendingPresentEnhanced;

    private void OnFrameArrived(PreviewFrame frame)
    {
        OriginalPreviewImage = frame.Image;
        _services.FpsMonitor.ObserveCaptureFrame(frame.Image, frame.DecodedAtTicks, _services.Session.MeasuredFps);

        // When the enhanced preview is active its worker submits the enhanced pixels instead.
        if (!_services.Enhancement.ShouldEnhancePreview || ShowOriginal)
        {
            SubmitBitmapForGeneration(frame.Image, frame.Sequence, frame.DecodedAtTicks);
        }

        if (_services.Enhancement.ShouldEnhancePreview && !ShowOriginal)
        {
            StartEnhancementWorker();
            var arrival = new Core.Enhancement.FrameArrival(frame.Sequence, frame.DecodedAtTicks, frame.DecodeMs);
            // A slower processor replaces the stale queued frame instead of rejecting this fresh one.
            if (!EnhancementQueue.Submit(new QueuedPreviewFrame(frame.Image, arrival)))
            {
                _services.Enhancement.NoteDropped();
            }

            Interlocked.Exchange(ref _framePending, 1);
            return;
        }

        if (Interlocked.Exchange(ref _framePending, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _framePending = 0;
            PreviewImage = frame.Image;
            // The frame counts as presented once the compositor has drawn it. The plain preview
            // is tracked by the capture session only; enhancement latency samples stay separate.
            Interlocked.Exchange(ref _pendingPresentTicks, frame.DecodedAtTicks);
            Interlocked.Exchange(ref _pendingPresentSequence, frame.Sequence);
            Interlocked.Exchange(ref _pendingPresentEnhanced, 0);
            Raise(nameof(ShowNoSignal));
            Raise(nameof(HasPreviewFrame));
        }));
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        var ticks = Interlocked.Exchange(ref _pendingPresentTicks, 0);
        if (ticks == 0)
        {
            return;
        }

        var sequence = Interlocked.Exchange(ref _pendingPresentSequence, 0);
        var enhanced = Interlocked.Exchange(ref _pendingPresentEnhanced, 0) == 1;
        _services.Session.NotePresented(ticks);
        if (enhanced)
        {
            _services.Enhancement.NotePresented(sequence, ticks, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }

    private void RaiseCaptureState()
    {
        Raise(nameof(IsPreviewing));
        Raise(nameof(IsRecording));
        Raise(nameof(IsPaused));
        Raise(nameof(CaptureStateText));
        Raise(nameof(StatusText));
        Raise(nameof(DeviceConnectionText));
        Raise(nameof(ShowNoSignal));
        Raise(nameof(HasPreviewFrame));
        Raise(nameof(ReplaySaveState));
        Raise(nameof(RecordingSummary));
        Raise(nameof(DroppedFramesText));
        Raise(nameof(IsSourceCaptureCard));
        Raise(nameof(IsSourceDisplay));
        Raise(nameof(IsSourceWindow));
        Raise(nameof(IsSourceTest));
        Raise(nameof(IsSourceDropdown));
    }

    private void RaiseAllSettings()
    {
        Raise(nameof(Config));
        Raise(nameof(SelectedResolution));
        Raise(nameof(SelectedFps));
        Raise(nameof(SelectedSourceKind));
        Raise(nameof(SelectedVideoDevice));
        Raise(nameof(SelectedCodec));
        Raise(nameof(SelectedContainer));
        Raise(nameof(SelectedEncoder));
        Raise(nameof(SelectedRateControl));
        Raise(nameof(SelectedBitrate));
        Raise(nameof(SelectedReplayDuration));
        Raise(nameof(RecordingFolder));
        Raise(nameof(RecordingFolderShort));
        Raise(nameof(SeparateAudioTracks));
        Raise(nameof(HardwareEncoding));
        Raise(nameof(HdrEnabled));
        Raise(nameof(IsHdrAvailable));
        Raise(nameof(BitrateSummary));
        Raise(nameof(ReplaySummary));
        Raise(nameof(ReplayEnabled));
        Raise(nameof(ResolutionLabel));
        Raise(nameof(OutputResolutionLabel));
        Raise(nameof(FpsChipLabel));
        Raise(nameof(PlatformChipLabel));
        Raise(nameof(PlatformTitle));
        Raise(nameof(IsPlatformPS5));
        Raise(nameof(IsPlatformXbox));
        Raise(nameof(IsPlatformPC));
        Raise(nameof(DeviceCardTitle));
        Raise(nameof(DeviceCardSubtitle));
        Raise(nameof(DeviceConnected));
        Raise(nameof(RecordingSummary));
        Raise(nameof(EngineStatusText));
        Raise(nameof(IsEngineInstalled));
    }

    private static string FormatBytes(long bytes) => bytes >= 1073741824
        ? $"{bytes / 1073741824.0:0.00} GB"
        : bytes >= 1048576 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0} KB";

    private static string FormatDuration(int seconds) => seconds >= 60
        ? $"{seconds / 60}m{(seconds % 60 > 0 ? (seconds % 60) + "s" : string.Empty)}"
        : $"{seconds}s";

    public async Task ShutdownAsync()
    {
        _uiTimer.Stop();
        System.Windows.Media.CompositionTarget.Rendering -= OnCompositionRendering;
        StopNativePresenter();
        await _services.Session.StopAsync();
    }
}

