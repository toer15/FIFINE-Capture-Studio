using System.Globalization;
using System.Windows.Data;

namespace FifineCaptureStudio.Converters;

/// <summary>Turns (percent, track width) into a filled-bar width for animated progress bars.</summary>
public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return 0d;
        }

        double percent = 0;
        if (values[0] is double d)
        {
            percent = d;
        }
        else if (values[0] is not null && double.TryParse(values[0].ToString(), out var parsed))
        {
            percent = parsed;
        }

        var width = values[1] is double w && !double.IsNaN(w) ? w : 0;
        return Math.Clamp(width * percent / 100.0, 0, width);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
