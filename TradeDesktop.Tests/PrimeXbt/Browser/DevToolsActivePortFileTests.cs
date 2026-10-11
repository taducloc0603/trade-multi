using TradeDesktop.Infrastructure.PrimeXbt.Browser;

namespace TradeDesktop.Tests.PrimeXbt.Browser;

public sealed class DevToolsActivePortFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "devtools-port-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryParse_ValidFile_ReturnsPortAndPath()
    {
        Assert.True(DevToolsActivePortFile.TryParse("53412\r\n/devtools/browser/abc-123\r\n", out var port, out var path));
        Assert.Equal(53412, port);
        Assert.Equal("/devtools/browser/abc-123", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("abc\n/devtools/browser/x")]
    [InlineData("0\n/devtools/browser/x")]
    [InlineData("70000\n/devtools/browser/x")]
    public void TryParse_InvalidContent_ReturnsFalse(string? content)
        => Assert.False(DevToolsActivePortFile.TryParse(content, out _, out _));

    [Fact]
    public void TryRead_MissingFile_ReturnsFalse()
        => Assert.False(DevToolsActivePortFile.TryRead(_dir, out _, out _));

    [Fact]
    public void TryRead_ThenDelete_RoundTrips()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, DevToolsActivePortFile.FileName), "9222\n/devtools/browser/x");

        Assert.True(DevToolsActivePortFile.TryRead(_dir, out var port, out _));
        Assert.Equal(9222, port);

        DevToolsActivePortFile.TryDelete(_dir);
        Assert.False(DevToolsActivePortFile.TryRead(_dir, out _, out _));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }
}
