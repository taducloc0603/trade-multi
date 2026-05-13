namespace TradeDesktop.App.ViewModels;

public sealed class ManualHwndColumnItemViewModel : ObservableObject
{
    private string _chartHwndA = string.Empty;
    private string _tradeHwndA = string.Empty;
    private string _chartHwndB = string.Empty;
    private string _tradeHwndB = string.Empty;
    private int _displayIndex = 1;

    public int DisplayIndex
    {
        get => _displayIndex;
        set => SetProperty(ref _displayIndex, value);
    }

    public string ChartHwndA
    {
        get => _chartHwndA;
        set => SetProperty(ref _chartHwndA, value);
    }

    public string TradeHwndA
    {
        get => _tradeHwndA;
        set => SetProperty(ref _tradeHwndA, value);
    }

    public string ChartHwndB
    {
        get => _chartHwndB;
        set => SetProperty(ref _chartHwndB, value);
    }

    public string TradeHwndB
    {
        get => _tradeHwndB;
        set => SetProperty(ref _tradeHwndB, value);
    }
}