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

## Round 4 (first fully-observable AUTO day, 2026-06-22 replay)

The observability package from round 3 worked: the AUTO log is finally readable end-to-end,
including the 2 failed arm attempts that round 3 could only hypothesize about — **"silent arm
failure" is CONFIRMED as a real UX gap**, not a theoretical guard. Operators can now see an arm
attempt fail instead of it vanishing with zero trace.

With the pipe now visible, the funnel got measured properly for the first time: **20+ Countdown
episodes**, `tradeBacked` **never failed inside Countdown (0/29 rows)** — the day-1 tautology and
its round-2/round-3 follow-on fixes are holding — and full-confluence moments genuinely **exist**
(e.g. frac 0.92 / tb 1.00 / delta 195 / z 2.91 / dist 0.5 — every gate passing simultaneously).
`HoldCount` reached **2 of K=3** at its best point across the whole day. **Zero fires.**

### Root cause: persistence vs 20Hz jitter, not levels

The binding constraint is the CONSECUTIVE-hold requirement, not any threshold. `pre` must hold for
3 consecutive 20Hz engine ticks (150ms), and `tapeZScore`/`Fraction` jitter tick-to-tick even during
genuine sustained consumption — a single dip resets `HoldCount` to 0, so the countdown never
survives to a 3rd consecutive pass despite the underlying setup remaining intact. Thresholds are
deliberately NOT moving: the round-3 grid already proved loosening Delta/Z makes forward quality
worse, not better, so tightening the PERSISTENCE rule (not the gates) is the fix.

**Fix (this round):** `Engine/ControllerStateMachine.cs` — replaced the consecutive-hold `HoldCount`
bookkeeping in `StepCountdown`'s fire path with a K-window shift register (`Candidate.PassBits`):
every judged tick shifts in a pass/fail bit (capped to the last `KWindow` ticks), and fire requires
the window to hold `>= K` set bits AND the CURRENT tick to be a passing, near-wall tick. `K` stays
3; new `ControllerConfig.KWindow = 5` (3-of-5) tolerates 1-2 jitter dips inside a sustained-
confluence run instead of demanding perfection tick-to-tick. The Armed-phase veto-dwell counter
(`HoldCount`, round-2, still-consecutive by design) was kept fully separate — the two never share
state, verified by zeroing both explicitly at the Armed→Countdown transition. Regression tests:
`Fires_on_3_of_5_window_tolerating_a_single_tick_z_dip`,
`Never_fires_when_max_passes_in_any_5_window_stays_below_k`,
`Does_not_fire_on_a_failing_current_tick_even_with_two_prior_passes_in_window`,
`Single_broken_snapshot_does_not_reset_the_window_fires_on_recovery_within_kwindow` (renamed/
reworked — its old assertion, that a single broken snapshot resets the count to 0, is no longer the
behavior; that IS the round-4 fix).

### Config: HOLD, with one non-move noted

`ControllerConfig`'s existing thresholds (Delta/Z/FireFrac/MinTradeBackedRatio) are unchanged this
round — only the persistence rule moved. Delta near-misses were observed (28 vs floor 30, -19 vs
floor -30) but **NOT acted on** at n=2 — far too thin to move a threshold on, same bar as round 3's
n=3 grid.

**Round-5 correction:** those "delta near-misses" were misread — the SIGN was against the setup
(delta +28 of BUYERS on a SHORT candidate that requires ≤ −30; −19 of sellers on a LONG requiring
≥ +30). They were direction-opposed flow, not narrow misses; the delta gate correctly blocked
counter-flow fires both times. `DeltaFloor` is validated by them, not questioned.

### Next-run acceptance criteria

1. At least 1 real fire under the new K-window rule, ideally several, to see whether persistence was
   in fact the whole blocker or whether a fire still needs to clear the order pipe end-to-end.
2. `HoldCount`/`ShortHoldCount` in the AUTO log/sig CSV visibly shows a jitter-tolerant progression
   (e.g. 1, 2, 1, 2, 3) instead of resetting to 0 on every dip — confirms the window is live on real
   data, not just in tests.
3. ~~Re-open the Delta near-miss question~~ Resolved by the round-5 correction above — closed.

## Round 5 (second observable AUTO day — same replay day 06-22, stopped 15:17)

