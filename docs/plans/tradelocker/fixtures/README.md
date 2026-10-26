# Fixture TradeLocker (OPEN_DEMO)

Dùng cho test offline Phase 3 và probe A7. Không chứa token. Account/position id đều là của tài khoản **demo**.

| File | Nội dung | Nguồn |
|---|---|---|
| `tradable-unit-aggregates.b64` | Payload đầu tiên của stream `v3.tradable-unit-aggregates` (protobuf `ProtoTradableUnitAggregateEvent`, base64): 33 tradable unit / instrument / ticker, ~28 KB, đến trong **một** frame | `scan\tradelocker-spike probe --save`, 2026-10-26 |
| `quote.b64` | Một `ProtoQuote` của XAUUSD (`v3.tickers.806102601859910870.quotes`) | như trên |
| `position-aggregates.b64` | Snapshot đầu của `v3.position-aggregates`: 3 position (SPX500 BUY, SPX500 SELL, XAUUSD SELL; 0.01 lot) + 3 market order INCREASE | như trên |
| `expected.json` | Giá trị đã decode bằng decoder tham chiếu (`scan\trade-locker`, `@bufbuild/protobuf` + `descriptors.json`). Test C# phải ra đúng các giá trị này | decode 2026-10-26 |

## Frame đã bắt từ web (đợt quét 2026-10-07, `scan\trade-locker\docs\tradelocker-findings.md` §5)

Frame mở BUY 0.01 SPX500 (REQUEST_RESPONSE, WebSocket binary, không tiền tố độ dài):

```
00 00 00 27                                   stream id 39
11 00                                         type REQUEST_RESPONSE (0x04 << 10) | METADATA (0x100)
00 00 16                                      metadata length 22
fe 00 00 12 11 76 33 2e 70 6f 73 69 74 69 6f 6e 73 3a 6f 70 65 6e
                                              composite: 0x80|0x7E routing, len 18, tag len 17, "v3.positions:open"
08 01                                         side = BUY
12 12 38 30 36 39 30 35 31 39 39 32 30 35 34 33 35 36 37 36
                                              tradable_unit_id = "806905199205435676"
1a 0e 0a 0c 08 01 12 04 30 2e 30 31 18 02 28 01
                                              orders[0].market_order { side=BUY, quantity="0.01", tif=IOC, intent=INCREASE }
```

Frame đóng: `<sid> 11 00 00 00 2a fe 00 00 26 25 "v3.positions.<18 chữ số id>:close"`, data rỗng.

Test bắt buộc ở Phase 3: `OrderPlanner` + `RSocketFrameCodec` encode BUY với stream id 39 phải ra **đúng từng byte** frame trên.
