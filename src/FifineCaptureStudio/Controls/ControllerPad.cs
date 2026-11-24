using System.Windows;
using System.Windows.Media;

namespace FifineCaptureStudio.Controls;

/// <summary>
/// Vector gamepad that mirrors real controller state: analog sticks move, triggers
/// fill and face buttons light up as they are pressed. State comes from the
/// controller monitor, never from simulated input.
/// </summary>
public sealed class ControllerPad : FrameworkElement
{
    public static readonly DependencyProperty LeftXProperty = DP("LeftX", 0.0);
    public static readonly DependencyProperty LeftYProperty = DP("LeftY", 0.0);
    public static readonly DependencyProperty RightXProperty = DP("RightX", 0.0);
    public static readonly DependencyProperty RightYProperty = DP("RightY", 0.0);
    public static readonly DependencyProperty LeftTriggerProperty = DP("LeftTrigger", 0.0);
    public static readonly DependencyProperty RightTriggerProperty = DP("RightTrigger", 0.0);
    public static readonly DependencyProperty PressedAProperty = BP("PressedA");
    public static readonly DependencyProperty PressedBProperty = BP("PressedB");
    public static readonly DependencyProperty PressedXProperty = BP("PressedX");
    public static readonly DependencyProperty PressedYProperty = BP("PressedY");
    public static readonly DependencyProperty ConnectedProperty = BP("Connected");
    public static readonly DependencyProperty LeftBumperProperty = BP("LeftBumper");
    public static readonly DependencyProperty RightBumperProperty = BP("RightBumper");

    private static DependencyProperty DP(string name, double def) => DependencyProperty.Register(
        name, typeof(double), typeof(ControllerPad),
        new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty BP(string name) => DependencyProperty.Register(
        name, typeof(bool), typeof(ControllerPad),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double LeftX { get => (double)GetValue(LeftXProperty); set => SetValue(LeftXProperty, value); }
    public double LeftY { get => (double)GetValue(LeftYProperty); set => SetValue(LeftYProperty, value); }
    public double RightX { get => (double)GetValue(RightXProperty); set => SetValue(RightXProperty, value); }
    public double RightY { get => (double)GetValue(RightYProperty); set => SetValue(RightYProperty, value); }
    public double LeftTrigger { get => (double)GetValue(LeftTriggerProperty); set => SetValue(LeftTriggerProperty, value); }
    public double RightTrigger { get => (double)GetValue(RightTriggerProperty); set => SetValue(RightTriggerProperty, value); }
    public bool PressedA { get => (bool)GetValue(PressedAProperty); set => SetValue(PressedAProperty, value); }
    public bool PressedB { get => (bool)GetValue(PressedBProperty); set => SetValue(PressedBProperty, value); }
    public bool PressedX { get => (bool)GetValue(PressedXProperty); set => SetValue(PressedXProperty, value); }
    public bool PressedY { get => (bool)GetValue(PressedYProperty); set => SetValue(PressedYProperty, value); }
    public bool Connected { get => (bool)GetValue(ConnectedProperty); set => SetValue(ConnectedProperty, value); }
    public bool LeftBumper { get => (bool)GetValue(LeftBumperProperty); set => SetValue(LeftBumperProperty, value); }
    public bool RightBumper { get => (bool)GetValue(RightBumperProperty); set => SetValue(RightBumperProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 60 || h < 40)
        {
            return;
        }

        var dim = new SolidColorBrush(Color.FromRgb(0x2C, 0x38, 0x50));
        var body = new SolidColorBrush(Color.FromRgb(0x16, 0x1E, 0x2E));
        var plate = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x37));
        var accent = Connected
            ? new SolidColorBrush(Color.FromRgb(0x82, 0x75, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0x35, 0x3F, 0x55));
        var cyan = new SolidColorBrush(Color.FromRgb(0x35, 0xC9, 0xFF));

        var bodyWidth = w * 0.86;
        var bodyHeight = h * 0.46;
        var left = (w - bodyWidth) / 2;
        var top = h * 0.34;

        // Controller body: rounded "wings" shape.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left + bodyWidth * 0.18, top), true, true);
            ctx.LineTo(new Point(left + bodyWidth * 0.82, top), true, true);
            ctx.BezierTo(
                new Point(left + bodyWidth * 1.0, top),
                new Point(left + bodyWidth * 1.0, top + bodyHeight * 0.55),
                new Point(left + bodyWidth * 0.93, top + bodyHeight * 0.95), true, true);
            ctx.BezierTo(
                new Point(left + bodyWidth * 0.88, top + bodyHeight * 1.12),
                new Point(left + bodyWidth * 0.76, top + bodyHeight * 1.06),
                new Point(left + bodyWidth * 0.72, top + bodyHeight * 0.92), true, true);
            ctx.LineTo(new Point(left + bodyWidth * 0.28, top + bodyHeight * 0.92), true, true);
            ctx.BezierTo(
                new Point(left + bodyWidth * 0.24, top + bodyHeight * 1.06),
                new Point(left + bodyWidth * 0.12, top + bodyHeight * 1.12),
                new Point(left + bodyWidth * 0.07, top + bodyHeight * 0.95), true, true);
            ctx.BezierTo(
                new Point(left, top + bodyHeight * 0.55),
                new Point(left, top),
                new Point(left + bodyWidth * 0.18, top), true, true);
        }

        geometry.Freeze();

        var fill = new LinearGradientBrush(Color.FromRgb(0x1A, 0x22, 0x33), Color.FromRgb(0x0F, 0x15, 0x22), 90);
        fill.Freeze();
        var pen = new Pen(accent, 1.6);
        dc.DrawGeometry(fill, pen, geometry);

