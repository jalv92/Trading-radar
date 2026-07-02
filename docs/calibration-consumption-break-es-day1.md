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

**Fix (commits `34295fe` + `0d9a63a`):** proximity gate in `AdvanceArmedOrCountdown` — the
Countdown/Cooldown decision only runs when `|Mid − WallPrice| < AwayTicks × tick`. While far, the
candidate stays Armed and **re-baselines** `Peak = Min = cur` every tick (Fraction/TradeBacked
read 0 — nothing judgeable yet), so the judgment on arrival spans ONLY the approach window —
far-away cancel churn cannot force an instant pull-veto at the touch (the review caught that
first version, which kept accumulating Peak while far, just deferred the tautology). Reuses
`AwayTicks` — no new knob — and by construction Countdown can only be entered near-touch, which
also resolves the "instant away-abandon on Countdown entry" concern.

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

## Round 2 (post-fix capture, same replay day 2026-06-24)

First capture with the day-1 proximity-gate fix live. Acceptance check against the day-1 criteria:
Countdown reached **exactly once** (criterion 1 satisfied — the bottleneck was real, not the only
one); Armed reached the near-touch judgment band 7 more times without a verdict; fires **0**;
`tradeBacked*` nonzero in only ~0.7% of judged (near-touch) rows — real signal now reaches the gate,
but still thin at this sample size (n=1 Countdown).

Population-level analysis of the day's 543 pull-vetoes found the veto judgment itself was
tautological at distance (same shape as day-1, one gate closer to the wall): 100% of vetoes beyond
2 ticks read `TradeBackedFraction = 0.000` by construction, and the flat `MinDropBand = 3` sat AT
the p90–p95 jitter ceiling (median Drop-at-veto was exactly 3.0 — the threshold and the noise floor
were the same number).

### Validated params (kept as-is)

| Param | Value | Evidence |
|---|---|---|
| `FireFrac` | 0.7 | The only relaxation tested that would have fired on the day's one confirmed-bad episode (forward −10 ticks) — no looser threshold needed. |
| `ReloadFrac` | 0.25 | Correctly vetoed that same episode before it could fire — the reload gate is doing its job, not blocking good setups. |
| `ChopSlowZ` / `ChopAltCount` | keep | Chop pair joint trigger rate 8.6–10.6% — healthy (not over- or under-firing). |

### Structural changes (this round)

1. **`MinDropBand` → `MinDropBandFrac` (0.12 of Peak).** A flat contract count can't scale across
   wall sizes; the old `3` sat exactly at the p90–p95 jitter ceiling (median Drop-at-veto == 3.0),
   so it was measuring noise, not signal. 12% of Peak clears that ceiling at median wall sizes.
2. **`JudgeTicks` (2) — judge radius inside `AwayTicks`.** `TradeBackedFraction` was 0.000 in 100%
   of vetoes beyond 2 ticks (`TradedAt` only matches prints AT the wall) — verdicts now render only
   inside this radius; Peak/Min keep accumulating between `JudgeTicks` and `AwayTicks` so the
   at-touch judgment still sees the real approach drop, it just doesn't render a premature verdict.
3. **K-dwell on the pull-veto path + `DistTicksValid` including Cooldown.** The veto now requires K
   consecutive judgeable sub-ratio ticks (same debounce StepCountdown already had on the fire path),
   and `ctrl*DistTicks` reads 0 on 100% of veto rows because `IsIdentityHeld` excluded Cooldown —
   blind exactly at the judgment moment. `WallPrice` is never cleared on the veto transition, so the
   veto row can now log the real judgment distance.

### Review notes (round-2 gate)

- The far-rebaseline branch also zeroes `HoldCount` now (reviewer reproduced a stale dwell count
  surviving a one-tick gap past `AwayTicks` → veto committed K−n ticks early).
