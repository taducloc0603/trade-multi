namespace TradeDesktop.App.ViewModels;

public sealed class OrderInfoFieldViewModel : ObservableObject
{
    private string _label;
    private string _value;

    public OrderInfoFieldViewModel(string label, string value)
    {
        _label = label;
        _value = value;
    }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}