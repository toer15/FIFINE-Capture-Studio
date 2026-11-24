using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Enhancement;

namespace FifineCaptureStudio.ViewModels;

public sealed record EnhancementOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class StudioViewModel
{
    private LatestFrameQueue<QueuedPreviewFrame>? _enhanceQueue;
    private CancellationTokenSource? _enhanceCts;
    private Task? _enhanceWorker;

    /// <summary>One decoded preview frame plus the boundaries the worker needs for telemetry.</summary>
    private sealed record QueuedPreviewFrame(BitmapSource Image, FrameArrival Arrival);
    private BitmapSource? _originalPreviewImage;
    private bool _showOriginal;
    private bool _quickPanelOpen;

    public IReadOnlyList<EnhancementProfile> EnhancementProfiles { get; } = new[]
    {
        EnhancementProfile.Natural,
        EnhancementProfile.UltraSharp,
        EnhancementProfile.Cinematic,
        EnhancementProfile.Vibrant,
        EnhancementProfile.CleanImage,
        EnhancementProfile.Custom
    };

    public IReadOnlyList<EnhancementPerformanceMode> PerformanceModes { get; } = new[]
    {
        EnhancementPerformanceMode.UltraLowLatency,
        EnhancementPerformanceMode.Balanced,
        EnhancementPerformanceMode.VisualQuality
    };

    public IReadOnlyList<EnhancementOption<SuperResolutionTarget>> SuperResolutionTargets { get; } = new[]
    {
        new EnhancementOption<SuperResolutionTarget>(SuperResolutionTarget.Native, "Native source resolution"),
        new EnhancementOption<SuperResolutionTarget>(SuperResolutionTarget.P1080, "1080p (1920 × 1080)"),
        new EnhancementOption<SuperResolutionTarget>(SuperResolutionTarget.P1440, "1440p (2560 × 1440)"),
        new EnhancementOption<SuperResolutionTarget>(SuperResolutionTarget.P2160, "4K (3840 × 2160)")
    };

    public IReadOnlyList<QualityPreset> QualityPresets { get; } = Enum.GetValues<QualityPreset>();
    public IReadOnlyList<UpscaleStrength> UpscaleStrengths { get; } = Enum.GetValues<UpscaleStrength>();
    public IReadOnlyList<ColourProfile> ColourProfiles { get; } = Enum.GetValues<ColourProfile>();
    public IReadOnlyList<ComparisonMode> ComparisonModes { get; } = Enum.GetValues<ComparisonMode>();

    // ------------------------------------------------------------------ bindings

    public bool EnhancementEnabled
    {
        get => Config.Enhancement.Enabled;
        set
        {
            if (Config.Enhancement.Enabled == value) return;
            Config.Enhancement.Enabled = value;
            SaveEnhancement();
            // The preview size is baked into the FFmpeg arguments: enabling enhancement must rebuild
            // the pipeline so native frames reach the engine instead of the light 1600 px stream.
            _ = RestartForEnhancementChangeAsync();
        }
    }

    public EnhancementProfile SelectedProfile
    {
        get => Config.Enhancement.Profile;
        set
        {
            EnhancementPresets.Apply(Config.Enhancement, value);
            SaveEnhancement();
            RaiseAllEnhancement();
        }
    }

    public EnhancementPerformanceMode SelectedPerformanceMode
    {
        get => Config.Enhancement.Performance;
        set
        {
            Config.Enhancement.Performance = value;
            ApplyPerformanceDefaults(value);
            SaveEnhancement();
            RaiseAllEnhancement();
        }
    }

    public EnhancementOption<SuperResolutionTarget>? SelectedSuperResolution
    {
        get => SuperResolutionTargets.FirstOrDefault(o => o.Value == Config.Enhancement.Target);
        set
        {
            if (value is null) return;
            Config.Enhancement.Target = value.Value;
            SaveEnhancement();
            RaiseAllEnhancement();
        }
    }

    public QualityPreset SelectedQualityPreset
    {
        get => Config.Enhancement.Quality;
        set { Config.Enhancement.Quality = value; SaveEnhancement(); }
    }

    public UpscaleStrength SelectedUpscaleStrength
    {
        get => Config.Enhancement.Upscale;
        set { Config.Enhancement.Upscale = value; SaveEnhancement(); RaiseAllEnhancement(); }
    }

