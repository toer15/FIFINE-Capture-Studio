using FifineCaptureStudio.Capture;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Enhancement;
using Xunit;

namespace FifineCaptureStudio.Tests;

/// <summary>
/// Regression coverage for the native-preview overload fallback. This is deliberately a
/// frame-by-frame model of the real decision: 89 slow frames must not downgrade, the 90th must,
/// and a healthy frame must restart the consecutive run.
/// </summary>
public class PreviewFallbackMonitorTests
{
    [Fact]
    public void Ninety_consecutive_overloaded_frames_are_required()
    {
        var monitor = new PreviewFallbackMonitor();

        for (var i = 0; i < PreviewFallbackMonitor.FramesBeforeFallback - 1; i++)
        {
            Assert.False(monitor.Observe(7.6, 60));
        }

        Assert.False(monitor.FallbackApplied);
        Assert.Equal(89, monitor.OverloadedFrames);
        Assert.True(monitor.Observe(7.6, 60));
        Assert.True(monitor.FallbackApplied);
        Assert.Equal(90, monitor.OverloadedFrames);
    }

    [Theory]
    [InlineData(60, 7.49, false)]
    [InlineData(60, 7.51, true)]
    [InlineData(30, 14.99, false)]
    [InlineData(30, 15.01, true)]
    [InlineData(24, 18.74, false)]
    [InlineData(24, 18.76, true)]
    public void Threshold_is_45_percent_of_the_source_frame_budget(int fps, double decodeMs, bool overloaded)
    {
        var monitor = new PreviewFallbackMonitor();
        monitor.Observe(decodeMs, fps);

        Assert.Equal(overloaded ? 1 : 0, monitor.OverloadedFrames);
    }

    [Theory]
    [InlineData(60, 7.5)]
    [InlineData(30, 15.0)]
    [InlineData(24, 18.75)]
    public void Threshold_values_match_the_frame_budget(int fps, double expected)
    {
        Assert.Equal(expected, PreviewFallbackMonitor.ThresholdMs(fps), 3);
    }

    [Fact]
    public void A_healthy_frame_clears_the_consecutive_overload_run()
    {
        var monitor = new PreviewFallbackMonitor();
        for (var i = 0; i < 10; i++)
        {
            monitor.Observe(100, 60);
        }

        // Averages down below the threshold and resets the run before it can reach 90.
        for (var i = 0; i < 80; i++)
        {
            monitor.Observe(1, 60);
        }

        Assert.False(monitor.FallbackApplied);
        Assert.Equal(0, monitor.OverloadedFrames);
    }

    [Fact]
    public void Manual_re_enable_clears_the_fallback_and_its_history()
    {
        var monitor = new PreviewFallbackMonitor();
        for (var i = 0; i < PreviewFallbackMonitor.FramesBeforeFallback; i++)
        {
            monitor.Observe(20, 30);
        }

        Assert.True(monitor.FallbackApplied);
        monitor.RequestFullResolution();

        Assert.False(monitor.FallbackApplied);
        Assert.Equal(0, monitor.OverloadedFrames);
        Assert.Equal(0, monitor.AverageDecodeMs);
        Assert.False(monitor.Observe(7.6, 60));
    }
}

public class NativePipelineTests
{
    private static readonly EncoderInfo Encoder =
        new("h264_amf", "AMD AMF H.264", "AMD", VideoCodecKind.H264, true);

    private static PipelineOptions FourKOptions(string? filter, int previewWidth, int encodeWidth = 3840, int encodeHeight = 2160) => new()
    {
        Source = SourceSpec.TestPattern(3840, 2160, 60),
        PreviewEnabled = true,
        PreviewWidth = previewWidth,
        RecordPath = "C:\\temp\\stage2.mp4",
        EncodeWidth = encodeWidth,
        EncodeHeight = encodeHeight,
        EncodeFps = 60,
        Encoder = Encoder,
        EnhancementFilter = filter ?? string.Empty
    };

    [Fact]
    public void FourK_test_pattern_builds_native_mjpeg_and_a_full_resolution_recording()
    {
        const string filter = "libplacebo=w=3840:h=2160:upscaler=lanczos:contrast=1.1,cas=strength=0.35";
        var command = string.Join(" ", PipelineArgs.Build(FourKOptions(filter, 3840)));

        Assert.Contains("testsrc2=size=3840x2160:rate=60", command);
        Assert.Contains("-c:v mjpeg", command);
        Assert.DoesNotContain("scale=1600:900", command);
        Assert.Contains(filter, command);
        Assert.Contains("-r 60", command);
    }

    [Fact]
    public void FourK_to_1440_output_keeps_the_4k_source_and_preview_input()
    {
        var options = FourKOptions(
            PipelineArgs.BuildRecordingEnhancementFilter(
                new EnhancementSettings { Enabled = true, RecordingEnhanced = true },
                2560,
                1440),
            3840,
            2560,
            1440);
        var command = string.Join(" ", PipelineArgs.Build(options));

        Assert.Contains("testsrc2=size=3840x2160:rate=60", command);
        Assert.Contains("libplacebo=w=2560:h=1440", command);
        Assert.DoesNotContain("scale=2560:1440", command);
    }

    [Fact]
    public void Native_preview_size_is_3840_by_2160_for_a_4k_source()
    {
        var size = PipelineArgs.EstimatePreviewSize(FourKOptions(null, 3840));
        Assert.Equal((3840, 2160), size);
    }

    [Fact]
    public void Preview_fallback_to_1600_does_not_change_the_recording_output()
    {
        const string filter = "libplacebo=w=3840:h=2160";
        var command = string.Join(" ", PipelineArgs.Build(FourKOptions(filter, 1600)));

        Assert.Contains("scale=1600:900:flags=fast_bilinear", command);
        Assert.Contains("libplacebo=w=3840:h=2160", command);
        Assert.DoesNotContain("scale=3840:2160", command);
    }
}
