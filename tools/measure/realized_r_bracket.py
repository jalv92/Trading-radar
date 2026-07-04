#!/usr/bin/env python3
"""Offline realized-R bracket grader — AbsorptionScalper exit design over the
existing AUTO fills + the "not armed" guard-skips, plus a random-entry baseline.

Grades what already happened (60 AUTO fills logged in lr-auto-*.csv) and what
almost happened (15 guard_skip="not armed at fire time" rows) against the
bracket in docs/strategy-absorption-scalper.md §2.3, replayed over the ~20Hz
`mid` path in lr-signals-*.csv. This is a PROXY: fills/exits are simulated on
the mid series, not real book/trade-tape execution — see the results doc for
every inflation source.

Bracket (ES, tick=0.25):
  initial stop  = -6 ticks from entry
  target        = +8 ticks from entry
  BE trigger    = +3 ticks MFE -> stop moves (2 variants, doc wording is ambiguous):
                    be_entry_plus1t  -> stop to entry +1t (long) / -1t (short)  [locks 1t]
                    be_entry_minus1t -> stop to entry -1t (long) / +1t (short)  [small giveback]
  time-stop     = until BE only: if +3t not reached within T seconds, exit at market
                    (2 variants: 30s / 45s)
  tie-break     = if one snapshot-to-snapshot gap spans BOTH stop and target
                  (or both target and the BE-stop), count the stop (conservative).

Usage:
  python3 realized_r_bracket.py --data-dir <dir with lr-signals-*.csv, lr-auto-*.csv> \\
      [--fires-json <prior-analysis fires_final.json>] [--primary-files-json <day->file map>] \\
      [--n-random 250] [--seed 20260703] [--out-json out.json]

Self-check: python3 realized_r_bracket.py --selftest
"""
import argparse, csv, glob, json, math, os, random, re, sys
from datetime import datetime

import numpy as np
import pandas as pd

TICK = 0.25
STOP_TICKS = 6.0
TARGET_TICKS = 8.0
BE_TRIGGER_TICKS = 3.0
BE_VARIANTS = {'be_entry_plus1t': 1.0, 'be_entry_minus1t': -1.0}
TIME_STOP_VARIANTS = {'ts30s': 30.0, 'ts45s': 45.0}
COST_SCENARIOS = {'gross': 0.0, 'net_1.5t': 1.5, 'net_2.5t': 2.5}
HORIZON_CAP_S = 300.0
GUARD_SKIP_FILL_WINDOW_S = 15.0  # mirrors AUTO's own unfilled-limit age-out
DEFAULT_VARIANT = 'be_entry_plus1t__ts30s'  # the literal-default reading, used for headline sensitivity cuts

FNAME_RE = re.compile(r'-(\d{8})-(\d{6})\.csv$')


def parse_filename_wallclock(path):
    m = FNAME_RE.search(path)
    if not m:
        return None
    return datetime.strptime(m.group(1) + m.group(2), '%Y%m%d%H%M%S')


def parse_content_time(s):
    """Content-time strings look like '2026-06-15T09:44:31.8720000' (7-digit frac)."""
    if not s:
        return None
    try:
        base, frac = s.split('.')
        return datetime.strptime(base + '.' + frac[:6], '%Y-%m-%dT%H:%M:%S.%f')
    except Exception:
        try:
            return datetime.strptime(s, '%Y-%m-%dT%H:%M:%S')
        except Exception:
            return None


# --------------------------------------------------------------------------
# Signals-file inventory + the matching method (mirrors the prior analysis'
# match_and_outcome.py: candidates = files whose [min_time,max_time] contains
# the content-time; tie-break = closest file-wallclock to the auto session's
# own wallclock, i.e. "same logging session").
# --------------------------------------------------------------------------

def load_signals_meta(data_dir):
    meta = []
    for path in sorted(glob.glob(os.path.join(data_dir, 'lr-signals-*.csv'))):
        wc = parse_filename_wallclock(path)
        try:
            t = pd.to_datetime(pd.read_csv(path, usecols=['time'])['time'], errors='coerce').dropna()
        except Exception:
            continue
        if len(t) == 0:
            continue
        inst = 'X' if '-X-' in os.path.basename(path) else 'ES'
        meta.append({'file': os.path.basename(path), 'path': path, 'instrument': inst,
                     'wallclock': wc, 'min_time': t.min(), 'max_time': t.max(), 'n_rows': len(t)})
    return meta


