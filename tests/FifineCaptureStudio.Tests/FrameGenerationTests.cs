using System.IO;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.FrameGeneration;
using Xunit;

namespace FifineCaptureStudio.Tests;

/// <summary>
/// Pure tests for the frame-generation timeline and settings. GPU execution is covered by
/// --fg-bench and the native presenter self-test, which need a real display and model.
/// </summary>
public class FrameGenerationTests
{
    private const long Frequency = 10_000_000;
    private static readonly long Interval = Frequency / 60;

    [Fact]
    public void Pair_schedule_keeps_original_generated_original_order()
    {
        var (original, generated, next, lookahead) =
            FrameGenerationSchedule.BuildPair(1_000_000, Interval, emitHeldOriginal: true);

        Assert.Equal(1_000_000, original);
        Assert.Equal(Interval / 2, generated - original);
        Assert.Equal(Interval, next - original);
        Assert.Equal(Interval, lookahead);
        Assert.True(original < generated);
        Assert.True(generated < next);
    }

    [Fact]
    public void Pair_schedule_holds_the_first_original_until_its_partner_arrives()
    {
        var (original, generated, _, _) =
            FrameGenerationSchedule.BuildPair(500, Interval, emitHeldOriginal: false);

        // The held original is presented immediately; the generated midpoint is exactly one half
        // interval later, never at the same timestamp as either real frame.
        Assert.Equal(500, original);
        Assert.Equal(500 + Interval / 2, generated);
    }

    [Theory]
    [InlineData(5.0, 16.667, FrameGenerationLatency.Competitive, true)]
    [InlineData(5.0, 16.667, FrameGenerationLatency.Balanced, false)]
    [InlineData(9.0, 16.667, FrameGenerationLatency.Balanced, true)]
    [InlineData(9.0, 16.667, FrameGenerationLatency.Smooth, false)]
    [InlineData(13.0, 16.667, FrameGenerationLatency.Smooth, true)]
    public void Late_generated_frames_are_dropped_according_to_latency_preference(
        double lateMs, double intervalMs, FrameGenerationLatency latency, bool expected)
    {
        Assert.Equal(expected, FrameGenerationSchedule.ShouldDrop(lateMs, intervalMs, latency));
    }

    [Fact]
    public void Quality_presets_map_to_64_aligned_internal_sizes()
    {
        var settings = new FrameGenerationSettings();

        foreach (var quality in Enum.GetValues<FrameGenerationQuality>())
        {
            settings.Quality = quality;
            var (width, height) = settings.InternalSize;
            Assert.Equal(0, width % 64);
            Assert.Equal(0, height % 64);
            Assert.True(width > 0 && height > 0);
        }

        settings.Quality = FrameGenerationQuality.Performance;
        Assert.Equal((768, 448), settings.InternalSize);
        settings.Quality = FrameGenerationQuality.Quality;
        Assert.Equal((1024, 576), settings.InternalSize);
    }

    [Fact]
    public void Only_implemented_multiplier_and_targets_survive_normalisation()
    {
        var settings = new FrameGenerationSettings
        {
            Multiplier = 4,
            TargetOutputFps = 240,
            Quality = (FrameGenerationQuality)99,
            Latency = (FrameGenerationLatency)99
        };

        settings.Normalize();

        Assert.Equal(2, settings.Multiplier);
        Assert.Equal(0, settings.TargetOutputFps);
        Assert.Equal(FrameGenerationQuality.Balanced, settings.Quality);
        Assert.Equal(FrameGenerationLatency.Balanced, settings.Latency);

        settings.TargetOutputFps = 120;
        settings.Normalize();
        Assert.Equal(120, settings.TargetOutputFps);
    }

    [Fact]
    public void Scaler_produces_requested_size_and_opaque_pixels()
    {
        const int sw = 8, sh = 8, dw = 4, dh = 4;
        var source = new byte[sw * sh * 4];
        for (var i = 0; i < sw * sh; i++)
        {
            source[i * 4] = (byte)(i % 256);
            source[i * 4 + 1] = 10;
            source[i * 4 + 2] = 20;
            source[i * 4 + 3] = 128;
        }

        var destination = new byte[dw * dh * 4];
        BgraScaler.Scale(source, sw, sh, destination, dw, dh);

        Assert.Equal(dw * dh * 4, destination.Length);
        for (var i = 0; i < dw * dh; i++)
        {
            Assert.Equal(10, destination[i * 4 + 1]);
            Assert.Equal(20, destination[i * 4 + 2]);
            Assert.Equal(255, destination[i * 4 + 3]);
        }
    }

    [Fact]
    public void Scene_cut_detector_separates_identical_frames_from_a_real_cut()
    {
        var black = new byte[16 * 16 * 4];
        var white = new byte[16 * 16 * 4];
        for (var i = 0; i < white.Length; i++)
        {
            white[i] = 255;
        }

        Assert.Equal(0, BgraScaler.MeanDifference(black, black));
        Assert.True(BgraScaler.MeanDifference(black, white) > 32);
    }

    [Fact]
    public void Service_counts_source_generated_and_skipped_separately()
    {
        var root = Path.Combine(Path.GetTempPath(), "fifine-fg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            using var service = new FrameGenerationService(settings);
            service.NoteSourceFrame();
            service.NoteSourceFrame();
            service.NoteSkipped();
            for (var i = 0; i < 8; i++)
            {
                service.RecordInference(11.5, 14.0);
            }

            Assert.Equal(2, service.SourceFrames);
            Assert.Equal(8, service.GeneratedFrames);
            Assert.Equal(1, service.SkippedInterpolations);
            Assert.Equal(11.5, service.AverageInferenceMs, 3);
            Assert.Equal(11.5, service.P50InferenceMs, 3);
            Assert.Equal(11.5, service.P95InferenceMs, 3);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
