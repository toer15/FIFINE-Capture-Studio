using System.Text.Json.Serialization;

namespace FifineCaptureStudio.Core.Monitoring;

public enum FpsMeasurementSource
{
    Unavailable,
    PresentMon,
    VisualEstimate
}

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>Persisted FPS-monitor preferences. The selected PID is only a convenience; the process
/// name is checked before it is reused so a recycled PID can never point at another program.</summary>
public sealed class FpsMonitorSettings
{
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    public bool OverlayVisible { get; set; } = true;
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopLeft;
    public double OverlayScale { get; set; } = 1.0;
    public int GameProcessId { get; set; }
    public string GameProcessName { get; set; } = string.Empty;

    public void Normalize()
    {
        if (!Enum.IsDefined(OverlayCorner)) OverlayCorner = OverlayCorner.TopLeft;
        OverlayScale = Math.Clamp(OverlayScale, 0.75, 1.5);
        if (GameProcessId < 0) GameProcessId = 0;
        GameProcessName ??= string.Empty;
    }
}

public sealed record GameProcessInfo(int Id, string Name, string Title)
{
    public string Display => string.IsNullOrWhiteSpace(Title) ? $"{Name} ({Id})" : $"{Title} — {Name} ({Id})";
}

/// <summary>One measurement with explicit provenance. Estimated values are never presented as exact.</summary>
public sealed record FpsReading(
    bool Available,
    FpsMeasurementSource Source,
    string SourceLabel,
    double? Fps,
    double Confidence,
    bool IsEstimate,
    bool IsStreamLimited,
    bool IsStatic,
    string Detail,
    double FrameTimeMs,
    double PresentedFps,
    double DisplayedFps)
{
    public static FpsReading Unavailable(string detail) =>
        new(false, FpsMeasurementSource.Unavailable, "Unavailable", null, 0, false, false, false,
            detail, 0, 0, 0);
}

/// <summary>Small immutable payload handed to the native presenter for its on-frame overlay.</summary>
public sealed record FpsOverlaySnapshot(
    bool Visible,
    OverlayCorner Corner,
    double Scale,
    string Game,
    string FrameGenTotal,
    string Generated,
    string Presented,
    string Input,
    string Qualifier,
    string Multiplier,
    bool FrameGenVisible);

/// <summary>Latest visual-cadence estimate. Confidence is always supplied with the number.</summary>
public sealed record CadenceReading(
    bool Available,
    double Fps,
    double Confidence,
    bool IsStreamLimited,
    bool IsStatic,
    string Detail,
    int Samples);