AUTO armed all day (log clean: one arm event, zero fires, zero guard skips — the machine had nothing
to act on). Countdown gate audit of all 21 rows matched round 4's shape exactly, with one decisive
addition: **the run stopped at 15:17, and the day's only two full-confluence moments sit at 15:47
and 16:06** — the segment run simply contained no firing moment at the round-4 bar.

**`FireFrac` 0.7 → 0.6 (calibration-acceleration, Sim).** Across the two observable days, every
episode with ALL other gates green (delta −309/+524, z > 1.5, trade-backed 1.00, at the wall)
arrived at consumption 0.62–0.68 and died on FireFrac alone (10:51 SHORT, 13:19 LONG). The round-3
grid had already singled out 0.6 as the only volume lever without a clear quality cost. At 0.6 this
run projects ~2 fires. Re-derive once ~15–20 real fires exist; not a validated-quality claim.

**Deterministic prediction for the next run** (falsifiable end-to-end pipeline test): replaying the
SAME day (2026-06-22) through ~16:10 must produce fires around 10:51 (SHORT), 13:19 (LONG) at the
new FireFrac, and near 15:47 / 16:06 even at the old bar — each leaving a complete
prestage→submit→order_update lifecycle in the AUTO log.

## Round 6 (window-blink root cause — the big one)

### Verdict

**95-96% of ALL Armed/Countdown candidate resets this session were phantom blink-abandons**, not
real setup failures. `RadarTab.SizeAtPrice` gated its match on `nodes[i].InWindow`, so the instant
a pinned wall's `RadarNode.InWindow` flipped false (a momentary slide outside the MBP-10 window),
the lookup fell through to `return 0` even though `LiquidityMemory.MarkBlind` only clears the
`InWindow` flag — `LastKnownSize` is untouched and still the last real observation. That phantom 0
fed into the engine's `cur <= 0` abandon check and destroyed the candidate regardless of distance
from the wall or how mature the setup was. The 10:51:10.9 all-gates-green SHORT fire (frac 0.92, tb
1.00, delta −309, z 2.91, at the wall — the exact episode round 5 flagged as dying on `FireFrac`
alone) actually died 792ms later from this blink kill chain, not from consumption falling short.
The identity-hop guard added in an earlier round (the "wall moved ≥1 tick, abandon" check) was
never the culprit — it measured **0 firings across ~7,900 resets** this session. The blink was the
whole story.

### Fix: bounded blind-trust in the identity feed

New static helper `WallTracker.TrustedSize(bool inWindow, double ageSeconds, long lastKnownSize,
double blindTrustSeconds)` (`Engine/WallTracker.cs`) — trusts `LastKnownSize` while `inWindow`, OR
while blind for less than `blindTrustSeconds`; returns 0 only once the blind window is exceeded.
`RadarTab.SizeAtPrice` now calls this instead of gating the match on `InWindow` (the old gate meant
a blind node was never even found, so the trust decision never had a chance to run). New
`RadarTab.BlindTrustSeconds = 1.0` (`const double`, explicitly flagged placeholder — calibrate next
round, same as `ControllerConfig.KWindow` in round 4).

**Why unconditional trust was rejected** (i.e. why not just drop the InWindow check entirely and
always read `LastKnownSize`): a genuinely pulled wall would then freeze the fed size forever instead
of decaying to 0, and `Fraction`/`Peak` in the Controller are seeded off that fed wall size — an
unbounded trust could let a stale, long-gone wall keep a Countdown alive indefinitely. The bounded
window catches that: past `BlindTrustSeconds` the feed drops to 0 and the existing abandon/reload
logic takes over exactly as it did before this fix. Separately, `TradeBackedFraction` is computed
off real trade prints (not the wall-size feed), so even during the trust window no fire can complete
on fabricated consumption — a fire still requires real tape evidence, the trust window only protects
the wall-identity/size half of the setup from a one-tick observation gap.

**Deliberate asymmetry, not an oversight:** the dominant-wall recompute loop in `MaybeRunEngine`
(picks which wall is biggest above/below mid, to arm NEW candidates on) still gates strictly on
`InWindow` — unchanged. Picking a new wall to arm on must see it live right now; tracking an
already-armed candidate's own wall through a momentary blink is a different job, handled entirely by
`SizeAtPrice`/`ResolveWallFeed`/`TrustedSize`. Both spots are commented to say so.

