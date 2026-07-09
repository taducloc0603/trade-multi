"""
DataExporter Monitor
Đọc real-time shared memory do EA MT4/MT5 ghi (channel `Local\\MT_<ID>_*`).
Chỉ hoạt động trên Windows (named shared memory + ctypes.windll).
"""

import ctypes
import mmap
import os
import struct
import time as time_mod
from datetime import datetime, timezone

# ─── Hằng số layout — phải khớp với EA ───────────────────────────────────
HEADER_SIZE     = 16
TRADES_SIZE     = 4096
HISTORY_SIZE    = 65536
TICK_SIZE       = 256
TRADE_RECORD    = 100
HISTORY_RECORD  = 124

MAX_TRADES   = (TRADES_SIZE  - HEADER_SIZE) // TRADE_RECORD       # = 40
MAX_HISTORY  = (HISTORY_SIZE - HEADER_SIZE) // HISTORY_RECORD     # = 528

# Format string (little-endian, không padding) cho từng record.
FMT_HEADER   = "<iQ4x"                       # count + timestamp + 4B padding
FMT_TRADE    = "<QdddddiQQ32s"               # = 100B
FMT_HISTORY  = "<QidddddddQQQ32s"            # = 124B
FMT_TICK     = "<dddQ16s"                    # = 48B

# Fail fast nếu hằng số bị sửa lệch với EA.
assert struct.calcsize(FMT_HEADER)  == HEADER_SIZE,    "FMT_HEADER mismatch"
assert struct.calcsize(FMT_TRADE)   == TRADE_RECORD,   "FMT_TRADE mismatch"
assert struct.calcsize(FMT_HISTORY) == HISTORY_RECORD, "FMT_HISTORY mismatch"
assert struct.calcsize(FMT_TICK)    == 48,             "FMT_TICK mismatch"

# Ngưỡng đánh giá độ tươi của data (ms).
FRESH_MS = 500
STALE_MS = 2000
# Sau ngần này (giây) mà ea_ms vẫn = 0 → coi như EA chưa khởi động.
EA_WAIT_TIMEOUT = 3.0

# ─── ANSI colors (Windows 10+ Terminal, mọi terminal hiện đại) ───────────
class C:
    R    = "\033[0m"
    B    = "\033[1m"
    DIM  = "\033[2m"
    RED  = "\033[31m"
    GRN  = "\033[32m"
    YEL  = "\033[33m"
    CYN  = "\033[36m"
    MAG  = "\033[35m"
    GRY  = "\033[90m"


def enable_ansi() -> None:
    """Bật VT-processing trên Windows console (Win 10 trở lên)."""
    if os.name == "nt":
        os.system("")


def clear_screen() -> None:
    os.system("cls" if os.name == "nt" else "clear")


# ─── Tick count (ms) — đồng bộ với EA `GetTickCount64()` trên Windows ───
if os.name == "nt":
    _get_tick = ctypes.windll.kernel32.GetTickCount64
    _get_tick.restype = ctypes.c_uint64
    def now_ms() -> int:
        return _get_tick()
else:
    def now_ms() -> int:
        return int(time_mod.time() * 1000)


# ─── Helpers UI ──────────────────────────────────────────────────────────
def fmt_time_ms(ms: int) -> str:
    """Trả về chuỗi 12 ký tự visible (đã pad sẵn để tránh lệch khi có ANSI)."""
    if not ms:
        return f"{C.DIM}{'—':<12}{C.R}"
    return (
        datetime.fromtimestamp(ms / 1000, tz=timezone.utc)
        .strftime("%H:%M:%S.")
        + f"{ms % 1000:03d}"
    )


def fmt_freshness(delay_ms: int) -> str:
    if delay_ms < 0:
        return f"{C.DIM}● clock-skew{C.R}"
    if delay_ms < FRESH_MS:
        return f"{C.GRN}● LIVE{C.R}"
    if delay_ms < STALE_MS:
        return f"{C.YEL}● SLOW{C.R}"
    return f"{C.RED}● STALE{C.R}"


def fmt_profit(p: float) -> str:
    if p > 0:
        return f"{C.GRN}+{p:>7.2f}{C.R}"
    if p < 0:
        return f"{C.RED}{p:>8.2f}{C.R}"
    return f"{C.DIM}{p:>8.2f}{C.R}"


