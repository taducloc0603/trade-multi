using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TradeDesktop.Application.Models;

namespace TradeDesktop.App.Helpers;

public sealed class SystemLogColorConverter : IValueConverter
{
    private static readonly Brush DefaultBrush = CreateBrush("#101828");
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SystemLogItem { IsSignal: true } item
            ? SignalLifecycleBrushes.Resolve(SignalLifecycleBrushes.ResolveSystemOutcome(item.Category))
            : DefaultBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush CreateBrush(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