- `ctrl*DistTicks` is nonzero for the WHOLE 10s Cooldown window (mid-to-vetoed-wall distance), not
  just the veto row — the veto-row value is the judgment distance; later rows track the drift.
- **Round-3 watch item:** the K-consecutive dwell is gameable by a timed pull → partial-refill →
  pull cycle (dwell resets on each refill; the side just stays Armed and never vetoes — bounded,
  can't cause a fire). If the next capture shows that pattern on real walls, consider "N sub-ratio
  ticks in the last M" instead of pure consecutive.
- Cosmetic: the Cockpit countdown bar can now sit past the `FireFrac` marker while plainly Armed at
  3–5 ticks (no verdict zone) — banner stays truthful; retouch only if it misleads in Replay.

### Open investigations (NOT coded this round)

- **8.3% non-monotonic CSV timestamps.** The lenses' race theory (two writers interleaving rows)
  partially contradicts `MaybeRunEngine` as written — needs a real trace before touching anything,
  not a guess-and-fix.
- **Sub-1s Waiting↔Armed flicker in ~53–57% of episodes.** Two conflicting root-cause theories in
  play — `SignificanceBand` hysteresis (wall oscillating around the threshold) vs. zero-size depth
  blips (a momentary bad read, not a real pull) — diagnose which one it actually is before coding
  either fix.

## Round 3 (5-day AUTO run, zero positions)

Five AUTO replay days. **3 engine fires, ZERO positions opened.** The decision trail was lost
almost immediately: `TryAutoFire`/`SetAutoArmed`/`MaybeAutoCancel` only ever called `Diag()`, which
writes to NT8's Output window — nobody was watching it live, and NT8 doesn't persist it. By the
time the gap was noticed, there was no way to reconstruct *why* 3 fires produced 0 orders. This is
an **unresolvable-blindness finding**, not a diagnosis: the round closed without being able to pick
a single root cause from the candidates below.

### Ranked hypotheses (unconfirmed — this is why the observability package exists)

1. **Never-armed at fire time** — `TryAutoFire`'s guard 1a (`if (!_autoArmed) return;`) was a silent
   return with no Diag at all. If AUTO wasn't actually armed (checkbox state lost, arm never
   completed, disarmed by a guard the operator didn't see) at the moment any of the 3 fires landed,
   every one of them would vanish with zero trace — the single most likely candidate precisely
   because it was the one guard that couldn't leave evidence.
2. **Stale/busy guard eating the fire** — guard 2 (`_activeLimit != null || !IsFlat(...)`) or the
   anti-stale guard 4 skipping silently-enough-to-miss in the Output tab scroll.
3. **ATM lost at fire time** — guard 5 (`_atmSelector.SelectedAtmStrategy == null`) force-disarms;
   plausible if the selector's async repopulate deselected the template between arm and fire without
   raising `DropDownClosed` (the exact gap F20's own review flagged as unverified).
4. **Pre-build / stale package** — the running add-on binary not actually containing the code being
   reviewed (recompile not picked up, per the existing NT8 caveat that open Add-On windows don't
   refresh on recompile).
5. **Platform-level order/ATM failure** — `SubmitRaw`'s `CreateOrder`/`Submit`/`StartAtmStrategy`
   throwing or silently rejecting in a way that never reached the Output tab (buried in NT8's own
   log, not this control's).

None of these could be ranked with confidence from Output-tab scrollback alone — that is the whole
point of the fix below.

### Fire-at-5-ticks bug (code-verified, fixed this round)

`StepCountdown`'s only distance check was the `AwayTicks` (6-tick) abandon. Entry into Countdown
requires `< JudgeTicks` (2 ticks, via `AdvanceArmedOrCountdown`), but once inside, `HoldCount` could
keep accumulating while price drifted away — a fire could render anywhere up to `AwayTicks` out.
**Real fire 1 (SHORT, 2026-06-22 20:12)** entered Countdown at 1.5 ticks and fired at 5.0 ticks after
a fast snap during the 150ms K-dwell — pre-staged 5 ticks off the wall, mechanically the worst of
the 3 fires (the other two weren't traceable well enough to measure their fire-time distance at
all — another symptom of the blindness above).

**Fix:** `Engine/ControllerStateMachine.cs`, `StepCountdown` — added a `nearWall` term to the `pre`
conjunction, reusing `JudgeTicks` (no new knob): `Math.Abs(inp.Mid - c.WallPrice) / _tick <
_cfg.JudgeTicks`. A drift outside the judge radius mid-dwell now resets `HoldCount` via the existing
`if (!pre) { c.HoldCount = 0; ... }` path — consistent with how the veto-dwell side already treats a
drift-away as "nothing judged, restart the count." Fire must be judged where the order will actually
be staged: at the wall. Regression test:
`Countdown_drift_beyond_judge_radius_resets_hold_and_blocks_fire_until_back_near_wall`.

### Config: HOLD (grid re-confirms round-2, does not override it)

A parameter grid was run across the round-3 data before any code changes. **Every one of the 54
cells** (loosening `DeltaFloor`/`ZFloor` combinations) produced a **win-rate ≤ 46%**, and **loosening
either Delta or Z made forward returns worse, not better** — the opposite of what a "gates too tight"
theory would predict. With only **n=3** real fires feeding the grid, this is far too thin to move any
threshold on — it argues against loosening, it does not argue for tightening either.
**`ControllerConfig`'s thresholds are unchanged this round.** The blocker isn't threshold tuning; it's
that 3 fires is not a sample, and the tooling to collect a real one (the observability package below)
didn't exist until now.

### Observability package shipped this round

- `RadarChartTrader.cs` guard 1a now Diags before returning (`"AUTO skip — not armed at fire
  time."`) — the #1 blind spot above can never recur silently.
- `SetAutoArmed` now Diags the arm transition too (previously disarm-only): `"AUTO armed — account
  X, ATM Y."`.
- New persistent, append-only CSV: `lr-auto-<instrument>-<yyyyMMdd-HHmmss>.csv` in the same
  `MyDocuments/NinjaTrader 8/LiquidityRadar` folder as `lr-signals-*`. Schema:
  `time,event,side,price,mid,detail`, event ∈ `{arm, disarm, prestage, guard_skip, submit,
  atm_attach, order_update, auto_cancel}`. **Independent of the Rec toggle** — it captures every AUTO
  decision whether or not a capture session is running, so a repeat of "3 fires, 0 evidence" is
  structurally impossible.
- `lr-signals-*` gained an `autoArmed` column (from `RadarChartTrader.IsAutoArmed`), so every engine
  snapshot row can be cross-referenced against whether AUTO was actually armed at that instant —
  directly answers hypothesis #1 without needing the AUTO log at all.

### Next-run acceptance criteria

1. **The AUTO log is complete** — every fire in `lr-signals-*` has a matching `prestage` row in
   `lr-auto-*`, and every `prestage` row resolves to either a `guard_skip` (with a specific guard
   named) or a `submit`. No fire may vanish with zero trace again.
2. **`autoArmed` column present and sane** — nonzero true/false transitions align with the operator's
   actual arm/disarm clicks and the `arm`/`disarm` rows in the AUTO log.
3. **100% of fires have `DistTicks < 2`** — the near-wall fix above verified against real data, not
   just the unit test.
4. **At least 1 order reaches a terminal state** (`order_update` shows Filled, Cancelled, or
   Rejected) — proof the pipe from fire to broker/Sim actually completes end-to-end at least once.
5. **`HoldCount` visibly progresses 0→K in the AUTO log/sig CSV** across at least one real Countdown
   — proof the K-dwell is being exercised by real data, not just by tests.
6. **No threshold gets declared "validated"** under roughly 15–20 real fires. n=3 was already too
   thin for the grid above; the same bar applies to anything this round's data might suggest.