def match_file(meta, content_time, auto_wallclock, instrument='ES'):
    cands = [m for m in meta if m['instrument'] == instrument
             and m['min_time'] <= content_time <= m['max_time']]
    if not cands:
        return None
    return min(cands, key=lambda m: abs((m['wallclock'] - auto_wallclock).total_seconds()))


_mid_cache = {}


def load_mid_arrays(data_dir, filename):
    """Returns (t_epoch_seconds float64 array, mid float64 array), sorted, mid>0 only."""
    if filename not in _mid_cache:
        df = pd.read_csv(os.path.join(data_dir, filename), usecols=['time', 'mid'])
        df['time'] = pd.to_datetime(df['time'], errors='coerce')
        df = df.dropna(subset=['time'])
        df = df[df['mid'] > 0]
        df = df.sort_values('time')
        t = df['time'].values.astype('datetime64[ns]').astype('int64') / 1e9
        m = df['mid'].values.astype(float)
        _mid_cache[filename] = (t, m)
    return _mid_cache[filename]


# --------------------------------------------------------------------------
# Bracket engine
# --------------------------------------------------------------------------

def first_span_idx(fav, level, start=0):
    """First index i>=start+1 such that the [prev,cur] snapshot-to-snapshot gap
    (fav[i-1] -> fav[i]) spans `level`. None if never."""
    if start + 1 >= len(fav):
        return None
    prev = fav[start:-1]
    cur = fav[start + 1:]
    lo = np.minimum(prev, cur)
    hi = np.maximum(prev, cur)
    hit = (lo <= level) & (hi >= level)
    if not hit.any():
        return None
    return start + 1 + int(np.argmax(hit))


def run_trade(entry_price, direction, entry_ts, tt, mm, be_variant_ticks, time_stop_s,
              tick=TICK, target_ticks=TARGET_TICKS, stop_ticks=STOP_TICKS,
              be_trigger_ticks=BE_TRIGGER_TICKS, horizon_cap_s=HORIZON_CAP_S):
    """direction: +1 long, -1 short. tt/mm: full-day epoch-seconds/mid arrays (sorted).
    Returns a dict with outcome, exit_ticks (gross, in ticks, +/-), exit_time_s (secs
    from entry), be_triggered, reached_3t_before_stop."""
    i0 = np.searchsorted(tt, entry_ts, side='left')
    i1 = np.searchsorted(tt, entry_ts + horizon_cap_s, side='right')
    sub_t, sub_m = tt[i0:i1], mm[i0:i1]
    if len(sub_t) == 0:
        return {'outcome': 'no_data', 'exit_ticks': None, 'exit_time_s': None,
                'be_triggered': False, 'reached_3t_before_stop': None}

    t = np.concatenate(([entry_ts], sub_t))
    fav = (np.concatenate(([entry_price], sub_m)) - entry_price) * direction / tick

    idx_target = first_span_idx(fav, target_ticks)
    idx_stop0 = first_span_idx(fav, -stop_ticks)
    idx_be = first_span_idx(fav, be_trigger_ticks)
    rel = t - t[0]
    ts_hits = np.where(rel >= time_stop_s)[0]
    idx_timestop = int(ts_hits[0]) if len(ts_hits) else None

    reached_3t_before_stop = (idx_be is not None) and (idx_stop0 is None or idx_be <= idx_stop0)

    cands = [(n, i) for n, i in
             (('target', idx_target), ('stop', idx_stop0), ('be_trigger', idx_be), ('time_stop', idx_timestop))
             if i is not None]
    if not cands:
        return {'outcome': 'unresolved_end_of_data', 'exit_ticks': float(fav[-1]),
                'exit_time_s': float(t[-1] - t[0]), 'be_triggered': False,
                'reached_3t_before_stop': reached_3t_before_stop}

    cands.sort(key=lambda x: x[1])
    first_idx = cands[0][1]
    tie = [n for n, i in cands if i == first_idx]
    if 'stop' in tie and 'target' in tie:
        winner = 'stop'          # conservative tie-break (task spec)
    elif 'stop' in tie:
        winner = 'stop'
    elif 'target' in tie:
        winner = 'target'
    else:
        winner = tie[0]

    if winner == 'target':
        return {'outcome': 'target', 'exit_ticks': float(target_ticks),
                'exit_time_s': float(t[first_idx] - t[0]), 'be_triggered': False,
                'reached_3t_before_stop': reached_3t_before_stop}
    if winner == 'stop':
        return {'outcome': 'stop_initial', 'exit_ticks': float(-stop_ticks),
                'exit_time_s': float(t[first_idx] - t[0]), 'be_triggered': False,
                'reached_3t_before_stop': reached_3t_before_stop}
    if winner == 'time_stop':
        return {'outcome': 'time_stop', 'exit_ticks': float(fav[first_idx]),
                'exit_time_s': float(t[first_idx] - t[0]), 'be_triggered': False,
                'reached_3t_before_stop': reached_3t_before_stop}

    # winner == 'be_trigger': phase 2, stop moves to be_variant_ticks, time-stop drops,
    # target level unchanged (idx_target, if any, is guaranteed > idx_be here).
    idx_stopbe = first_span_idx(fav, be_variant_ticks, start=idx_be)
    cands2 = [(n, i) for n, i in (('target', idx_target), ('stop_be', idx_stopbe)) if i is not None]
    if not cands2:
        return {'outcome': 'unresolved_end_of_data_after_be', 'exit_ticks': float(fav[-1]),
                'exit_time_s': float(t[-1] - t[0]), 'be_triggered': True,
                'reached_3t_before_stop': True}
    cands2.sort(key=lambda x: x[1])
    idx2 = cands2[0][1]
    tie2 = [n for n, i in cands2 if i == idx2]
    w2 = 'stop_be' if len(tie2) > 1 else tie2[0]
    exit_ticks2 = target_ticks if w2 == 'target' else be_variant_ticks
    return {'outcome': w2, 'exit_ticks': float(exit_ticks2), 'exit_time_s': float(t[idx2] - t[0]),
            'be_triggered': True, 'reached_3t_before_stop': True}


