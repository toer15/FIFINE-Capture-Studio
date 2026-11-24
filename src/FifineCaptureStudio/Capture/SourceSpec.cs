using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.Capture;

public sealed record SourceSpec(
    CaptureSourceKind Kind,
    string Label,
    string DeviceName,
    string WindowTitle,
    int DisplayX,
    int DisplayY,
    int Width,
    int Height,
    int Fps,
    string InputFormat)
{
    public static SourceSpec TestPattern(int width, int height, int fps) =>
        new(CaptureSourceKind.TestPattern, "Test pattern (verification source)", string.Empty, string.Empty,
            0, 0, width, height, fps, "Auto");
}

public static class SourceResolver
{
    public static SourceSpec FromSettings(AppSettings settings, IReadOnlyList<DisplayInfo> displays)
    {
        var capture = settings.Capture;
        var width = capture.ResolutionWidth;
        var height = capture.ResolutionHeight;
        var fps = capture.Fps;

        switch (capture.SourceKind)
        {
            case CaptureSourceKind.Display:
            {
                var display = displays.FirstOrDefault(d => d.Index == capture.DisplayIndex) ?? displays.FirstOrDefault();
                return new SourceSpec(
                    CaptureSourceKind.Display,
                    display is null ? "Desktop" : $"Display {display.Index + 1} · {display.Width}×{display.Height}",
                    string.Empty,
                    string.Empty,
                    display?.X ?? 0,
                    display?.Y ?? 0,
                    width,
                    height,
                    fps,
                    "Auto");
            }

            case CaptureSourceKind.Window:
                return new SourceSpec(
                    CaptureSourceKind.Window,
                    string.IsNullOrWhiteSpace(capture.WindowTitle) ? "Select a game window" : capture.WindowTitle,
                    string.Empty,
                    capture.WindowTitle,
                    0,
                    0,
                    width,
                    height,
                    fps,
                    "Auto");

            case CaptureSourceKind.TestPattern:
                return SourceSpec.TestPattern(width, height, fps);

            default:
                return new SourceSpec(
                    CaptureSourceKind.CaptureCard,
                    string.IsNullOrWhiteSpace(capture.VideoDeviceName) ? "No capture device" : capture.VideoDeviceName,
                    capture.VideoDeviceName,
                    string.Empty,
                    0,
                    0,
                    width,
                    height,
                    fps,
                    capture.InputPixelFormat);
        }
    }

    public static string Describe(CaptureSourceKind kind) => kind switch
    {
        CaptureSourceKind.CaptureCard => "HDMI capture card (DirectShow)",
        CaptureSourceKind.Display => "Desktop display (Windows GDI capture)",
        CaptureSourceKind.Window => "Game window (Windows GDI capture)",
        _ => "Generated test pattern"
    };
}
