using System.Text.Json.Serialization;

namespace FifineCaptureStudio.Core.FrameGeneration;

public enum FrameGenerationQuality
{
    Performance,
    Balanced,
    Quality
}

public enum FrameGenerationLatency
{
    Competitive,
    Balanced,
    Smooth
}

/// <summary>
/// Persisted frame-generation configuration. Only genuine 2X is exposed; the multiplier is clamped
/// to 2 on load so an edited settings file cannot make the UI claim an unimplemented mode.
/// </summary>
public sealed class FrameGenerationSettings
{
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    public bool Enabled { get; set; }
    public int Multiplier { get; set; } = 2;
    public int TargetOutputFps { get; set; }
    public FrameGenerationQuality Quality { get; set; } = FrameGenerationQuality.Balanced;
    public FrameGenerationLatency Latency { get; set; } = FrameGenerationLatency.Balanced;

    public void Normalize()
    {
        Multiplier = 2;
        if (!Enum.IsDefined(Quality)) Quality = FrameGenerationQuality.Balanced;
        if (!Enum.IsDefined(Latency)) Latency = FrameGenerationLatency.Balanced;
        // Only 2X is implemented, so only its 120 fps output is an honest target. Higher targets
        // stay out of the settings until a multiplier actually exists for them.
        if (TargetOutputFps != 120) TargetOutputFps = 0;
    }

    /// <summary>Internal inference size. All are multiples of the model's /64 requirement.</summary>
    [JsonIgnore]
    public (int Width, int Height) InternalSize => Quality switch
    {
        FrameGenerationQuality.Performance => (768, 448),
        FrameGenerationQuality.Quality => (1024, 576),
        _ => (896, 512)
    };

    [JsonIgnore]
    public string QualityLabel => Quality switch
    {
        FrameGenerationQuality.Performance => "Performance · 768 × 448 internal",
        FrameGenerationQuality.Quality => "Quality · 1024 × 576 internal",
        _ => "Balanced · 896 × 512 internal"
    };
}

/// <summary>One frame on the presenter timeline. DueTicks uses Stopwatch timestamps.</summary>
public sealed class ScheduledFrame
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long DueTicks { get; init; }
    public required bool Generated { get; init; }
    public required long SourceSequence { get; init; }

    public double DueMs(long nowTicks) => (DueTicks - nowTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}

/// <summary>
/// Timeline maths for one source pair, kept pure so the ordering and latency behaviour can be tested
/// without a GPU. The first original is held until its partner arrives, which is the unavoidable cost
/// of temporal interpolation: one source interval of look-ahead.
/// </summary>
public static class FrameGenerationSchedule
{
    public static (long Original, long Generated, long Next, long LookaheadTicks) BuildPair(
        long nowTicks,
        long sourceIntervalTicks,
        bool emitHeldOriginal)
    {
        var half = sourceIntervalTicks / 2;
        var lookahead = sourceIntervalTicks;
        var original = nowTicks;
        var generated = nowTicks + half;
        var next = nowTicks + sourceIntervalTicks;
        return (original, generated, next, lookahead);
    }

    /// <summary>
    /// A late generated frame is only worth showing if it still lands before the next real frame.
    /// Competitive drops anything more than a quarter interval late; Smooth tolerates half.
    /// </summary>
    public static bool ShouldDrop(double lateMs, double sourceIntervalMs, FrameGenerationLatency latency)
    {
        var tolerance = latency switch
        {
            FrameGenerationLatency.Competitive => sourceIntervalMs * 0.25,
            FrameGenerationLatency.Smooth => sourceIntervalMs * 0.75,
            _ => sourceIntervalMs * 0.5
        };
        return lateMs > tolerance;
    }
}

/// <summary>Fast bilinear BGRA downscale used before inference and for bypass frames.</summary>
public static class BgraScaler
{
    public static void Scale(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, Span<byte> destination, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height)
        {
            source[..(width * height * 4)].CopyTo(destination);
            return;
        }

        var xStep = sourceWidth / (double)width;
        var yStep = sourceHeight / (double)height;
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(sourceHeight - 1, (int)(y * yStep));
            var row = sy * sourceWidth * 4;
            var outRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(sourceWidth - 1, (int)(x * xStep));
                var input = row + sx * 4;
                var output = outRow + x * 4;
                destination[output] = source[input];
                destination[output + 1] = source[input + 1];
                destination[output + 2] = source[input + 2];
                destination[output + 3] = 255;
            }
        }
    }

    /// <summary>Mean absolute difference between two BGRA frames, used for scene-cut detection.</summary>
    public static double MeanDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        var pixels = Math.Min(a.Length, b.Length) / 4;
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * 4;
            sum += Math.Abs(a[offset] - b[offset])
                   + Math.Abs(a[offset + 1] - b[offset + 1])
                   + Math.Abs(a[offset + 2] - b[offset + 2]);
        }

        return sum / (double)Math.Max(1, pixels * 3);
    }
}
