using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class RandomLockStatusBuilderTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 5, 0, 0, DateTimeKind.Utc);

    private static RandomLockInputs Inputs(
        bool openEnabled = true,
        bool closeEnabled = true,
        AutoTradeActionType lastType = AutoTradeActionType.None,
        TradingPositionSide lastSide = TradingPositionSide.None,
        DateTime? lastAt = null,
        int lastRandom = 0,
        int selectedPostClose = 300,
        DateTime? lastCloseConfirmedAt = null,
        DateTime? lastOpenConfirmedAt = null,
        TradingPositionSide lastOpenSide = TradingPositionSide.None,
        DateTime? globalUntil = null)
        => new(
            openEnabled, 3, 10,
            closeEnabled, 5, 15,
            lastType, lastSide, lastAt, lastRandom,
            300, 600, selectedPostClose, lastCloseConfirmedAt,
            300, lastOpenConfirmedAt, lastOpenSide,
            globalUntil);

    private static RandomLockStatusRow Row(IReadOnlyList<RandomLockStatusRow> rows, string key)
        => rows.Single(r => r.Key == key);

    [Fact]
    public void NoDispatchYet_AllRowsIdle_AndKeysAreStable()
    {
        var rows = RandomLockStatusBuilder.Build(Inputs(), Now);

        Assert.Equal(
            new[]
            {
                RandomLockStatusBuilder.KeySameOpen, RandomLockStatusBuilder.KeySameClose,
                RandomLockStatusBuilder.KeyPostClose, RandomLockStatusBuilder.KeyOpposite,
                RandomLockStatusBuilder.KeyGlobal,
            },
            rows.Select(r => r.Key));
        Assert.All(rows, r => Assert.False(r.IsBlocking));
        Assert.Equal("3..10s", Row(rows, RandomLockStatusBuilder.KeySameOpen).RangeText);
        Assert.Equal("-", Row(rows, RandomLockStatusBuilder.KeySameOpen).SelectedText);
        Assert.Equal("Chưa có Auto dispatch", RandomLockStatusBuilder.FormatLastAutoDispatch(Inputs()));
    }

    [Fact]
    public void SameOpen_BlocksUntilDispatchPlusSampledSeconds()
    {
        var inputs = Inputs(
            lastType: AutoTradeActionType.Open, lastSide: TradingPositionSide.Buy,
            lastAt: Now.AddSeconds(-4), lastRandom: 7);

        var blocking = Row(RandomLockStatusBuilder.Build(inputs, Now), RandomLockStatusBuilder.KeySameOpen);
        Assert.True(blocking.IsBlocking);
        Assert.Equal("7s", blocking.SelectedText);
        Assert.Equal("3s", blocking.RemainingText);
        Assert.Equal("CHẶN Open BUY + Close", blocking.StatusText);
        // Dispatch cuối là Open thì dòng Same Close không liên quan.
        Assert.False(Row(RandomLockStatusBuilder.Build(inputs, Now), RandomLockStatusBuilder.KeySameClose).IsBlocking);

        var expired = Row(RandomLockStatusBuilder.Build(inputs, Now.AddSeconds(3)), RandomLockStatusBuilder.KeySameOpen);
        Assert.False(expired.IsBlocking);
        Assert.Equal("RẢNH", expired.StatusText);
        Assert.Equal("-", expired.RemainingText);
    }

    [Fact]
    public void SameClose_BlocksAfterCloseDispatch()
    {
        var inputs = Inputs(
            lastType: AutoTradeActionType.Close, lastSide: TradingPositionSide.Sell,
            lastAt: Now.AddSeconds(-2), lastRandom: 12);

        var row = Row(RandomLockStatusBuilder.Build(inputs, Now), RandomLockStatusBuilder.KeySameClose);

        Assert.True(row.IsBlocking);
        Assert.Equal("5..15s", row.RangeText);
        Assert.Equal("12s", row.SelectedText);
        Assert.Equal("10s", row.RemainingText);
        Assert.Equal("CHẶN Close", row.StatusText);
    }

    [Theory]
    [InlineData(false, true, RandomLockStatusBuilder.KeySameOpen)]
    [InlineData(true, false, RandomLockStatusBuilder.KeySameClose)]
    public void DisabledGroup_ShowsOff_AndNeverBlocks(bool openEnabled, bool closeEnabled, string disabledKey)
    {
        // Dispatch cuối thuộc nhóm bị tắt, còn đang trong thời gian random cũ.
        var lastType = disabledKey == RandomLockStatusBuilder.KeySameOpen
            ? AutoTradeActionType.Open
            : AutoTradeActionType.Close;
        var inputs = Inputs(
            openEnabled: openEnabled, closeEnabled: closeEnabled,
            lastType: lastType, lastSide: TradingPositionSide.Buy,
            lastAt: Now.AddSeconds(-1), lastRandom: 20);

        var row = Row(RandomLockStatusBuilder.Build(inputs, Now), disabledKey);

        Assert.Equal("OFF", row.RangeText);
        Assert.Equal("TẮT", row.StatusText);
        Assert.False(row.IsBlocking);
    }

    [Fact]
    public void PostClose_UsesLaterDeadlineOfDispatchAndConfirm_WithoutAdding()
    {
        // Dispatch lúc -100s, confirm lúc -90s, cùng duration 120s: deadline = confirm + 120 = +30s.
        var inputs = Inputs(
            lastType: AutoTradeActionType.Close, lastSide: TradingPositionSide.Buy,
            lastAt: Now.AddSeconds(-100), lastRandom: 5,
            selectedPostClose: 120, lastCloseConfirmedAt: Now.AddSeconds(-90));

        var row = Row(RandomLockStatusBuilder.Build(inputs, Now), RandomLockStatusBuilder.KeyPostClose);

        Assert.True(row.IsBlocking);
        Assert.Equal("300..600s", row.RangeText);
        Assert.Equal("120s", row.SelectedText);
        Assert.Equal("30s", row.RemainingText);
        Assert.Equal("CHẶN Open", row.StatusText);
    }

    [Fact]
    public void PostClose_WithoutAnyCloseAnchor_IsIdleAndHidesDefaultSelection()
    {
        var row = Row(RandomLockStatusBuilder.Build(Inputs(selectedPostClose: 300), Now), RandomLockStatusBuilder.KeyPostClose);

        Assert.False(row.IsBlocking);
        Assert.Equal("-", row.SelectedText);
    }

    [Fact]
    public void Opposite_BlocksOppositeSideOfLastConfirmedOpen()
    {
        var inputs = Inputs(lastOpenConfirmedAt: Now.AddSeconds(-255), lastOpenSide: TradingPositionSide.Buy);

        var row = Row(RandomLockStatusBuilder.Build(inputs, Now), RandomLockStatusBuilder.KeyOpposite);

        Assert.True(row.IsBlocking);
        Assert.Equal("300s (cố định)", row.RangeText);
        Assert.Equal("45s", row.RemainingText);
        Assert.Equal("CHẶN Open SELL", row.StatusText);
    }

    [Fact]
    public void Global_BlocksUntilLockUntil()
    {
        var row = Row(
            RandomLockStatusBuilder.Build(Inputs(globalUntil: Now.AddSeconds(8.2)), Now),
            RandomLockStatusBuilder.KeyGlobal);

        Assert.True(row.IsBlocking);
        Assert.Equal("9s", row.RemainingText);
        Assert.Equal("CHẶN mọi Auto", row.StatusText);
    }

    [Fact]
    public void FromCoordinator_ReflectsSampledSameActionAfterDispatch()
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), new CloseSignalEngineFactory(), null, new Random(1));
        coordinator.UpdateSameActionLockConfig(17, 17);
        var now = DateTime.UtcNow;
        Assert.True(coordinator.TryAcquireTradeAction(now, "OPEN", "open", side: TradingPositionSide.Sell).Acquired);

        var inputs = RandomLockInputs.From(coordinator);
        var row = Row(RandomLockStatusBuilder.Build(inputs, now.AddSeconds(2)), RandomLockStatusBuilder.KeySameOpen);

        Assert.Equal(AutoTradeActionType.Open, coordinator.LastAutoDispatchType);
        Assert.Equal(17, coordinator.LastAutoRandomIntervalSeconds);
        Assert.Equal("17s", row.SelectedText);
        Assert.Equal("15s", row.RemainingText);
        Assert.StartsWith("OPEN SELL lúc ", RandomLockStatusBuilder.FormatLastAutoDispatch(inputs));
    }

    [Theory]
    [InlineData(45, null, "45s")]
    [InlineData(45, -33.0, "45s còn 12s")]
    [InlineData(45, -45.0, "45s ✓")]
    [InlineData(0, -1.0, "0s ✓")]
    public void FormatCountdown_ShowsPendingRemainingOrDone(
        int seconds, double? anchorOffsetSeconds, string expected)
    {
        DateTime? anchor = anchorOffsetSeconds is { } offset ? Now.AddSeconds(offset) : null;

        Assert.Equal(expected, SlotRandomTextFormatter.FormatCountdown(seconds, anchor, Now));
    }

    [Fact]
    public void SlotFormatters_ReadSlotRandomValues_AndHandleMissingSlot()
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), new CloseSignalEngineFactory(), null, new Random(1));
        coordinator.UpdatePostOpenLockConfig(60, 60);
        var confirmedAt = DateTime.UtcNow;
        coordinator.RegisterSyncedSlot(
            "p1", TradingPositionSide.Buy, TradingOpenMode.GapBuy, 11, 22, confirmedAt, holdingSeconds: 30);
        var slot = coordinator.GetSlotByPairId("p1");

        Assert.Equal("60s còn 50s", SlotRandomTextFormatter.FormatPostOpen(slot, confirmedAt.AddSeconds(10)));
        Assert.Equal("60s ✓", SlotRandomTextFormatter.FormatPostOpen(slot, confirmedAt.AddSeconds(60)));
        Assert.Equal("-", SlotRandomTextFormatter.FormatHwndProfile(slot));
        Assert.Equal("-", SlotRandomTextFormatter.FormatHolding(null, confirmedAt));
        Assert.Equal("-", SlotRandomTextFormatter.FormatPostOpen(null, confirmedAt));
    }
}