        // Touchpad / centre strip
        var pad = new Rect(left + bodyWidth * 0.40, top + bodyHeight * 0.12, bodyWidth * 0.20, bodyHeight * 0.34);
        dc.DrawRoundedRectangle(plate, null, pad, 5, 5);

        // Sticks
        var stickRadius = Math.Min(bodyWidth, bodyHeight) * 0.115;
        DrawStick(dc, new Point(left + bodyWidth * 0.22, top + bodyHeight * 0.38), stickRadius, LeftX, LeftY, dim, accent);
        DrawStick(dc, new Point(left + bodyWidth * 0.62, top + bodyHeight * 0.66), stickRadius, RightX, RightY, dim, accent);

        // D-pad
        var dpadCenter = new Point(left + bodyWidth * 0.49, top + bodyHeight * 0.68);
        var dpadSize = stickRadius * 0.75;
        dc.DrawRoundedRectangle(dim, null, new Rect(dpadCenter.X - dpadSize, dpadCenter.Y - dpadSize * 0.32, dpadSize * 2, dpadSize * 0.64), 1.5, 1.5);
        dc.DrawRoundedRectangle(dim, null, new Rect(dpadCenter.X - dpadSize * 0.32, dpadCenter.Y - dpadSize, dpadSize * 0.64, dpadSize * 2), 1.5, 1.5);

        // Face buttons
        var faceCenter = new Point(left + bodyWidth * 0.79, top + bodyHeight * 0.30);
        var buttonRadius = stickRadius * 0.46;
        var spread = buttonRadius * 1.9;
        DrawFace(dc, faceCenter.X, faceCenter.Y - spread, buttonRadius, Color.FromRgb(0x45, 0xD6, 0x9A), PressedY);
        DrawFace(dc, faceCenter.X + spread, faceCenter.Y, buttonRadius, Color.FromRgb(0xFF, 0x6B, 0x81), PressedB);
        DrawFace(dc, faceCenter.X, faceCenter.Y + spread, buttonRadius, Color.FromRgb(0x35, 0xC9, 0xFF), PressedA);
        DrawFace(dc, faceCenter.X - spread, faceCenter.Y, buttonRadius, Color.FromRgb(0xA7, 0x9C, 0xFF), PressedX);

        // Bumpers
        var bumperBrush = LeftBumper ? accent : plate;
        dc.DrawRoundedRectangle(bumperBrush, null, new Rect(left + bodyWidth * 0.13, top - bodyHeight * 0.10, bodyWidth * 0.22, bodyHeight * 0.13), 4, 4);
        bumperBrush = RightBumper ? accent : plate;
        dc.DrawRoundedRectangle(bumperBrush, null, new Rect(left + bodyWidth * 0.65, top - bodyHeight * 0.10, bodyWidth * 0.22, bodyHeight * 0.13), 4, 4);

        // Triggers
        DrawTrigger(dc, new Rect(left + bodyWidth * 0.15, top - bodyHeight * 0.24, bodyWidth * 0.18, bodyHeight * 0.10), LeftTrigger, cyan);
        DrawTrigger(dc, new Rect(left + bodyWidth * 0.67, top - bodyHeight * 0.24, bodyWidth * 0.18, bodyHeight * 0.10), RightTrigger, cyan);

        // Status LED strip
        if (Connected)
        {
            var glow = new Pen(new SolidColorBrush(Color.FromArgb(90, 0x82, 0x75, 0xFF)), 6);
            dc.DrawLine(glow, new Point(left + bodyWidth * 0.42, top + bodyHeight * 0.55),
                new Point(left + bodyWidth * 0.58, top + bodyHeight * 0.55));
            dc.DrawLine(new Pen(cyan, 2), new Point(left + bodyWidth * 0.44, top + bodyHeight * 0.55),
                new Point(left + bodyWidth * 0.56, top + bodyHeight * 0.55));
        }
    }

    private static void DrawStick(DrawingContext dc, Point center, double radius, double x, double y, Brush rim, Brush accent)
    {
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x21)), new Pen(rim, 1.4), center, radius, radius);
        var inner = new Point(center.X + Math.Clamp(x, -1, 1) * radius * 0.55, center.Y - Math.Clamp(y, -1, 1) * radius * 0.55);
        var dot = new SolidColorBrush(Color.FromRgb(0x2B, 0x36, 0x4C));
        dc.DrawEllipse(dot, null, inner, radius * 0.55, radius * 0.55);
        dc.DrawEllipse(accent, null, inner, radius * 0.18, radius * 0.18);
    }

    private static void DrawFace(DrawingContext dc, double cx, double cy, double radius, Color color, bool pressed)
    {
        var brush = new SolidColorBrush(pressed ? color : Color.FromRgb(0x22, 0x2C, 0x3E));
        var glow = new SolidColorBrush(Color.FromArgb(pressed ? (byte)140 : (byte)0, color.R, color.G, color.B));
        if (pressed)
        {
            dc.DrawEllipse(glow, null, new Point(cx, cy), radius * 1.7, radius * 1.7);
        }

        dc.DrawEllipse(brush, null, new Point(cx, cy), radius, radius);
        if (!pressed)
        {
            var outline = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));
            dc.DrawEllipse(null, new Pen(outline, 1.2), new Point(cx, cy), radius, radius);
        }
    }

    private static void DrawTrigger(DrawingContext dc, Rect rect, double value, Brush colour)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x20, 0x2F)), null, rect, 3, 3);
        var width = rect.Width * Math.Clamp(value, 0, 1);
        if (width > 1)
        {
            dc.DrawRoundedRectangle(colour, null, new Rect(rect.X, rect.Y, width, rect.Height), 3, 3);
        }
    }
}
