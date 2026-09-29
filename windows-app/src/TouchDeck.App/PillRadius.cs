using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TouchDeck.App;

/// <summary>Corner radius = half the element's height, so a pill's ends are true half circles at any size.</summary>
public sealed class PillRadius : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double h && h > 0 ? h / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
