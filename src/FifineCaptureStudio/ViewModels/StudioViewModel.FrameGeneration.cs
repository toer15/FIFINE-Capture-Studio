using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Enhancement;
using FifineCaptureStudio.Core.FrameGeneration;
using FifineCaptureStudio.Rendering;

namespace FifineCaptureStudio.ViewModels;

public sealed partial class StudioViewModel
{
    private NativeFramePresenter? _nativePresenter;
    private FrameGenerationPipeline? _frameGenerationPipeline;
    private bool _frameGenerationBypass;
    private bool _frameGenerationActive;
    private bool _frameGenerationBusy;
    private string _frameGenerationProgress = string.Empty;
    private long _fgSessionGeneratedStart;
    private long _fgSessionSourceStart;
    private long _fgSessionSkippedStart;
    private bool _fgSessionReportPending;

    public IReadOnlyList<FrameGenerationQuality> FrameGenerationQualityOptions { get; } =
        Enum.GetValues<FrameGenerationQuality>();

    public IReadOnlyList<FrameGenerationLatency> FrameGenerationLatencyOptions { get; } =
        Enum.GetValues<FrameGenerationLatency>();

    // Only 2X exists, so 120 is the only fixed target that can be honoured. 144/165/240 would need
    // a genuine 3X/4X mode; they are deliberately absent rather than shown as decorative options.
    public IReadOnlyList<int> FrameGenerationTargets { get; } = new[] { 0, 120 };

    public IReadOnlyList<string> FrameGenerationTargetLabels { get; } = new[]
    {
        "Auto (2× source)", "120 FPS"
    };

    public string SelectedFrameGenerationTargetLabel
    {
        get
        {
            var index = Array.IndexOf(FrameGenerationTargets.ToArray(), Config.FrameGeneration.TargetOutputFps);
            return FrameGenerationTargetLabels[Math.Max(0, index)];
        }
        set
        {
            var index = Array.IndexOf(FrameGenerationTargetLabels.ToArray(), value);
            if (index >= 0)
            {
                FrameGenerationTarget = FrameGenerationTargets[index];
                Raise(nameof(SelectedFrameGenerationTargetLabel));
            }
        }
    }

    public bool FrameGenerationEnabled
    {
        get => Config.FrameGeneration.Enabled;
        set
        {
            if (Config.FrameGeneration.Enabled == value) return;
            Config.FrameGeneration.Enabled = value;
            _services.Settings.SaveSoon();
            // The presenter's input path changes when frame generation is enabled or disabled. Close
            // it rather than leaving a direct-GPU presentation window with no producer.
            if (_nativePresenter is not null)
            {
                StopNativePresenter();
            }

            FrameGenerationBypass = false;
            RaiseAllFrameGeneration();
        }
    }

    public FrameGenerationQuality FrameGenerationQuality
    {
        get => Config.FrameGeneration.Quality;
        set
        {
            if (Config.FrameGeneration.Quality == value) return;
            Config.FrameGeneration.Quality = value;
            _services.Settings.SaveSoon();
            // A loaded engine keeps its internal shape; the presenter and pipeline must be rebuilt
            // around the new one rather than silently presenting mismatched sizes.
            StopNativePresenter();
            _services.FrameGeneration.Reload();
            RaiseAllFrameGeneration();
        }
    }

    public FrameGenerationLatency FrameGenerationLatencyPreference
    {
        get => Config.FrameGeneration.Latency;
        set
        {
            if (Config.FrameGeneration.Latency == value) return;
            Config.FrameGeneration.Latency = value;
            _services.Settings.SaveSoon();
            if (_frameGenerationPipeline is not null)
            {
                _frameGenerationPipeline.Latency = value;
            }

            RaiseAllFrameGeneration();
        }
    }

