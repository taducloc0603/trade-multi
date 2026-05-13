namespace TradeDesktop.App.ViewModels;

public sealed class OrderInfoTabViewModel
{
    public OrderInfoTabViewModel(
        OrderTabType tabType,
        string tabHeader,
        OrderPanelStatusViewModel leftPanel,
        OrderPanelStatusViewModel rightPanel)
    {
        TabType = tabType;
        TabHeader = tabHeader;
        LeftPanel = leftPanel;
        RightPanel = rightPanel;
    }

    public OrderTabType TabType { get; }
    public string TabHeader { get; }
    public OrderPanelStatusViewModel LeftPanel { get; }
    public OrderPanelStatusViewModel RightPanel { get; }
}