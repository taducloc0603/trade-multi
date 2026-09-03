namespace TradeDesktop.Application.Models;

public enum SignalCycleKind
{
    OpenBuy = 0,
    OpenSell = 1,
    NormalCloseBuy = 2,
    NormalCloseSell = 3,
    SosCloseBuy = 4,
    SosCloseSell = 5,
    Tp = 6
}

/// <summary>Read-only diagnostics snapshot. It never participates in signal decisions.</summary>
public sealed record SignalCycleStatus(
    SignalCycleKind Kind,
    string DisplayName,
    int? SlotId,
    string CycleId,
    int CurrentCount,
    int RequiredCount,
    string Status,
    double? LastValue,
    string LastReason,
    DateTime? UpdatedAtUtc,
    string LastEvent = "",
    string LastEventCycleId = "",
    int LastEventCount = 0,
    double? LastEventValue = null,
    string LastEventReason = "",
    DateTime? LastEventAtUtc = null)
{
    // RequiredCount = 0 nghĩa là chu kỳ không có số mẫu đích (TP chốt theo thời gian);
    // khi đó chỉ hiển thị số mẫu đã thu, tiến độ thời gian nằm ở LastReason.
    public string ProgressText => RequiredCount > 0
        ? $"{CurrentCount}/{RequiredCount}"
        : CurrentCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ShortCycleId => CycleId.Length <= 8 ? CycleId : CycleId[..8];
    public string UpdatedTimeText => UpdatedAtUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "-";
    public string ValueText => LastValue?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
    public string DetailText =>
        $"cycle {ShortCycleId} · {UpdatedTimeText} · value {ValueText}" +
        (string.IsNullOrWhiteSpace(LastReason) ? string.Empty : $" · {LastReason}");
}
