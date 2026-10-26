using System.Text;
using System.Text.Json;
using TradeDesktop.Application.Services.TradeLocker;

namespace TradeDesktop.Tests.TradeLocker;

// Phase 3 (docs/plans/tradelocker): lõi giao thức offline — codec protobuf tối thiểu, frame RSocket, symbol index,
// position store, planner, matcher, error mapper, history projector. Fixture = frame thật OPEN_DEMO (Phase 0),
// giá trị kỳ vọng lấy từ decoder tham chiếu (expected.json), KHÔNG từ chính code đang test.
public sealed class TradeLockerProtocolTests
{
    private static byte[] Fixture(string name)
        => Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TradeLocker", "Fixtures", name + ".b64")));

    private static JsonElement Expected(params string[] path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TradeLocker", "Fixtures", "expected.json")));
        var e = doc.RootElement;
        foreach (var key in path) e = e.GetProperty(key);
        return e.Clone();
    }

    private static TradeLockerSymbolIndex IndexFromFixture()
    {
        Assert.True(TradeLockerMessages.TryDecodeTradableUnits(Fixture("tradable-unit-aggregates"), out var units));
        var index = new TradeLockerSymbolIndex();
        index.Apply(units);
        return index;
    }

    private static TradeLockerPositionStore StoreFromFixture()
    {
        Assert.True(TradeLockerMessages.TryDecodePositionEvent(Fixture("position-aggregates"), out var ev));
        var store = new TradeLockerPositionStore(() => 1000);
        store.Apply(ev);
        return store;
    }

    // ---------- protobuf / messages ----------

    [Fact]
    public void Quote_Fixture_MatchesReferenceDecoder()
    {
        Assert.True(TradeLockerMessages.TryDecodeQuote(Fixture("quote"), out var quote));

        Assert.Equal(decimal.Parse(Expected("quote", "bid").GetString()!, System.Globalization.CultureInfo.InvariantCulture), quote.Bid);
        Assert.Equal(decimal.Parse(Expected("quote", "ask").GetString()!, System.Globalization.CultureInfo.InvariantCulture), quote.Ask);
    }

    [Fact]
    public void Quote_MissingOrNonNumericOrZero_IsRejected()
    {
        Assert.False(TradeLockerMessages.TryDecodeQuote(new ProtoWriter().String(1, "4000.1").ToArray(), out _)); // thiếu bid
        Assert.False(TradeLockerMessages.TryDecodeQuote(new ProtoWriter().String(1, "x").String(2, "4000").ToArray(), out _));
        Assert.False(TradeLockerMessages.TryDecodeQuote(new ProtoWriter().String(1, "0").String(2, "0").ToArray(), out _));
        Assert.False(TradeLockerMessages.TryDecodeQuote(new byte[] { 0x0A, 0x50 }, out _)); // độ dài vượt buffer
    }

