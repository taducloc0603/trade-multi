#!/usr/bin/env python3
"""Test cho fast-leg-report.py trên phiên mẫu ở fixtures/fast-leg/ (số liệu chọn tay để tính trước được).

Chạy: python -I test-fast-leg-report.py
"""

import importlib.util
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURE = os.path.join(HERE, "fixtures", "fast-leg", "20261103_095900")

_spec = importlib.util.spec_from_file_location("fast_leg_report", os.path.join(HERE, "fast-leg-report.py"))
rpt = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = rpt   # @dataclass tra module qua sys.modules
_spec.loader.exec_module(rpt)


def load_fixture():
    sessions = [rpt.resolve_session(FIXTURE)]
    data = rpt.load(sessions)
    return sessions, data, rpt.build_report(sessions, data, 5)


class ParseTests(unittest.TestCase):
    def setUp(self):
        self.sessions, self.data, self.rep = load_fixture()

    def test_resolve_session_from_any_file_of_the_session(self):
        for suffix in ("-gap-tick.log", "-trade-log.log", "-signal-outcome.log", ""):
            s = rpt.resolve_session(FIXTURE + suffix)
            self.assertEqual("20261103_095900", s.name)
            self.assertTrue(s.gap_tick and s.trade_log and s.signal_outcome)

    def test_resolve_session_missing_raises(self):
        with self.assertRaises(SystemExit):
            rpt.resolve_session(os.path.join(HERE, "fixtures", "fast-leg", "19990101_000000"))

    def test_gap_tick_counts_only_lines_with_c_fields(self):
        self.assertEqual(7, self.data.gap_tick_lines)
        self.assertEqual(6, len(self.data.ticks))
        self.assertEqual(6.0, self.data.ticks[1].move)   # |c_move=-6|
        self.assertIsNone(self.data.ticks[3].age)        # Down: c_age=-

    def test_config_parsed_from_trade_log(self):
        self.assertEqual("observe", self.data.config["mode"])
        self.assertEqual(5.0, self.data.config["max_move"])
        self.assertEqual(1000, self.data.config["max_age_ms"])

    def test_reconciliation_trades_match_open_confirmed(self):
        self.assertEqual(2, self.data.open_confirmed_lines)
        self.assertEqual(2, len(self.data.trades))
        self.assertEqual(1, sum(1 for t in self.data.trades if t.signal is not None))

    def test_duplicate_open_confirmed_is_reported_not_double_counted(self):
        with tempfile.TemporaryDirectory() as tmp:
            prefix = os.path.join(tmp, "20261103_100000")
            with open(FIXTURE + "-trade-log.log", encoding="utf-8") as src:
                lines = src.read().splitlines()
            dup = next(l for l in lines if "signalId=s1 " in l and "SIGNAL_OPEN_CONFIRMED" in l)
            with open(prefix + "-trade-log.log", "w", encoding="utf-8") as fh:
                fh.write("\n".join(lines + [dup]) + "\n")
            data = rpt.load([rpt.resolve_session(prefix)])
        self.assertEqual(3, data.open_confirmed_lines)
        self.assertEqual(2, len(data.trades))

    def test_orphan_end_line_ignored(self):
        self.assertNotIn("20261103_095900:orphan", self.data.signals)
        self.assertEqual(3, len(self.data.signals))


class MarkoutTests(unittest.TestCase):
    def setUp(self):
        _, self.data, self.rep = load_fixture()
        self.s = {s.signal_id: s for s in self.data.signals.values()}

    def test_buy_retained_and_decay(self):
        self.assertEqual(8, self.s["s1"].retained(5))
        self.assertEqual(4, self.s["s1"].decay(5))
        self.assertEqual(3, self.s["s1"].retained(10))

    def test_sell_is_sign_flipped(self):
        # track_gap=SELL: gap -15 → thuận 15; future[4] = -3 → còn 3 ⇒ decay 12.
        self.assertEqual(3, self.s["s2"].retained(5))
        self.assertEqual(12, self.s["s2"].decay(5))

    def test_short_future_has_no_markout(self):
        self.assertIsNone(self.s["s3"].retained(5))
        self.assertIsNone(self.s["s3"].decay(5))

    def test_legacy_trade_line_takes_fast_fields_from_signal(self):
        t1 = next(t for t in self.data.trades if t.signal_id == "s1")
        self.assertEqual(1.0, t1.fast_move)
        self.assertEqual("Confirmed", t1.fast_verdict)
        self.assertEqual(-2.0, t1.total_slip)

    def test_trade_line_fast_fields_win_and_are_absolute(self):
        t4 = next(t for t in self.data.trades if t.signal_id == "s4")
        self.assertEqual(7.0, t4.fast_move)
        self.assertEqual(-6.0, t4.total_slip)
        self.assertIsNone(t4.signal)


