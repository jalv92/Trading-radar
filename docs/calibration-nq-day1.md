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

---

# Day-2 addendum (replay 2026-07-07, captured 2026-07-19 22:09 — FIRST run on the day-1 priors)

Full-day capture `lr-signals-NQ-20260719-220946.csv` (27,486 rows, replay 07:05→22:00, **Break
active 100% of the day** — the exact experiment day-1 lacked). Build verified live: every fire's
|delta15s| ≥ 41 (the old floor 15 would have fired earlier), preset label current.

## Funnel — the day-1 priors work

| Layer | Full day | RTH | ES reference |
|---|---|---|---|
| Armed edges | 1,141 (~76/hr) | 817 (**~126/hr**) | target 50–100/hr — slightly hot, same order |
| Countdown edges | 175 | 150 | ES saw 8–35/day — NQ's funnel BREATHES |
| Fires | 4 | 4 | 0–5/day expected ✓ |

Kill-chain of the 171 dead Countdowns (last-row gate view): **frac<0.6 → 37%**, **delta<30 → 35%**,
z-unlatched-view → 23%, other (reload/away/vanish) → 6%. Same hierarchy ES measured (frac #1,
delta #2) — the funnel shape transfers. Hold progression (1s and 2s) now visible in the CSV.

## The 4 fires — all full-confluence, 3/4 favorable with huge forward runs

| # | Side | Time | frac | tb | delta | z | dist | fwd 30s/60s/120s (ticks, signed) |
|---|---|---|---|---|---|---|---|---|
| 1 | L | 12:00:49 | 0.91 | 1.00 | +84 | 1.01 | 1.0t | **+44.5 / +62.5 / +110.5** |
| 2 | S | 13:10:14 | 0.98 | 0.96 | −137 | 1.40 | 1.0t | **+74.5 / +111.5 / +75.5** |
| 3 | L | 15:34:39 | 0.91 | 1.00 | +41 | 1.22 | 0.0t | −53.5 / −88.0 / −169.5 (false break) |
| 4 | L | 15:54:58 | 0.94 | 1.00 | +58 | 1.44 | 1.5t | +37.5 / +69.0 / +47.0 |

n=4 — NO edge claim. But the GEOMETRY is the headline: winners ran **60–110 NQ ticks (15–28 pts,
$300–550/contract) within 1–2 minutes**, and the loser ran just as far against. NQ Consumption-Break
outcomes are an order of magnitude larger than ES's (ES median MFE was 9.5t) — the NQ ATM must be
designed for this scale (wide targets or trail; a cloned ES_3C's 48t first target would have been
hit by fires 1–2 but is likely still too tight for the runner leg; the 24t stop ≈ survivable on
fires 1/2/4, instantly dead on fire 3).

## AUTO observability loss — 4 gradeable trades missed

AUTO armed at 07:05 (`arm` row, Playback101 + ATM "AtmStrategy"), yet **all 4 fires logged
`guard_skip — not armed at fire time`, with NO disarm row in between.** Code path check: every
FORCED disarm writes a `disarm` CSV row, but a **human uncheck (reason == null) writes nothing** —
so the consistent story is a manual AUTO uncheck sometime after 07:05 (silent in the CSV by
design). Cost: zero `lr-fires-NQ` rows, zero realized-R. Follow-up candidates: (a) log human
unchecks too (`disarm — human uncheck`), closing the last silent path; (b) next run, leave AUTO
armed — the Cap/day + Money Management already bound the exposure.

## Next-capture asks (in order)

1. **Same setup, AUTO left armed** with an NQ ATM (clone ES_3C to start; expect the runner target to
   need widening once ~10+ fires exist) → `lr-fires-NQ-*.csv` gets realized R + MFE/MAE.
2. 2–3 more distinct Break-RTH days before moving any threshold (ES rounds 3–8 rule; nothing
   validates under ~15–20 real fires).
3. Arm rate at 126/hr is ~25% above target: leave band 12 alone for now; only if Countdown quality
   degrades across days consider 13–14 (walls p95 was 13 today).
4. A React day (band 8 / delta 30) once Break's fire pipeline is producing graded rows.