    [Fact]
    public void PositionEvent_Fixture_MatchesReferenceDecoder()
    {
        Assert.True(TradeLockerMessages.TryDecodePositionEvent(Fixture("position-aggregates"), out var ev));
        var expected = Expected("positionAggregates", "payload", "value");

        var expectedPositions = expected.GetProperty("positions").EnumerateArray().ToList();
        Assert.Equal(expectedPositions.Count, ev.Positions.Count);
        foreach (var (p, e) in ev.Positions.Zip(expectedPositions))
        {
            Assert.Equal(e.GetProperty("id").GetString(), p.Id);
            Assert.Equal(e.GetProperty("tradableUnitId").GetString(), p.TradableUnitId);
            Assert.Equal(e.GetProperty("side").GetInt32(), (int)p.Side);
            Assert.Equal(e.GetProperty("status").GetInt32(), p.Status);
            Assert.Equal(long.Parse(e.GetProperty("createdAt").GetString()!), p.CreatedAtMs);
        }

        var expectedOrders = expected.GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("orderType").GetProperty("value")).ToList();
        Assert.Equal(expectedOrders.Count, ev.Orders.Count);
        foreach (var (o, e) in ev.Orders.Zip(expectedOrders))
        {
            Assert.Equal(1, o.Kind); // market
            Assert.Equal(e.GetProperty("id").GetString(), o.Id);
            Assert.Equal(e.GetProperty("positionId").GetString(), o.PositionId);
            Assert.Equal(e.GetProperty("intent").GetInt32(), (int)o.Intent);
            Assert.Equal(e.GetProperty("status").GetInt32(), o.Status);
            Assert.Equal(decimal.Parse(e.GetProperty("filledQuantity").GetString()!, System.Globalization.CultureInfo.InvariantCulture), o.FilledQuantity);
            Assert.Equal(decimal.Parse(e.GetProperty("averagePrice").GetString()!, System.Globalization.CultureInfo.InvariantCulture), o.AveragePrice);
        }
    }

    [Fact]
    public void PositionEvent_DeletedIds_Decoded()
    {
        var data = new ProtoWriter().Message(2, new ProtoWriter().String(1, "111").String(1, "222")).ToArray();

        Assert.True(TradeLockerMessages.TryDecodePositionEvent(data, out var ev));
        Assert.True(ev.IsDeletion);
        Assert.Equal(["111", "222"], ev.DeletedIds);
    }

    [Fact]
    public void PositionEvent_NeitherAggregateNorDeletion_OrGarbage_IsRejected()
    {
        Assert.False(TradeLockerMessages.TryDecodePositionEvent(new ProtoWriter().String(9, "x").ToArray(), out _));
        Assert.False(TradeLockerMessages.TryDecodePositionEvent(new byte[] { 0xFF, 0xFF, 0xFF }, out _));
    }

    [Fact]
    public void TradableUnits_Fixture_ResolvesXauusdLikeReferenceDecoder()
    {
        var symbol = IndexFromFixture().Resolve("XAUUSD");
        var expected = Expected("tradableUnits", "xauusd");

        Assert.NotNull(symbol);
        Assert.Equal(expected.GetProperty("tradableUnitId").GetString(), symbol!.TradableUnitId);
        Assert.Equal(expected.GetProperty("ticker").GetProperty("id").GetString(), symbol.TickerId);
        Assert.Equal(100m, symbol.LotSize);
        Assert.Equal(0.01m, symbol.TickSize);
        Assert.Equal(0.01m, symbol.LotStep);
        Assert.Equal(0.01m, symbol.MinimumLot);
        Assert.Equal(1000m, symbol.MaximumLot);
        Assert.True(symbol.IsTradingActive);
        Assert.Equal(Expected("tradableUnits", "counts", "tradableUnits").GetInt32(), IndexFromFixture().Count);
    }

    [Theory]
    [InlineData("xauusd")]
    [InlineData(" XAUUSD ")]
    public void SymbolIndex_ExactMatchIgnoresCaseAndSpaces(string query)
    {
        Assert.NotNull(IndexFromFixture().Resolve(query));
    }

    [Theory]
    [InlineData("XAU")]
    [InlineData("XAUUSD.s")]
    [InlineData("")]
    [InlineData(null)]
    public void SymbolIndex_PartialOrUnknown_ResolvesNull(string? query)
    {
        Assert.Null(IndexFromFixture().Resolve(query));
    }

    [Fact]
    public void ErrorFrameMessage_Base64ProtoError_Decoded_PlainTextKept()
    {
        var protoError = new ProtoWriter().String(1, "NOT_FOUND").String(2, "Not found").ToArray();

        var decoded = TradeLockerMessages.DecodeErrorFrameMessage(Convert.ToBase64String(protoError));
        Assert.Equal(new TradeLockerError("NOT_FOUND", "Not found"), decoded);
        Assert.Equal(TradeLockerErrorCategory.NotFound, TradeLockerErrorMapper.Classify(decoded));

        var plain = TradeLockerMessages.DecodeErrorFrameMessage("Closed. ");
        Assert.Equal("", plain.Code);
        Assert.Equal("Closed. ", plain.Description);
        Assert.Equal(TradeLockerErrorCategory.Unknown, TradeLockerErrorMapper.Classify(plain));
    }

    [Theory]
    [InlineData("QUANTITY_NOT_ON_LOT_STEP_GRID", TradeLockerErrorCategory.Rejected)]
    [InlineData("INPUT_VALIDATION", TradeLockerErrorCategory.Rejected)]
    [InlineData("NOT_FOUND", TradeLockerErrorCategory.NotFound)]
    [InlineData("SOMETHING_NEW", TradeLockerErrorCategory.Unknown)]
    public void ErrorMapper_KnownPhase0Codes(string code, TradeLockerErrorCategory expected)
    {
        Assert.Equal(expected, TradeLockerErrorMapper.Classify(new TradeLockerError(code, "")));
    }

    [Fact]
    public void Notification_OrderRejected_DecodedWithCorrelationId()
    {
        var data = new ProtoWriter()
            .String(1, "corr-1")
            .Message(3, new ProtoWriter().Varint(1, 1).String(2, "Insufficient margin"))
            .String(5, "Order rejected")
            .ToArray();

        Assert.True(TradeLockerMessages.TryDecodeNotification(data, out var n));
        Assert.True(n.IsOrderRejected);
        Assert.Equal("corr-1", n.CorrelationId);
        Assert.Equal("Insufficient margin", n.RejectDescription);
        Assert.Equal("Order rejected", n.Title);
    }

    // ---------- RSocket frames ----------

    [Fact]
    public void OpenBuy_EncodesByteForByteLikeTheCapturedWebFrame()
    {
        // fixtures/README.md: frame thật bắt từ web 2026-10-07 (BUY 0.01 SPX500, stream id 39).
        var expectedHex =
            "00000027" + "1100" + "000016" +
            "fe000012" + "11" + Convert.ToHexString(Encoding.ASCII.GetBytes("v3.positions:open")) +
            "0801" + "1212" + Convert.ToHexString(Encoding.ASCII.GetBytes("806905199205435676")) +
            "1a0e0a0c08011204" + Convert.ToHexString(Encoding.ASCII.GetBytes("0.01")) + "18022801";
        var symbol = new TradeLockerSymbol("SPX500", "806905199205435676", "i", "t", 1, 0.1m, 1m, 0.01m, 0.01m, 100m);

        var plan = TradeLockerOrderPlanner.PlanOpen(symbol, TradeLockerSide.Buy, 0.01m).Plan!;
        var frame = RSocketFrameCodec.EncodeRequestResponse(39, plan.Route, plan.Data);

        Assert.Equal(expectedHex.ToUpperInvariant(), Convert.ToHexString(frame));
    }

    [Fact]
    public void CloseFrame_HasCapturedShape_EmptyData()
    {
        var plan = TradeLockerOrderPlanner.PlanClose(TradeLockerTicketCodec.Encode(902413599387337493)).Plan!;
        var frame = RSocketFrameCodec.EncodeRequestResponse(41, plan.Route, plan.Data);

        Assert.Equal("v3.positions.902413599387337493:close", plan.Route);
        // stream(4) + type/flags(2) + metadata len(3) + composite header(4) + tag len(1) = 14 byte, rồi route 37 byte, data rỗng.
        Assert.Equal("00000029" + "1100" + "00002A" + "FE000026" + "25", Convert.ToHexString(frame[..14]));
        Assert.Equal(14 + 37, frame.Length);
    }

    [Fact]
    public void Setup_EncodesVersionKeepaliveMimesAndBearerAuth()
    {
        var frame = RSocketFrameCodec.EncodeSetup("eyJabc", 5000, 15000);

        Assert.Equal("00000000" + "0500" + "00010000" + "00001388" + "00003A98", Convert.ToHexString(frame[..18])); // SETUP|M, v1.0, 5 s, 15 s
        var text = Encoding.ASCII.GetString(frame);
        Assert.Contains(RSocketFrameCodec.MetadataMimeType, text);
        Assert.Contains(RSocketFrameCodec.DataMimeType, text);
        Assert.EndsWith("ü\u0000\u0000\u0007\u0081eyJabc", Encoding.Latin1.GetString(frame)); // 0xFC auth, len 7, 0x81 bearer
    }

    [Fact]
    public void PayloadFrame_DecodedWithMetadataAndFlags()
    {
        var frame = new byte[] { 0, 0, 0, 3, 0x29, 0x60, 0, 0, 2, 0xAA, 0xBB, 0x01, 0x02 }; // PAYLOAD|M|C|N? -> type 0x0A
        frame[4] = (byte)((RSocketFrameType.Payload << 2) | 0x01); // type<<10 | M(0x100) ⇒ byte cao
        frame[5] = RSocketFlags.Next | RSocketFlags.Complete;

        Assert.True(RSocketFrameCodec.TryDecode(frame, out var f));
        Assert.Equal(3, f.StreamId);
        Assert.Equal(RSocketFrameType.Payload, f.Type);
        Assert.True(f.Has(RSocketFlags.Next) && f.Has(RSocketFlags.Complete) && f.Has(RSocketFlags.Metadata));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, f.Metadata.ToArray());
        Assert.Equal(new byte[] { 0x01, 0x02 }, f.Data.ToArray());
    }

    [Fact]
    public void ErrorAndKeepAliveFrames_Decoded_TruncatedRejected()
    {
        var error = new byte[] { 0, 0, 0, 5, (byte)(RSocketFrameType.Error << 2), 0, 0, 0, 2, 1, (byte)'h', (byte)'i' };
        Assert.True(RSocketFrameCodec.TryDecode(error, out var e));
        Assert.Equal(0x201u, e.ErrorCode);
        Assert.Equal("hi", e.ErrorMessage);

        var keepAlive = RSocketFrameCodec.EncodeKeepAlive(respond: true, [9]);
        Assert.True(RSocketFrameCodec.TryDecode(keepAlive, out var k));
        Assert.Equal(RSocketFrameType.KeepAlive, k.Type);
        Assert.True(k.Has(RSocketFlags.Respond));
        Assert.Equal(new byte[] { 9 }, k.Data.ToArray());

        Assert.False(RSocketFrameCodec.TryDecode(new byte[] { 0, 0, 0 }, out _));
        Assert.False(RSocketFrameCodec.TryDecode(new byte[] { 0, 0, 0, 1, (byte)(RSocketFrameType.Payload << 2) | 1, 0, 0, 0, 9 }, out _)); // metadata dài hơn frame
    }

    [Fact]
    public void FragmentAssembler_JoinsFollowsFramesPerStream()
    {
        var assembler = new RSocketFragmentAssembler();
        RSocketFrame Part(int stream, byte[] data, bool follows) =>
            new(stream, RSocketFrameType.Payload, RSocketFlags.Next | (follows ? RSocketFlags.Follows : 0), ReadOnlyMemory<byte>.Empty, data);

        Assert.Null(assembler.Feed(Part(1, [1, 2], follows: true)));
        Assert.Equal(new byte[] { 7 }, assembler.Feed(Part(3, [7], follows: false))!.Value.ToArray()); // stream khác không lẫn
        Assert.Null(assembler.Feed(Part(1, [3], follows: true)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, assembler.Feed(Part(1, [4], follows: false))!.Value.ToArray());
        Assert.Equal(0, assembler.PendingStreams);
    }

    [Fact]
    public void RequestStreamAndRequestN_Encode()
    {
        var stream = RSocketFrameCodec.EncodeRequestStream(1, "v3.position-aggregates", 100);
        Assert.Equal("00000001" + "1900" + "00000064", Convert.ToHexString(stream[..10])); // 0x06<<10|M, n=100
        Assert.Equal("00000001" + "2000" + "00000064", Convert.ToHexString(RSocketFrameCodec.EncodeRequestN(1, 100)));
        Assert.Equal("00000001" + "2400", Convert.ToHexString(RSocketFrameCodec.EncodeCancel(1)));
    }

    // ---------- position store ----------

    [Fact]
    public void PositionStore_Fixture_QuantityAndVwapEntry_HedgeKeptSeparate()
    {
        var store = StoreFromFixture();
        var open = store.Open().ToDictionary(p => p.Id);

        Assert.Equal(3, open.Count);
        Assert.Equal(0.01m, open["895596180090928800"].Quantity);
        Assert.Equal(7822.1m, open["895596180090928800"].EntryPrice);
        Assert.Equal(TradeLockerSide.Buy, open["895596180090928800"].Side);
        Assert.Equal(TradeLockerSide.Sell, open["895678797842759191"].Side); // SPX500 Buy + Sell cùng lúc = hedge, không net
        Assert.Equal(4104.2m, open["895683725856354606"].EntryPrice);
        Assert.True(store.Synced);
        Assert.Equal(1UL, store.Version);
    }

    [Fact]
    public void PositionStore_DecreaseOrderAndDeletion()
    {
        var store = StoreFromFixture();
        var close = new TradeLockerOrderMsg(1, "c1", "895683725856354606", TradeLockerSide.Buy, TradeLockerOrderStatus.Filled,
            TradeLockerIntent.Decrease, 0.01m, 0.01m, 4100m);

        Assert.True(store.Apply(new TradeLockerPositionEvent([], [close], [])));
        Assert.Equal(2, store.Open().Count); // qty về 0 ⇒ không còn là vị thế mở
        Assert.Equal(2UL, store.Version);

        Assert.True(store.Apply(new TradeLockerPositionEvent([], [], ["895596180090928800"])));
        Assert.Single(store.Open());
        Assert.Null(store.Get("895596180090928800"));
    }

    [Fact]
    public void PositionStore_UnchangedEvent_DoesNotBumpVersion_TradeRecordsUseLotsAndTicketCodec()
    {
        var store = StoreFromFixture();
        Assert.True(TradeLockerMessages.TryDecodePositionEvent(Fixture("position-aggregates"), out var again));

        Assert.False(store.Apply(again));
        Assert.Equal(1UL, store.Version);

        var records = store.ToTradeRecords(unit => unit == "806903684076031137" ? "XAUUSD" : null);
        var xau = Assert.Single(records, r => r.Symbol == "XAUUSD");
        Assert.Equal(0.01, xau.Lot, 6);
        Assert.Equal(1, xau.TradeType);
        Assert.Equal(4104.2, xau.Price, 6);
        Assert.Equal(1000UL, xau.OpenEaTimeLocal);
        Assert.True(TradeLockerTicketCodec.TryDecode(xau.Ticket, out var id));
        Assert.Equal(895683725856354606L, id);
    }

    [Fact]
    public void PositionStore_ResetForNewConnection_DropsStateAndUnsyncs()
    {
        var store = StoreFromFixture();

        store.ResetForNewConnection();

        Assert.False(store.Synced);
        Assert.Empty(store.All());
    }

    // ---------- planner / matcher ----------

    private static readonly TradeLockerSymbol Xau = new("XAUUSD", "806903684076031137", "i", "t", 1, 0.01m, 100m, 0.01m, 0.01m, 1000m);

    [Theory]
    [InlineData(0.015, "không là bội")]
    [InlineData(0.001, "< min")]
    [InlineData(1001, "> max")]
    public void PlanOpen_InvalidLots_RejectedBeforeSending(double lots, string reason)
    {
        var result = TradeLockerOrderPlanner.PlanOpen(Xau, TradeLockerSide.Buy, (decimal)lots);

        Assert.False(result.IsSuccess);
        Assert.Contains(reason, result.Error);
    }

    [Fact]
    public void PlanOpen_SuspendedOrUnknownSymbol_Rejected()
    {
        Assert.False(TradeLockerOrderPlanner.PlanOpen(Xau with { TradingStatus = 3 }, TradeLockerSide.Buy, 0.01m).IsSuccess);
        Assert.False(TradeLockerOrderPlanner.PlanOpen(null, TradeLockerSide.Buy, 0.01m).IsSuccess);
        Assert.False(TradeLockerOrderPlanner.PlanOpen(Xau, TradeLockerSide.Unspecified, 0.01m).IsSuccess);
    }

    [Fact]
    public void PlanOpen_FormatsQuantityWithoutTrailingZeros()
    {
        Assert.Equal("0.1", TradeLockerOrderPlanner.PlanOpen(Xau, TradeLockerSide.Sell, 0.10m).Plan!.Quantity);
        Assert.Equal("1", TradeLockerOrderPlanner.FormatLots(1.00m));
    }

    [Fact]
    public void PlanClose_NonTradeLockerTicket_Rejected()
    {
        Assert.False(TradeLockerOrderPlanner.PlanClose(77_460_990UL).IsSuccess);
        Assert.False(TradeLockerOrderPlanner.PlanClose(0x4000_0000_0000_0001UL).IsSuccess); // ticket cTrader
    }

    private static TradeLockerPositionView View(string id, TradeLockerSide side, decimal qty, int status = TradeLockerPositionStatus.Open, string unit = "806903684076031137")
        => new(id, unit, side, status, qty, 4000m, 0, 0, 0);

    [Fact]
    public void Matcher_ExactlyOneNewPosition_Matched()
    {
        var before = new HashSet<string> { "old" };
        var result = TradeLockerOrderMatcher.Match(before,
            [View("old", TradeLockerSide.Buy, 0.01m), View("new", TradeLockerSide.Buy, 0.01m), View("other", TradeLockerSide.Sell, 0.01m)],
            "806903684076031137", TradeLockerSide.Buy, 0.01m);

        Assert.Equal(TradeLockerMatchStatus.Matched, result.Status);
        Assert.Equal("new", result.PositionId);
    }

    [Fact]
    public void Matcher_TwoNewSameSide_Ambiguous_NeverGuesses()
    {
        // Phase 0 R9: hai lệnh BUY đồng thời ⇒ hai position mới cùng chiều.
        var result = TradeLockerOrderMatcher.Match(new HashSet<string>(),
            [View("a", TradeLockerSide.Buy, 0.01m), View("b", TradeLockerSide.Buy, 0.01m)],
            "806903684076031137", TradeLockerSide.Buy, 0.01m);

        Assert.Equal(TradeLockerMatchStatus.Ambiguous, result.Status);
        Assert.Null(result.PositionId);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void Matcher_PartialFillOrNotOpenYet_NotFoundYet()
    {
        Assert.Equal(TradeLockerMatchStatus.NotFound, TradeLockerOrderMatcher.Match(new HashSet<string>(),
            [View("a", TradeLockerSide.Buy, 0.005m)], "806903684076031137", TradeLockerSide.Buy, 0.01m).Status);
        Assert.Equal(TradeLockerMatchStatus.NotFound, TradeLockerOrderMatcher.Match(new HashSet<string>(),
            [View("a", TradeLockerSide.Buy, 0.01m, TradeLockerPositionStatus.New)], "806903684076031137", TradeLockerSide.Buy, 0.01m).Status);
        Assert.Equal(TradeLockerMatchStatus.NotFound, TradeLockerOrderMatcher.Match(new HashSet<string>(),
            [View("a", TradeLockerSide.Buy, 0.01m, unit: "other")], "806903684076031137", TradeLockerSide.Buy, 0.01m).Status);
    }

    // ---------- history ----------

    [Fact]
    public void History_ParsePage_ProjectsMoneyFieldsAndTicket()
    {
        const string json = """
            {"data":[
              {"positionId":"902413599387337493","instrumentName":"XAUUSD","positionSide":"BUY","closedAmount":"0.01",
               "averageOpenPrice":"4008.64","closePrice":"4009.10","openDateTime":"2026-10-26T04:29:30.700Z",
               "closeDateTime":"1792990186000","commission":"-0.05","swap":"0.0","pnl":"0.46"},
              {"positionId":"bad","positionSide":"BUY"}],
             "links":{"next":"?page=2"}}
            """;

        var page = TradeLockerHistoryProjector.ParsePage(json);
        Assert.Equal("?page=2", page.Next);
        var record = Assert.Single(TradeLockerHistoryProjector.Project(page.Items));

        Assert.True(TradeLockerTicketCodec.TryDecode(record.Ticket, out var id));
        Assert.Equal(902413599387337493L, id);
        Assert.Equal(0, record.TradeType);
        Assert.Equal(0.01, record.Volume, 6);
        Assert.Equal(0.46, record.Profit, 6);
        Assert.Equal(-0.05, record.Commission, 6);
        Assert.Equal(1792990186000UL, record.CloseTimeMsc);
        Assert.Equal((ulong)DateTimeOffset.Parse("2026-10-26T04:29:30.700Z").ToUnixTimeMilliseconds(), record.OpenTimeMsc);
    }

    [Fact]
    public void History_PartialCloses_GroupedPerPosition_WeightedClosePrice()
    {
        var rows = new[]
        {
            new TradeLockerClosedPosition("5", "XAUUSD", TradeLockerSide.Sell, 0.01m, 4000m, 3990m, 1, 10, 0m, 0m, 1m),
            new TradeLockerClosedPosition("5", "XAUUSD", TradeLockerSide.Sell, 0.03m, 4000m, 3980m, 1, 20, 0m, 0m, 6m),
        };

        var record = Assert.Single(TradeLockerHistoryProjector.Project(rows));

        Assert.Equal(0.04, record.Volume, 6);
        Assert.Equal(3982.5, record.ClosePrice, 6);
        Assert.Equal(7.0, record.Profit, 6);
        Assert.Equal(20UL, record.CloseTimeMsc);
        Assert.Equal(1, record.TradeType);
    }

    [Fact]
    public void History_RealHeraFixture_ParsesCursorAndMoneyPnl()
    {
        // Trang thật lấy 2026-10-26 (OPEN_DEMO): mới nhất trước, 20 dòng, links.next = {"cursorId": "…"}.
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TradeLocker", "Fixtures", "hera-closed-positions.json"));

        var page = TradeLockerHistoryProjector.ParsePage(json);
        var records = TradeLockerHistoryProjector.Project(page.Items);

        Assert.Equal(20, page.Items.Count);
        Assert.Equal("895676211073617617", page.Next);
        var latest = records.Single(r => TradeLockerTicketCodec.TryDecode(r.Ticket, out var id) && id == 902413599383143131L);
        Assert.Equal("XAUUSD", latest.Symbol);
        Assert.Equal(0, latest.TradeType);
        Assert.Equal(0.01, latest.Volume, 6);
        Assert.Equal(4008.64, latest.OpenPrice, 6);
        Assert.Equal(4008.94, latest.ClosePrice, 6);
        Assert.Equal(0.3, latest.Profit, 6); // tiền: 0.30 giá × 0.01 lot × lot_size 100
        Assert.Equal((ulong)DateTimeOffset.Parse("2026-10-26T04:29:44.219Z").ToUnixTimeMilliseconds(), latest.CloseTimeMsc);
    }

    [Fact]
    public void History_GarbageJson_GivesEmptyPage()
    {
        var page = TradeLockerHistoryProjector.ParsePage("not json");

        Assert.Empty(page.Items);
        Assert.Null(page.Next);
    }
}