def mfe_mae_ticks(entry_price, direction, entry_ts, tt, mm, horizon_s, tick=TICK):
    i0 = np.searchsorted(tt, entry_ts, side='left')
    i1 = np.searchsorted(tt, entry_ts + horizon_s, side='right')
    sub = mm[i0:i1]
    if len(sub) == 0:
        return None, None
    fav = (sub - entry_price) * direction / tick
    return float(fav.max()), float(-fav.min())


def grade_trade(entry_price, side, entry_ts, tt, mm):
    direction = 1.0 if side == 'Buy' else -1.0
    out = {}
    for be_name, be_ticks in BE_VARIANTS.items():
        for ts_name, ts_s in TIME_STOP_VARIANTS.items():
            out[f'{be_name}__{ts_name}'] = run_trade(entry_price, direction, entry_ts, tt, mm, be_ticks, ts_s)
    mfe30, mae30 = mfe_mae_ticks(entry_price, direction, entry_ts, tt, mm, 30)
    mfe60, mae60 = mfe_mae_ticks(entry_price, direction, entry_ts, tt, mm, 60)
    out['_mfe30'], out['_mae30'], out['_mfe60'], out['_mae60'] = mfe30, mae30, mfe60, mae60
    return out


# --------------------------------------------------------------------------
# 1) The 60 AUTO fills (head-start reuse, or self-derive fallback)
# --------------------------------------------------------------------------

def load_fires_from_json(fires_json_path):
    with open(fires_json_path) as fh:
        data = json.load(fh)
    fires = []
    for r in data:
        fires.append({
            'content_time': parse_content_time(r['time']),
            'side': r['side'],
            'price': float(r['price']),
            'matched_signals_file': r.get('matchedSignalsFile'),
            'content_day': r.get('contentDay'),
            'build_group': r.get('buildGroup'),
            'session_file': r.get('sessionFile'),
        })
    return fires


