# NQ — Day-1 Calibration Diagnostic (Replay 2026-07-06, captured 2026-07-19)

First full-day NQ Rec capture, run on the ES-inherited config (NQ preset: `SignificanceBand=12`,
`DeltaFloor=15`, `MinAbsSize=6`; topbar: MinSize Auto ×1.8, K× 1.5, Persist 1000 ms; Money Mgmt
$600/$1500; AUTO off). **This is a diagnostic + initial-prior derivation, not a validated
calibration** — one day, one regime, zero fires. Same discipline as `calibration-consumption-break-es-day1.md`.

## Capture

- `lr-signals-NQ-20260719-214639.csv` — 27,105 rows, replay 2026-07-06 06:03 → 23:05 (17 h incl. ETH;
  RTH cut = 11,444 rows). Companion `lr-capture-NQ-*.csv` (5.1 MB) + `lr-sessions-NQ.csv` ledger row.
- **Setup split: Break active only 06:03–06:55 ETH (1,390 rows), Reactive the rest of the day** —
  the day therefore measured React's funnel and the RAW input distributions, but left Break's
  fire-path gates (FireFrac / trade-backed / K-window / reload) without a single RTH episode.
- Ledger: Break arms 3, fires 0 · React watches 4, fires 0, abandons 4 (all `AwayDrift`).

## Funnel results

- **React is structurally dead on NQ with its ES-scale defaults.** `ReactiveConfig` has NO
  per-instrument preset (`new ReactiveConfig()` everywhere): `SignificanceBand=60`, `DeltaFloor=30`.
  A ≥60-contract dominant wall exists in **0.23%** of rows (joint arm-eligibility ~0.3 edges/hr → the
  observed 4 watches/17 h). Every watch abandoned `AwayDrift`.
- **Break reached Countdown once in its 52 ETH minutes** (06:37, LONG): frac 0.56, **tradeBacked =
  1.000**, dist 1.5t, z −1.2 (z was the staller; HoldCount never advanced). The proximity/judge/
  trade-backed plumbing from the ES rounds transfers to NQ — the consumption read works.

## RTH input distributions (09:30–16:00, the calibration basis)

| Metric | p50 | p66 | p75 | p85 | p90 | p95 |
|---|---|---|---|---|---|---|
| Dominant wall size | 5 | 5 | 6 | **7** | **8** | 9 |
| \|delta15s\| | 20 | **31** | 40 | 57 | 71 | 94 |
| tapeZ | −0.8 | −0.5 | −0.2 | 0.3 | 0.6 | 0.9 |
| \|tapeAccel\| | 2.1 | 3.6 | 4.9 | 7.6 | 10.2 | 16.1 |
| prints/sec | 3 | 5 | 8 | 12 | 15 | 23 |
| bestBid size | 2 | 2 | 2 | 3 | 3 | 4 |

Dominant-wall pass-rates (RTH rows with a wall ≥ band on either side): ≥6 → 44.1% · ≥8 → 15.7% ·
≥10 → 7.8% · ≥12 → 4.6%. Arm-EDGE proxy (distinct wall-≥band episodes): band 6 ≈ 282/hr ·
band 8 ≈ 166/hr · **band 12 ≈ 55/hr** · React joint eligibility (band, delta, ≤3t, |accel|≥1):
(8, 15) ≈ 7.7/hr · (6, 15) ≈ 21/hr · current (60, 30) ≈ 0.3/hr.

`adaptiveSig` (raw-book feed) maxed at **5** all day — inert below the floor 12, as the
`adaptive_sig_feed_sim.py` corpus run predicted.

## Verdict: enough data for an INITIAL calibration — with a revised conclusion