class ReportTests(unittest.TestCase):
    def setUp(self):
        _, self.data, self.rep = load_fixture()

    def test_bucket_boundaries(self):
        self.assertEqual("0-2", rpt.bucket_of(0.0))
        self.assertEqual("0-2", rpt.bucket_of(1.99))
        self.assertEqual("2-4", rpt.bucket_of(2.0))
        self.assertEqual("4-5", rpt.bucket_of(4.99))
        self.assertEqual(">=5", rpt.bucket_of(5.0))
        self.assertEqual("n/a", rpt.bucket_of(None))

    def test_percentile_nearest_rank(self):
        self.assertIsNone(rpt.percentile([], 50))
        self.assertEqual(2, rpt.percentile([3, 1, 2], 50))
        self.assertEqual(10, rpt.percentile(list(range(1, 11)), 100))
        self.assertEqual(9, rpt.percentile(list(range(1, 11)), 90))

    def test_hour_rows(self):
        rows = {r["hour"]: r for r in self.rep["hours"]}
        self.assertEqual(2, rows["2026-11-03 09:00"]["ticks"])
        self.assertEqual(4, rows["2026-11-03 10:00"]["ticks"])
        self.assertEqual(6, rows["TOTAL"]["ticks"])
        self.assertAlmostEqual(100 / 6, rows["TOTAL"]["down_pct"])
        self.assertEqual(8.0, rows["TOTAL"]["max_abs_move"])   # Down bị loại khỏi |move|/age
        self.assertEqual(1200.0, rows["TOTAL"]["max_age_ms"])

    def _sweep(self, k, age):
        return next(r for r in self.rep["sweep"] if r["max_move"] == k and r["max_age_ms"] == age)

    def test_sweep_matches_evaluator_order(self):
        # k=5, age 1000: Down 1, Stale 1 (1200 ms), TooFast 2 (6, 8) ⇒ 4/6.
        r = self._sweep(5, 1000)
        self.assertAlmostEqual(200 / 6, r["toofast_pct"])
        self.assertAlmostEqual(400 / 6, r["block_open_pct"])
        # Nới max_age lên 1500: tick 1200 ms hết Stale, |move|=3 < 5 ⇒ 3/6.
        self.assertAlmostEqual(50.0, self._sweep(5, 1500)["block_open_pct"])
        # k=3: thêm tick 4.5 ⇒ TooFast 3; tick 3 vẫn Stale ở 1000 ms ⇒ 5/6.
        self.assertAlmostEqual(500 / 6, self._sweep(3, 1000)["block_open_pct"])

    def test_stats_weighted_by_samples(self):
        self.assertAlmostEqual(4.0, self.rep["stats"]["stale_hb_alive_pct"])   # (10×1000 + 2×3000) / 4000
        self.assertEqual(2, self.rep["stats"]["minutes"])
        self.assertEqual(1800, self.rep["stats"]["max_age"])

    def test_edge_by_bucket(self):
        trades = {r["bucket"]: r for r in self.rep["edge_trades"]}
        self.assertEqual(-2.0, trades["0-2"]["mean_total_slip"])
        self.assertEqual(4.0, trades["0-2"]["mean_decay_5"])
        self.assertEqual(-6.0, trades[">=5"]["mean_total_slip"])
        self.assertEqual(0, trades[">=5"]["markout_n"])
        signals = {r["bucket"]: r for r in self.rep["edge_signals"]}   # chỉ OPEN
        self.assertEqual({"0-2", ">=5"}, set(signals))
        self.assertEqual(12.0, signals[">=5"]["mean_decay_5"])

    def test_simulation(self):
        sim = {r["max_move"]: r for r in self.rep["simulation"]}
        self.assertEqual(2, sim[5]["open_signals"])          # CLOSE s3 không tính
        self.assertEqual(1, sim[5]["blocked"])
        self.assertEqual(4.0, sim[5]["kept_mean_decay_5"])
        self.assertEqual(12.0, sim[5]["blocked_mean_decay_5"])
        self.assertEqual(-2.0, sim[5]["kept_mean_total_slip"])
        self.assertEqual(-6.0, sim[5]["blocked_mean_total_slip"])
        self.assertEqual(0, sim[7]["blocked"])
        self.assertEqual(-4.0, sim[8]["kept_mean_total_slip"])

    def test_defer_release_abandon_router_alert_counts(self):
        self.assertEqual({"OPEN": 2, "CLOSE": 1}, dict(self.data.defer))
        self.assertEqual({"OPEN": 1}, dict(self.data.release))
        self.assertEqual([150], self.data.release_ms["OPEN"])
        self.assertEqual({("OPEN", "defer_timeout"): 1, ("CLOSE", "cycle_reset"): 1}, dict(self.data.abandon))
        self.assertEqual({"FAST_LEG_TOO_FAST": 1}, dict(self.data.router_blocks))
        self.assertEqual({"FAST_LEG_TOO_FAST": 1}, dict(self.data.lifecycle_blocks))   # QUOTA_* bị bỏ qua
        self.assertEqual({"FAST_LEG_DOWN": 1, "FAST_LEG_RECOVERED": 1}, dict(self.data.alerts))