    public ColourProfile SelectedColourProfile
    {
        get => Config.Enhancement.Colour;
        set
        {
            EnhancementPresets.ApplyColour(Config.Enhancement, value);
            SaveEnhancement();
            RaiseAllEnhancement();
        }
    }

    public ComparisonMode SelectedComparison
    {
        get => Config.Enhancement.Comparison;
        set
        {
            Config.Enhancement.Comparison = value;
            SaveEnhancement();
            Raise(nameof(IsComparisonSingle));
            Raise(nameof(IsComparisonSplit));
            Raise(nameof(IsComparisonSideBySide));
        }
    }

    public bool IsComparisonSingle => Config.Enhancement.Comparison == ComparisonMode.Single;
    public bool IsComparisonSplit => Config.Enhancement.Comparison == ComparisonMode.Split;
    public bool IsComparisonSideBySide => Config.Enhancement.Comparison == ComparisonMode.SideBySide;

    public bool ShowOriginal
    {
        get => _showOriginal;
        set
        {
            if (Set(ref _showOriginal, value))
            {
                Raise(nameof(EnhancementBadgeText));
                PreviewImage = value ? _originalPreviewImage : _enhancedPreviewImage;
            }
        }
    }

    public bool QuickPanelOpen
    {
        get => _quickPanelOpen;
        set
        {
            if (!Set(ref _quickPanelOpen, value))
            {
                return;
            }

            Config.Ui.QuickPanelOpen = value;
            _services.Settings.SaveSoon();
        }
    }

    public bool EnhancementDenoise
    {
        get => Config.Enhancement.Denoise;
        set { Config.Enhancement.Denoise = value; SaveEnhancement(); }
    }

    public bool EnhancementDeblock
    {
        get => Config.Enhancement.Deblock;
        set { Config.Enhancement.Deblock = value; SaveEnhancement(); }
    }

    public bool RecordingEnhanced
    {
        get => Config.Enhancement.RecordingEnhanced;
        set
        {
            Config.Enhancement.RecordingEnhanced = value;
            SaveEnhancement();
            _ = RestartForEnhancementChangeAsync();
        }
    }

    public bool PreviewEnhanced
    {
        get => Config.Enhancement.PreviewEnhanced;
        set
        {
            if (Config.Enhancement.PreviewEnhanced == value) return;
            Config.Enhancement.PreviewEnhanced = value;
            SaveEnhancement();
            _services.Session.RequestFullResolutionPreview();
            _ = RestartForEnhancementChangeAsync();
        }
    }

    /// <summary>
    /// Requests the capture pipeline's native frame size for the enhancement engine. Turning this
    /// off keeps the lightweight 1600 px preview. Auto-downgraded by CaptureSession when CPU MJPEG
    /// decoding cannot sustain the source frame rate.
    /// </summary>
    public bool FullResolutionPreview
    {
        get => Config.Enhancement.FullResolutionPreview;
        set
        {
            if (Config.Enhancement.FullResolutionPreview == value) return;
            Config.Enhancement.FullResolutionPreview = value;
            SaveEnhancement();
            _services.Session.RequestFullResolutionPreview();
            _ = RestartForEnhancementChangeAsync();
        }
    }

    public bool FullscreenEnhanced
    {
        get => Config.Enhancement.FullscreenEnhanced;
        set { Config.Enhancement.FullscreenEnhanced = value; SaveEnhancement(); }
    }

    public double EnhancementDetail
    {
        get => Config.Enhancement.DetailRecovery;
        set { Config.Enhancement.DetailRecovery = value; SaveEnhancement(); }
    }

    public double EnhancementSharpening
    {
        get => Config.Enhancement.Sharpening;
        set { Config.Enhancement.Sharpening = value; SaveEnhancement(); }
    }

    public double EnhancementEdge
    {
        get => Config.Enhancement.EdgeClarity;
        set { Config.Enhancement.EdgeClarity = value; SaveEnhancement(); }
    }

    public double EnhancementTexture
    {
        get => Config.Enhancement.TextureEnhancement;
        set { Config.Enhancement.TextureEnhancement = value; SaveEnhancement(); }
    }

