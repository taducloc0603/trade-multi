#!/usr/bin/env python3
"""Báo cáo ngưỡng sàn C (Fast Leg) — Phase 7, công cụ OFFLINE, chỉ đọc log.

Chạy (chỉ stdlib, nên chạy với -I để không nạp module lạ từ thư mục hiện tại):

    python -I fast-leg-report.py <phiên> [<phiên> ...] [--out DIR] [--markout-ticks N]

<phiên> là đường dẫn tới MỘT file bất kỳ của phiên, hoặc phần tiền tố chung, ví dụ
    %USERPROFILE%\\Desktop\\trade-log\\20261103_122716
Tool tự tìm `-gap-tick.log`, `-trade-log.log`, `-signal-outcome.log` của phiên; thiếu file nào thì bỏ phần đó.
Nhiều phiên thì gộp số liệu.

Đầu ra (thư mục --out, mặc định ./fast-leg-report-<phiên đầu>):
    summary.md           tóm tắt 5 mục bên dưới
    ticks-by-hour.csv    (1) phân bố |move| / tuổi tick C theo giờ, tỉ lệ verdict
    threshold-sweep.csv  (2) % thời gian TooFast / Stale / chặn Open ở các ngưỡng max_move 3–10 × max_age
    signals.csv          (3) mọi signal OPEN/CLOSE trong -signal-outcome: verdict C + markout future_gaps
    trades.csv           (3) mỗi cặp đã mở (SIGNAL_OPEN_CONFIRMED): verdict C + slippage A/B + markout
    simulation.csv       (4) mô phỏng max_move = k: số signal bị chặn và edge còn lại

Định nghĩa (đọc kỹ trước khi kết luận):
  * |move| = biên độ mid sàn C trong cửa sổ windowMs, tính bằng point (cột c_move / fast_move_pts, lấy trị tuyệt đối).
  * "Thuận" của gap: nhánh GapBuy trigger khi gap >= ngưỡng (gap càng lớn càng có lợi), nhánh GapSell trigger khi
    gap <= -ngưỡng (gap càng âm càng có lợi). signed(g) = g nếu track_gap=BUY, -g nếu SELL.
  * retained@N = signed(future_gaps[N-1]): phần gap CÒN LẠI sau N tick (~N×50 ms) kể từ tick signal.
    decay@N = signed(gap_at_signal) - retained@N. decay > 0 = gap khép lại trước khi kịp khớp (xấu).
    Đây là proxy markout của slide 10b (Long): nếu sàn C đúng, nhóm |move| lớn phải có decay lớn hơn.
  * Slippage A/B lấy nguyên từ SIGNAL_OPEN_CONFIRMED: > 0 = khớp CÓ LỢI hơn giá kỳ vọng
    (DashboardViewModel: BUY = Expected Ask - Fill Ask, SELL = Fill Bid - Expected Bid, × point).
  * Ở mode=filter, signal có |move| >= max_move đã bị HOÃN trước khi phát ⇒ dữ liệu bị cắt cụt tại ngưỡng đang
    dùng. Mô phỏng (4) chỉ không lệch khi phiên chạy mode=observe.
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import math
import os
import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field

KV_RE = re.compile(r'(\w+)=("([^"]*)"|\S+)')
TIME_RE = re.compile(r'^\[(\d{2}):(\d{2}):(\d{2})\.(\d{3})\]')
FULL_TS_RE = re.compile(r'^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]')
MOVE_BUCKETS = [("0-2", 0.0, 2.0), ("2-4", 2.0, 4.0), ("4-5", 4.0, 5.0), (">=5", 5.0, math.inf)]
SWEEP_MOVES = list(range(3, 11))
SWEEP_AGES = [1000, 1500, 2000, 2500, 3000]
DEFAULT_CONFIG = {"mode": "?", "max_move": 5.0, "window_ms": 200, "max_age_ms": 1000}
VERDICTS = ("Confirmed", "TooFast", "Stale", "Down")


# ---------------------------------------------------------------- tiện ích

def parse_kv(line: str) -> dict[str, str | None]:
    out: dict[str, str | None] = {}
    for m in KV_RE.finditer(line):
        value = m.group(3) if m.group(3) is not None else m.group(2)
        out[m.group(1)] = None if value == "-" else value
    return out


def to_float(v: str | None) -> float | None:
    if v is None:
        return None
    try:
        f = float(v)
    except ValueError:
        return None
    return f if math.isfinite(f) else None


def to_int(v: str | None) -> int | None:
    f = to_float(v)
    return None if f is None else int(round(f))


def percentile(values: list[float], p: float) -> float | None:
    """Nearest-rank percentile; None khi rỗng."""
    if not values:
        return None
    s = sorted(values)
    k = max(0, min(len(s) - 1, math.ceil(p / 100.0 * len(s)) - 1))
    return s[k]


def mean(values: list[float]) -> float | None:
    return sum(values) / len(values) if values else None


def pct(part: int, whole: int) -> float | None:
    return 100.0 * part / whole if whole else None


def fmt(v, digits: int = 1) -> str:
    if v is None:
        return "-"
    if isinstance(v, float):
        return f"{v:.{digits}f}"
    return str(v)


def bucket_of(move: float | None) -> str:
    if move is None:
        return "n/a"
    for name, lo, hi in MOVE_BUCKETS:
        if lo <= move < hi:
            return name
    return "n/a"


def signed_gap(gap: int | None, track_gap: str | None) -> int | None:
    if gap is None:
        return None
    return gap if (track_gap or "BUY").upper() == "BUY" else -gap


def parse_gap_list(v: str | None) -> list[int | None]:
    if not v:
        return []
    return [to_int(x) if x not in ("", "-", "null") else None for x in v.split("|")]


# ---------------------------------------------------------------- mô hình

@dataclass
class CTick:
    when: dt.datetime
    move: float | None   # |c_move|
    age: int | None
    verdict: str


@dataclass
class Signal:
    signal_id: str
    session: str
    when: str
    tag: str              # EXEC | BLOCKED
    action: str
    side: str
    trigger_type: str | None
    track_gap: str | None
    gap_at_signal: int | None
    block_reason: str | None
    pair_id: str | None
    fast_verdict: str | None
    fast_move: float | None
    fast_age: int | None
    fast_deferred_ms: int | None
    future: list[int | None] = field(default_factory=list)
    status: str | None = None

    def retained(self, n: int) -> int | None:
        if len(self.future) < n:
            return None
        return signed_gap(self.future[n - 1], self.track_gap)

    def decay(self, n: int) -> int | None:
        r = self.retained(n)
        base = signed_gap(self.gap_at_signal, self.track_gap)
        return None if r is None or base is None else base - r


@dataclass
class Trade:
    signal_id: str
    session: str
    when: str
    pair_id: str | None
    side: str | None
    a_slip: float | None
    b_slip: float | None
    fast_verdict: str | None
    fast_move: float | None
    fast_age: int | None
    fast_deferred_ms: int | None
    signal: Signal | None = None

    @property
    def total_slip(self) -> float | None:
        if self.a_slip is None or self.b_slip is None:
            return None
        return self.a_slip + self.b_slip


@dataclass
class Session:
    name: str
    gap_tick: str | None
    trade_log: str | None
    signal_outcome: str | None


@dataclass
class Data:
    ticks: list[CTick] = field(default_factory=list)
    gap_tick_lines: int = 0
    signals: dict[str, Signal] = field(default_factory=dict)
    trades: list[Trade] = field(default_factory=list)
    open_confirmed_lines: int = 0
    config: dict = field(default_factory=lambda: dict(DEFAULT_CONFIG))
    config_seen: list[str] = field(default_factory=list)
    stats_rows: list[dict[str, float]] = field(default_factory=list)
    defer: Counter = field(default_factory=Counter)
    release: Counter = field(default_factory=Counter)
    release_ms: dict[str, list[int]] = field(default_factory=lambda: defaultdict(list))
    abandon: Counter = field(default_factory=Counter)
    router_blocks: Counter = field(default_factory=Counter)
    lifecycle_blocks: Counter = field(default_factory=Counter)
    alerts: Counter = field(default_factory=Counter)


# ---------------------------------------------------------------- tìm file

SUFFIXES = {"gap_tick": "-gap-tick.log", "trade_log": "-trade-log.log", "signal_outcome": "-signal-outcome.log"}


def resolve_session(arg: str) -> Session:
    base = arg
    for suffix in list(SUFFIXES.values()) + ["-gap-stability-raw.log"]:
        if base.endswith(suffix):
            base = base[: -len(suffix)]
            break
    paths = {k: (base + s if os.path.isfile(base + s) else None) for k, s in SUFFIXES.items()}
    if not any(paths.values()):
        raise SystemExit(f"Không tìm thấy file log nào cho phiên '{arg}' (đã thử {base}-*.log)")
    return Session(os.path.basename(base), paths["gap_tick"], paths["trade_log"], paths["signal_outcome"])


def read_lines(path: str):
    with open(path, encoding="utf-8-sig", errors="replace") as fh:
        for line in fh:
            yield line.rstrip("\r\n")


# ---------------------------------------------------------------- parse

def parse_gap_tick(path: str, data: Data) -> None:
    day: dt.date | None = None
    last: dt.datetime | None = None
    for line in read_lines(path):
        if day is None:
            m = re.search(r"Date: (\d{4}-\d{2}-\d{2})", line)
            if m:
                day = dt.date.fromisoformat(m.group(1))
            continue
        if "[GAP_TICK] " not in line:
            continue
        data.gap_tick_lines += 1
        if " c_verdict=" not in line:
            continue
        m = TIME_RE.match(line)
        if not m:
            continue
        when = dt.datetime.combine(day, dt.time(int(m[1]), int(m[2]), int(m[3]), int(m[4]) * 1000))
        # File chỉ ghi giờ: qua nửa đêm thì cộng ngày (lùi > 12 h mới coi là sang ngày, tránh nhiễu thứ tự nhỏ).
        while last is not None and when < last - dt.timedelta(hours=12):
            when += dt.timedelta(days=1)
        last = when
        kv = parse_kv(line)
        move = to_float(kv.get("c_move"))
        data.ticks.append(CTick(when, abs(move) if move is not None else None, to_int(kv.get("c_age")),
                                kv.get("c_verdict") or "?"))


def parse_config_token(text: str, data: Data) -> None:
    data.config_seen.append(text)
    cfg = dict(DEFAULT_CONFIG)
    for tok in text.split("|"):
        if tok in ("observe", "filter"):
            cfg["mode"] = tok
        elif m := re.fullmatch(r"move([\d.]+)", tok):
            cfg["max_move"] = float(m[1])
        elif m := re.fullmatch(r"win(\d+)", tok):
            cfg["window_ms"] = int(m[1])
        elif m := re.fullmatch(r"age(\d+)", tok):
            cfg["max_age_ms"] = int(m[1])
    data.config = cfg


def parse_trade_log(path: str, session: str, data: Data) -> None:
    seen_trades = {t.signal_id for t in data.trades}
    for line in read_lines(path):
        if "[FAST_LEG]" in line:
            kv = parse_kv(line)
            if "[FAST_LEG][INFO] config=" in line and kv.get("config"):
                parse_config_token(kv["config"], data)
            elif "[FAST_LEG][STATS]" in line:
                data.stats_rows.append({k: to_float(v) for k, v in kv.items() if to_float(v) is not None})
            elif "[FAST_LEG][DEFER]" in line:
                data.defer[kv.get("action") or "?"] += 1
            elif "[FAST_LEG][RELEASE]" in line:
                action = kv.get("action") or "?"
                data.release[action] += 1
                ms = to_int(kv.get("deferred_ms"))
                if ms is not None:
                    data.release_ms[action].append(ms)
            elif "[FAST_LEG][ABANDON]" in line:
                data.abandon[(kv.get("action") or "?", kv.get("reason") or "?")] += 1
            for code in ("FAST_LEG_DOWN", "FAST_LEG_RECOVERED", "FAST_LEG_NOT_READY_AT_START"):
                if f"] {code}" in line:
                    data.alerts[code] += 1
            continue
        if "Open blocked by fast leg" in line:
            data.router_blocks[parse_kv(line).get("code") or "?"] += 1
            continue
        if "[SIGNAL_OPEN_BLOCKED]" in line or "[SIGNAL_OPEN_CANCELLED]" in line:
            code = parse_kv(line).get("reasonCode")
            if code and code.startswith("FAST_LEG_"):
                data.lifecycle_blocks[code] += 1
            continue
        if "[SIGNAL_OPEN_CONFIRMED]" in line:
            data.open_confirmed_lines += 1
            kv = parse_kv(line)
            sid = kv.get("signalId")
            if not sid or sid in seen_trades:
                continue
            seen_trades.add(sid)
            m = FULL_TS_RE.match(line)
            data.trades.append(Trade(
                signal_id=sid, session=session, when=m[1] if m else "-",
                pair_id=kv.get("pairId"), side=kv.get("side"),
                a_slip=to_float(kv.get("aSlippagePts")), b_slip=to_float(kv.get("bSlippagePts")),
                fast_verdict=kv.get("fastVerdict"), fast_move=_abs(to_float(kv.get("fastMovePts"))),
                fast_age=to_int(kv.get("fastAgeMs")), fast_deferred_ms=to_int(kv.get("fastDeferredMs"))))


def _abs(v: float | None) -> float | None:
    return None if v is None else abs(v)


def parse_signal_outcome(path: str, session: str, data: Data) -> None:
    for line in read_lines(path):
        m = re.search(r"\[SIGNAL_OUTCOME\]\[(SIGNAL|END)\]\[(\w+)\]", line)
        if not m:
            continue
        kv = parse_kv(line)
        sid = kv.get("signal_id")
        if not sid:
            continue
        key = f"{session}:{sid}"
        if m[1] == "SIGNAL":
            ts = FULL_TS_RE.match(line)
            data.signals[key] = Signal(
                signal_id=sid, session=session, when=ts[1] if ts else "-", tag=m[2],
                action=(kv.get("action") or "?").upper(), side=(kv.get("side") or "?").upper(),
                trigger_type=kv.get("trigger_type"), track_gap=kv.get("track_gap"),
                gap_at_signal=to_int(kv.get("gap_at_signal")), block_reason=kv.get("block_reason"),
                pair_id=kv.get("pair_id"), fast_verdict=kv.get("fast_verdict"),
                fast_move=_abs(to_float(kv.get("fast_move_pts"))), fast_age=to_int(kv.get("fast_age_ms")),
                fast_deferred_ms=to_int(kv.get("fast_deferred_ms")))
        else:
            sig = data.signals.get(key)
            if sig is None:
                continue
            sig.future = parse_gap_list(kv.get("future_gaps"))
            sig.status = kv.get("status")


def load(sessions: list[Session]) -> Data:
    data = Data()
    for s in sessions:
        if s.gap_tick:
            parse_gap_tick(s.gap_tick, data)
        if s.signal_outcome:
            parse_signal_outcome(s.signal_outcome, s.name, data)
        if s.trade_log:
            parse_trade_log(s.trade_log, s.name, data)
    for t in data.trades:
        t.signal = data.signals.get(f"{t.session}:{t.signal_id}")
        if t.signal is not None:
            # Trade-log cũ (trước Phase 3) không có fast*: lấy từ dòng [SIGNAL] của cùng signal.
            t.fast_verdict = t.fast_verdict or t.signal.fast_verdict
            t.fast_move = t.fast_move if t.fast_move is not None else t.signal.fast_move
            t.fast_age = t.fast_age if t.fast_age is not None else t.signal.fast_age
            if t.fast_deferred_ms is None:
                t.fast_deferred_ms = t.signal.fast_deferred_ms
    return data


# ---------------------------------------------------------------- tính toán

def hour_rows(ticks: list[CTick]) -> list[dict]:
    groups: dict[str, list[CTick]] = defaultdict(list)
    for t in ticks:
        groups[t.when.strftime("%Y-%m-%d %H:00")].append(t)
    rows = [_tick_row(k, v) for k, v in sorted(groups.items())]
    if ticks:
        rows.append(_tick_row("TOTAL", ticks))
    return rows


def _tick_row(label: str, ticks: list[CTick]) -> dict:
    n = len(ticks)
    counts = Counter(t.verdict for t in ticks)
    moves = [t.move for t in ticks if t.move is not None and t.verdict != "Down"]
    ages = [float(t.age) for t in ticks if t.age is not None and t.verdict != "Down"]
    row = {"hour": label, "ticks": n}
    for v in VERDICTS:
        row[f"{v.lower()}_pct"] = pct(counts[v], n)
    row.update({
        "p50_abs_move": percentile(moves, 50), "p90_abs_move": percentile(moves, 90),
        "max_abs_move": max(moves) if moves else None,
        "p50_age_ms": percentile(ages, 50), "p90_age_ms": percentile(ages, 90),
        "max_age_ms": max(ages) if ages else None,
    })
    return row


def sweep_rows(ticks: list[CTick], current_age: int) -> list[dict]:
    ages = sorted(set(SWEEP_AGES + [current_age]))
    n = len(ticks)
    rows = []
    for age_limit in ages:
        down = sum(1 for t in ticks if t.verdict == "Down")
        stale = sum(1 for t in ticks if t.verdict != "Down" and (t.age is None or t.age > age_limit))
        for k in SWEEP_MOVES:
            fast = sum(1 for t in ticks if t.verdict != "Down" and t.age is not None and t.age <= age_limit
                       and t.move is not None and t.move >= k)
            rows.append({"max_move": k, "max_age_ms": age_limit, "toofast_pct": pct(fast, n),
                         "stale_pct": pct(stale, n), "down_pct": pct(down, n),
                         "block_open_pct": pct(fast + stale + down, n)})
    return rows


def edge_rows(items, move_of, n: int, slip_of=None) -> list[dict]:
    groups: dict[str, list] = defaultdict(list)
    for it in items:
        groups[bucket_of(move_of(it))].append(it)
    rows = []
    for name in [b[0] for b in MOVE_BUCKETS] + ["n/a"]:
        g = groups.get(name, [])
        if not g:
            continue
        sig = [(it.signal if slip_of else it) for it in g]
        retained = [float(s.retained(n)) for s in sig if s is not None and s.retained(n) is not None]
        decay = [float(s.decay(n)) for s in sig if s is not None and s.decay(n) is not None]
        row = {"bucket": name, "n": len(g), "markout_n": len(retained),
               f"mean_retained_{n}": mean(retained), f"mean_decay_{n}": mean(decay)}
        if slip_of:
            slips = [slip_of(it) for it in g if slip_of(it) is not None]
            row["mean_total_slip"] = mean(slips)
        rows.append(row)
    return rows


def simulation_rows(signals: list[Signal], trades: list[Trade], n: int) -> list[dict]:
    opens = [s for s in signals if s.action == "OPEN" and s.fast_move is not None]
    tr = [t for t in trades if t.fast_move is not None]
    rows = []
    for k in SWEEP_MOVES:
        kept = [s for s in opens if s.fast_move < k]
        blocked = [s for s in opens if s.fast_move >= k]
        kept_t = [t.total_slip for t in tr if t.fast_move < k and t.total_slip is not None]
        blocked_t = [t.total_slip for t in tr if t.fast_move >= k and t.total_slip is not None]
        rows.append({
            "max_move": k, "open_signals": len(opens), "blocked": len(blocked),
            "blocked_pct": pct(len(blocked), len(opens)),
            f"kept_mean_retained_{n}": mean([float(s.retained(n)) for s in kept if s.retained(n) is not None]),
            f"blocked_mean_retained_{n}": mean([float(s.retained(n)) for s in blocked if s.retained(n) is not None]),
            f"kept_mean_decay_{n}": mean([float(s.decay(n)) for s in kept if s.decay(n) is not None]),
            f"blocked_mean_decay_{n}": mean([float(s.decay(n)) for s in blocked if s.decay(n) is not None]),
            "trades": len(tr), "kept_trades": len(kept_t), "kept_mean_total_slip": mean(kept_t),
            "blocked_trades": len(blocked_t), "blocked_mean_total_slip": mean(blocked_t),
        })
    return rows


def stats_summary(rows: list[dict[str, float]]) -> dict:
    if not rows:
        return {}
    weight = [r.get("samples", 1.0) for r in rows]
    total = sum(weight) or 1.0

    def wavg(key):
        vals = [(r[key], w) for r, w in zip(rows, weight) if key in r]
        return sum(v * w for v, w in vals) / sum(w for _, w in vals) if vals else None

    return {"minutes": len(rows), "samples": int(total), "ticks": int(sum(r.get("ticks", 0) for r in rows)),
            "stale_hb_alive_pct": wavg("stale_hb_alive_pct"), "stale_pct": wavg("stale_pct"),
            "down_pct": wavg("down_pct"), "toofast_pct": wavg("toofast_pct"),
            "max_age": max((r["max_age"] for r in rows if "max_age" in r), default=None)}


# ---------------------------------------------------------------- xuất

def write_csv(path: str, rows: list[dict]) -> None:
    with open(path, "w", newline="", encoding="utf-8") as fh:
        if not rows:
            fh.write("")
            return
        keys: list[str] = []
        for r in rows:
            for k in r:
                if k not in keys:
                    keys.append(k)
        w = csv.DictWriter(fh, fieldnames=keys)
        w.writeheader()
        for r in rows:
            w.writerow({k: (f"{v:.3f}" if isinstance(v, float) else ("" if v is None else v)) for k, v in r.items()})


def md_table(rows: list[dict], cols: list[str], digits: int = 1) -> str:
    if not rows:
        return "_(không có dữ liệu)_\n"
    out = ["| " + " | ".join(cols) + " |", "|" + "---|" * len(cols)]
    for r in rows:
        out.append("| " + " | ".join(fmt(r.get(c), digits) for c in cols) + " |")
    return "\n".join(out) + "\n"


def signal_row(s: Signal, n: int) -> dict:
    return {"session": s.session, "when": s.when, "signal_id": s.signal_id, "tag": s.tag, "action": s.action,
            "side": s.side, "trigger_type": s.trigger_type, "block_reason": s.block_reason, "pair_id": s.pair_id,
            "track_gap": s.track_gap, "gap_at_signal": s.gap_at_signal, "fast_verdict": s.fast_verdict,
            "fast_abs_move_pts": s.fast_move, "fast_age_ms": s.fast_age, "fast_deferred_ms": s.fast_deferred_ms,
            "move_bucket": bucket_of(s.fast_move), "future_ticks": len(s.future), "status": s.status,
            f"retained_{n}": s.retained(n), f"decay_{n}": s.decay(n),
            f"retained_{2 * n}": s.retained(2 * n), f"decay_{2 * n}": s.decay(2 * n)}


def trade_row(t: Trade, n: int) -> dict:
    s = t.signal
    return {"session": t.session, "when": t.when, "signal_id": t.signal_id, "pair_id": t.pair_id, "side": t.side,
            "fast_verdict": t.fast_verdict, "fast_abs_move_pts": t.fast_move, "fast_age_ms": t.fast_age,
            "fast_deferred_ms": t.fast_deferred_ms, "move_bucket": bucket_of(t.fast_move),
            "a_slip_pts": t.a_slip, "b_slip_pts": t.b_slip, "total_slip_pts": t.total_slip,
            "gap_at_signal": s.gap_at_signal if s else None,
            f"retained_{n}": s.retained(n) if s else None, f"decay_{n}": s.decay(n) if s else None}


def build_report(sessions: list[Session], data: Data, n: int) -> dict:
    signals = sorted(data.signals.values(), key=lambda s: (s.session, s.when))
    return {
        "hours": hour_rows(data.ticks),
        "sweep": sweep_rows(data.ticks, int(data.config["max_age_ms"])),
        "signals": [signal_row(s, n) for s in signals],
        "trades": [trade_row(t, n) for t in data.trades],
        "edge_trades": edge_rows(data.trades, lambda t: t.fast_move, n, slip_of=lambda t: t.total_slip),
        "edge_signals": edge_rows([s for s in signals if s.action == "OPEN"], lambda s: s.fast_move, n),
        "simulation": simulation_rows(signals, data.trades, n),
        "stats": stats_summary(data.stats_rows),
    }


def render_summary(sessions: list[Session], data: Data, rep: dict, n: int) -> str:
    cfg = data.config
    L: list[str] = []
    L.append("# Báo cáo ngưỡng sàn C (Fast Leg)\n")
    L.append(f"Phiên: {', '.join(s.name for s in sessions)}  ")
    distinct = list(dict.fromkeys(data.config_seen))
    if not distinct:
        L.append("Cấu hình: không có dòng `[FAST_LEG][INFO] config=` — dùng mặc định cho mục 2.  ")
    for text in distinct:
        L.append(f"Cấu hình đọc từ log: `{text}`  ")
    if len(distinct) > 1:
        L.append(f"⚠ Các phiên dùng {len(distinct)} cấu hình khác nhau; mục 2 lấy max_age của cấu hình cuối.  ")
    L.append(f"Markout: N = {n} tick (~{n * 50} ms) và {2 * n} tick (~{2 * n * 50} ms).\n")
    if any("|filter|" in f"|{t}|" for t in distinct):
        L.append("> ⚠ Phiên chạy **mode=filter**: signal có |move| >= max_move đã bị hoãn trước khi phát, nên mục 3–4 bị "
                 "cắt cụt tại ngưỡng đang dùng. Muốn chọn ngưỡng mới hãy chạy `observe` rồi chạy lại báo cáo.\n")

    trades_matched = sum(1 for t in data.trades if t.signal is not None)
    L.append("## Kiểm tra đối chiếu\n")
    L.append(f"- Dòng `[GAP_TICK]`: {data.gap_tick_lines}; có `c_*`: {len(data.ticks)}")
    L.append(f"- Dòng `SIGNAL_OPEN_CONFIRMED`: {data.open_confirmed_lines}; cặp trong báo cáo: {len(data.trades)} "
             f"({'KHỚP' if data.open_confirmed_lines == len(data.trades) else 'LỆCH — có dòng trùng signalId hoặc thiếu signalId'}); "
             f"ghép được markout: {trades_matched}")
    L.append(f"- Signal trong `-signal-outcome`: {len(rep['signals'])} "
             f"(OPEN {sum(1 for s in data.signals.values() if s.action == 'OPEN')}, "
             f"CLOSE {sum(1 for s in data.signals.values() if s.action == 'CLOSE')}); "
             f"có verdict C: {sum(1 for s in data.signals.values() if s.fast_verdict)}\n")

    L.append("## 1. Phân bố |move| và tuổi tick sàn C theo giờ\n")
    L.append(md_table(rep["hours"], ["hour", "ticks", "confirmed_pct", "toofast_pct", "stale_pct", "down_pct",
                                     "p50_abs_move", "p90_abs_move", "max_abs_move", "p50_age_ms", "p90_age_ms",
                                     "max_age_ms"]))
    st = rep["stats"]
    if st:
        L.append(f"\nTừ {st['minutes']} dòng `[FAST_LEG][STATS]` ({st['samples']} mẫu, {st['ticks']} tick): "
                 f"**Stale khi heartbeat còn sống = {fmt(st['stale_hb_alive_pct'])} %**, Stale {fmt(st['stale_pct'])} %, "
                 f"Down {fmt(st['down_pct'])} %, TooFast {fmt(st['toofast_pct'])} %, max_age {fmt(st['max_age'], 0)} ms. "
                 "Stale-khi-sống cao nghĩa là feed C thưa chứ không phải terminal hỏng — dữ liệu để quyết lại D9. "
                 "STATS đếm cả lúc CHƯA Start còn gap-tick chỉ ghi khi đang Start, nên hai nguồn có thể lệch nhau.\n")
    else:
        L.append("\nKhông có dòng `[FAST_LEG][STATS]` (sàn C tắt hoặc thiếu trade-log).\n")

    L.append(f"## 2. % thời gian bị chặn Open theo ngưỡng (max_age đang dùng = {cfg['max_age_ms']} ms)\n")
    cur = [r for r in rep["sweep"] if r["max_age_ms"] == cfg["max_age_ms"]]
    L.append(md_table(cur, ["max_move", "toofast_pct", "stale_pct", "down_pct", "block_open_pct"]))
    L.append("\nMa trận `block_open_pct` (hàng = max_move, cột = max_age ms):\n")
    ages = sorted({r["max_age_ms"] for r in rep["sweep"]})
    grid = []
    for k in SWEEP_MOVES:
        row = {"max_move": k}
        for a in ages:
            row[str(a)] = next((r["block_open_pct"] for r in rep["sweep"]
                                if r["max_move"] == k and r["max_age_ms"] == a), None)
        grid.append(row)
    L.append(md_table(grid, ["max_move"] + [str(a) for a in ages]))
    L.append("\n`c_move` trong gap-tick được tính với window đang dùng; muốn thử window khác phải chạy phiên mới.\n")

    L.append("## 3. Edge theo nhóm |move| (slide 10b)\n")
    L.append("**Cặp đã mở** (slippage A+B, > 0 là có lợi):\n")
    L.append(md_table(rep["edge_trades"], ["bucket", "n", "mean_total_slip", "markout_n", f"mean_retained_{n}",
                                           f"mean_decay_{n}"], 2))
    L.append("\n**Mọi signal OPEN** (kể cả bị chặn vì lý do khác — vẫn có future_gaps):\n")
    L.append(md_table(rep["edge_signals"], ["bucket", "n", "markout_n", f"mean_retained_{n}", f"mean_decay_{n}"], 2))
    L.append("\nKỳ vọng nếu sàn C có ích: nhóm |move| lớn có decay lớn hơn và slippage tệ hơn. Mẫu < 30 mỗi nhóm "
             "thì chưa kết luận được.\n")

    L.append("## 4. Mô phỏng max_move = k\n")
    L.append(md_table(rep["simulation"], ["max_move", "open_signals", "blocked", "blocked_pct",
                                          f"kept_mean_decay_{n}", f"blocked_mean_decay_{n}", "kept_trades",
                                          "kept_mean_total_slip", "blocked_trades", "blocked_mean_total_slip"], 2))

    L.append("\n## 5. Hoãn / bỏ / chặn do sàn C\n")
    rows = []
    for action in sorted(set(data.defer) | set(data.release) | {a for a, _ in data.abandon}):
        ms = data.release_ms.get(action, [])
        rows.append({"action": action, "DEFER": data.defer[action], "RELEASE": data.release[action],
                     "ABANDON": sum(v for (a, _), v in data.abandon.items() if a == action),
                     "p50_deferred_ms": percentile([float(x) for x in ms], 50),
                     "max_deferred_ms": float(max(ms)) if ms else None})
    L.append(md_table(rows, ["action", "DEFER", "RELEASE", "ABANDON", "p50_deferred_ms", "max_deferred_ms"], 0))
    if data.abandon:
        L.append("\nABANDON theo lý do:\n")
        L.append(md_table([{"action": a, "reason": r, "count": c} for (a, r), c in sorted(data.abandon.items())],
                          ["action", "reason", "count"]))
    L.append(f"\nRouter chặn Open (`[ROUTER][WARN] Open blocked by fast leg`): "
             f"{dict(data.router_blocks) or 0}; lifecycle `reasonCode=FAST_LEG_*`: {dict(data.lifecycle_blocks) or 0}.  ")
    L.append(f"Cảnh báo sức khoẻ: {dict(data.alerts) or 0}.\n")
    return "\n".join(L)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Báo cáo ngưỡng sàn C (Fast Leg) từ log một hoặc nhiều phiên.")
    ap.add_argument("sessions", nargs="+", help="tiền tố phiên hoặc một file bất kỳ của phiên")
    ap.add_argument("--out", help="thư mục xuất (mặc định ./fast-leg-report-<phiên đầu>)")
    ap.add_argument("--markout-ticks", type=int, default=5, help="N tick cho retained/decay (mặc định 5 ≈ 250 ms)")
    args = ap.parse_args(argv)
    if args.markout_ticks < 1:
        ap.error("--markout-ticks phải >= 1")

    sessions = [resolve_session(s) for s in args.sessions]
    data = load(sessions)
    n = args.markout_ticks
    rep = build_report(sessions, data, n)
    out = args.out or os.path.join(os.getcwd(), f"fast-leg-report-{sessions[0].name}")
    os.makedirs(out, exist_ok=True)
    write_csv(os.path.join(out, "ticks-by-hour.csv"), rep["hours"])
    write_csv(os.path.join(out, "threshold-sweep.csv"), rep["sweep"])
    write_csv(os.path.join(out, "signals.csv"), rep["signals"])
    write_csv(os.path.join(out, "trades.csv"), rep["trades"])
    write_csv(os.path.join(out, "simulation.csv"), rep["simulation"])
    summary = render_summary(sessions, data, rep, n)
    with open(os.path.join(out, "summary.md"), "w", encoding="utf-8") as fh:
        fh.write(summary)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print(summary)
    print(f"\n→ Đã ghi: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