    public int FrameGenerationTarget
    {
        get => Config.FrameGeneration.TargetOutputFps;
        set
        {
            if (Config.FrameGeneration.TargetOutputFps == value) return;
            Config.FrameGeneration.TargetOutputFps = value;
            _services.Settings.SaveSoon();
            RaiseAllFrameGeneration();
        }
    }

    public string FrameGenerationTargetLabel(int value) => value == 0 ? "Auto (2× source)" : value + " FPS";

    public string FrameGenerationSkippedText => _services.FrameGeneration.SkippedInterpolations.ToString("N0");

    public bool FrameGenerationActive => _frameGenerationActive;
    public bool NativePresenterActive => _nativePresenter is not null;

    /// <summary>
    /// The native presenter can be used for enhanced source frames even when RIFE is off. It still
    /// requires the enhancement pipeline and fullscreen enhancement routing to have something to show.
    /// </summary>
    public bool CanOpenNativePresenter =>
        Config.FrameGeneration.Enabled
        || (Config.Enhancement.Enabled && Config.Enhancement.FullscreenEnhanced);

    public bool FrameGenerationModelInstalled => _services.FrameGeneration.ModelInstalled;

    public bool FrameGenerationBusy => _frameGenerationBusy;

    public string FrameGenerationProgress => _frameGenerationProgress;

    public bool FrameGenerationBypass
    {
        get => _frameGenerationBypass;
        set
        {
            if (!Set(ref _frameGenerationBypass, value)) return;
            if (_frameGenerationPipeline is not null)
            {
                _frameGenerationPipeline.Bypass = value;
            }

            Raise(nameof(FrameGenerationBypass));
            Raise(nameof(FrameGenerationBypassLabel));
        }
    }

    public string FrameGenerationBypassLabel => _frameGenerationBypass ? "Bypass: on" : "Bypass";

    // ------------------------------------------------------------------ stats

    public string FrameGenerationStatusText => _services.FrameGeneration.Status;

    public string FrameGenerationModelText => _services.FrameGeneration.ModelDescription;

    public string FrameGenerationEngineText
    {
        get
        {
            var service = _services.FrameGeneration;
            if (!service.Ready)
            {
                return "Not loaded";
            }

            var vram = service.VramMb > 0 ? $" · {service.VramMb:0} MB VRAM" : string.Empty;
            return $"{service.Provider} · {_services.Performance.GpuName} · {(service.GpuVerified ? "GPU verified" : service.PlacementReport)}{vram}";
        }
    }

    public string FrameGenerationInternalText
    {
        get
        {
            var (width, height) = Config.FrameGeneration.InternalSize;
            return $"{width} × {height} inference";
        }
    }

    public string FrameGenerationConversionText
    {
        get
        {
            var source = _services.Session.MeasuredFps > 0
                ? _services.Session.MeasuredFps
                : _services.FrameGeneration.SourceFps;
            var presented = _nativePresenter?.PresentedFps ?? 0;
            var generated = _services.FrameGeneration.GeneratedFps;
            if (source <= 0)
            {
                return "Waiting for gameplay frames…";
            }

            return presented > 0
                ? $"{source:0} FPS source → {generated:0} generated/s → {presented:0} FPS presented"
                : $"{source:0} FPS source → 2× interpolation";
        }
    }

    public string FrameGenerationInferenceText
    {
        get
        {
            var service = _services.FrameGeneration;
            return service.AverageInferenceMs <= 0
                ? "—"
                : $"{service.AverageInferenceMs:0.00} ms avg · {service.P95InferenceMs:0.00} ms p95";
        }
    }

    public string FrameGenerationPresentedText
    {
        get
        {
            var presenter = _nativePresenter;
            if (presenter is null) return "—";
            return $"{presenter.PresentedFps:0.0} FPS · {presenter.DisplayRefreshHz} Hz display";
        }
    }

    public string FrameGenerationGeneratedText => _services.FrameGeneration.GeneratedFrames.ToString("N0");