    public double EnhancementAntiAliasing
    {
        get => Config.Enhancement.AntiAliasing;
        set { Config.Enhancement.AntiAliasing = value; SaveEnhancement(); }
    }

    public double EnhancementCompression
    {
        get => Config.Enhancement.CompressionReduction;
        set { Config.Enhancement.CompressionReduction = value; SaveEnhancement(); }
    }

    public double EnhancementBrightness
    {
        get => Config.Enhancement.Brightness;
        set { Config.Enhancement.Brightness = value; SaveEnhancement(); }
    }

    public double EnhancementContrast
    {
        get => Config.Enhancement.Contrast;
        set { Config.Enhancement.Contrast = value; SaveEnhancement(); }
    }

    public double EnhancementSaturation
    {
        get => Config.Enhancement.Saturation;
        set { Config.Enhancement.Saturation = value; SaveEnhancement(); }
    }

    public double EnhancementVibrance
    {
        get => Config.Enhancement.Vibrance;
        set { Config.Enhancement.Vibrance = value; SaveEnhancement(); }
    }

    public double EnhancementGamma
    {
        get => Config.Enhancement.Gamma;
        set { Config.Enhancement.Gamma = value; SaveEnhancement(); }
    }

    public double EnhancementTemperature
    {
        get => Config.Enhancement.Temperature;
        set { Config.Enhancement.Temperature = value; SaveEnhancement(); }
    }

    public double EnhancementWhiteBalance
    {
        get => Config.Enhancement.WhiteBalance;
        set { Config.Enhancement.WhiteBalance = value; SaveEnhancement(); }
    }

    public double EnhancementBlackLevel
    {
        get => Config.Enhancement.BlackLevel;
        set { Config.Enhancement.BlackLevel = value; SaveEnhancement(); }
    }

    public double EnhancementShadows
    {
        get => Config.Enhancement.ShadowDetail;
        set { Config.Enhancement.ShadowDetail = value; SaveEnhancement(); }
    }

    public double EnhancementHighlights
    {
        get => Config.Enhancement.HighlightRecovery;
        set { Config.Enhancement.HighlightRecovery = value; SaveEnhancement(); }
    }

    // ------------------------------------------------------------------ stats

    public string EnhancementAdapterText => _services.Enhancement.AdapterName;
    public string EnhancementStatusText => _services.Enhancement.Status;
    public bool EnhancementAvailable => _services.Enhancement.Available;
    public string EnhancementFpsText => _services.Enhancement.MeasuredFps <= 0
        ? "—"
        : _services.Enhancement.MeasuredFps.ToString("0.0") + " fps";
    public string EnhancementFrameTimeText => _services.Enhancement.AverageFrameMs <= 0
        ? "—"
        : _services.Enhancement.AverageFrameMs.ToString("0.00") + " ms";
    public string EnhancementUpscaleMsText => _services.Enhancement.UpscaleMs <= 0
        ? "—"
        : _services.Enhancement.UpscaleMs.ToString("0.00") + " ms";
    public string EnhancementNeuralMsText => _services.Enhancement.DenoiseMs <= 0
        ? "—"
        : _services.Enhancement.DenoiseMs.ToString("0.00") + " ms";
    public string EnhancementColourMsText => _services.Enhancement.ColourMs <= 0
        ? "—"
        : _services.Enhancement.ColourMs.ToString("0.00") + " ms";
    public string EnhancementVramText => _services.Enhancement.VramUsedMb <= 0
        ? "—"
        : _services.Enhancement.VramUsedMb.ToString("0") + " MB";
    public string EnhancementProcessedText => _services.Enhancement.ProcessedFrames.ToString("N0");
    public string EnhancementDroppedText => _services.Enhancement.DroppedFrames.ToString("N0");

    public string EnhancementInputResolutionText
    {
        get
        {
            var enhanced = _services.Enhancement.ShouldEnhancePreview;
            var w = enhanced ? _services.Enhancement.LastInputWidth : 0;
            var h = enhanced ? _services.Enhancement.LastInputHeight : 0;
            if (w <= 0)
            {
                w = _services.Session.ActivePreviewWidth;
                h = _services.Session.ActivePreviewHeight;
            }

            return w > 0 ? $"{w} × {h}" : "—";
        }
    }