def fmt_type(t: int) -> str:
    return f"{C.GRN}BUY {C.R}" if t == 0 else f"{C.RED}SELL{C.R}"


def banner(channel: str, mode: str) -> None:
    if channel == "?":
        title = f"  DataExporter Monitor · mode = {mode}"
    else:
        title = f"  DataExporter Monitor · channel = {channel} · mode = {mode}"
    pad = max(0, 78 - len(title))
    print(f"{C.B}{C.CYN}╔{'═'*78}╗{C.R}")
    print(f"{C.B}{C.CYN}║{C.R}{C.B}{title}{' '*pad}{C.B}{C.CYN}║{C.R}")
    print(f"{C.B}{C.CYN}╚{'═'*78}╝{C.R}")


def wait_for_ea(shm: mmap.mmap, label: str) -> bool:
    """Chờ EA ghi data đầu tiên. Trả về False nếu quá EA_WAIT_TIMEOUT giây."""
    start = time_mod.time()
    while time_mod.time() - start < EA_WAIT_TIMEOUT:
        _, ea_ms = struct.unpack_from(FMT_HEADER, shm, 0)
        if ea_ms > 0:
            return True
        time_mod.sleep(0.1)
    print(f"\n{C.RED}  EA chưa ghi data vào `Local\\MT_..._{label}` sau {EA_WAIT_TIMEOUT:.0f}s.{C.R}")
    print(f"  {C.YEL}Kiểm tra:{C.R}")
    print(f"    1. EA DataExporter đã được attach lên chart MT4/MT5 chưa?")
    print(f"    2. `EA_CHANNEL_ID` trong EA có khớp channel bạn nhập không?")
    print(f"    3. Tab Experts trên MT4/MT5 có dòng `[OK] Khởi động thành công` không?")
    return False


def status_line(ea_ms: int, delay: int, frames: int, skipped: int, extra: str = "") -> None:
    print(
        f"  EA:{C.CYN}{ea_ms:>14}{C.R}   "
        f"Py:{C.CYN}{now_ms():>14}{C.R}   "
        f"Δ:{delay:>5} ms   "
        f"{fmt_freshness(delay)}"
    )
    print(
        f"  Frames: {C.B}{frames:>6}{C.R}   "
        f"Skipped: {C.DIM}{skipped:>6}{C.R}   "
        f"{extra}"
    )
    print()


# ─── Readers ─────────────────────────────────────────────────────────────
def read_trades(channel: str) -> None:
    last_ts = -1
    frames = 0
    skipped = 0
    with mmap.mmap(-1, TRADES_SIZE, f"Local\\MT_{channel}_Trades") as shm:
        if not wait_for_ea(shm, "Trades"):
            return
        while True:
            try:
                count, ea_ms = struct.unpack_from(FMT_HEADER, shm, 0)
                if ea_ms == last_ts:
                    skipped += 1
                    time_mod.sleep(0.02)
                    continue
                if not (0 <= count <= MAX_TRADES):
                    # corrupted / chưa được ghi → chờ
                    time_mod.sleep(0.05)
                    continue

                trades = []
                off = HEADER_SIZE
                for _ in range(count):
                    rec = struct.unpack_from(FMT_TRADE, shm, off)
                    trades.append(rec)
                    off += TRADE_RECORD

                # Seqlock-like guard: nếu EA ghi đè khi đang đọc body,
                # timestamp ở header sẽ khác → bỏ frame này, đọc lại.
                _, ea_ms_after = struct.unpack_from(FMT_HEADER, shm, 0)
                if ea_ms_after != ea_ms:
                    skipped += 1
                    continue

                last_ts = ea_ms
                frames += 1
                delay = now_ms() - ea_ms

                clear_screen()
                banner(channel, "TRADES")
                status_line(ea_ms, delay, frames, skipped, f"Records: {C.B}{count}{C.R}/{MAX_TRADES}")

                # Table
                print(f"  ┌{'─'*8}┬{'─'*12}┬{'─'*6}┬{'─'*6}┬{'─'*10}┬{'─'*10}┬{'─'*10}┬{'─'*10}┬{'─'*14}┐")
                print(f"  │ {'Symbol':<6} │ {'Ticket':<10} │ {'Type':<4} │ {'Lot':<4} │ {'Price':<8} │ {'SL':<8} │ {'TP':<8} │ {'Profit':<8} │ {'Open Time':<12} │")
                print(f"  ├{'─'*8}┼{'─'*12}┼{'─'*6}┼{'─'*6}┼{'─'*10}┼{'─'*10}┼{'─'*10}┼{'─'*10}┼{'─'*14}┤")

                if not trades:
                    print(f"  │ {C.DIM}{'(không có vị thế nào đang mở)':<92}{C.R} │")
                else:
                    for t in trades:
                        ticket, lot, price, sl, tp, profit, ttype, time_msc, open_ea, sym = t
                        symbol = sym.decode("utf-8", errors="ignore").rstrip("\x00")
                        print(
                            f"  │ {symbol:<6} │ {ticket:<10} │ {fmt_type(ttype)} │ "
                            f"{lot:<4.2f} │ {price:<8.5f} │ {sl:<8.5f} │ {tp:<8.5f} │ "
                            f"{fmt_profit(profit)} │ {fmt_time_ms(time_msc)} │"
                        )
                print(f"  └{'─'*8}┴{'─'*12}┴{'─'*6}┴{'─'*6}┴{'─'*10}┴{'─'*10}┴{'─'*10}┴{'─'*10}┴{'─'*14}┘")
                print(f"\n  {C.DIM}[Ctrl+C] thoát{C.R}")
                time_mod.sleep(0.05)
            except KeyboardInterrupt:
                print(f"\n{C.YEL}Đã dừng.{C.R}")
                return
            except struct.error as e:
                print(f"{C.RED}Lỗi parse: {e}{C.R}")
                time_mod.sleep(0.5)


