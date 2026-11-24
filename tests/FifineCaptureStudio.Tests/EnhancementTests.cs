using System.IO;
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

public class PreviewResolutionTests
{
    [Theory]
    [InlineData(1920, true, true, 1920)]
    [InlineData(2560, true, true, 2560)]
    [InlineData(3840, true, true, 3840)]
    [InlineData(7680, true, true, 3840)]
    [InlineData(1920, false, true, 1600)]
    [InlineData(1920, true, false, 1600)]
    public void Enhancement_asks_for_native_frames_and_plain_preview_stays_light(int source, bool enhancement, bool fullResolution, int expected)
    {
        Assert.Equal(expected, PipelineArgs.ResolvePreviewWidth(source, enhancement, fullResolution));
    }
}

public class LatestFrameQueueTests
{
    [Fact]
    public void Newest_frame_replaces_a_stale_queued_frame()
    {
        using var queue = new LatestFrameQueue<string>();

        Assert.True(queue.Submit("old"));
        Assert.False(queue.Submit("new"));
        Assert.Equal(1, queue.Replaced);
        Assert.Equal(2, queue.Submitted);

        Assert.True(queue.TryTake(out var frame));
        Assert.Equal("new", frame);
        Assert.Equal(1, queue.Taken);
        Assert.False(queue.HasFrame);
    }

    [Fact]
    public void Repeated_submissions_cannot_build_a_backlog()
    {
        using var queue = new LatestFrameQueue<string>();
        for (var i = 0; i < 100; i++)
        {
            queue.Submit("frame-" + i);
        }

        Assert.True(queue.TryTake(out var frame));
        Assert.Equal("frame-99", frame);
        Assert.False(queue.TryTake(out _));
        Assert.Equal(99, queue.Replaced);
    }

    [Fact]
    public void Take_returns_null_when_nothing_is_queued()
    {
        using var queue = new LatestFrameQueue<string>();
        Assert.False(queue.TryTake(out var frame));
        Assert.Null(frame);
    }
}

public class EnhancementTimingTests
{
    [Fact]
    public void Readback_wait_is_separate_from_gpu_copy_time()
    {
        var timings = new EnhancementTimings(1, 2, 3, 4, 5, 6);

        Assert.Equal(15, timings.GpuMs);
        Assert.Equal(21, timings.TotalMs);
    }

    [Fact]
    public void Present_percentiles_use_real_samples()
    {
        var root = Path.Combine(Path.GetTempPath(), "fifine-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new EnhancementService(new SettingsService(root));
            var frequency = System.Diagnostics.Stopwatch.Frequency;
            for (var i = 0; i < 12; i++)
            {
                var arrival = System.Diagnostics.Stopwatch.GetTimestamp();
                service.NotePresented(i, arrival, arrival + frequency / 100);
            }

            Assert.Equal(12, service.PresentedFrames);
            Assert.InRange(service.PresentAverageMs, 9.0, 11.0);
            Assert.InRange(service.PresentP95Ms, 9.0, 60.0);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

public class RecordingFilterTests
{
    [Fact]
    public void Recording_filter_maps_the_live_colour_and_sharpen_settings()
    {
        var settings = new EnhancementSettings
        {
            Enabled = true,
            RecordingEnhanced = true,
            Denoise = true,
            Brightness = 0.1,
            Contrast = 0.2,
            Saturation = 0.3,
            Vibrance = 0.2,
            Gamma = -0.1,
            Sharpening = 0.4
        };

        var filter = PipelineArgs.BuildRecordingEnhancementFilter(settings, 2560, 1440);

        Assert.Contains("libplacebo=w=2560:h=1440", filter);
        Assert.Contains("deband=true", filter);
        Assert.Contains("brightness=0.1", filter);
        Assert.Contains("contrast=1.2", filter);
        Assert.Contains("saturation=1.4", filter);
        Assert.Contains("gamma=0.9", filter);
        Assert.Contains("cas=strength=0.4", filter);
        Assert.DoesNotContain("minterpolate", filter);
    }

    [Fact]
    public void Recording_filter_is_empty_when_enhanced_recording_is_off()
    {
        var settings = new EnhancementSettings { Enabled = true, RecordingEnhanced = false };
        Assert.Equal(string.Empty, PipelineArgs.BuildRecordingEnhancementFilter(settings, 1920, 1080));

        settings.Enabled = false;
        settings.RecordingEnhanced = true;
        Assert.Equal(string.Empty, PipelineArgs.BuildRecordingEnhancementFilter(settings, 1920, 1080));
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
