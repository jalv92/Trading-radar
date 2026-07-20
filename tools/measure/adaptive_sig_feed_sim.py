#!/usr/bin/env python3
"""Offline simulation of the DepthBaseline wall-candidate feed (multiday verdict item 4).

The 2026-07-03 multiday analysis found the shipped AdaptiveSignificance inert: RadarTab feeds
DepthBaseline every individual book level, whose P85 sits structurally below the wall population,
so `max(SignificanceBand, adaptiveSig)` is always decided by the compiled floor. The verdict
demanded THIS simulation before wiring any fix: rebase the sample feed to wall-candidate sizes
offline against the Rec CSV corpus and see where P85 lands.

Per lr-signals-*.csv session this prints:
  wallP50/wallP85  — percentiles of the dominant-wall sizes (wallAboveCur/wallBelowCur > 0),
                     the closest CSV proxy for the wall-candidate feed the fix would wire.
  adSigP50/adSigP85 — the CURRENT adaptiveSig column (raw-book-level feed) for comparison.
  adSig>60%        — share of rows where the current adaptive term actually exceeds the ES floor.

Result on the 2026-07-19 corpus (13 ES + 4 NQ + 1 X sessions, 224k wall samples):
  ES: wall P85 = 67-96 (matches the verdict's predicted 70-96) vs adaptiveSig P85 = 41-56 —
      the current feed is inert (>99.9% of rows below the 60 floor). Wiring the wall feed would
      RAISE the ES arm bar to ~72-96: a calibration change, not a bug fix. NOT wired (2026-07-19).
  NQ: wall P85 = 6-7 — BELOW the NQ preset floor of 12, so the wall feed would ALSO be inert on
      NQ, and the preset's SignificanceBand=12 may be HIGH for NQ, not low. Feed this into the
      NQ day-1 calibration instead of wiring blind.

Usage: python3 adaptive_sig_feed_sim.py [dir-with-lr-signals-csvs]
       (default: the Windows-side LiquidityRadar folder via /mnt/c)
"""
import csv, glob, sys


def pct(vals, p):
    if not vals:
        return None
    vals = sorted(vals)
    return vals[min(len(vals) - 1, round(p * (len(vals) - 1)))]


def main():
    base = sys.argv[1] if len(sys.argv) > 1 else '/mnt/c/Users/javlo/Documents/NinjaTrader 8/LiquidityRadar'
    files = sorted(glob.glob(base.rstrip('/') + '/lr-signals-*.csv'))
    if not files:
        sys.exit(f"no lr-signals-*.csv under {base}")
    print(f"{'file':<42} {'rows':>6} {'wallP50':>7} {'wallP85':>7} {'adSigP50':>8} {'adSigP85':>8} {'adSig>60%':>9}")
    agg = []
    for f in files:
        walls, adsig = [], []
        with open(f, newline='') as fh:
            r = csv.reader(fh)
            header = next(r)
            try:
                iA = header.index('wallAboveCur'); iB = header.index('wallBelowCur')
            except ValueError:
                continue   # pre-43-col build — skip
            iS = header.index('adaptiveSig') if 'adaptiveSig' in header else None
            for row in r:
                if len(row) <= max(iA, iB):
                    continue
                try:
                    a = int(float(row[iA])); b = int(float(row[iB]))
                    if a > 0: walls.append(a)
                    if b > 0: walls.append(b)
                    if iS is not None and row[iS]:
                        adsig.append(int(float(row[iS])))
                except (ValueError, IndexError):
                    continue
        if not walls:
            continue
        agg.extend(walls)
        over60 = (sum(1 for v in adsig if v > 60) / len(adsig) * 100) if adsig else float('nan')
        name = f.split('/')[-1].replace('lr-signals-', '')
        print(f"{name:<42} {len(walls):>6} {pct(walls,.5):>7} {pct(walls,.85):>7} "
              f"{(pct(adsig,.5) if adsig else '-'):>8} {(pct(adsig,.85) if adsig else '-'):>8} {over60:>8.1f}%")
    print(f"\nAGGREGATE dominant-wall sizes: n={len(agg)}, p50={pct(agg,.5)}, p85={pct(agg,.85)}, p95={pct(agg,.95)}")


if __name__ == '__main__':
    main()
