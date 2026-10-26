namespace TradeDesktop.Application.Services;

// Ngưỡng cho dòng log `[MARKET][WARN] Latency spike` (CHỈ log, không đụng guard/quyết định giao dịch).
// Chân MMF: LatencyMs là độ trễ truyền tick ⇒ ngưỡng cứng mặc định (500 ms) như cũ.
// Chân B socket (cTrader / PrimeXBT / TradeLocker): LatencyMs là TUỔI TICK, tăng dần giữa hai tick (tick TradeLocker
// ~500 ms) ⇒ ngưỡng cứng 500 ms vượt liên tục (~41 dòng/phút, soak 2026-10-26). Dùng ngưỡng guard của chính chân B
// (`confirmLatencyB` hiệu lực) để log chỉ báo khi tick đứng lâu tới mức guard sẽ chặn.
// confirmLatencyB ≤ 0 (guard tắt / chưa cấu hình) ⇒ giữ ngưỡng mặc định.
public static class LatencySpikeThresholdResolver
{
    public static double Resolve(bool latencyIsTickAge, int confirmLatencyMsB, double defaultThresholdMs)
        => latencyIsTickAge && confirmLatencyMsB > 0 ? confirmLatencyMsB : defaultThresholdMs;
}
