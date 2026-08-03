using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Tests.Config;

public sealed class ScheduleSleepingEvaluatorTests
{
    private const string EnabledSchedule = """
        {
          "enabled": true,
          "timezoneMode": "device_local",
          "blockedRanges": [
            { "start": "07:30", "end": "08:00" },
            { "start": "22:00", "end": "23:59" }
          ]
        }
        """;

    [Theory]
    [InlineData(7, 30)]
    [InlineData(7, 45)]
    [InlineData(8, 0)]
    [InlineData(23, 59)]
    public void IsOpenBlocked_InsideEnabledRange_ReturnsTrue(int hour, int minute)
    {
        Assert.True(ScheduleSleepingEvaluator.IsOpenBlocked(
            EnabledSchedule,
            new DateTime(2026, 8, 3, hour, minute, 0, DateTimeKind.Local)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"enabled\":true}")]
    [InlineData("{\"enabled\":true,\"blockedRanges\":[]}")]
    [InlineData("not-json")]
    public void IsOpenBlocked_MissingOrInvalidConfiguration_ReturnsFalse(string? json)
    {
        Assert.False(ScheduleSleepingEvaluator.IsOpenBlocked(json, DateTime.Now));
    }

    [Fact]
    public void IsOpenBlocked_WhenDisabled_ReturnsFalse()
    {
        var json = """{"enabled":false,"blockedRanges":[{"start":"00:00","end":"23:59"}]}""";

        Assert.False(ScheduleSleepingEvaluator.IsOpenBlocked(json, DateTime.Now));
    }

    [Theory]
    [InlineData(23, 30, true)]
    [InlineData(0, 30, true)]
    [InlineData(12, 0, false)]
    public void IsOpenBlocked_RangeCrossingMidnight_IsEvaluatedCorrectly(
        int hour,
        int minute,
        bool expected)
    {
        var json = """{"enabled":true,"blockedRanges":[{"start":"22:00","end":"06:00"}]}""";
        var localNow = new DateTime(2026, 8, 3, hour, minute, 0, DateTimeKind.Local);

        Assert.Equal(expected, ScheduleSleepingEvaluator.IsOpenBlocked(json, localNow));
    }

    [Fact]
    public void IsOpenBlocked_InvalidRange_IsIgnored()
    {
        var json = """{"enabled":true,"blockedRanges":[{"start":"7:30","end":"08:00"}]}""";

        Assert.False(ScheduleSleepingEvaluator.IsOpenBlocked(
            json,
            new DateTime(2026, 8, 3, 7, 45, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void Coordinator_SleepingSchedule_BlocksOpenButDoesNotBlockClose()
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory());
        coordinator.UpdateScheduleSleepingConfig(
            """{"enabled":true,"blockedRanges":[{"start":"00:00","end":"23:59"}]}""");

        Assert.False(coordinator.CanOpenNewSlot(TradingPositionSide.Buy, out var reason));
        Assert.Contains("SCHEDULE_SLEEPING", reason);
        Assert.True(coordinator.CanCloseNow(out _));
    }
}
