# Consumption-Break — Day-1 Calibration (ES, Replay 2026-06-22)

First full-day Rec capture of the Consumption-Break setup. **This is a diagnostic, not a
calibration** — one day, one instrument, one regime (a −226-tick trend-down day with pronounced
ask/bid depth asymmetry). Analysis: 3-lens workflow (trade-backed diagnosis, threshold
distributions, episode counterfactuals) + synthesis, 2026-07-01.

## Capture

- File: `lr-signals-ES-20260701-194217.csv` — 14,932 snapshots, RTH 09:30–19:16, ~1 row / 2 s
  (heartbeat throttle; the engine runs 20 Hz internally between rows).
- Controller states observed: Waiting / Armed / Cooldown only. **Countdown: 0 rows. Fires: 0.**
- `tradeBackedLong` nonzero in 5/14,932 rows (max 0.17); `tradeBackedShort` never — while
  `consumeFrac*` reached 0.93. Pull-veto Cooldown rows: 1,829 long / 2,271 short.

## Root cause (confirmed, not statistical hand-waving)

`AdvanceArmedOrCountdown` judged the pull-veto (`TradeBackedFraction >= MinTradeBackedRatio`,
else Cooldown) as soon as `Drop >= MinDropBand`, with **no distance precondition**.
`BookMirror.TradedAt` only matches prints AT the wall price (± half tick). A wall thinning while
mid sits several ticks away therefore reads trade-backed = 0 **by construction** — the gate was
tautologically unsatisfiable at distance:

- 94.2% (long) / 91.2% (short) of the day's vetoes fired with the wall > 2 ticks from mid;
  median veto distance 6.1 / 6.5 ticks.
- Concrete trace (rows 253–259, 09:39): wall 7593.00 drops 108 → 30 (72% consumed-frac) while
  mid sits at 7594.13 (4.5 ticks away) → veto. Mechanically impossible to be trade-backed.
- `ConsumptionTracker.Read` / `TradedAt` / `InferAggressor` were read line-by-line: no
  arithmetic/matching defect. Missing precondition, not broken measurement.

**Fix (commit `34295fe`):** proximity gate in `AdvanceArmedOrCountdown` — the Countdown/Cooldown
decision only runs when `|Mid − WallPrice| < AwayTicks × tick`; far thinning stays Armed
(Peak/Min and Fraction/TradeBackedFraction telemetry keep updating). Reuses `AwayTicks` — no new
knob — and by construction Countdown can now only be entered near-touch, which also resolves the
"instant away-abandon on Countdown entry" concern.

## Config decisions (day-1)

| Param | Value | Change | Confidence | Evidence (one line) |
|---|---|---|---|---|
| Proximity gate | reuse `AwayTicks` (6) | **NEW** | high | Root cause above. |
| `DeltaFloor` | **30** | 8 → 30 | medium | At 8 the gate passed 50.1%/36.3% of rows (coin-flip); 30 ≈ p66/p82. Marginal proxy — re-verify Countdown-conditional. |
| `SignificanceBand` | 60 | keep | medium | ≈ p83–p90 of dominant-wall sizes (~73 arms/hr combined). Regime-unstable intraday (afternoon long-side p90 ≈ 46) — do NOT adapt off one day. |
| `FireFrac` | 0.7 | keep | medium | Reached in ~1% of armed episodes — appropriately rare, not the bottleneck. |
| `MinTradeBackedRatio` | 0.6 | keep (placeholder) | low | Input was structurally ~0 all day; nothing to tune against until post-fix data. |
| `ZFloor` | 1.5 | keep | low | Global pass rate 1.26% (a floor — 2 s rows undersample the 20 Hz EWMA). Direction-agnostic by design; DeltaFloor is the directional gate. |
| `K`, `ReloadFrac`, `Cooldown`, `MinDropBand` | keep | — | low | Check-sites never executed (0 Countdown rows) or unreadable at 2 s cadence. MinDropBand likely too small (~3–4% of median wall) — re-derive as fraction of Peak from post-fix data. |
| `ChopSlowZ` / `ChopAltCount` | keep | — | low | z-leg alone passes 71.7% of rows (over-trigger risk) but alternations weren't logged — recalibrate BOTH together next capture. |

## Capture upgrades shipped (same commit)

- New CSV columns: `tapeAlternations`, `ctrlLongHold`, `ctrlShortHold`, `ctrlLongDistTicks`,
  `ctrlShortDistTicks`, `ctrlLongCooldownUntil`, `ctrlShortCooldownUntil` (backed by new
  `ControllerOutput` fields: HoldCount / DistTicks / CooldownUntil per side).
- **Event-triggered sig rows**: a row is written immediately on any per-side state change (in
  addition to the 2 s heartbeat). Day-1 proved a full arm → 61%-drop → veto cycle can complete
  invisibly between two heartbeats (rows 9458–9459, 14:52).
- `ctrl*CooldownUntil` is NOT reset when a side returns to Waiting — it shows the LAST cooldown's
  expiry; cross-reference the state column when reading the CSV.

## Next-capture acceptance criteria

1. **Countdown > 0 rows** somewhere in the day. Still exactly 0 → the proximity gate wasn't the
   (only) bottleneck; re-diagnose before touching thresholds.
2. Arms ≈ same order of magnitude (~50–100/hr combined). A 5× deviation = different regime, not
   a config defect.
3. Countdowns/day: low single digits to a couple dozen (near-touch Armed time was ~3–4%).
   Hundreds = gates too loose. Cross-check every Countdown entry starts within `AwayTicks` via
   `ctrlDistTicks`.
4. Fires/day: 0–5 expected at the current stack. A 0-fire day is fine IF Countdown is reachable
   and its gates show non-tautological distributions.
5. `tradeBacked*` must show genuine nonzero values near Countdown — the sanity check that the
   fix unblocked real reads.
6. Recalibrate `ChopSlowZ`+`ChopAltCount` jointly from `tapeAlternations`; measure K against
   `ctrl*Hold`; re-derive `MinDropBand` as a fraction of Peak.
7. 2–3 more sessions across regimes before locking anything.

## Disclaimer

Zero trades occurred; no PF/Sharpe/expectancy layer applies yet. Every downstream-gate number
(FireFrac, DeltaFloor, ZFloor, K, ReloadFrac, Chop*, Cooldown) is a marginal-distribution proxy
measured while the funnel was blocked upstream — re-derive once Countdown is reachable.
