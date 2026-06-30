#!/usr/bin/env python3
"""Plan D — measure PressureModel signal edges on Liquidity Radar capture CSVs.

Capture format (RadarTab "Rec"): time,type,side,price,peak,last,prevState,newState,
conf,inWindow,age,mid,medBid,medAsk.  type is 'evt' (a node state change) or 'mid'
(a 2s snapshot).  IMPORTANT: this capture only carries wall/absorbed EVENTS + mid +
MEDIAN book sizes.  It does NOT carry the full book, best-bid/ask sizes, trade tape,
or a wall's continuous size trajectory -- so of the 5 PressureModel signals, only two
PROXIES are measurable here:
  * Absorbed-wall direction  -> proxy for the wall/structural signal
  * sign(medBid - medAsk)     -> lossy proxy for Imbalance
InsideThin, AirPocket, Delta, and partial-erosion need the ENHANCED capture
(RadarTab logs full PressureInputs per snapshot) -- run that on Market Replay, then
re-run this script pointed at the richer file.

Method is metric-robust: SIGNAL direction vs OPPOSITE direction on the same metric
(net mid move over a horizon).  An edge exists only if signal >> opposite >> 50%.

Usage: python3 measure_signals.py <capture.csv> [<capture2.csv> ...]
"""
import csv, sys, bisect
from datetime import datetime, timedelta

HORIZONS = [15, 30, 60]
TICK = 0.25

def parse_time(s):
    if '.' in s:
        head, frac = s.split('.'); s = head + '.' + frac[:6].ljust(6, '0')
    return datetime.fromisoformat(s)

def load(path):
    mids, absorbed, imb = [], [], []
    with open(path, newline='') as fh:
        for row in csv.DictReader(fh):
            if not row['time']:
                continue
            t = parse_time(row['time'])
            if row['mid']:
                mids.append((t, float(row['mid'])))
            if row['type'] == 'evt' and row['newState'] == 'Absorbed':
                absorbed.append((t, float(row['price']), row['side'], int(row['peak'] or 0)))
            if row['type'] == 'mid' and row['medBid'] and row['medAsk']:
                imb.append((t, int(row['medBid']), int(row['medAsk']), float(row['mid'])))
    mids.sort()
    return mids, absorbed, imb

def mid_after(mids, ts, t, tol=5):
    i = bisect.bisect_left(ts, t)
    if i >= len(mids):
        return None
    dt, mv = mids[i]
    return mv if (dt - t).total_seconds() <= tol else None

def absorbed_edge(mids, absorbed):
    ts = [m[0] for m in mids]
    res = {}
    for h in HORIZONS:
        for flt, name in [(0, 'all'), (100, 'peak>=100')]:
            sig = opp = 0
            for (t0, P, side, peak) in absorbed:
                if peak < flt:
                    continue
                m0 = mid_after(mids, ts, t0)
                mh = mid_after(mids, ts, t0 + timedelta(seconds=h))
                if m0 is None or mh is None:
                    continue
                d = mh - m0
                sd = 1 if side == 'Bid' else -1   # Bid absorbed=support->up ; Ask=resistance->down
                if d * sd > 1e-9: sig += 1
                elif d * sd < -1e-9: opp += 1
            res[(h, name)] = (sig, opp)
    return res

def imbalance_edge(mids, imb):
    ts = [m[0] for m in mids]
    res = {}
    for h in HORIZONS:
        win = loss = 0
        for (t, mb, ma, m0) in imb:
            if mb == ma:
                continue
            mh = mid_after(mids, ts, t + timedelta(seconds=h))
            if mh is None:
                continue
            d = mh - m0
            lean = 1 if mb > ma else -1   # heavier bid queue -> up (queue-imbalance)
            if d * lean > 1e-9: win += 1
            elif d * lean < -1e-9: loss += 1
        res[h] = (win, loss)
    return res

def pct(a, b):
    return 100.0 * a / (a + b) if (a + b) else 0.0

def main(paths):
    A, I = {}, {}
    for p in paths:
        mids, absorbed, imb = load(p)
        print(f"\n# {p.split('/')[-1]}  mids={len(mids)} absorbed={len(absorbed)} imb-snaps={len(imb)}")
        ae = absorbed_edge(mids, absorbed)
        for h in HORIZONS:
            for name in ['all', 'peak>=100']:
                s, o = ae[(h, name)]
                print(f"  ABSORBED @{h:>2}s {name:<10} n={s+o:<4} signal={pct(s,o):4.0f}% opposite={pct(o,s):4.0f}%")
                A[(h, name)] = tuple(x + y for x, y in zip(A.get((h, name), (0, 0)), (s, o)))
        ie = imbalance_edge(mids, imb)
        for h in HORIZONS:
            w, l = ie[h]
            print(f"  IMBALANCE @{h:>2}s n={w+l:<6} win={pct(w,l):4.1f}%")
            I[h] = tuple(x + y for x, y in zip(I.get(h, (0, 0)), (w, l)))
    print("\n# COMBINED")
    for h in HORIZONS:
        for name in ['all', 'peak>=100']:
            s, o = A[(h, name)]
            print(f"  ABSORBED @{h:>2}s {name:<10} n={s+o:<4} signal={pct(s,o):4.0f}% opposite={pct(o,s):4.0f}%")
    for h in HORIZONS:
        w, l = I[h]
        print(f"  IMBALANCE @{h:>2}s n={w+l:<6} win={pct(w,l):4.1f}%")
    print("\n# VERDICT: signal ~= opposite ~= 50% => NO net-directional edge measurable here.")
    print("#   Consistent with playbook: absorbed = level-hold/timing, not a directional call.")
    print("#   Median-imbalance proxy ~50% but lossy. Real signals need the enhanced capture.")

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(2)
    main(sys.argv[1:])

# ===== Measured 2026-06-29 on the 2 ES days (6/22 + 6/24, 918 absorbed) =====
# ABSORBED signal vs opposite (net mid direction): ~47-55% / ~45-53% across all
#   horizons & the peak>=100 filter -> coin flip, no directional edge.
# IMBALANCE (median proxy): @15s 51.3% @30s 50.9% @60s 50.1% -> ~coin flip (lossy proxy).
# => existing capture CANNOT validate the 5 cockpit signals' directional weights.
#    Enhanced capture (full book mass + best sizes + aggressor delta + erosion frac per
#    snapshot, with forward mid) is required; run it on Replay, then re-run this script.
