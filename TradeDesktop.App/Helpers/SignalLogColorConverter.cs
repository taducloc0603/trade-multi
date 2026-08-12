using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TradeDesktop.Application.Models;

namespace TradeDesktop.App.Helpers;

public sealed class SignalLogColorConverter : IValueConverter
{
    private static readonly Brush DetectedBrush = CreateBrush("#175CD3");
    private static readonly Brush ConfirmedBrush = CreateBrush("#067647");
    private static readonly Brush BlockedBrush = CreateBrush("#B54708");
    private static readonly Brush FailedBrush = CreateBrush("#B42318");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SignalLogItem item
            ? item.Outcome switch
            {
                "Confirmed" => ConfirmedBrush,
                "Blocked" => BlockedBrush,
                "Failed" => FailedBrush,
                _ => DetectedBrush
            }
            : DetectedBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush CreateBrush(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
