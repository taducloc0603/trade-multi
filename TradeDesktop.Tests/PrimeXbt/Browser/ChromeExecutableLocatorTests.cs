using TradeDesktop.Infrastructure.PrimeXbt.Browser;

namespace TradeDesktop.Tests.PrimeXbt.Browser;

public sealed class ChromeExecutableLocatorTests
{
    private static readonly Dictionary<string, string?> Env = new()
    {
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
        ["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local"
    };

    private static string? Find(HashSet<string> existing, Dictionary<string, string>? appPaths = null)
        => ChromeExecutableLocator.Find(
            existing.Contains,
            exe => appPaths is not null && appPaths.TryGetValue(exe, out var p) ? p : null,
            name => Env.GetValueOrDefault(name));

    [Fact]
    public void PrefersChromeOverEdge()
    {
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        Assert.Equal(chrome, Find([chrome, edge]));
    }

    [Fact]
    public void AppPathRegistration_WinsOverDefaultFolders()
    {
        var registered = @"D:\Apps\Chrome\chrome.exe";
        var standard = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        Assert.Equal(registered, Find([registered, standard], new() { ["chrome.exe"] = registered }));
    }

    [Fact]
    public void FallsBackToEdge_WhenNoChrome()
    {
        var edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        Assert.Equal(edge, Find([edge]));
    }

    [Fact]
    public void FindsPerUserChromeInstall()
    {
        var perUser = @"C:\Users\u\AppData\Local\Google\Chrome\Application\chrome.exe";
        Assert.Equal(perUser, Find([perUser]));
    }

    [Fact]
    public void ReturnsNull_WhenNoBrowser()
        => Assert.Null(Find([]));

    [Fact]
    public void AppPathRegisteredButMissingOnDisk_IsSkipped()
    {
        var standard = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        Assert.Equal(standard, Find([standard], new() { ["chrome.exe"] = @"D:\gone\chrome.exe" }));
    }
}
