using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Valve;

/// <summary>Indents a tree row by its level so a flat DataGrid can look hierarchical.</summary>
public sealed class IndentToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new Thickness(System.Convert.ToDouble(value) + 10, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Scales a 0..1 fraction into a pixel width for the inline rate bars.</summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double fraction = System.Convert.ToDouble(value);
        double max = parameter is string text && double.TryParse(text, out double parsed) ? parsed : 110;
        return Math.Clamp(fraction, 0, 1) * max;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RateIsActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d && d > 0.01;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