def derive_fires_from_scratch(data_dir, meta):
    """Fallback if no head-start JSON is supplied: parse lr-auto-*.csv 'fill' events
    (+ old-schema order_update->Filled synth) and match each to a signals file."""
    fill_re = re.compile(r'filled (\d+)/(\d+) @ ([\d.]+)')
    fires = []
    for af in sorted(glob.glob(os.path.join(data_dir, 'lr-auto-*.csv'))):
        wc = parse_filename_wallclock(af)
        with open(af, newline='', encoding='utf-8') as fh:
            rows = list(csv.DictReader(fh))
        has_explicit_fill = set()
        for row in rows:
            if row['event'] == 'fill':
                m = re.search(r'order #(\d+)', row['detail'])
                if m:
                    has_explicit_fill.add(m.group(1))
        seen = set()
        for row in rows:
            ct = parse_content_time(row['time'])
            if ct is None:
                continue
            if row['event'] == 'fill':
                m = fill_re.search(row['detail'])
                if m and int(m.group(1)) > 0:
                    best = match_file(meta, ct, wc)
                    if best:
                        fires.append({'content_time': ct, 'side': row['side'], 'price': float(m.group(3)),
                                      'matched_signals_file': best['file'], 'content_day': str(ct.date()),
                                      'build_group': 'derived', 'session_file': os.path.basename(af)})
            elif row['event'] == 'order_update' and row['detail'].endswith('-> Filled.'):
                m = re.search(r'order #(\d+)', row['detail'])
                oid = m.group(1) if m else None
                if oid and oid not in has_explicit_fill and oid not in seen:
                    seen.add(oid)
                    best = match_file(meta, ct, wc)
                    if best:
                        fires.append({'content_time': ct, 'side': row['side'], 'price': float(row['price']),
                                      'matched_signals_file': best['file'], 'content_day': str(ct.date()),
                                      'build_group': 'derived', 'session_file': os.path.basename(af)})
    return fires


# --------------------------------------------------------------------------
# 2) The 15 "not armed at fire time" guard_skips -> prestage limit -> 15s fill proxy
# --------------------------------------------------------------------------

def extract_not_armed_skips(data_dir, meta):
    skips = []
    for af in sorted(glob.glob(os.path.join(data_dir, 'lr-auto-*.csv'))):
        wc = parse_filename_wallclock(af)
        with open(af, newline='', encoding='utf-8') as fh:
            rows = list(csv.DictReader(fh))
        prev = None
        for row in rows:
            if row['event'] == 'guard_skip' and 'not armed' in row['detail']:
                ct = parse_content_time(row['time'])
                limit_price = float(row['price'])
                side = row['side']
                # the prestage row logged just before the skip carries the limit price
                if prev is not None and prev['event'] == 'prestage' and prev['side'] == side:
                    limit_price = float(prev['price'])
                skips.append({'content_time': ct, 'side': side, 'limit_price': limit_price,
                              'auto_file': os.path.basename(af), 'auto_wallclock': wc})
            prev = row
    for s in skips:
        best = match_file(meta, s['content_time'], s['auto_wallclock'])
        s['matched_signals_file'] = best['file'] if best else None
    return skips


def simulate_guard_skip_fill(skip, data_dir, window_s=GUARD_SKIP_FILL_WINDOW_S):
    if not skip['matched_signals_file']:
        return {**skip, 'filled': False, 'reason': 'no_matching_signals_file'}
    tt, mm = load_mid_arrays(data_dir, skip['matched_signals_file'])
    t0 = pd.Timestamp(skip['content_time']).value / 1e9
    i0 = np.searchsorted(tt, t0, side='left')
    i1 = np.searchsorted(tt, t0 + window_s, side='right')
    win = mm[i0:i1]
    if len(win) == 0:
        return {**skip, 'filled': False, 'reason': 'no_data_in_window'}
    if skip['side'] == 'Buy':
        hit = win <= skip['limit_price']
    else:
        hit = win >= skip['limit_price']
    if not hit.any():
        return {**skip, 'filled': False, 'reason': 'never_traded_through_within_15s'}
    fill_idx = i0 + int(np.argmax(hit))
    return {**skip, 'filled': True, 'fill_entry_ts': float(tt[fill_idx]),
            'fill_entry_price': skip['limit_price']}


# --------------------------------------------------------------------------
# 3) Random RTH baseline
# --------------------------------------------------------------------------

def pick_canonical_file(day_str, meta, primary_files=None):
    if primary_files and day_str in primary_files:
        return primary_files[day_str]
    day = pd.Timestamp(day_str).date()
    best, best_n = None, -1
    for m in meta:
        if m['instrument'] != 'ES':
            continue
        if m['min_time'].date() > day or m['max_time'].date() < day:
            continue
        tt, mm = load_mid_arrays(os.path.dirname(m['path']), m['file'])
        t = pd.to_datetime(tt, unit='s')
        rth_lo = pd.Timestamp(day).replace(hour=9, minute=30)
        rth_hi = pd.Timestamp(day).replace(hour=16, minute=0)
        n = int(((t >= rth_lo) & (t <= rth_hi)).sum())
        if n > best_n:
            best, best_n = m['file'], n
    return best


