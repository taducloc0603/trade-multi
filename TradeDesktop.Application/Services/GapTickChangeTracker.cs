using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

/// <summary>
/// hold_confirm_mode=ticks: đánh dấu snapshot nào là tick MỚI của từng chân. Tick của một chân là
/// snapshot có (bid, ask) của chân đó khác snapshot trước; snapshot đầu tiên luôn là tick.
/// Reader poll 50 ms nên phần lớn snapshot là giá đứng yên: chúng vẫn là MẪU của Cycle nhưng
/// không phải tick.
///
/// Gọi đúng MỘT lần mỗi snapshot, ở một chỗ duy nhất (<c>PortfolioCoordinator.ProcessSnapshot</c>)
/// để engine Open và mọi engine Close của từng slot thấy cùng một bộ cờ. Không reset theo Reset của
/// coordinator vì đây chỉ là phép so giá. Không thread-safe: chạy trên luồng xử lý snapshot.
/// </summary>
public sealed class GapTickChangeTracker
{
    private bool _hasLastQuote;
    private (decimal? Bid, decimal? Ask) _lastQuoteA;
    private (decimal? Bid, decimal? Ask) _lastQuoteB;

    public GapSignalSnapshot Mark(GapSignalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var quoteA = (snapshot.ExchangeABid, snapshot.ExchangeAAsk);
        var quoteB = (snapshot.ExchangeBBid, snapshot.ExchangeBAsk);
        var isNewTickA = !_hasLastQuote || quoteA != _lastQuoteA;
        var isNewTickB = !_hasLastQuote || quoteB != _lastQuoteB;
        _hasLastQuote = true;
        _lastQuoteA = quoteA;
        _lastQuoteB = quoteB;

        return snapshot.IsNewTickA == isNewTickA && snapshot.IsNewTickB == isNewTickB
            ? snapshot
            : snapshot with { IsNewTickA = isNewTickA, IsNewTickB = isNewTickB };
    }
}