class EdgeCaseTests(unittest.TestCase):
    def test_midnight_rollover(self):
        with tempfile.TemporaryDirectory() as tmp:
            prefix = os.path.join(tmp, "20261103_235900")
            with open(prefix + "-gap-tick.log", "w", encoding="utf-8") as fh:
                fh.write("[23:59:00.000] Date: 2026-11-03\n")
                for ts in ("23:59:59.950", "00:00:00.000", "00:00:00.050"):
                    fh.write(f"[{ts}] [GAP_TICK] gap_buy=1 c_age=10 c_move=1 c_verdict=Confirmed point=100\n")
            data = rpt.load([rpt.resolve_session(prefix)])
        self.assertEqual(["2026-11-03", "2026-11-04", "2026-11-04"],
                         [t.when.date().isoformat() for t in data.ticks])

    def test_session_without_fast_leg_still_reports(self):
        with tempfile.TemporaryDirectory() as tmp:
            prefix = os.path.join(tmp, "20261009_155152")
            with open(prefix + "-gap-tick.log", "w", encoding="utf-8") as fh:
                fh.write("[15:51:00.000] Date: 2026-10-09\n[15:51:01.000] [GAP_TICK] gap_buy=1 gap_sell=2 point=100\n")
            sessions = [rpt.resolve_session(prefix)]
            data = rpt.load(sessions)
            rep = rpt.build_report(sessions, data, 5)
            text = rpt.render_summary(sessions, data, rep, 5)
        self.assertEqual(0, len(data.ticks))
        self.assertEqual([], rep["hours"])
        self.assertIn("không có dòng `[FAST_LEG][INFO] config=`", text)

    def test_main_writes_all_outputs(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "out")
            self.assertEqual(0, rpt.main([FIXTURE, "--out", out]))
            for name in ("summary.md", "ticks-by-hour.csv", "threshold-sweep.csv", "signals.csv", "trades.csv",
                         "simulation.csv"):
                self.assertTrue(os.path.getsize(os.path.join(out, name)) > 0, name)
            with open(os.path.join(out, "summary.md"), encoding="utf-8") as fh:
                summary = fh.read()
        self.assertIn("cặp trong báo cáo: 2 (KHỚP)", summary)
        self.assertNotIn("mode=filter", summary)   # fixture chạy observe ⇒ không có cảnh báo cắt cụt

    def test_filter_mode_warns_about_censoring(self):
        _, data, rep = load_fixture()
        data.config_seen = ["filter|quiet|move5|win200|age1000"]
        self.assertIn("mode=filter", rpt.render_summary([], data, rep, 5))


if __name__ == "__main__":
    unittest.main(verbosity=1)
