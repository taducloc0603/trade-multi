using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TradeDesktop.Application.Models;

namespace TradeDesktop.App.Helpers;

public sealed class SignalLogColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MinimalSignalLogItem item
            ? SignalLifecycleBrushes.Resolve(item.Outcome)
            : SignalLifecycleBrushes.Detected;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

}
