namespace FifineCaptureStudio.Capture;

/// <summary>
/// Decides when native-resolution MJPEG decoding can no longer meet the source frame budget.
/// The decision is deliberately based only on measured decode time: a sustained overload must
/// last <see cref="FramesBeforeFallback"/> consecutive decoded frames before the preview is
/// downgraded. This is separate from recording, which always keeps the full configured source.
/// </summary>
public sealed class PreviewFallbackMonitor
{
    public const int FramesBeforeFallback = 90;
    public const double BudgetFraction = 0.45;
    public const double MinimumThresholdMs = 4.0;

    public double AverageDecodeMs { get; private set; }
    public int OverloadedFrames { get; private set; }
    public bool FallbackApplied { get; private set; }

    /// <summary>Decode time that counts as overloaded for a frame rate.</summary>
    public static double ThresholdMs(int fps) =>
        Math.Max(MinimumThresholdMs, (1000.0 / Math.Max(24, fps)) * BudgetFraction);

    /// <summary>
    /// Records one decoded frame. Returns true exactly when the fallback has just been earned.
    /// A healthy frame clears the consecutive-overload run without resetting the smoothed average.
    /// </summary>
    public bool Observe(double decodeMs, int fps)
    {
        if (FallbackApplied || decodeMs <= 0)
        {
            return false;
        }

        AverageDecodeMs = AverageDecodeMs <= 0
            ? decodeMs
            : (AverageDecodeMs * 0.95) + (decodeMs * 0.05);

        if (AverageDecodeMs < ThresholdMs(fps))
        {
            OverloadedFrames = 0;
            return false;
        }

        OverloadedFrames++;
        if (OverloadedFrames < FramesBeforeFallback)
        {
            return false;
        }

        FallbackApplied = true;
        return true;
    }

    /// <summary>Clears the downgrade so the user can retry native input after recovering.</summary>
    public void RequestFullResolution()
    {
        AverageDecodeMs = 0;
        OverloadedFrames = 0;
        FallbackApplied = false;
    }
}