def read_history(channel: str) -> None:
    last_ts = -1
    frames = 0
    skipped = 0
    with mmap.mmap(-1, HISTORY_SIZE, f"Local\\MT_{channel}_History") as shm:
        if not wait_for_ea(shm, "History"):
            return
        while True:
            try:
                count, ea_ms = struct.unpack_from(FMT_HEADER, shm, 0)
                if ea_ms == last_ts:
                    skipped += 1
                    time_mod.sleep(0.02)
                    continue
                if not (0 <= count <= MAX_HISTORY):
                    time_mod.sleep(0.05)
                    continue

                deals = []
                off = HEADER_SIZE
                for _ in range(count):
                    rec = struct.unpack_from(FMT_HISTORY, shm, off)
                    deals.append(rec)
                    off += HISTORY_RECORD

                # Seqlock-like guard (xem read_trades).
                _, ea_ms_after = struct.unpack_from(FMT_HEADER, shm, 0)
                if ea_ms_after != ea_ms:
                    skipped += 1
                    continue

                last_ts = ea_ms
                frames += 1
                delay = now_ms() - ea_ms

                clear_screen()
                banner(channel, "HISTORY")
                status_line(ea_ms, delay, frames, skipped, f"Records: {C.B}{count}{C.R}/{MAX_HISTORY}")

                print(f"  ┌{'─'*8}┬{'─'*12}┬{'─'*6}┬{'─'*10}┬{'─'*10}┬{'─'*10}┬{'─'*8}┬{'─'*14}┐")
                print(f"  │ {'Symbol':<6} │ {'Ticket':<10} │ {'Type':<4} │ {'Open':<8} │ {'Close':<8} │ {'Profit':<8} │ {'Comm':<6} │ {'Close Time':<12} │")
                print(f"  ├{'─'*8}┼{'─'*12}┼{'─'*6}┼{'─'*10}┼{'─'*10}┼{'─'*10}┼{'─'*8}┼{'─'*14}┤")

                if not deals:
                    print(f"  │ {C.DIM}{'(không có lịch sử trong 24h qua)':<83}{C.R} │")
                else:
                    for d in deals:
                        (ticket, dtype, vol, op, cp, sl, tp, comm, profit,
                         ot, ct, ea_ct, sym) = d
                        symbol = sym.decode("utf-8", errors="ignore").rstrip("\x00")
                        print(
                            f"  │ {symbol:<6} │ {ticket:<10} │ {fmt_type(dtype)} │ "
                            f"{op:<8.5f} │ {cp:<8.5f} │ {fmt_profit(profit)} │ "
                            f"{comm:<6.2f} │ {fmt_time_ms(ct):<12} │"
                        )
                print(f"  └{'─'*8}┴{'─'*12}┴{'─'*6}┴{'─'*10}┴{'─'*10}┴{'─'*10}┴{'─'*8}┴{'─'*14}┘")
                print(f"\n  {C.DIM}[Ctrl+C] thoát{C.R}")
                time_mod.sleep(0.05)
            except KeyboardInterrupt:
                print(f"\n{C.YEL}Đã dừng.{C.R}")
                return
            except struct.error as e:
                print(f"{C.RED}Lỗi parse: {e}{C.R}")
                time_mod.sleep(0.5)


