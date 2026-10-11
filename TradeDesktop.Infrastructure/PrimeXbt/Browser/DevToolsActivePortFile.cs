namespace TradeDesktop.Infrastructure.PrimeXbt.Browser;

// Chrome chạy với `--remote-debugging-port=0` tự chọn cổng ngẫu nhiên và ghi vào `<user-data-dir>\DevToolsActivePort`:
// dòng 1 = port, dòng 2 = path websocket cấp browser. File có thể còn sót sau khi Chrome tắt/crash ⇒ chỉ parse ở đây,
// việc cổng còn sống hay không do caller kiểm bằng HTTP /json/version.
public static class DevToolsActivePortFile
{
    public const string FileName = "DevToolsActivePort";

    public static bool TryRead(string userDataDir, out int port, out string browserPath)
    {
        port = 0;
        browserPath = string.Empty;
        try
        {
            var path = Path.Combine(userDataDir, FileName);
            return File.Exists(path) && TryParse(File.ReadAllText(path), out port, out browserPath);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryParse(string? content, out int port, out string browserPath)
    {
        port = 0;
        browserPath = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 1 || !int.TryParse(lines[0], out var parsed) || parsed is <= 0 or > 65535)
        {
            return false;
        }

        port = parsed;
        browserPath = lines.Length > 1 ? lines[1] : string.Empty;
        return true;
    }

    public static void TryDelete(string userDataDir)
    {
        try
        {
            File.Delete(Path.Combine(userDataDir, FileName));
        }
        catch
        {
            // File đang bị Chrome giữ hoặc không tồn tại: bỏ qua.
        }
    }
}
