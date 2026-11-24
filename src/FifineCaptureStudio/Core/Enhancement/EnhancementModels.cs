namespace FifineCaptureStudio.Core.Enhancement;

public enum EnhancementProfile
{
    Off,
    Natural,
    UltraSharp,
    Cinematic,
    Vibrant,
    CleanImage,
    Custom
}

public enum SuperResolutionTarget
{
    Native,
    P1080,
    P1440,
    P2160
}

public enum QualityPreset
{
    Performance,
    Balanced,
    Quality,
    UltraQuality
}

public enum UpscaleStrength
{
    Off,
    Low,
    Medium,
    High,
    Maximum
}

public enum FrameGenerationMode
{
    Off,
    X2,
    X3,
    X4,
    Auto
}

public enum EnhancementPerformanceMode
{
    UltraLowLatency,
    Balanced,
    VisualQuality
}

public enum ColourProfile
{
    Natural,
    Gaming,
    Cinematic,
    Vibrant,
    Custom
}

public enum ComparisonMode
{
    Single,
    Split,
    SideBySide
}

/// <summary>
/// One immutable snapshot of the enhancement configuration. The render thread reads a snapshot,
/// so changing a slider while frames are in flight cannot produce a half-applied pipeline.
/// </summary>
public sealed record EnhancementConfig
{
    public bool Enabled { get; init; }
    public EnhancementProfile Profile { get; init; } = EnhancementProfile.Natural;
    public EnhancementPerformanceMode Performance { get; init; } = EnhancementPerformanceMode.Balanced;

    // Super resolution
    public SuperResolutionTarget Target { get; init; } = SuperResolutionTarget.Native;
    public QualityPreset Quality { get; init; } = QualityPreset.Balanced;
    public UpscaleStrength Upscale { get; init; } = UpscaleStrength.Medium;
    public double DetailRecovery { get; init; } = 0.35;
    public double Sharpening { get; init; } = 0.35;
    public double EdgeClarity { get; init; } = 0.25;
    public double TextureEnhancement { get; init; } = 0.2;
    public double AntiAliasing { get; init; } = 0.2;
    public double CompressionReduction { get; init; } = 0.3;

    // Neural / denoise
    public bool Denoise { get; init; } = true;
    public bool Deblock { get; init; } = true;

    // Colour
    public ColourProfile Colour { get; init; } = ColourProfile.Natural;
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 0.1;
    public double Saturation { get; init; } = 0.1;
    public double Vibrance { get; init; } = 0.1;
    public double Gamma { get; init; }
    public double Temperature { get; init; }
    public double WhiteBalance { get; init; }
    public double BlackLevel { get; init; }
    public double ShadowDetail { get; init; } = 0.15;
    public double HighlightRecovery { get; init; } = 0.1;

    // Frame generation
    public FrameGenerationMode FrameGeneration { get; init; } = FrameGenerationMode.Off;
    public int OutputFpsTarget { get; init; }
    public bool GeneratedFramesForRecording { get; init; }

    // Output routing
    public bool PreviewEnhanced { get; init; } = true;
    public bool FullscreenEnhanced { get; init; } = true;
    public bool RecordingEnhanced { get; init; }

    // Comparison
    public ComparisonMode Comparison { get; init; } = ComparisonMode.Single;
    public bool ShowOriginal { get; init; }

    public bool HasFrameGeneration => FrameGeneration != FrameGenerationMode.Off;
    public bool HasSuperResolution => Target != SuperResolutionTarget.Native && Upscale != UpscaleStrength.Off;

    public static int Multiplier(FrameGenerationMode mode) => mode switch
    {
        FrameGenerationMode.X2 => 2,
        FrameGenerationMode.X3 => 3,
        FrameGenerationMode.X4 => 4,
        _ => 1
    };

    public static (int Width, int Height) Resolution(SuperResolutionTarget target) => target switch
    {
        SuperResolutionTarget.P1080 => (1920, 1080),
        SuperResolutionTarget.P1440 => (2560, 1440),
        SuperResolutionTarget.P2160 => (3840, 2160),
        _ => (0, 0)
    };

    public static double StrengthFactor(UpscaleStrength strength) => strength switch
    {
        UpscaleStrength.Low => 0.35,
        UpscaleStrength.Medium => 0.6,
        UpscaleStrength.High => 0.85,
        UpscaleStrength.Maximum => 1.0,
        _ => 0.0
    };
}

/// <summary>
/// GPU work measured with timestamp queries plus the CPU-side wait that is not GPU time.
/// <see cref="ReadbackWaitMs"/> is the wall-clock block inside Map (copy completion), while
/// <see cref="ReadbackMs"/> is only the GPU copy command itself — they must not be conflated.
/// </summary>
public readonly record struct EnhancementTimings(
    double UploadMs,
    double DenoiseMs,
    double ColourMs,
    double UpscaleMs,
    double ReadbackMs,
    double ReadbackWaitMs = 0)
{
    public double GpuMs => UploadMs + DenoiseMs + ColourMs + UpscaleMs + ReadbackMs;
    public double TotalMs => GpuMs + ReadbackWaitMs;
}

public sealed record EnhancedFrame(
    byte[] Pixels,
    int Stride,
    int SourceWidth,
    int SourceHeight,
    int OutputWidth,
    int OutputHeight,
    EnhancementTimings Timings,
    bool Enhanced)
{
    public System.Windows.Media.Imaging.BitmapSource ToBitmap()
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            OutputWidth,
            OutputHeight,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            Pixels,
            Stride);
        bitmap.Freeze();
        return bitmap;
    }
}

public sealed record GpuAdapterInfo(
    string Name,
    string DriverVersion,
    ulong DedicatedVideoMemory,
    bool IsAmd,
    bool SupportsCompute,
    int VendorId)
{
    public string VramText => DedicatedVideoMemory == 0
        ? "—"
        : (DedicatedVideoMemory / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";
}