    public string FrameGenerationDroppedText => _services.FrameGeneration.DroppedFrames.ToString("N0");

    public string FrameGenerationLatencyText
    {
        get
        {
            var service = _services.FrameGeneration;
            if (!FrameGenerationActive) return "—";
            var vram = service.VramMb > 0 ? $" · {service.VramMb:0} MB VRAM" : string.Empty;
            return $"{service.QueueAgeMs:0.0} ms queue · {_nativePresenter?.LatenessMs ?? 0:0.0} ms late{vram}";
        }
    }

    public string FrameGenerationSceneCutsText => _services.FrameGeneration.SceneCuts.ToString("N0");

    public string FrameGenerationDisplayText
    {
        get
        {
            var refresh = DisplayRefresh.Get(IntPtr.Zero, out var monitor);
            return $"{refresh} Hz · {monitor} · 2× target {(refresh >= 120 ? "supported" : "not supported")}";
        }
    }

    public string FrameGenerationModelActionText => FrameGenerationModelInstalled
        ? "Reinstall model"
        : "Install 22 MB model";

    public string FrameGenerationBodyText =>
        "Interpolated output, not native game rendering. Generated frames are presented only on the " +
        "FIFINE fullscreen display; recordings and passthrough HDMI keep the original frames.";

    // ------------------------------------------------------------------ commands

    public System.Windows.Input.ICommand InstallFrameGenerationModelCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand ToggleFrameGenerationBypassCommand { get; private set; } = null!;

    private void InitializeFrameGeneration()
    {
        InstallFrameGenerationModelCommand = new AsyncRelayCommand(InstallModelAsync);
        ToggleFrameGenerationBypassCommand = new RelayCommand(() => FrameGenerationBypass = !FrameGenerationBypass);
    }

    private async Task InstallModelAsync()
    {
        if (_frameGenerationBusy)
        {
            return;
        }

        _frameGenerationBusy = true;
        _frameGenerationProgress = "0%";
        RaiseAllFrameGeneration();
        try
        {
            var progress = new Progress<double>(value => Dispatch(() =>
            {
                _frameGenerationProgress = (value * 100).ToString("0") + "%";
                Raise(nameof(FrameGenerationProgress));
            }));
            var result = await RifeModel.DownloadAsync(progress, CancellationToken.None);
            Notify(result.Message, !result.Ok);
            if (result.Ok)
            {
                _services.FrameGeneration.Reload();
            }
        }
        finally
        {
            _frameGenerationBusy = false;
            _frameGenerationProgress = string.Empty;
            RaiseAllFrameGeneration();
        }
    }