### `AgeSeconds` semantics — verified before trusting them

Confirmed against `Engine/LiquidityMemory.cs` (`ObserveLive` sets `LastSeen = now` on every
observed tick; `MarkBlind` only clears `InWindow`, never touches `LastSeen`) and the `Snapshot`
projection (`AgeSeconds = (now - LastSeen).TotalSeconds`, `Engine/Primitives.cs` doc comment "since
lastSeen"): `AgeSeconds` sits at ~0 the entire time a node is observed and only starts climbing once
it goes blind. That is exactly the semantics `TrustedSize` needs (age-since-last-real-observation),
confirmed by reading the code rather than assumed.

### Hardening: sig CSV provenance (`src`, `seq`)

Two new trailing columns on `lr-signals-*`: `src` (`'D'` when the engine run was triggered from the
depth handler, `'T'` from the trade handler — threaded as a `char` parameter through
`MaybeRunEngine`'s two call sites) and `seq` (a monotonically incrementing `long`, `++_engineRunSeq`,
incremented inside the engine-run critical section — i.e. only on ticks that actually ran the
engine, not on throttled/rebased ticks). This instruments, but does NOT fix, the confirmed-but-
deferred timestamp defect below.

### Deferred defect (confirmed real, 0 lost fires measured, NOT patched)

`MaybeRunEngine`'s `deltaMs < 0` branch (small backward replay steps / out-of-order feed ticks)
re-bases `_lastEngineRun = now` and returns without running the engine. This re-clears the ~20Hz
throttle on every sub-second out-of-order stamp — real, confirmed, but measured **0 lost fires**
from it this session. Left exactly as-is, with a one-line comment pointing at this section: do not
patch until the `src`/`seq` trace confirms whether the physical source is Replay itself or the feed
ordering, rather than guessing at a fix for a defect that hasn't cost a single fire yet.

### Acceptance criteria for the next run

1. Re-running replay 2026-06-22 through 10:51–10:56 must produce the SHORT episode firing, not
   resetting at 10:51:11.7 as it did this session.
2. Hold progression 0→1→2→3 (or whatever K is that round) must be visible through a FIXED wall
   identity — no phantom identity churn from a blink resetting the candidate mid-hold.
3. `ctrlWallAboveSz`/`ctrlWallBelowSz` must stay nonzero through blinks shorter than
   `BlindTrustSeconds`, and drop to 0 for blinks at or beyond it — the CSV should show this directly.
4. The blink-abandon share of all resets must drop from ~95% to near-zero on the same replay day,
   while genuine pulls (real 0s past the trust window) still abandon correctly.
5. Any new fire gets audited for trade-backed integrity at the fire tick — the trust window must
   never be the thing that let a fire fire.
6. NO threshold retuning off this round's n=1 kill-chain finding — `BlindTrustSeconds = 1.0` stays a
   placeholder until several real sessions establish a real blink-duration distribution.
7. Re-measure duplicate/non-monotonic sig-CSV row rates using the new `src`/`seq` columns BEFORE
   touching the round-6-deferred `deltaMs < 0` throttle branch — instrument first, patch second.

## Round 7 (final-gate calibration on real Countdown episodes)

### Kill-chain shares (n=65 killed Countdown episodes, 4 captures)

- **Reload veto — 34% of kills, working as designed.** Among ITS kills, 18–21% are good
  (legitimate defense reload) vs **63–71% false** (the reload band is too wide, but every
  loosening tested in the round-3 grid trades that false share for worse quality elsewhere — see
  HOLDs below). Not touched this round.
- **Vanish/identity abandon — 60% headline share, but 36/39 sampled kills were pre-fix
  contaminated** (the round-6 blink false-positive, or an earlier reset path since closed) — once
  those are excluded the real vanish share collapses; it is not the round-7 story.
- **z-coincidence — the proximate staller in 63% of the remaining kills.** The 1s-trailing
  TapeZScore window swings as low as −1.86 within ~236ms of a tick where every OTHER confluence
  term (Fraction, TradeBackedFraction, delta, near-wall, !chop) was already green — a window-
  statistic edge effect, not a real change in tape speed. This is Change 1.

### The honest reset

Forward-return arbitration against the 4 flagship episodes (10:51 SHORT, 13:19 LONG, plus the two
round-5 15:47/16:06 moments) found **0 clean good-breaks** — every one that would have fired under
the round-5/6 gates was, on a forward-return check, a false break, a reload-vetoed defense, or the
z-coincidence staller above with no way to tell which without the fire actually happening. Round 7
does NOT claim a validated edge. Expected outcome of these three changes: **~2–4 fires/day at
coin-flip quality (~1:1 good:false)** — the point is observability to grade real fires against real
forward returns, not a claimed win rate. Treat every fire from this build as a labeled sample, not
a signal.

### The three changes

1. **z-latch (`ControllerConfig.ZTrustSeconds`, `Candidate.LastZPassTime`).** Bridges a genuine
   `TapeZScore >= ZFloor` pass across a bounded wall-clock window (mirrors the shipped
   `BlindTrustSeconds` precedent) instead of requiring the pass on the exact SAME tick as every
   other confluence term. `ZFloor` itself is UNCHANGED — this fixes the coincidence requirement,
   not the level (round-3 grid: lowering `ZFloor` costs quality). Reset alongside `PassBits` at
   every site that already zeroes it (identity/vanish, retention re-baseline, reload veto, away,
   `!nearWall` drift) plus the Armed→Countdown entry — the latch's lifetime pins 1:1 to the
   `PassBits` invariant, no separate lifecycle to get wrong.
2. **`ControllerOutput.Long/ShortPeak/Min` exposure.** Verbatim candidate `Peak`/`Min` surfaced for
   the sig CSV — the tracked consumption bounds behind `Fraction`/`TradeBackedFraction` were
   previously invisible to calibration.
3. **`RadarTab` hold-triggered capture rows.** A third sig-CSV write trigger fires the instant
   either side's `HoldCount` moves, on top of the existing 2s heartbeat and state-change triggers.
   Measured: median 38-engine-tick gaps between Countdown rows, and **HoldCount had NEVER been
   observed >0 across 241/241 captured rows** despite the engine tracking it every run — the
   heartbeat/state-change cadence was silently skipping the exact ticks that matter. This is the
   highest-leverage observability change this round and the prerequisite for grading everything
   else: without it, a Countdown that holds for 2 judged ticks and then reload-vetoes leaves zero
   trace that it ever got that far.

### Explicit HOLDs (measured, not touched this round)

- **`ReloadFrac` 0.25.** Every loosening tested in the round-3 grid admits a 63–71% false-break
  share among the reload veto's kills — the veto is doing its job at the current width.
- **`ZFloor` 1.5.** The z-latch fixes the SAME-tick coincidence requirement; lowering the level
  itself was already measured (round-3) to cost quality independent of the coincidence problem.
- **`KWindow`/`K` 5/3.** Unchanged 3-of-5 persistence window (round-4) — not implicated in this
  round's kill-chain analysis.
- **`FireFrac` 0.6 — flagged, not fixed.** Its round-5 justification (the only quality-neutral
  volume lever in the 54-cell grid) was derived from threshold-crossing counts, not fired-episode
  forward-return outcomes, and that justification failed forward-return arbitration this round.
  Re-derive from actual fired-episode outcomes once this round's instrumentation produces enough of
  them — not touched now because there is nothing yet to re-derive it FROM.

### Acceptance criteria for the next run

1. **Instrumentation gate first.** `ctrlLongHold`/`ctrlShortHold` must show a value `> 0` mid-
   episode in the sig CSV — if every row still reads 0 outside a fire/veto tick, the hold-triggered
   write isn't working and nothing else in this list can be graded.
2. Replaying 10:51 (SHORT) must reach `HoldCount >= 2` at the tick that was previously the z-fail
   staller — the z-latch bridging that tick — but the episode may still legitimately end in a
   reload veto; a Cooldown outcome is NOT a regression here, only a frozen HoldCount is.
3. Replaying 13:19 (LONG) must STILL end in Cooldown/no-fire. If it fires, the z-latch leaked past
   a tick it should not have bridged (an over-wide `ZTrustSeconds`, or a missed reset site) —
   treat as a bug, not a new signal.
4. Quality bar: roughly 1:1 good:false across the fires this build produces. If it comes in worse
   than 1:2, the fix is to shorten `ZTrustSeconds` alone — do not touch `ZFloor`, `FireFrac`, or
   `ReloadFrac` off this signal, per the HOLDs above.
