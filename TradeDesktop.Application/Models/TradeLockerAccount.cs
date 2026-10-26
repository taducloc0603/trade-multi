namespace TradeDesktop.Application.Models;

// Một dòng của kerber `GET v1/accounts`. Id = "PRDTL#O#<accId>", AccId = chuỗi số dùng cho JWT và id trong hermes.
// Cluster (vd "apl0") ⇒ host api.<cluster>.tradelocker.com.
public sealed record TradeLockerAccount(
    string Id,
    string AccId,
    string Name,
    string Type,
    string Engine,
    string Status,
    string Cluster)
{
    // Chỉ engine APOLLO dùng được giao thức hermes đã khảo sát (Phase 0).
    public bool IsApollo => string.Equals(Engine, "APOLLO", StringComparison.OrdinalIgnoreCase);

    // Loại tài khoản kerber: OPEN_DEMO trên demo đã khảo sát. Mọi loại chứa "DEMO" coi là demo; còn lại coi là THẬT.
    public bool IsDemo => Type.Contains("DEMO", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Id} ({Name}) type={Type} engine={Engine} status={Status} cluster={Cluster}";
}
