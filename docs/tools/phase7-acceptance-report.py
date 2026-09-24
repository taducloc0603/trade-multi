#!/usr/bin/env python3
"""Đối chiếu nghiệm thu Phase 7 Bước C từ log chạy thật (VPS).

Dùng:  python phase7-acceptance-report.py <thu-muc-chua-log>

Đọc:
  {yyyyMMdd}-ctrader.log         — phiên FIX sàn B, có [ORDER][SENT]/[ORDER][RESULT]
  {yyyyMMdd_HHmmss}-trade-log.log — log phiên (chỉ có sau khi bấm Start): [VM] [ROUTER] [CYCLE] [SLOT]

In ra bảng từng mục của checklist Bước C kèm bằng chứng, và liệt kê mục KHÔNG tìm thấy bằng chứng —
những mục đó coi như CHƯA nghiệm thu, không được suy diễn là đạt.
"""
import re
import sys
from collections import Counter
from pathlib import Path

TS = re.compile(r'^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)\]')


def load(folder: Path):
    lines = []
    for path in sorted(folder.glob('*.log')):
        try:
            with path.open(encoding='utf-8', errors='replace') as handle:
                for raw in handle:
                    if TS.match(raw):
                        lines.append((path.name, raw.rstrip('\n')))
        except OSError as exc:
            print(f'  ! không đọc được {path.name}: {exc}')
    return lines


def count(lines, pattern):
    rx = re.compile(pattern)
    return [l for _, l in lines if rx.search(l)]


def show(title, hits, sample=2, need=True):
    mark = 'CO' if hits else ('THIEU' if need else '-')
    print(f'  [{mark:5s}] {title}: {len(hits)}')
    for line in hits[:sample]:
        print(f'          {line[:150]}')


def main(folder: Path):
    lines = load(folder)
    print(f'Đọc {len(lines)} dòng từ {folder}\n')

    print('== Lệnh gửi ra sàn B ==')
    sent = count(lines, r'\[ORDER\]\[SENT\]')
    ok = count(lines, r'\[ORDER\]\[RESULT\].*success=True')
    bad = count(lines, r'\[ORDER\]\[RESULT\].*success=False')
    show('lệnh gửi đi', sent, 3)
    show('khớp', ok, 3)
    show('thất bại/từ chối', bad, 3, need=False)
    kinds = Counter(re.search(r'kind=(\w+)', l).group(1) for l in sent if 'kind=' in l)
    print(f'          phân loại: {dict(kinds)}')

    print('\n== Vòng đời auto ==')
    show('tín hiệu OPEN', count(lines, r'OpenByGap|OPEN_TRIGGER|\[CYCLE\].*OPEN'), 2)
    show('tín hiệu CLOSE', count(lines, r'CloseByGap|CloseByTp|\[CYCLE\].*CLOSE'), 2)
    show('slot mở', count(lines, r'\[SLOT\].*(Live|PendingOpen)'), 2)
    show('history record sàn B', count(lines, r'History record positionId='), 2)

    print('\n== Đường recovery ==')
    show('rollback partial open', count(lines, r'CloseOpenedLegByTimeout'), 2)
    show('đóng leg còn lại sau external close', count(lines, r'CloseRemainingLegAfterExternalClose'), 2, need=False)
    show('nút Đóng per-pair', count(lines, r'ManualPairClose'), 2, need=False)
    show('recovery sau restart', count(lines, r'RECOVERY|recovery'), 2, need=False)

    print('\n== Tuân thủ quy tắc ==')
    show('Rule F — re-resolve ticket trước close', count(lines, r'TryRefreshCloseRows|SelectCloseCandidateForTicket'), 2)
    show('policy chặn (phải KHÔNG có với lệnh hợp lệ)', count(lines, r'MANUAL_WITHOUT_SIGNAL_DISABLED|LATEST_.*_INVALID'), 3, need=False)

    print('\n== Sức khoẻ phiên FIX ==')
    for name, pattern in (('logout', r'logged out'), ('NGẮT MẠCH', r'NG.T M.CH'),
                          ('UnsupportedVersion', r'UnsupportedVersion'), ('digits mismatch', r'DIGITS_MISMATCH')):
        hits = count(lines, pattern)
        print(f'  [{"CANH BAO" if hits else "SACH":8s}] {name}: {len(hits)}')

    print('\n== Vị thế còn lại ==')
    tail = [l for _, l in lines if 'positions_synced' in l][-1:] or ['(không thấy dòng STATS TRADE)']
    print(f'  dòng STATS cuối: {tail[0][:170]}')
    print('\nMục ghi THIEU = chưa có bằng chứng trong log, KHÔNG được coi là đạt.')


if __name__ == '__main__':
    main(Path(sys.argv[1] if len(sys.argv) > 1 else '.'))