def random_baseline_for_day(day_str, data_dir, canonical_file, n_random, rng):
    tt, mm = load_mid_arrays(data_dir, canonical_file)
    day = pd.Timestamp(day_str).date()
    t = pd.to_datetime(tt, unit='s')
    rth_lo = pd.Timestamp(day).replace(hour=9, minute=30)
    rth_hi = pd.Timestamp(day).replace(hour=16, minute=0)
    valid_idx = np.where((t >= rth_lo) & (t <= rth_hi))[0]
    if len(valid_idx) == 0:
        return []
    trades = []
    picks = rng.choices(list(valid_idx), k=n_random)
    for idx in picks:
        entry_ts = float(tt[idx])
        entry_price = float(mm[idx])
        for side in ('Buy', 'Sell'):
            g = grade_trade(entry_price, side, entry_ts, tt, mm)
            trades.append({'content_day': day_str, 'side': side, 'entry_price': entry_price,
                           'entry_ts': entry_ts, 'grades': g})
    return trades


# --------------------------------------------------------------------------
# Stats helpers (stdlib + numpy only)
# --------------------------------------------------------------------------

def wilson_ci(k, n, z=1.96):
    if n == 0:
        return (None, None)
    p = k / n
    denom = 1 + z * z / n
    center = p + z * z / (2 * n)
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n))
    return ((center - half) / denom, (center + half) / denom)


def two_proportion_z(k1, n1, k2, n2):
    if n1 == 0 or n2 == 0:
        return None, None
    p1, p2 = k1 / n1, k2 / n2
    p_pool = (k1 + k2) / (n1 + n2)
    se = math.sqrt(p_pool * (1 - p_pool) * (1 / n1 + 1 / n2))
    if se == 0:
        return None, None
    z = (p1 - p2) / se
    p_value = 2 * (1 - 0.5 * (1 + math.erf(abs(z) / math.sqrt(2))))
    return z, p_value


def bootstrap_expectancy_gap_ci(a_exits, b_exits, n_boot=5000, seed=20260703):
    """Percentile bootstrap CI on mean(a) - mean(b), resampling each side independently."""
    if not a_exits or not b_exits:
        return None
    rng = np.random.default_rng(seed)
    a = np.array(a_exits)
    b = np.array(b_exits)
    diffs = np.empty(n_boot)
    for i in range(n_boot):
        diffs[i] = rng.choice(a, size=len(a), replace=True).mean() - rng.choice(b, size=len(b), replace=True).mean()
    lo, hi = np.percentile(diffs, [2.5, 97.5])
    return {'mean_diff': float(a.mean() - b.mean()), 'ci95': [float(lo), float(hi)], 'n_boot': n_boot}


def summarize_variant(trades_grades, variant_key, cost_scenarios=COST_SCENARIOS):
    exits = [g[variant_key]['exit_ticks'] for g in trades_grades
             if g[variant_key]['exit_ticks'] is not None]
    n = len(exits)
    outcomes = {}
    for g in trades_grades:
        o = g[variant_key]['outcome']
        outcomes[o] = outcomes.get(o, 0) + 1
    wins = sum(1 for x in exits if x > 0)
    result = {'n': n, 'outcomes': outcomes, 'win_rate_pct': round(100 * wins / n, 1) if n else None,
              '_exits': exits}
    for cname, cost in cost_scenarios.items():
        if n:
            result[f'expectancy_ticks_{cname}'] = round(sum(x - cost for x in exits) / n, 3)
        else:
            result[f'expectancy_ticks_{cname}'] = None
    return result


# --------------------------------------------------------------------------
# Self-test
# --------------------------------------------------------------------------

