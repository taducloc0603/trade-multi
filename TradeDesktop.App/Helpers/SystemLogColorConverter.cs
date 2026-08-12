using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TradeDesktop.Application.Models;

namespace TradeDesktop.App.Helpers;

public sealed class SystemLogColorConverter : IValueConverter
{
    private static readonly Brush DebugBrush = CreateBrush("#667085");
    private static readonly Brush InfoBrush = CreateBrush("#344054");
    private static readonly Brush WarnBrush = CreateBrush("#B54708");
    private static readonly Brush ErrorBrush = CreateBrush("#B42318");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SystemLogItem item
            ? item.Severity switch
            {
                SystemLogSeverity.Debug => DebugBrush,
                SystemLogSeverity.Warn => WarnBrush,
                SystemLogSeverity.Error => ErrorBrush,
                _ => InfoBrush
            }
            : InfoBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush CreateBrush(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
