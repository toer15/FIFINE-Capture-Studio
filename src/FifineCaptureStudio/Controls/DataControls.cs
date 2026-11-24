using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FifineCaptureStudio.Controls;

/// <summary>Lightweight sparkline that renders a history of samples with an area gradient.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(
        nameof(FillOpacity), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.20, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowBaselineProperty = DependencyProperty.Register(
        nameof(ShowBaseline), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double FillOpacity
    {
        get => (double)GetValue(FillOpacityProperty);
        set => SetValue(FillOpacityProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public bool ShowBaseline
    {
        get => (bool)GetValue(ShowBaselineProperty);
        set => SetValue(ShowBaselineProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        if (ShowBaseline)
        {
            var line = new Pen(new SolidColorBrush(Color.FromArgb(70, 120, 140, 180)), 1);
            line.Freeze();
            dc.DrawLine(line, new Point(0, height - 0.5), new Point(width, height - 0.5));
        }

        var samples = Values?.ToList() ?? new List<double>();
        if (samples.Count < 2)
        {
            return;
        }

        var max = MaxValue > 0 ? MaxValue : Math.Max(1, samples.Max());
        max = Math.Max(max, 0.001);

        var step = width / Math.Max(1, samples.Count - 1);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var firstY = height - Math.Clamp(samples[0] / max, 0, 1) * (height - 4) - 2;
            ctx.BeginFigure(new Point(0, firstY), false, false);
            for (var i = 1; i < samples.Count; i++)
            {
                var x = i * step;
                var y = height - Math.Clamp(samples[i] / max, 0, 1) * (height - 4) - 2;
                ctx.LineTo(new Point(x, y), true, false);
            }
        }

        geometry.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            var firstY = height - Math.Clamp(samples[0] / max, 0, 1) * (height - 4) - 2;
            ctx.BeginFigure(new Point(0, height), true, true);
            ctx.LineTo(new Point(0, firstY), false, false);
            for (var i = 1; i < samples.Count; i++)
            {
                var x = i * step;
                var y = height - Math.Clamp(samples[i] / max, 0, 1) * (height - 4) - 2;
                ctx.LineTo(new Point(x, y), false, false);
            }

            ctx.LineTo(new Point(width, height), false, false);
        }

        area.Freeze();

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.MediumPurple;
        var fill = new LinearGradientBrush(
            Color.FromArgb((byte)(FillOpacity * 255), color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B),
            new Point(0.5, 0), new Point(0.5, 1));
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        var pen = new Pen(Stroke, StrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>Segmented level meter matching the mixer styling of the studio.</summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(int), typeof(LevelMeter),
        new FrameworkPropertyMetadata(9, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var segments = Math.Max(1, Segments);
        var level = Math.Clamp(Level, 0, 1);
        var lit = (int)Math.Round(level * segments);

        var off = new SolidColorBrush(Color.FromRgb(0x22, 0x2C, 0x3E));
        off.Freeze();

        if (Orientation == Orientation.Vertical)
        {
            var gap = 2.0;
            var barWidth = Math.Max(1.5, ActualWidth);
            var barHeight = Math.Max(2.0, (ActualHeight - gap * (segments - 1)) / segments);
            for (var i = 0; i < segments; i++)
            {
                var y = ActualHeight - (i + 1) * barHeight - i * gap;
                Brush brush = off;
                if (i < lit)
                {
                    brush = i >= segments - 1
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x49, 0x68))
                        : i >= segments - 3
                            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x45))
                            : new SolidColorBrush(Color.FromRgb(0x45, 0xD6, 0x9A));
                    brush.Freeze();
                }

                dc.DrawRoundedRectangle(brush, null, new Rect(0, y, barWidth, barHeight), 1, 1);
            }
        }
        else
        {
            var gap = 2.0;
            var segmentWidth = Math.Max(2.0, (ActualWidth - gap * (segments - 1)) / segments);
            for (var i = 0; i < segments; i++)
            {
                var x = i * (segmentWidth + gap);
                Brush brush = off;
                if (i < lit)
                {
                    brush = i >= segments - 1
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x49, 0x68))
                        : i >= segments - 3
                            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x45))
                            : new SolidColorBrush(Color.FromRgb(0x45, 0xD6, 0x9A));
                    brush.Freeze();
                }

                dc.DrawRoundedRectangle(brush, null, new Rect(x, 0, segmentWidth, ActualHeight), 1, 1);
            }
        }
    }
}

/// <summary>Circular gauge with a coloured progress arc and centred value.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("CPU", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("0%", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(RingGauge),
        new FrameworkPropertyMetadata(Color.FromRgb(0x82, 0x75, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionInsideProperty = DependencyProperty.Register(
        nameof(CaptionInside), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool CaptionInside
    {
        get => (bool)GetValue(CaptionInsideProperty);
        set => SetValue(CaptionInsideProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 24)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 6;
        var thickness = Math.Max(3.0, size * 0.075);

        var track = new Pen(new SolidColorBrush(Color.FromArgb(255, 0x22, 0x2C, 0x3E)), thickness);
        track.Freeze();
        dc.DrawEllipse(null, track, center, radius, radius);

        var ratio = Math.Clamp(Value / Math.Max(0.001, MaxValue), 0, 1);
        if (ratio > 0)
        {
            var pen = new Pen(new SolidColorBrush(Accent), thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();

            var start = -90.0;
            var sweep = ratio * 359.9;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                var startPoint = PointOnCircle(center, radius, start);
                var endPoint = PointOnCircle(center, radius, start + sweep);
                ctx.BeginFigure(startPoint, false, false);
                ctx.ArcTo(endPoint, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var valueText = new FormattedText(ValueText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            Math.Max(11, size * 0.20), Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2 - (CaptionInside ? 2 : 0)));

        if (CaptionInside)
        {
            var captionFace = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
            var caption = new FormattedText(Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, captionFace,
                Math.Max(8, size * 0.085), new SolidColorBrush(Color.FromRgb(0x92, 0x9D, 0xB1)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y + valueText.Height / 2 - 2));
        }
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