**The corpus-level hypothesis "SignificanceBand=12 is HIGH for NQ" is REVISED by the RTH edge-rate
view.** 12 sits at ~p97 of wall sizes (vs ES's p83–p90 methodology), BUT NQ walls churn so much
faster that band 12 already produces **~55 arm-edges/hr — inside the ES day-1 acceptance target of
50–100 arms/hr**. Matching ES's percentile (band 6–7) would flood 226–282 arms/hr, 3–4× the ES rate.
Arm-rate equivalence, not percentile equivalence, is the right yardstick here.

### Proposed initial NQ config (day-1 priors — placeholders until fires exist)

| Knob | Current | Proposed | Basis | Confidence |
|---|---|---|---|---|
| Break `SignificanceBand` | 12 | **keep 12** | ~55 arm-edges/hr RTH ≈ ES's 50–100/hr target; fallback 10 (~110/hr) if the next RTH Break capture shows arm starvation | medium (edge proxy — Break never ran RTH) |
| Break `DeltaFloor` | 15 | **30** | 15 ≈ p40 RTH (near-coin-flip, same defect ES day-1 found at 8); 30 = p66 RTH, mirroring ES's 30 ≈ p66 placement. The 07-04 scaling prior (×0.5 flow ratio) was wrong — NQ RTH aggressor flow ≈ ES's, not half | medium (marginal distribution; Countdown-conditional n=3) |
| `MinAbsSize` | 6 | keep 6 | ≈ dominant-wall p75-p85; capture ran MinSize Auto ×1.8 (~4 live) — keep **Auto ON** as the operating mode | medium |
| Detection topbar | — | lock **K× 1.5, Persist 1000 ms, Auto ×1.8** | these were the capture's regime; the numbers above are conditional on them | — |
| `ZFloor` / `ZTrustSeconds` | 1.5 / 0.35 | keep | z is self-normalizing (RTH p95 = 0.9; fires require genuine bursts, the latch bridges jitter) — multiday verdict: never adapt z | high |
| `JudgeTicks` / `AwayTicks` | 2 / 6 | keep, WATCH | zero RTH Countdown data; NQ's tick-velocity may need wider radii — judge from the next Break-RTH capture's veto/dist shapes | low |
| React preset (**new wiring needed**) | ES defaults 60/30 | **`SignificanceBand=8`, `DeltaFloor=30`** (rest unchanged) | band 8 = p90 RTH → joint watch rate ~5–8/hr (~40–60/day, gradeable volume); one knob at a time — the wall gate was the binding constraint, judge `AccelFloor`/proximity only after the funnel breathes | medium |
| Break fire-path gates (FireFrac, MinTradeBackedRatio, ReloadFrac, K/KWindow) | ES values | keep | ZERO NQ RTH episodes — nothing to re-derive from yet | — |

React wiring note: `InstrumentPreset` must grow a `ReactiveConfig` member (compiled switch, same
ADR constraint — no runtime config), and `RadarTab`'s three `new ReactiveConfig()` sites must take
it from the preset. ES keeps today's defaults verbatim.

## What this day could NOT measure

1. **Break's RTH funnel** — the setup dropdown sat on React from 06:55 onward. The arm-rate numbers
   above are edge proxies, not observed Armed transitions.
2. **Any fire-path gate** (quality thresholds) — 0 fires, 0 RTH Countdowns.
3. **Realized R** — AUTO off, no ATM → `lr-fires-NQ` empty. An NQ ATM template (even a cloned ES_3C:
   24t = $120/lote en NQ) is needed before fires become gradeable.

## Next-capture acceptance criteria (mirror of the ES day-1 discipline)

1. **Run Strategy = Break during RTH** with the proposed config (and a separate React-day after the
   preset wiring lands). One Rec session per replay day, distinct content days.
2. Break arms ≈ 50–100/hr RTH. Below ~20 → drop band to 10; above ~200 → raise it; re-derive, don't
   guess.
3. Countdown reachable during RTH with genuine `tradeBacked > 0` near the wall (already proven once
   at 06:37 ETH — must repeat under RTH participation).
4. Fires 0–5/day; run AUTO + a placeholder NQ ATM so every fire lands in `lr-fires-NQ-*.csv` with
   realized R and MFE/MAE (the input the definitive NQ ATM design needs).
5. React (post-wiring): watches ~30–60/day and an abandon-reason MIX — 100% `AwayDrift` again means
   the proximity/away radii are the next binding constraint, not the wall gate.
6. **No threshold gets declared validated under ~15–20 real fires** (ES rounds 3–8 rule). 2–3
   sessions across regimes before locking anything.
