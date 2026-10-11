using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Fuseo.App.Views;

/// <summary>true → Collapsed / false → Visible（BooleanToVisibilityConverter の逆）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
