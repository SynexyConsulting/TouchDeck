using System.Globalization;
using System.Windows.Data;

namespace TouchDeck.App;

/// <summary>true -> false and back (e.g. the Esc radio button shows !JigF15).</summary>
public sealed class NotBool : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