def _selftest():
    tick = TICK
    entry = 100.0
    # straight run-up should hit target before anything else
    t = np.array([0., 1, 2, 3, 4, 5, 6, 7, 8])
    m = entry + np.arange(9) * tick  # +1 tick per second, long
    r = run_trade(entry, 1.0, 0.0, t, m, BE_VARIANTS['be_entry_plus1t'], 30.0)
    assert r['outcome'] == 'target', r
    assert r['exit_ticks'] == TARGET_TICKS

    # straight drop should hit the initial stop
    m2 = entry - np.arange(9) * tick
    r2 = run_trade(entry, 1.0, 0.0, t, m2, BE_VARIANTS['be_entry_plus1t'], 30.0)
    assert r2['outcome'] == 'stop_initial', r2
    assert r2['exit_ticks'] == -STOP_TICKS
    assert r2['reached_3t_before_stop'] is False

    # up to +3t (BE triggers) then back down through BE-minus1 stop
    t3 = np.array([0., 1, 2, 3, 4, 5])
    m3 = entry + np.array([0, 1, 2, 3, 1, -1]) * tick
    r3 = run_trade(entry, 1.0, 0.0, t3, m3, BE_VARIANTS['be_entry_minus1t'], 30.0)
    assert r3['be_triggered'] is True, r3
    assert r3['outcome'] == 'stop_be', r3
    assert r3['exit_ticks'] == -1.0
    assert r3['reached_3t_before_stop'] is True

    # BE-plus1 variant on the same path: stop is above where be_entry_minus1 would be,
    # so it should have exited earlier/at a better (or equal) price.
    r3b = run_trade(entry, 1.0, 0.0, t3, m3, BE_VARIANTS['be_entry_plus1t'], 30.0)
    assert r3b['exit_ticks'] >= r3['exit_ticks'], (r3b, r3)

    # gap that spans both stop and target in one 50ms-style step -> conservative "stop"
    t4 = np.array([0., 0.05])
    m4 = np.array([entry, entry + (TARGET_TICKS + 1) * tick])  # jump straight past target...
    # ...but rig direction short so this jump is actually adverse past both levels:
    r4 = run_trade(entry, -1.0, 0.0, t4, m4, BE_VARIANTS['be_entry_plus1t'], 30.0)
    assert r4['outcome'] == 'stop_initial', r4  # for a short, price rising = stop side

    # time-stop: never reaches +3t within 30s -> exits at market, flat-ish P&L
    t5 = np.linspace(0, 40, 41)
    m5 = entry + np.where(t5 < 35, 0.5, 0.5) * tick  # sits at +0.5t the whole time
    r5 = run_trade(entry, 1.0, 0.0, t5, m5, BE_VARIANTS['be_entry_plus1t'], 30.0)
    assert r5['outcome'] == 'time_stop', r5
    assert abs(r5['exit_ticks'] - 0.5) < 1e-9

    # no data at all -> no_data
    r6 = run_trade(entry, 1.0, 100000.0, t, m, BE_VARIANTS['be_entry_plus1t'], 30.0)
    assert r6['outcome'] == 'no_data', r6

    # wilson CI sanity: wide interval for small n, narrow for large n at same rate
    lo_small, hi_small = wilson_ci(5, 10)
    lo_big, hi_big = wilson_ci(500, 1000)
    assert (hi_small - lo_small) > (hi_big - lo_big)

    # bootstrap CI sanity: identical distributions -> CI straddles 0
    same = [1.0, -1.0, 2.0, -2.0, 0.5] * 20
    b = bootstrap_expectancy_gap_ci(same, same, n_boot=500)
    assert b['ci95'][0] <= 0.0 <= b['ci95'][1], b

    print("selftest OK")


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--data-dir', help='dir with lr-signals-*.csv and lr-auto-*.csv')
    ap.add_argument('--fires-json', default=None,
                     help='prior-analysis fires_final.json (60 fills w/ matchedSignalsFile); '
                          'if omitted, fires are re-derived from --data-dir')
    ap.add_argument('--primary-files-json', default=None,
                     help='optional {content_day: canonical signals file} map for the random baseline; '
                          'falls back to "file with most RTH rows that day" if omitted')
    ap.add_argument('--n-random', type=int, default=250, help='random RTH entries per day per side (>=200)')
    ap.add_argument('--seed', type=int, default=20260703)
    ap.add_argument('--out-json', default=None, help='optional path to dump full results as JSON')
    ap.add_argument('--selftest', action='store_true', help='run assert-based self-check and exit')
    args = ap.parse_args()

    if args.selftest:
        _selftest()
        return

    if not args.data_dir:
        ap.error('--data-dir is required (unless --selftest)')

    meta = load_signals_meta(args.data_dir)
    print(f"# {len(meta)} signals files indexed under {args.data_dir}")

    primary_files = None
    if args.primary_files_json:
        with open(args.primary_files_json) as fh:
            primary_files = json.load(fh)

    # --- 1) grade the 60 AUTO fills ---
    if args.fires_json:
        fires = load_fires_from_json(args.fires_json)
        print(f"# loaded {len(fires)} fires from head-start JSON {args.fires_json}")
    else:
        fires = derive_fires_from_scratch(args.data_dir, meta)
        print(f"# derived {len(fires)} fires from scratch (no --fires-json supplied)")

    fire_results = []
    for f in fires:
        if not f['matched_signals_file']:
            continue
        tt, mm = load_mid_arrays(args.data_dir, f['matched_signals_file'])
        entry_ts = pd.Timestamp(f['content_time']).value / 1e9
        g = grade_trade(f['price'], f['side'], entry_ts, tt, mm)
        fire_results.append({**f, 'grades': g})

    # --- 2) the 15 "not armed" guard-skips -> 15s prestage-limit fill proxy ---
    skips = extract_not_armed_skips(args.data_dir, meta)
    skip_sim = [simulate_guard_skip_fill(s, args.data_dir) for s in skips]
    filled_skips = [s for s in skip_sim if s.get('filled')]
    unfilled_skips = [s for s in skip_sim if not s.get('filled')]
    print(f"# not-armed guard_skips: {len(skips)} total, {len(filled_skips)} would have filled "
          f"within {GUARD_SKIP_FILL_WINDOW_S:.0f}s (proxy), {len(unfilled_skips)} unfilled")

    skip_results = []
    for s in filled_skips:
        tt, mm = load_mid_arrays(args.data_dir, s['matched_signals_file'])
        g = grade_trade(s['fill_entry_price'], s['side'], s['fill_entry_ts'], tt, mm)
        skip_results.append({**s, 'grades': g})

    combined_results = fire_results + skip_results
    print(f"# graded: {len(fire_results)} real fills + {len(skip_results)} backfilled not-armed fills "
          f"= {len(combined_results)} total")

    # --- 3) random baseline ---
    days = sorted(set(r['content_day'] for r in combined_results if r.get('content_day')))
    if not days:
        days = sorted(set(str(pd.Timestamp(r['content_time']).date()) for r in combined_results))
    print(f"# random baseline over {len(days)} content-days: {days}")

    rng = random.Random(args.seed)
    baseline_trades = []
    canonical_by_day = {}
    for d in days:
        cf = pick_canonical_file(d, meta, primary_files)
        canonical_by_day[d] = cf
        if not cf:
            print(f"#   {d}: no canonical file found, skipped")
            continue
        day_trades = random_baseline_for_day(d, args.data_dir, cf, args.n_random, rng)
        baseline_trades.extend(day_trades)
        print(f"#   {d}: canonical={cf}  n_random={args.n_random}x2sides={len(day_trades)}")

    # --- 4) aggregate + compare ---
    variant_keys = [f'{b}__{t}' for b in BE_VARIANTS for t in TIME_STOP_VARIANTS]

    def race_stats(records):
        k = sum(1 for r in records if r['grades'][variant_keys[0]]['reached_3t_before_stop'] is True)
        n = sum(1 for r in records if r['grades'][variant_keys[0]]['reached_3t_before_stop'] is not None)
        return k, n

    k_fire, n_fire = race_stats(combined_results)
    k_base, n_base = race_stats(baseline_trades)
    rate_fire = k_fire / n_fire if n_fire else None
    rate_base = k_base / n_base if n_base else None
    ci_fire = wilson_ci(k_fire, n_fire) if n_fire else (None, None)
    ci_base = wilson_ci(k_base, n_base) if n_base else (None, None)
    z, p = two_proportion_z(k_fire, n_fire, k_base, n_base)

    print("\n# === P(reach +3t before initial -6t stop) : signal fires vs random baseline ===")
    print(f"  fires:    {k_fire}/{n_fire} = {rate_fire:.1%}  95% CI [{ci_fire[0]:.1%}, {ci_fire[1]:.1%}]" if n_fire else "  fires: n=0")
    print(f"  baseline: {k_base}/{n_base} = {rate_base:.1%}  95% CI [{ci_base[0]:.1%}, {ci_base[1]:.1%}]" if n_base else "  baseline: n=0")
    if z is not None:
        print(f"  two-proportion z={z:.2f}  p~={p:.4f}")

    print("\n# === bracket variant summary: fires+backfilled (n={}) ===".format(len(combined_results)))
    variant_summaries = {}
    for vk in variant_keys:
        s = summarize_variant([r['grades'] for r in combined_results], vk)
        variant_summaries[vk] = s
        print(f"  {vk:28s} n={s['n']:<4} win%={s['win_rate_pct']}  "
              f"exp(gross)={s['expectancy_ticks_gross']}t  "
              f"exp(net1.5t)={s['expectancy_ticks_net_1.5t']}t  exp(net2.5t)={s['expectancy_ticks_net_2.5t']}t")

    print("\n# === bracket variant summary: random baseline (n={}) ===".format(len(baseline_trades)))
    baseline_variant_summaries = {}
    for vk in variant_keys:
        s = summarize_variant([r['grades'] for r in baseline_trades], vk)
        baseline_variant_summaries[vk] = s
        print(f"  {vk:28s} n={s['n']:<4} win%={s['win_rate_pct']}  "
              f"exp(gross)={s['expectancy_ticks_gross']}t  "
              f"exp(net1.5t)={s['expectancy_ticks_net_1.5t']}t  exp(net2.5t)={s['expectancy_ticks_net_2.5t']}t")

    boot = bootstrap_expectancy_gap_ci(variant_summaries[DEFAULT_VARIANT]['_exits'],
                                        baseline_variant_summaries[DEFAULT_VARIANT]['_exits'])
    print(f"\n# === expectancy gap (fires+backfilled - baseline), gross ticks, variant={DEFAULT_VARIANT} ===")
    if boot:
        print(f"  mean_diff={boot['mean_diff']:.3f}t  95% bootstrap CI [{boot['ci95'][0]:.3f}, {boot['ci95'][1]:.3f}] (n_boot={boot['n_boot']})")

    by_build = {}
    for r in fire_results:
        bg = r.get('build_group', 'unknown')
        by_build.setdefault(bg, []).append(r)
    print("\n# === sensitivity: primary-2026-07-03-evening build vs superseded-older build ===")
    by_build_summary = {}
    for bg, recs in by_build.items():
        s = summarize_variant([r['grades'] for r in recs], DEFAULT_VARIANT)
        by_build_summary[bg] = s
        print(f"  {bg:36s} n={s['n']:<4} exp(gross,{DEFAULT_VARIANT})={s['expectancy_ticks_gross']}t")

    if args.out_json:
        def strip_exits(d):
            return {k: v for k, v in d.items() if k != '_exits'}

        out = {
            'meta': {'data_dir': args.data_dir, 'fires_json': args.fires_json,
                     'n_signals_files': len(meta), 'tick': TICK, 'stop_ticks': STOP_TICKS,
                     'target_ticks': TARGET_TICKS, 'be_trigger_ticks': BE_TRIGGER_TICKS,
                     'be_variants': BE_VARIANTS, 'time_stop_variants_s': TIME_STOP_VARIANTS,
                     'cost_scenarios_ticks': COST_SCENARIOS, 'horizon_cap_s': HORIZON_CAP_S,
                     'n_random_per_day_per_side': args.n_random, 'seed': args.seed,
                     'canonical_files_by_day': canonical_by_day},
            'guard_skip_not_armed': {'n_total': len(skips), 'n_filled_proxy': len(filled_skips),
                                     'n_unfilled': len(unfilled_skips),
                                     'unfilled_detail': [{'content_time': str(u['content_time']),
                                                          'side': u['side'], 'reason': u['reason']}
                                                         for u in unfilled_skips]},
            'n_fires_graded': len(fire_results),
            'n_backfilled_graded': len(skip_results),
            'race_3t_before_stop': {'fires': {'k': k_fire, 'n': n_fire, 'rate': rate_fire, 'ci95': ci_fire},
                                    'baseline': {'k': k_base, 'n': n_base, 'rate': rate_base, 'ci95': ci_base},
                                    'two_proportion_z': z, 'p_value_approx': p},
            'expectancy_gap_bootstrap': boot,
            'variant_summaries_fires_and_backfilled': {k: strip_exits(v) for k, v in variant_summaries.items()},
            'variant_summaries_baseline': {k: strip_exits(v) for k, v in baseline_variant_summaries.items()},
            'by_build_group': {bg: strip_exits(v) for bg, v in by_build_summary.items()},
        }
        with open(args.out_json, 'w') as fh:
            json.dump(out, fh, indent=2, default=str)
        print(f"\n# wrote full results to {args.out_json}")


if __name__ == '__main__':
    main()
