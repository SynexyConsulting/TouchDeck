using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TouchDeck.App;

/// <summary>A board's health as its status-dot colour (the Ok, Bad and Faint brushes).</summary>
public sealed class HealthBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.FindResource(value switch
        {
            Health.Ok => "Ok",
            Health.Bad => "Bad",
            _ => "Faint",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
