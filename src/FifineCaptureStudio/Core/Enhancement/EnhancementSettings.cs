namespace FifineCaptureStudio.Core.Enhancement;

/// <summary>Persisted enhancement configuration. Values are clamped on load, never trusted.</summary>
public sealed class EnhancementSettings
{
    public bool Enabled { get; set; }
    public EnhancementProfile Profile { get; set; } = EnhancementProfile.Natural;
    public EnhancementPerformanceMode Performance { get; set; } = EnhancementPerformanceMode.Balanced;

    // Super resolution
    public SuperResolutionTarget Target { get; set; } = SuperResolutionTarget.Native;
    public QualityPreset Quality { get; set; } = QualityPreset.Balanced;
    public UpscaleStrength Upscale { get; set; } = UpscaleStrength.Medium;
    public double DetailRecovery { get; set; } = 0.35;
    public double Sharpening { get; set; } = 0.35;
    public double EdgeClarity { get; set; } = 0.25;
    public double TextureEnhancement { get; set; } = 0.2;
    public double AntiAliasing { get; set; } = 0.2;
    public double CompressionReduction { get; set; } = 0.3;
    public bool Denoise { get; set; } = true;
    public bool Deblock { get; set; } = true;

    // Colour
    public ColourProfile Colour { get; set; } = ColourProfile.Natural;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 0.1;
    public double Saturation { get; set; } = 0.1;
    public double Vibrance { get; set; } = 0.1;
    public double Gamma { get; set; }
    public double Temperature { get; set; }
    public double WhiteBalance { get; set; }
    public double BlackLevel { get; set; }
    public double ShadowDetail { get; set; } = 0.15;
    public double HighlightRecovery { get; set; } = 0.1;

    // Frame generation
    public FrameGenerationMode FrameGeneration { get; set; } = FrameGenerationMode.Off;
    public int OutputFpsTarget { get; set; }
    public bool GeneratedFramesForRecording { get; set; }

    // Output routing
    public bool PreviewEnhanced { get; set; } = true;
    public bool FullscreenEnhanced { get; set; } = true;
    public bool RecordingEnhanced { get; set; }
    public ComparisonMode Comparison { get; set; } = ComparisonMode.Single;
    public string HoldOriginalShortcut { get; set; } = "Tab";

    public void Normalize()
    {
        if (!Enum.IsDefined(Profile)) Profile = EnhancementProfile.Natural;
        if (!Enum.IsDefined(Performance)) Performance = EnhancementPerformanceMode.Balanced;
        if (!Enum.IsDefined(Target)) Target = SuperResolutionTarget.Native;
        if (!Enum.IsDefined(Quality)) Quality = QualityPreset.Balanced;
        if (!Enum.IsDefined(Upscale)) Upscale = UpscaleStrength.Medium;
        if (!Enum.IsDefined(Colour)) Colour = ColourProfile.Natural;
        if (!Enum.IsDefined(FrameGeneration)) FrameGeneration = FrameGenerationMode.Off;
        if (!Enum.IsDefined(Comparison)) Comparison = ComparisonMode.Single;
        HoldOriginalShortcut = string.IsNullOrWhiteSpace(HoldOriginalShortcut) ? "Tab" : HoldOriginalShortcut.Trim();

        DetailRecovery = Clamp01(DetailRecovery);
        Sharpening = Clamp01(Sharpening);
        EdgeClarity = Clamp01(EdgeClarity);
        TextureEnhancement = Clamp01(TextureEnhancement);
        AntiAliasing = Clamp01(AntiAliasing);
        CompressionReduction = Clamp01(CompressionReduction);
        Brightness = Clamp(ContrastSafe(Brightness), -1, 1);
        Contrast = Clamp(ContrastSafe(Contrast), -1, 1);
        Saturation = Clamp(ContrastSafe(Saturation), -1, 1);
        Vibrance = Clamp(ContrastSafe(Vibrance), -1, 1);
        Gamma = Clamp(Gamma, -0.9, 0.9);
        Temperature = Clamp(Temperature, -1, 1);
        WhiteBalance = Clamp(WhiteBalance, -1, 1);
        BlackLevel = Clamp(BlackLevel, 0, 1);
        ShadowDetail = Clamp01(ShadowDetail);
        HighlightRecovery = Clamp01(HighlightRecovery);

        var allowed = new[] { 0, 30, 60, 90, 120, 144, 165, 240 };
        if (!allowed.Contains(OutputFpsTarget)) OutputFpsTarget = 0;
    }

    private static double Clamp01(double value) => Clamp(value, 0, 1);

    private static double ContrastSafe(double value) => double.IsFinite(value) ? value : 0;

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : 0;

