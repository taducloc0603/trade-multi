namespace TradeDesktop.Application.Services;

/// <summary>
/// Đầu vào 1 kênh mỗi poll — lấy từ trades-header (do EA ghi mỗi OnTimer/OnTick/OnTrade).
/// </summary>
/// <param name="MapAvailable">Map MMF có mở được không (false = terminal đóng).</param>
/// <param name="ParseSuccess">Đọc/parse header thành công không.</param>
/// <param name="Timestamp">Heartbeat = GetTickCount(64) EA ghi ở header (offset 4). Đứng yên ⇒ EA/terminal treo.</param>
/// <param name="Connected">TERMINAL_CONNECTED (1=ổn, 0=rớt server, -1=unknown khi không parse được).</param>
public readonly record struct ChannelConnectionInput(
    bool MapAvailable,
    bool ParseSuccess,
    ulong Timestamp,
    int Connected);

/// <summary>Kết quả đánh giá trạng thái kết nối tổng hợp 2 kênh.</summary>
public sealed record ConnectionStatus(bool IsDown, IReadOnlyList<string> Reasons);

/// <summary>
/// Đánh giá trạng thái kết nối 2 sàn từ trades-header. 1 kênh coi là "down" khi:
///  - map không mở/parse được (terminal đóng → MapNotFound), HOẶC
///  - Connected == 0 (rớt server), HOẶC
///  - heartbeat (Timestamp) đứng yên qua >= <c>frozenPollThreshold</c> poll liên tiếp
///    (EA/terminal treo — TERMINAL_CONNECTED không thể tự báo trường hợp này).
/// Tổng thể down nếu BẤT KỲ kênh nào down (1 pair cần cả 2 chân).
/// Stateful: nhớ timestamp + số poll đứng yên mỗi kênh giữa các lần gọi.
/// Dùng "timestamp không đổi" thay vì tuổi tuyệt đối để né wrap 32-bit GetTickCount của MT4.
/// </summary>
public sealed class ConnectionStatusEvaluator
{
    private readonly int _frozenPollThreshold;
    private ChannelState _stateA;
    private ChannelState _stateB;

    public ConnectionStatusEvaluator(int frozenPollThreshold = 6)
    {
        _frozenPollThreshold = Math.Max(1, frozenPollThreshold);
        Reset();
    }

    public void Reset()
    {
        _stateA = ChannelState.Initial;
        _stateB = ChannelState.Initial;
    }

    public ConnectionStatus Evaluate(ChannelConnectionInput a, ChannelConnectionInput b)
    {
        var (aDown, aReason) = EvaluateChannel("A", ref _stateA, a);
        var (bDown, bReason) = EvaluateChannel("B", ref _stateB, b);

        var reasons = new List<string>(2);
        if (aDown && aReason is not null) reasons.Add(aReason);
        if (bDown && bReason is not null) reasons.Add(bReason);

        return new ConnectionStatus(aDown || bDown, reasons);
    }

    private (bool Down, string? Reason) EvaluateChannel(string label, ref ChannelState state, ChannelConnectionInput input)
    {
        if (!input.MapAvailable || !input.ParseSuccess)
        {
            state = ChannelState.Initial; // mất map → reset heartbeat tracking
            return (true, $"Sàn {label}: mất map / không đọc được trades (terminal đóng?)");
        }

        // Heartbeat frozen tracking (dựa trên timestamp có đổi hay không).
        if (state.HasTimestamp && state.LastTimestamp == input.Timestamp)
        {
            state = state.WithIncrementedUnchanged();
        }
        else
        {
            state = ChannelState.WithTimestamp(input.Timestamp);
        }

        if (input.Connected == 0)
        {
            return (true, $"Sàn {label}: TERMINAL_CONNECTED=0 (rớt server / mất mạng)");
        }

        if (state.UnchangedPolls >= _frozenPollThreshold)
        {
            return (true, $"Sàn {label}: heartbeat đứng {state.UnchangedPolls} poll (EA/terminal treo?)");
        }

        return (false, null);
    }

    private readonly record struct ChannelState(bool HasTimestamp, ulong LastTimestamp, int UnchangedPolls)
    {
        public static ChannelState Initial => new(false, 0, 0);
        public static ChannelState WithTimestamp(ulong ts) => new(true, ts, 0);
        public ChannelState WithIncrementedUnchanged() => this with { UnchangedPolls = UnchangedPolls + 1 };
    }
}
