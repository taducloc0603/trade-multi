namespace TradeDesktop.Application.Services.Portfolio;

public enum LotMismatchChoice
{
    Continue,
    Stop
}

public enum FirstPairLotDecisionState
{
    Idle,
    AwaitingDecision,
    StopRequested,
    Continued
}

/// <summary>
/// Trạng thái quyết định khi cặp Auto đầu tiên của phiên Start lệch lot (B = ctrader/primexbt). Lệch ⇒ chờ người dùng
/// chọn Tiếp tục / Stop; trong lúc chờ (và sau khi chọn Stop) Auto Open bị chặn. Không ai chọn ⇒ chờ vô hạn, nhắc
/// định kỳ. Trả lời của popup thuộc phiên Start cũ (sau Stop/Start) bị bỏ qua theo <see cref="SessionId"/>.
/// CHỈ chặn Open — không tạo/đóng lệnh (Rule E). Ghi trên UI thread; <see cref="IsOpenBlocked"/> được đọc cả từ luồng
/// Auto Open nên trạng thái là volatile.
/// </summary>
public sealed class FirstPairLotDecisionGate
{
    private volatile FirstPairLotDecisionState _state = FirstPairLotDecisionState.Idle;

    public FirstPairLotDecisionState State
    {
        get => _state;
        private set => _state = value;
    }

    public int SessionId { get; private set; }

    public DateTime? RaisedAtUtc { get; private set; }

    public DateTime? LastReminderUtc { get; private set; }

    public bool IsOpenBlocked => State is FirstPairLotDecisionState.AwaitingDecision or FirstPairLotDecisionState.StopRequested;

    public bool IsAwaitingDecision => State == FirstPairLotDecisionState.AwaitingDecision;

    public bool IsStopRequested => State == FirstPairLotDecisionState.StopRequested;

    public void Raise(int sessionId, DateTime nowUtc)
    {
        State = FirstPairLotDecisionState.AwaitingDecision;
        SessionId = sessionId;
        RaisedAtUtc = nowUtc;
        LastReminderUtc = nowUtc;
    }

    /// <summary>true = nhận quyết định; false = bỏ qua (khác phiên, không còn chờ, hoặc đã quyết rồi).</summary>
    public bool Decide(int sessionId, LotMismatchChoice choice, DateTime nowUtc)
    {
        if (State != FirstPairLotDecisionState.AwaitingDecision || sessionId != SessionId)
        {
            return false;
        }

        State = choice == LotMismatchChoice.Continue
            ? FirstPairLotDecisionState.Continued
            : FirstPairLotDecisionState.StopRequested;
        return true;
    }

    /// <summary>true khi đang chờ quá <paramref name="interval"/> kể từ lần nhắc trước (cập nhật mốc nhắc).</summary>
    public bool ShouldRemind(DateTime nowUtc, TimeSpan interval)
    {
        if (State != FirstPairLotDecisionState.AwaitingDecision || LastReminderUtc is not { } last || nowUtc - last < interval)
        {
            return false;
        }

        LastReminderUtc = nowUtc;
        return true;
    }

    /// <summary>Stop xong / Start mới: về Idle. SessionId giữ nguyên để trả lời cũ vẫn bị coi là khác phiên.</summary>
    public void Reset()
    {
        State = FirstPairLotDecisionState.Idle;
        RaisedAtUtc = null;
        LastReminderUtc = null;
    }
}