    public EnhancementConfig ToConfig() => new()
    {
        Enabled = Enabled,
        Profile = Profile,
        Performance = Performance,
        Target = Target,
        Quality = Quality,
        Upscale = Upscale,
        DetailRecovery = DetailRecovery,
        Sharpening = Sharpening,
        EdgeClarity = EdgeClarity,
        TextureEnhancement = TextureEnhancement,
        AntiAliasing = AntiAliasing,
        CompressionReduction = CompressionReduction,
        Denoise = Denoise,
        Deblock = Deblock,
        Colour = Colour,
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Vibrance = Vibrance,
        Gamma = Gamma,
        Temperature = Temperature,
        WhiteBalance = WhiteBalance,
        BlackLevel = BlackLevel,
        ShadowDetail = ShadowDetail,
        HighlightRecovery = HighlightRecovery,
        FrameGeneration = FrameGeneration,
        OutputFpsTarget = OutputFpsTarget,
        GeneratedFramesForRecording = GeneratedFramesForRecording,
        PreviewEnhanced = PreviewEnhanced,
        FullscreenEnhanced = FullscreenEnhanced,
        RecordingEnhanced = RecordingEnhanced,
        Comparison = Comparison
    };
}

/// <summary>Presets write concrete slider values so a profile is reproducible and editable.</summary>
public static class EnhancementPresets
{
    public static void Apply(EnhancementSettings s, EnhancementProfile profile)
    {
        s.Profile = profile;
        s.Enabled = profile != EnhancementProfile.Off;

        switch (profile)
        {
            case EnhancementProfile.Natural:
                s.Target = SuperResolutionTarget.Native;
                s.Upscale = UpscaleStrength.Off;
                s.Denoise = true;
                s.Deblock = true;
                s.CompressionReduction = 0.3;
                s.DetailRecovery = 0.3;
                s.Sharpening = 0.25;
                s.EdgeClarity = 0.15;
                s.TextureEnhancement = 0.12;
                s.AntiAliasing = 0.2;
                ApplyColour(s, ColourProfile.Natural);
                break;

            case EnhancementProfile.UltraSharp:
                s.Denoise = true;
                s.Deblock = true;
                s.CompressionReduction = 0.35;
                s.DetailRecovery = 0.55;
                s.Sharpening = 0.6;
                s.EdgeClarity = 0.5;
                s.TextureEnhancement = 0.4;
                s.AntiAliasing = 0.3;
                ApplyColour(s, ColourProfile.Gaming);
                break;

            case EnhancementProfile.Cinematic:
                s.Denoise = true;
                s.CompressionReduction = 0.25;
                s.DetailRecovery = 0.3;
                s.Sharpening = 0.2;
                s.EdgeClarity = 0.15;
                s.TextureEnhancement = 0.1;
                s.AntiAliasing = 0.35;
                ApplyColour(s, ColourProfile.Cinematic);
                break;

            case EnhancementProfile.Vibrant:
                s.Denoise = true;
                s.CompressionReduction = 0.25;
                s.DetailRecovery = 0.35;
                s.Sharpening = 0.35;
                s.EdgeClarity = 0.25;
                s.TextureEnhancement = 0.25;
                ApplyColour(s, ColourProfile.Vibrant);
                break;

            case EnhancementProfile.CleanImage:
                s.Denoise = true;
                s.Deblock = true;
                s.CompressionReduction = 0.7;
                s.DetailRecovery = 0.15;
                s.Sharpening = 0.1;
                s.EdgeClarity = 0.05;
                s.TextureEnhancement = 0.05;
                s.AntiAliasing = 0.45;
                ApplyColour(s, ColourProfile.Natural);
                break;

            case EnhancementProfile.Custom:
                break;
        }
    }

    public static void ApplyColour(EnhancementSettings s, ColourProfile colour)
    {
        s.Colour = colour;
        switch (colour)
        {
            case ColourProfile.Natural:
                s.Brightness = 0;
                s.Contrast = 0.06;
                s.Saturation = 0.05;
                s.Vibrance = 0.08;
                s.Gamma = 0;
                s.Temperature = 0;
                s.BlackLevel = 0;
                s.ShadowDetail = 0.12;
                s.HighlightRecovery = 0.08;
                break;

            case ColourProfile.Gaming:
                s.Contrast = 0.16;
                s.Saturation = 0.14;
                s.Vibrance = 0.22;
                s.ShadowDetail = 0.28;
                s.HighlightRecovery = 0.16;
                s.BlackLevel = 0.04;
                break;

            case ColourProfile.Cinematic:
                s.Contrast = 0.2;
                s.Saturation = -0.02;
                s.Vibrance = 0.06;
                s.Gamma = -0.05;
                s.Temperature = -0.06;
                s.BlackLevel = 0.1;
                s.ShadowDetail = 0.08;
                s.HighlightRecovery = 0.3;
                break;

            case ColourProfile.Vibrant:
                s.Contrast = 0.12;
                s.Saturation = 0.32;
                s.Vibrance = 0.4;
                s.ShadowDetail = 0.2;
                s.HighlightRecovery = 0.1;
                break;
        }
    }
}