    /// <summary>
    /// Opens the dedicated native presenter. With RIFE enabled this also creates the existing CPU
    /// frame-generation pipeline; without it the presenter receives the enhancement engine's shared
    /// GPU texture directly, so the main WPF window keeps its controls and fallback preview but does
    /// not participate in high-frequency presentation.
    /// </summary>
    public async Task<bool> StartNativePresenterAsync(IntPtr owner)
    {
        if (_nativePresenter is not null)
        {
            return true;
        }

        var useFrameGeneration = Config.FrameGeneration.Enabled;
        if (!useFrameGeneration && !(Config.Enhancement.Enabled && Config.Enhancement.FullscreenEnhanced))
        {
            return false;
        }

        var service = _services.FrameGeneration;
        if (useFrameGeneration)
        {
            if (!service.ModelInstalled)
            {
                Notify("Frame generation needs the RIFE model. Install it on the Gaming Enhancement page first.", true);
                return false;
            }

            var ready = await service.EnsureReadyAsync();
            if (!ready || service.Engine is null)
            {
                Notify(service.Status, true);
                return false;
            }
        }

        var refresh = DisplayRefresh.Get(owner, out var monitor);
        // The presenter itself is valid at 60 Hz; only the generated 2x cadence needs 100 Hz+.
        if (useFrameGeneration && refresh < 100)
        {
            Notify($"{monitor} runs at {refresh} Hz. A 100 Hz or faster display is required for 2× frame generation.", true);
            return false;
        }

        var engine = useFrameGeneration ? service.Engine : null;
        var (width, height) = useFrameGeneration && engine is not null
            ? (engine.Width, engine.Height)
            : NativeOutputSize();

        var presenter = new NativeFramePresenter(width, height, OnPresenterClosed, OnPresenterBypassChanged);
        if (!presenter.Start(owner))
        {
            presenter.Dispose();
            Notify(presenter.Error ?? "The native fullscreen presenter could not start.", true);
            return false;
        }

        _nativePresenter = presenter;
        presenter.UpdateOverlay(BuildFpsOverlaySnapshot());
        if (useFrameGeneration && engine is not null)
        {
            _fgSessionGeneratedStart = service.GeneratedFrames;
            _fgSessionSourceStart = service.SourceFrames;
            _fgSessionSkippedStart = service.SkippedInterpolations;
            _fgSessionReportPending = true;
            _frameGenerationPipeline = new FrameGenerationPipeline(
                engine, service, engine.Width, engine.Height, presenter.Enqueue)
            {
                Bypass = _frameGenerationBypass,
                Latency = Config.FrameGeneration.Latency
            };
            _frameGenerationPipeline.Start();
        }
        else
        {
            // No interpolation pipeline: the enhancement worker feeds shared GPU textures directly.
            _frameGenerationPipeline = null;
            _fgSessionReportPending = false;
        }

        _frameGenerationActive = useFrameGeneration;
        RaiseAllFrameGeneration();
        return true;
    }

    public Task<bool> StartFrameGenerationPresenterAsync(IntPtr owner) =>
        Config.FrameGeneration.Enabled
            ? StartNativePresenterAsync(owner)
            : Task.FromResult(false);

    private (int Width, int Height) NativeOutputSize()
    {
        var (targetWidth, targetHeight) = Core.Enhancement.EnhancementConfig.Resolution(Config.Enhancement.Target);
        if (targetWidth > 0 && targetHeight > 0)
        {
            return (targetWidth, targetHeight);
        }

        var width = _services.Session.ActivePreviewWidth;
        var height = _services.Session.ActivePreviewHeight;
        return width > 0 && height > 0 ? (width, height) : (1920, 1080);
    }

