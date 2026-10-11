using Microsoft.Win32;

namespace TradeDesktop.Infrastructure.PrimeXbt.Browser;

// Tìm trình duyệt Chromium để mở cửa sổ PrimeXBT có cổng CDP: ưu tiên Chrome, không có thì Edge (Windows luôn có).
// Không có cả hai ⇒ null; UI hướng user dùng nút đăng nhập WebView2 cũ.
public static class ChromeExecutableLocator
{
    public static string? Find() => Find(File.Exists, ReadAppPath, Environment.GetEnvironmentVariable);

    // Tách nguồn dữ liệu (file tồn tại, registry App Paths, biến môi trường) để test được thứ tự ưu tiên.
    public static string? Find(
        Func<string, bool> fileExists,
        Func<string, string?> appPathLookup,
        Func<string, string?> environmentVariable)
    {
        foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
        {
            foreach (var candidate in Candidates(exe, appPathLookup, environmentVariable))
            {
                if (!string.IsNullOrWhiteSpace(candidate) && fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string?> Candidates(
        string exe,
        Func<string, string?> appPathLookup,
        Func<string, string?> environmentVariable)
    {
        yield return appPathLookup(exe);

        var relative = exe == "chrome.exe"
            ? Path.Combine("Google", "Chrome", "Application", exe)
            : Path.Combine("Microsoft", "Edge", "Application", exe);
        foreach (var root in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
        {
            var dir = environmentVariable(root);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                yield return Path.Combine(dir, relative);
            }
        }
    }

    private static string? ReadAppPath(string exe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(subKey + exe);
                if (key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
                {
                    return path.Trim('"');
                }
            }
            catch
            {
                // Không đọc được registry ⇒ thử nguồn kế tiếp.
            }
        }

        return null;
    }
}
