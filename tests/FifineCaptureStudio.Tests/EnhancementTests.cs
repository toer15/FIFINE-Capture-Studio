using FifineCaptureStudio.Capture;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Enhancement;
using Xunit;

namespace FifineCaptureStudio.Tests;

public class EnhancementSettingsTests
{
    [Fact]
    public void Defaults_are_neutral_and_disabled()
    {
        var settings = new AppSettings();
        AppSettings.Normalize(settings);

        Assert.False(settings.Enhancement.Enabled);
        Assert.Equal(SuperResolutionTarget.Native, settings.Enhancement.Target);
        Assert.Equal(UpscaleStrength.Medium, settings.Enhancement.Upscale);
        Assert.Equal(FrameGenerationMode.Off, settings.Enhancement.FrameGeneration);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Theory]
    [InlineData(-5, 0, -1)]
    [InlineData(42, 1, 1)]
    [InlineData(0.4, 0.4, 0.4)]
    public void Slider_values_are_clamped(double input, double expectedStrength, double expectedContrast)
    {
        var settings = new AppSettings();
        settings.Enhancement.Sharpening = input;
        settings.Enhancement.Contrast = input;
        AppSettings.Normalize(settings);

        Assert.Equal(expectedStrength, settings.Enhancement.Sharpening);
        Assert.Equal(expectedContrast, settings.Enhancement.Contrast);
    }

    [Fact]
    public void Unknown_enum_values_fall_back()
    {
        var settings = new AppSettings();
        settings.Enhancement.Target = (SuperResolutionTarget)99;
        settings.Enhancement.Profile = (EnhancementProfile)42;
        settings.Enhancement.OutputFpsTarget = 333;

        AppSettings.Normalize(settings);

        Assert.Equal(SuperResolutionTarget.Native, settings.Enhancement.Target);
        Assert.Equal(EnhancementProfile.Natural, settings.Enhancement.Profile);
        Assert.Equal(0, settings.Enhancement.OutputFpsTarget);
    }

    [Fact]
    public void Presets_write_concrete_values()
    {
        var settings = new EnhancementSettings();
        EnhancementPresets.Apply(settings, EnhancementProfile.CleanImage);

        Assert.True(settings.Enabled);
        Assert.True(settings.Denoise);
        Assert.True(settings.CompressionReduction >= 0.6);
        Assert.True(settings.Sharpening <= 0.15);

        EnhancementPresets.Apply(settings, EnhancementProfile.Vibrant);
        Assert.True(settings.Saturation > 0.2);
        Assert.Equal(ColourProfile.Vibrant, settings.Colour);
    }
}

public class EnhancementPipelineTests
{
    private static PipelineOptions Options(string filter) => new()
    {
        Source = SourceSpec.TestPattern(1920, 1080, 60),
        PreviewEnabled = true,
        RecordPath = "C:\\temp\\out.mp4",
        EncodeWidth = 2560,
        EncodeHeight = 1440,
        EncodeFps = 60,
        Encoder = new EncoderInfo("libx264", "CPU x264", "CPU", VideoCodecKind.H264, false),
        EnhancementFilter = filter
    };

    [Fact]
    public void Enhanced_recording_uses_the_gpu_filter_instead_of_a_plain_scale()
    {
        var args = PipelineArgs.Build(Options("libplacebo=w=2560:h=1440:contrast=1.1,cas=strength=0.4"));
        var filter = args[args.FindLastIndex(a => a == "-vf") + 1];

        Assert.Contains("libplacebo=w=2560:h=1440", filter);
        Assert.Contains("cas=strength=0.4", filter);
        Assert.DoesNotContain("scale=2560:1440", filter);
    }

    [Fact]
    public void Disabled_enhancement_keeps_the_existing_scale_path()
    {
        var args = PipelineArgs.Build(Options(string.Empty));
        var filter = args[args.FindLastIndex(a => a == "-vf") + 1];

        Assert.Contains("scale=2560:1440", filter);
        Assert.DoesNotContain("libplacebo", filter);
    }

    [Fact]
    public void Frame_generation_is_not_written_to_recordings_by_the_enhancement_engine()
    {
        var args = PipelineArgs.Build(Options("libplacebo=w=2560:h=1440"));
        var filter = args[args.FindLastIndex(a => a == "-vf") + 1];

        Assert.DoesNotContain("minterpolate", filter);
    }
}

public class EnhancementGpuTests
{
    [Fact]
    public void Engine_processes_a_real_frame_on_an_available_adapter()
    {
        var adapters = GpuDetection.Detect();
        var primary = GpuDetection.Primary(adapters);
        if (primary is null)
        {
            return; // No Direct3D 11 adapter in this environment.
        }

        using var engine = new EnhancementEngine(primary);

        const int width = 320;
        const int height = 180;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 4;
                pixels[o] = (byte)(x % 256);
                pixels[o + 1] = (byte)(y % 256);
                pixels[o + 2] = 96;
                pixels[o + 3] = 255;
            }
        }

        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();

        var frame = engine.Process(source, new EnhancementConfig
        {
            Enabled = true,
            Target = SuperResolutionTarget.P1080,
            Upscale = UpscaleStrength.High,
            Denoise = true,
            Deblock = true,
            Sharpening = 0.4
        });

        Assert.NotNull(frame);
        Assert.Equal(1920, frame!.OutputWidth);
        Assert.Equal(1080, frame.OutputHeight);
        Assert.Equal(1920 * 4, frame.Stride);
        Assert.True(frame.Pixels.Length >= 1920 * 1080 * 4);

        // Colour, not just alpha: a broken D3D11 binding once produced an opaque black frame that a
        // plain non-zero check happily accepted.
        var colourPixels = 0;
        for (var i = 0; i + 3 < frame.Pixels.Length; i += 4)
        {
            if (frame.Pixels[i] > 16 || frame.Pixels[i + 1] > 16 || frame.Pixels[i + 2] > 16)
            {
                colourPixels++;
            }
        }

        var totalPixels = frame.Pixels.Length / 4;
        Assert.True(colourPixels > totalPixels / 2, $"GPU output looks black: {colourPixels}/{totalPixels} coloured pixels");
    }
}
