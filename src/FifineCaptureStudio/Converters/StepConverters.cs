using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FifineCaptureStudio.Converters;

/// <summary>Visible when the bound value equals the converter parameter.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var left = value?.ToString();
        var right = parameter?.ToString();
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Fills the wizard progress bar up to the current step.</summary>
public sealed class StepFillConverter : IValueConverter
{
    private static readonly Brush Active = new SolidColorBrush(Color.FromRgb(0x82, 0x75, 0xFF));
    private static readonly Brush Inactive = new SolidColorBrush(Color.FromRgb(0x25, 0x2F, 0x44));

    static StepFillConverter()
    {
        Active.Freeze();
        Inactive.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var step = value is int i ? i : 0;
        var target = int.TryParse(parameter?.ToString(), out var p) ? p : 0;
        return step >= target ? Active : Inactive;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows an element only when the bound role matches the parameter.</summary>
public sealed class RoleToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var role = value?.ToString();
        var wanted = parameter?.ToString();
        return string.Equals(role, wanted, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