    public string EnhancementInputNote
    {
        get
        {
            if (!EnhancementEnabled)
            {
                return "Enhancement is off — the preview stays on the lightweight 1600 px stream.";
            }

            var source = _services.Session.ActiveSourceWidth;
            var input = _services.Session.ActivePreviewWidth;
            if (source > 0 && input >= source && input > 1600)
            {
                return $"Native {input} px frames enter the GPU engine; decode stays on the FFmpeg MJPEG transport.";
            }

            if (input > 1600)
            {
                return $"The enhancement engine receives {input} px frames (capture source {source} px).";
            }

            return "The enhancement engine receives the lightweight 1600 px preview; recording keeps the full source size. " +
                   "Enable full-resolution input when this GPU and source can decode native frames at the capture frame rate.";
        }
    }

    public string EnhancementSourceFpsText => _services.Session.MeasuredFps <= 0
        ? "—"
        : _services.Session.MeasuredFps.ToString("0.0") + " fps";

    public string EnhancementProcessedFpsText => _services.Enhancement.MeasuredFps <= 0
        ? "—"
        : _services.Enhancement.MeasuredFps.ToString("0.0") + " fps";

    public string EnhancementPresentedFpsText => _services.Enhancement.PresentedFps <= 0
        ? "—"
        : _services.Enhancement.PresentedFps.ToString("0.0") + " fps";

    public string EnhancementQueueAgeText => _services.Enhancement.QueueAgeMs <= 0
        ? "—"
        : _services.Enhancement.QueueAgeMs.ToString("0.0") + " ms";

    public string EnhancementDecodeText => _services.Enhancement.DecodeMs <= 0
        ? "—"
        : _services.Enhancement.DecodeMs.ToString("0.00") + " ms";

    public string EnhancementGpuText => _services.Enhancement.AverageFrameMs <= 0
        ? "—"
        : (_services.Enhancement.AverageFrameMs - _services.Enhancement.ReadbackWaitMs).ToString("0.00") + " ms";

    public string EnhancementTotalText => _services.Enhancement.AverageFrameMs <= 0
        ? "—"
        : _services.Enhancement.AverageFrameMs.ToString("0.00") + " ms";

    public string EnhancementPresentLatencyText
    {
        get
        {
            var svc = _services.Enhancement;
            if (svc.PresentSampleCount < 8 || svc.PresentAverageMs <= 0)
            {
                return "—";
            }

            return $"{svc.PresentAverageMs:0.0} ms avg · {svc.PresentP95Ms:0.0} ms p95";
        }
    }

    public string EnhancementLatencyBoundaryText =>
        "Decode-complete → compositor present, measured in-app. Shader time is GPU work only. " +
        "Camera, capture-card and display scan-out delay are outside this measurement.";

    public string EnhancementBadgeText
    {
        get
        {
            if (!EnhancementAvailable) return "Enhancement unavailable";
            if (!EnhancementEnabled) return "Enhancement off";
            var target = Config.Enhancement.Target == SuperResolutionTarget.Native
                ? "native"
                : $"{EnhancementConfig.Resolution(Config.Enhancement.Target).Width}p";
            return ShowOriginal ? "Original (held)" : $"Enhanced · {target}";
        }
    }

    public bool EnhancementBadgeVisible => EnhancementAvailable && EnhancementEnabled && HasPreviewFrame;

    public string EnhancementSourceResolutionText
    {
        get
        {
            var w = _services.Session.ActiveSourceWidth;
            var h = _services.Session.ActiveSourceHeight;
            return w > 0 ? $"{w} × {h}" : "—";
        }
    }

    public string EnhancementOutputResolutionText
    {
        get
        {
            var w = _services.Session.ActiveSourceWidth;
            var h = _services.Session.ActiveSourceHeight;
            var (tw, th) = EnhancementConfig.Resolution(Config.Enhancement.Target);
            if (tw > 0)
            {
                return $"{tw} × {th}";
            }

            return w > 0 ? $"{w} × {h} (native)" : "—";
        }
    }

    public string EnhancementOutputNote =>
        "Enhanced output is reconstructed from the incoming video. It does not add native detail that the console never sent.";

