namespace TradeDesktop.App.Services;

public interface ITelegramNotifier
{
    Task NotifyAsync(
        string eventCode,
        string severity,
        string detail,
        string? pairId = null,
        IReadOnlyDictionary<string, string?>? meta = null,
        CancellationToken cancellationToken = default);
}