def read_tick(channel: str) -> None:
    last_ts = -1
    frames = 0
    skipped = 0
    with mmap.mmap(-1, TICK_SIZE, f"Local\\MT_{channel}_Tick") as shm:
        if not wait_for_ea(shm, "Tick"):
            return
        while True:
            try:
                count, ea_ms = struct.unpack_from(FMT_HEADER, shm, 0)
                if ea_ms == last_ts:
                    skipped += 1
                    time_mod.sleep(0.02)
                    continue
                if count != 1:
                    time_mod.sleep(0.05)
                    continue

                bid, ask, spread, time_msc, sym = struct.unpack_from(FMT_TICK, shm, HEADER_SIZE)
                symbol = sym.decode("utf-8", errors="ignore").rstrip("\x00")

                # Seqlock-like guard (xem read_trades).
                _, ea_ms_after = struct.unpack_from(FMT_HEADER, shm, 0)
                if ea_ms_after != ea_ms:
                    skipped += 1
                    continue

                last_ts = ea_ms
                frames += 1
                delay = now_ms() - ea_ms

                clear_screen()
                banner(channel, "TICK")
                status_line(ea_ms, delay, frames, skipped)

                print(f"  ┌{'─'*14}┬{'─'*22}┐")
                print(f"  │ {'Symbol':<12} │ {C.B}{C.CYN}{symbol:<20}{C.R} │")
                print(f"  │ {'Bid':<12} │ {C.GRN}{bid:<20.5f}{C.R} │")
                print(f"  │ {'Ask':<12} │ {C.RED}{ask:<20.5f}{C.R} │")
                print(f"  │ {'Spread':<12} │ {spread:<20.5f} │")
                print(f"  │ {'Server time':<12} │ {fmt_time_ms(time_msc):<20} │")
                print(f"  └{'─'*14}┴{'─'*22}┘")
                print(f"\n  {C.DIM}[Ctrl+C] thoát{C.R}")
                time_mod.sleep(0.05)
            except KeyboardInterrupt:
                print(f"\n{C.YEL}Đã dừng.{C.R}")
                return
            except struct.error as e:
                print(f"{C.RED}Lỗi parse: {e}{C.R}")
                time_mod.sleep(0.5)


# ─── Menu ────────────────────────────────────────────────────────────────
MODES = {
    "1": ("TICK",    read_tick),
    "2": ("TRADES",  read_trades),
    "3": ("HISTORY", read_history),
}


def menu() -> None:
    clear_screen()
    banner("?", "MENU")
    print(f"  Chọn loại data muốn xem:\n")
    print(f"    {C.B}1{C.R}  Tick     — bid/ask/spread real-time")
    print(f"    {C.B}2{C.R}  Trades   — vị thế đang mở")
    print(f"    {C.B}3{C.R}  History  — lịch sử 24h")
    print()
    choice = input(f"  {C.CYN}>{C.R} ").strip()
    if choice not in MODES:
        print(f"{C.RED}  Lựa chọn không hợp lệ.{C.R}")
        return

    channel = input(f"  Channel ID (khớp `EA_CHANNEL_ID` trong EA): ").strip()
    if not channel:
        print(f"{C.RED}  Channel ID rỗng.{C.R}")
        return

    mode_name, fn = MODES[choice]
    print(f"\n  {C.DIM}Đang chờ EA ghi data... (đảm bảo EA đang chạy trên MT4/MT5){C.R}")
    try:
        fn(channel)
    except FileNotFoundError:
        print(f"{C.RED}  Không tìm thấy shared memory `Local\\MT_{channel}_{mode_name}`.{C.R}")
        print(f"  Kiểm tra EA đã chạy và `EA_CHANNEL_ID` khớp chưa.")
    except PermissionError as e:
        print(f"{C.RED}  Không có quyền truy cập shared memory: {e}{C.R}")
    except OSError as e:
        print(f"{C.RED}  Lỗi mở shared memory: {e}{C.R}")


if __name__ == "__main__":
    enable_ansi()
    menu()