    // ------------------------------------------------------------------ commands

    public ICommand ApplyEnhancementProfileCommand { get; private set; } = null!;
    public ICommand DisableEnhancementCommand { get; private set; } = null!;
    public ICommand OpenEnhancementPageCommand { get; private set; } = null!;
    public ICommand ToggleQuickPanelCommand { get; private set; } = null!;
    public ICommand ApplyColourProfileCommand { get; private set; } = null!;

    private void InitializeEnhancement()
    {
        _quickPanelOpen = Config.Ui.QuickPanelOpen;

        ApplyEnhancementProfileCommand = new RelayCommand(p =>
        {
            if (p is EnhancementProfile profile)
            {
                SelectedProfile = profile;
            }
        });

        ApplyColourProfileCommand = new RelayCommand(p =>
        {
            if (p is ColourProfile profile)
            {
                SelectedColourProfile = profile;
            }
        });

        DisableEnhancementCommand = new RelayCommand(() =>
        {
            EnhancementEnabled = false;
            ShowOriginal = true;
            RaiseAllEnhancement();
        });

        OpenEnhancementPageCommand = new RelayCommand(() => CurrentPage = "enhance");
        ToggleQuickPanelCommand = new RelayCommand(() => QuickPanelOpen = !QuickPanelOpen);
    }

    private void ApplyPerformanceDefaults(EnhancementPerformanceMode mode)
    {
        switch (mode)
        {
            case EnhancementPerformanceMode.UltraLowLatency:
                Config.Enhancement.Denoise = false;
                Config.Enhancement.Deblock = false;
                Config.Enhancement.AntiAliasing = 0.05;
                Config.Enhancement.Sharpening = 0.2;
                Config.Enhancement.Upscale = UpscaleStrength.Low;
                break;

            case EnhancementPerformanceMode.VisualQuality:
                Config.Enhancement.Denoise = true;
                Config.Enhancement.Deblock = true;
                Config.Enhancement.AntiAliasing = 0.35;
                Config.Enhancement.Sharpening = 0.45;
                Config.Enhancement.Upscale = UpscaleStrength.High;
                break;

            default:
                Config.Enhancement.Denoise = true;
                Config.Enhancement.Deblock = true;
                Config.Enhancement.AntiAliasing = 0.2;
                Config.Enhancement.Sharpening = 0.35;
                Config.Enhancement.Upscale = UpscaleStrength.Medium;
                break;
        }
    }

    private void SaveEnhancement()
    {
        Config.Enhancement.Normalize();
        _services.Settings.SaveSoon();
        _services.Enhancement.Reload();
        RaiseAllEnhancement();
    }

    private void RaiseAllEnhancement()
    {
        Raise(nameof(EnhancementEnabled));
        Raise(nameof(SelectedProfile));
        Raise(nameof(SelectedPerformanceMode));
        Raise(nameof(SelectedSuperResolution));
        Raise(nameof(SelectedQualityPreset));
        Raise(nameof(SelectedUpscaleStrength));
        Raise(nameof(SelectedColourProfile));
        Raise(nameof(SelectedComparison));
        Raise(nameof(IsComparisonSingle));
        Raise(nameof(IsComparisonSplit));
        Raise(nameof(IsComparisonSideBySide));
        Raise(nameof(EnhancementDenoise));
        Raise(nameof(EnhancementDeblock));
        Raise(nameof(PreviewEnhanced));
        Raise(nameof(FullscreenEnhanced));
        Raise(nameof(RecordingEnhanced));
        Raise(nameof(EnhancementDetail));
        Raise(nameof(EnhancementSharpening));
        Raise(nameof(EnhancementEdge));
        Raise(nameof(EnhancementTexture));
        Raise(nameof(EnhancementAntiAliasing));
        Raise(nameof(EnhancementCompression));
        Raise(nameof(EnhancementBrightness));
        Raise(nameof(EnhancementContrast));
        Raise(nameof(EnhancementSaturation));
        Raise(nameof(EnhancementVibrance));
        Raise(nameof(EnhancementGamma));
        Raise(nameof(EnhancementTemperature));
        Raise(nameof(EnhancementWhiteBalance));
        Raise(nameof(EnhancementBlackLevel));
        Raise(nameof(EnhancementShadows));
        Raise(nameof(EnhancementHighlights));
        RaiseEnhancementStats();
    }