    public void StopNativePresenter()
    {
        var presenter = _nativePresenter;
        _nativePresenter = null;
        _frameGenerationPipeline?.Dispose();
        _frameGenerationPipeline = null;
        presenter?.Stop();
        presenter?.Dispose();
        _frameGenerationActive = false;
        _frameGenerationBypass = false;
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => System.Windows.Application.Current?.MainWindow?.Activate()));
        _services.FrameGeneration.SetStatus(_services.FrameGeneration.Ready
            ? "Ready · presenter closed"
            : "Off");
        AppendFrameGenerationReport();
        RaiseAllFrameGeneration();
    }

    private void OnPresenterClosed() => Dispatch(() =>
    {
        if (_nativePresenter is null)
        {
            return;
        }

        _frameGenerationPipeline?.Dispose();
        _frameGenerationPipeline = null;
        _nativePresenter?.Dispose();
        _nativePresenter = null;
        _frameGenerationActive = false;
        _frameGenerationBypass = false;
        _services.FrameGeneration.SetStatus(_services.FrameGeneration.Ready
            ? "Ready · presenter closed"
            : "Off");
        AppendFrameGenerationReport();
        RaiseAllFrameGeneration();
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => System.Windows.Application.Current?.MainWindow?.Activate()));
    });

    /// <summary>Adds the pipeline's own counters to the presenter's session report.</summary>
    private void AppendFrameGenerationReport()
    {
        if (!_fgSessionReportPending)
        {
            return;
        }

        _fgSessionReportPending = false;
        try
        {
            var service = _services.FrameGeneration;
            File.AppendAllText(Path.Combine(AppPaths.Root, "fg-presenter.txt"),
                "source frames = " + (service.SourceFrames - _fgSessionSourceStart) + "\n" +
                "generated     = " + (service.GeneratedFrames - _fgSessionGeneratedStart) + "\n" +
                "skipped late  = " + (service.SkippedInterpolations - _fgSessionSkippedStart) + "\n" +
                "dropped pairs = " + service.DroppedFrames + "\n" +
                "inference     = " + service.AverageInferenceMs.ToString("0.00") + " ms avg / " +
                service.P95InferenceMs.ToString("0.00") + " ms p95\n" +
                "vram          = " + service.VramMb.ToString("0") + " MB\n");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private void OnPresenterBypassChanged(bool bypass) => Dispatch(() =>
    {
        _frameGenerationBypass = bypass;
        if (_frameGenerationPipeline is not null)
        {
            // Stop spending GPU time on pairs the presenter will not show.
            _frameGenerationPipeline.Bypass = bypass;
        }

        Raise(nameof(FrameGenerationBypass));
    });

    /// <summary>Submits a decoded (enhanced or plain) frame to the active frame-generation pipeline.</summary>
    private void SubmitFrameForGeneration(byte[] bgra, int width, int height, long sequence, long arrivalTicks)
    {
        _frameGenerationPipeline?.Submit(bgra, width, height, sequence, arrivalTicks);
    }

    private void SubmitBitmapForGeneration(BitmapSource image, long sequence, long arrivalTicks)
    {
        if (_frameGenerationPipeline is null)
        {
            return;
        }

        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        SubmitFrameForGeneration(pixels, width, height, sequence, arrivalTicks);
    }

    public void RaiseFrameGenerationStats()
    {
        Raise(nameof(FrameGenerationStatusText));
        Raise(nameof(FrameGenerationEngineText));
        Raise(nameof(FrameGenerationInternalText));
        Raise(nameof(FrameGenerationConversionText));
        Raise(nameof(FrameGenerationInferenceText));
        Raise(nameof(FrameGenerationPresentedText));
        Raise(nameof(FrameGenerationGeneratedText));
        Raise(nameof(FrameGenerationDroppedText));
        Raise(nameof(FrameGenerationLatencyText));
        Raise(nameof(FrameGenerationSceneCutsText));
        Raise(nameof(FrameGenerationSkippedText));
        Raise(nameof(FrameGenerationActive));
        Raise(nameof(NativePresenterActive));
        Raise(nameof(FrameGenerationBypass));
        Raise(nameof(FrameGenerationBypassLabel));
        Raise(nameof(FrameGenerationBodyText));
        Raise(nameof(FrameGenerationDisplayText));
        Raise(nameof(FpsChipLabel));
        Raise(nameof(FpsChipLive));
        Raise(nameof(OutputFpsLabel));
        Raise(nameof(FrameGenBadgeVisible));
        Raise(nameof(FrameGenMultiplierDisplay));
        Raise(nameof(FrameGenFpsDisplay));
        Raise(nameof(LiveFpsLabel));
    }

    private void RaiseAllFrameGeneration()
    {
        Raise(nameof(FrameGenerationEnabled));
        Raise(nameof(FrameGenerationQuality));
        Raise(nameof(FrameGenerationLatencyPreference));
        Raise(nameof(FrameGenerationTarget));
        Raise(nameof(SelectedFrameGenerationTargetLabel));
        Raise(nameof(FrameGenerationDisplayText));
        Raise(nameof(FrameGenerationModelInstalled));
        Raise(nameof(FrameGenerationModelActionText));
        Raise(nameof(FrameGenerationBusy));
        Raise(nameof(FrameGenerationProgress));
        Raise(nameof(FrameGenerationStatusText));
        RaiseFrameGenerationStats();
    }
}