    public void RaiseEnhancementStats()
    {
        Raise(nameof(EnhancementAdapterText));
        Raise(nameof(EnhancementStatusText));
        Raise(nameof(EnhancementAvailable));
        Raise(nameof(EnhancementFpsText));
        Raise(nameof(EnhancementFrameTimeText));
        Raise(nameof(EnhancementUpscaleMsText));
        Raise(nameof(EnhancementNeuralMsText));
        Raise(nameof(EnhancementColourMsText));
        Raise(nameof(EnhancementVramText));
        Raise(nameof(EnhancementProcessedText));
        Raise(nameof(EnhancementDroppedText));
        Raise(nameof(EnhancementBadgeText));
        Raise(nameof(EnhancementBadgeVisible));
        Raise(nameof(EnhancementSourceResolutionText));
        Raise(nameof(EnhancementOutputResolutionText));
        Raise(nameof(EnhancementInputResolutionText));
        Raise(nameof(EnhancementInputNote));
        Raise(nameof(EnhancementSourceFpsText));
        Raise(nameof(EnhancementProcessedFpsText));
        Raise(nameof(EnhancementPresentedFpsText));
        Raise(nameof(EnhancementQueueAgeText));
        Raise(nameof(EnhancementDecodeText));
        Raise(nameof(EnhancementGpuText));
        Raise(nameof(EnhancementTotalText));
        Raise(nameof(EnhancementPresentLatencyText));
        Raise(nameof(FullResolutionPreview));
    }

    // ------------------------------------------------------------------ frame routing

    private BitmapSource? _enhancedPreviewImage;

    public BitmapSource? OriginalPreviewImage
    {
        get => _originalPreviewImage;
        private set => Set(ref _originalPreviewImage, value);
    }

    private LatestFrameQueue<QueuedPreviewFrame> EnhancementQueue => _enhanceQueue ??= new LatestFrameQueue<QueuedPreviewFrame>();

    private void StartEnhancementWorker()
    {
        if (_enhanceWorker is { IsCompleted: false })
        {
            return;
        }

        var queue = EnhancementQueue;
        _enhanceCts?.Dispose();
        _enhanceCts = new CancellationTokenSource();
        var token = _enhanceCts.Token;

        _enhanceWorker = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                QueuedPreviewFrame? item;
                try
                {
                    item = await queue.TakeAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (item is null)
                {
                    continue;
                }

                EnhancedFrame? result = null;
                try
                {
                    result = _services.Enhancement.Process(item.Image, item.Arrival);
                }
                catch (Exception ex)
                {
                    Log.Debug("Enhancement worker: " + ex.Message);
                }

                _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    _framePending = 0;
                    var presented = result is not null;
                    if (presented)
                    {
                        _enhancedPreviewImage = result!.ToBitmap();
                        if (!ShowOriginal)
                        {
                            PreviewImage = _enhancedPreviewImage;
                        }
                    }
                    else if (!ShowOriginal)
                    {
                        PreviewImage = item.Image;
                    }

                    // The compositor paints this frame on the next rendering pass, which is the
                    // presentation boundary for capture-to-preview latency.
                    Interlocked.Exchange(ref _pendingPresentTicks, item.Arrival.ArrivalTicks);
                    Interlocked.Exchange(ref _pendingPresentSequence, item.Arrival.Sequence);
                    Interlocked.Exchange(ref _pendingPresentEnhanced, presented ? 1 : 0);

                    Raise(nameof(ShowNoSignal));
                    Raise(nameof(HasPreviewFrame));
                    RaiseEnhancementStats();
                }));
            }
        });
    }

    private void StopEnhancementWorker()
    {
        _enhanceCts?.Cancel();
        _enhanceQueue?.Complete();
        _enhanceWorker = null;
    }

    private async Task RestartForEnhancementChangeAsync()
    {
        if (!IsPreviewing || IsRecording)
        {
            return;
        }

        try
        {
            await _services.Session.RestartPreviewAsync();
        }
        catch (Exception ex)
        {
            Log.Debug("Enhancement restart failed: " + ex.Message);
        }
    }
}
