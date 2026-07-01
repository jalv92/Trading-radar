# Liquidity Radar — Consumption-Break Setup & Controller Spine — Design Spec

- **Date:** 2026-07-01
- **Status:** Approved design (brainstorm), pre-implementation
- **Type:** Evolution of the NinjaTrader 8 Liquidity Radar Add-On (floating window)
- **Scope:** **The radar only.** The `AbsorptionScalper` strategy is **out of scope** and untouched.
- **Supersedes / extends:** `docs/specs/2026-06-29-radar-cockpit-design.md` (the Cockpit spec). This spec **demotes the Cockpit's per-tick weighted-average verdict** and replaces it with a stateful setup detector. The engine, Aurora identity, threading model, and L2 constraints from the prior specs all stand.
- **Source research:** two tape-reading videos (`QGoLN96KIDI` breakouts, `HB1IbyuJ37s` hidden buyers/sellers) + a tape-scalping-by-control summary, distilled and mapped to ES in `tmp/synthesis-full.json` (workflow `radar-tape-brainstorm-research`).
- **Author:** Javier + Claude (brainstorm)

---

## 1. Summary

The Cockpit answers *"where is pressure loaded"* as a **memoryless per-tick weighted average** of five signals. Its Net% **flips long↔short every tick** because three of its five signals (imbalance / inside-thin / air-pocket) are nested subsets of the same latent variable — instantaneous book skew — which is also the noisiest, most spoofable input. Javier's verdict: *"eso va muy rápido y no me está aportando mucho; lo que más me ha aportado es saber cuántos contratos están esperando en un nivel."*

This spec replaces the oscillating meter with a **defined, stateful entry setup** that fires **once** on a confirmed change-of-control and then **latches until reset** — never a live oscillating bias. The first setup mechanized is **Consumption-Break** (the pattern in both source videos and Javier's tape-scalping summary): a resting wall in price's path gets **eaten toward zero with trades**, and the radar fires *"right before it snaps through."*

Three problems are solved together:

1. **Tape speed** (Javier's ask #1) — instrument the tape as **bounded, glanceable reads** (signed velocity bar, z-score, velocity-at-the-wall, a CHOP light), not a scrolling number. The setup consumes these.
2. **The five fast conditions** (ask #2) — collapse the three book-skew signals into **one low-weight "context" readout with no vote**; the two real signals (aggressor delta, wall erosion) become **confirmation/veto inputs to the state machine**, not oscillating votes.
3. **A defined buy/sell setup** (ask #3) — a **Controller state machine** (`WAITING → ARMED → COUNTDOWN → FIRE → RESET`, with a global `CHOP` state) whose monotonic consumption countdown is structurally incapable of flip-flopping.

**Honesty mandate (carried):** every threshold ships as a **placeholder knob measured from Rec CSV** on ES days, never a number ported from the videos' share counts. This is a **momentum scalp with false breaks**; the spec is honest about that (§10).

---

## 2. Changed roles (explicit)

- **Primary output changes from a % to a STATE.** The Cockpit's Net% verdict is demoted to a small, vote-less "book-skew context" readout. The primary output is the **Controller state + the consumption countdown**.
- **The radar fires a discrete setup and pre-stages an order** (Javier's choice: *"setup que dispara + pre-orden"*). This does not change the existing Chart Trader hard gate: order submission to a **real** account stays behind the `trading-risk-manager` VETO (F1–F17). Default account = Playback/Sim. **The human always clicks the final submit** — no auto-execution.

Everything the `AbsorptionScalper` consumes (`RadarNode.State` / `PeakSize` / memory) is **preserved** — all additions are additive; no existing engine output changes.

---

## 3. Architecture — what changes, where

| Unit | File(s) | Change |
|---|---|---|
| **Tape speed** | **new** `Engine/TapeSpeed.cs` (pure) | Rolling prints/sec + buy/sell vol/sec over a short window, an EWMA baseline + **z-score**, and **velocity-at-a-price**. Ingests the same `TradeEvent`s `BookMirror` already sees. |
| **Consumption read** | **new** `Engine/ConsumptionTracker.cs` (pure) — or a focused surface on `WallTracker` | Per dominant wall: `peakSize`, `currentSize`, `consumptionFraction = 1 − current/peak`, the **trade-backed** fraction of the drop (via `BookMirror.TradedAt`), and a **reload** flag (min-reached-then-refilled). Reuses the trade-vs-cancel discriminator already in `EpisodeClassifier`. |
| **Controller** | **new** `Engine/ControllerStateMachine.cs` (pure) | The heart. Per-side candidate state machine + global CHOP gate. Inputs: dominant wall + consumption read + `AggressorDelta` + tape-speed z-score + chop read. Output: `ControllerState` + a one-shot `FireEvent`. All thresholds in a `ControllerConfig` (placeholder, measured). Fully unit-testable — the anti-oscillation test is the key gate. |
| **Cockpit demotion** | `Engine/PressureModel.cs` | Collapse signals 1/2/3 (imbalance/inside-thin/air-pocket) into **one `BookSkewContext` factor** (single number, no green-light). Signals 4/5 stop being votes here; they feed the Controller. `AbsorptionScalper` does not read `PressureModel`, so this is free to restructure. |
| **Glue / threading** | `NinjaTrader/RadarTab.cs` | Feed `TradeEvent`s to `TapeSpeed`; assemble Controller inputs each run (already has `wallAbove/wallBelow`, delta, erosion); call the state machine; expose state + fire event to visuals + Chart Trader. |
| **Render** | `NinjaTrader/CockpitVisual.cs` | Replace the 5-condition list with: **Controller state + consumption countdown**, the **signed velocity bar + z-score gauge + CHOP light**, and a small vote-less **book-skew context** strip. Aurora tokens only. |
| **Execution** | `NinjaTrader/RadarChartTrader.cs` | On FIRE: pre-stage a break-direction LIMIT + light up "SETUP LONG/SHORT listo". Reconcile with today's wall-anchored (reversion) LMT (§8). Human clicks. |
| **Calibration capture** | `NinjaTrader/RadarTab.cs` (Rec path) | Extend `lr-signals-*.csv` with the columns needed to measure every new threshold (§9). |

`Engine/` stays NT-free and unit-tested. `RadarTab` remains the only threading boundary.

---

## 4. The Controller state machine (the anti-flip spine)

One machine holds **one candidate per side** (Long = dominant wall above the price; Short = dominant wall below), each with its own sub-state, plus a **global CHOP** overlay that suppresses all fires. Whichever side reaches FIRE first emits; the setup is inherently one-directional per wall because **a wall cannot un-consume**.

**Per-side sub-states** (Long shown; Short is the mirror, wall below, sell aggressor):

```
WAITING
  └─(dominant wall above exists, size ≥ SignificanceBand)──► ARMED   [record peakSize]
ARMED                                   // long is BLOCKED here regardless of book skew (§5)
  ├─(size drop is trade-backed: tradedThrough ≥ MinTradeBackedRatio·drop)──► COUNTDOWN
  └─(size drop NOT trade-backed = pull/spoof)──────────────────────────────► COOLDOWN [veto]
COUNTDOWN
  ├─(consumptionFraction ≥ FireFrac  AND  buy AggressorDelta ≥ DeltaFloor
  │   AND  tape z-score ≥ ZFloor  AND  held K snapshots  AND  not CHOP)────► FIRE (one-shot, latched)
  ├─(wall refills ≥ ReloadFrac of peak)──────────────────────────────────► COOLDOWN [reload veto]
  └─(price falls away ≥ AwayTicks, or wall leaves top-10)─────────────────► WAITING
FIRE
  └─(price crosses & holds past the wall, OR position taken, OR wall gone)─► WAITING  [reset]
COOLDOWN
  └─(CooldownMs elapsed on that price)───────────────────────────────────► WAITING
```

**Global CHOP** (overrides both candidates): prints/sec < baseline·`ChopSlowFrac` **AND** aggressor sign alternates ≥ `ChopAltCount` times over the window → state = `CHOP`, all FIRE transitions disabled. Rendered as its own state, **not** as an oscillating bias.

**Why it can't flip (the whole point):**
- `consumptionFraction` is **monotonic** — you cannot un-eat a wall, so the countdown never reverses tick-to-tick.
- **FIRE is one-shot + latched**; re-arming the same price requires a full RESET.
- **`held K snapshots`** is a hard debounce (Javier-approved, K ≈ 2–3): a single momentary print cannot fire.
- **CHOP is first-class**: in the chop the tool says *"no control"* instead of inventing a bias — this is exactly where the old meter fabricated flips.

`FireEvent = { Side, WallPrice, EntryHint, Fraction, DeltaAtFire, ZAtFire, Timestamp }`.

---

## 5. The signal (the video, made mechanical)

- **Arm:** dominant wall in the path (from `wallAbove`/`wallBelow`, already computed in `RadarTab` lines 405–413) whose size ≥ `SignificanceBand`. The band is a **percentile of recent depth** (measured), never a fixed contract count.
- **Countdown:** `consumptionFraction = 1 − currentSize/peakSize`, advanced **only by trade-backed decrements** — `BookMirror.TradedAt(wallPrice, breakAggressor, since)` must explain ≥ `MinTradeBackedRatio` of the size drop. Drop **without** trades = pull/spoof → veto + cooldown (do not arm a fake). This is the existing `EpisodeClassifier` / `ErosionReads` discriminator applied to the countdown.
- **Fire (conjunction, all required):** `fraction ≥ FireFrac` (≈0.7) **AND** `AggressorDelta` agrees (buy for an up-break) **AND** tape-speed z-score ≥ `ZFloor` (real acceleration, not a trickle) **AND** held K snapshots **AND** not CHOP.
- **Invalidation:** wall **refills** ≥ `ReloadFrac` (someone defending → bearish for a long → veto), price falls away, or the thinning turns trade-less.

## 5b. Suppression rule (from the video — the single biggest de-flip)

**While the armed wall is intact (not yet eroding), NO fire — regardless of book skew.** The meter can no longer go long while the wall it must break is still full. This is the `WAITING`/`ARMED` gate: book skew alone can never advance the machine past `ARMED`.

---

## 6. Tape-speed instrumentation (ask #1)

Built here because the setup consumes it. All derived from the trade ring already flowing through `OnMarketData` → `BookMirror.ApplyTrade`.

| Read | Definition | Why legible at speed |
|---|---|---|
| **Signed velocity bar** | buy-aggressor vol/sec vs sell-aggressor vol/sec, growing from center | You read a **length and a side**, not a scrolling digit — "who presses, how hard," in one glance. |
| **Tape-speed z-score** | prints/sec (and vol/sec) normalized vs an EWMA 1–2 min baseline, capped 0..3+ | **The fix for "va muy rápido":** same shape at 10 or 1000 prints/sec — you read *spiking vs normal*, not the raw rate. |
| **Velocity-at-the-wall** | prints/sec + vol/sec localized to the armed wall's price | Ties speed to the one read Javier trusts (size at a level); feeds the countdown's "being eaten at N/sec". |
| **CHOP light** | rate below baseline **and** aggressor color alternating | Tells you when **not** to trade — the anti-flip primitive (§4). |

`Engine/TapeSpeed.cs` maintains bucketed rolling counters + an EWMA mean/variance for the z-score; pure, time via timestamps.

---

## 7. Cockpit restructure (ask #2)

- **Collapse** imbalance + inside-thin + air-pocket → one `BookSkewContext` number, rendered as a **thin, vote-less reference strip**. It **cannot fire** anything.
- **Delta** and **wall-erosion** are removed as standalone oscillating votes; they become **Controller inputs** (confirmation for delta, veto for trade-less erosion).
- The Cockpit panel's primary area becomes the **Controller state + consumption countdown + tape-speed reads**.

Honest note carried from the Cockpit review: this kills the triple-counting of book skew that forced `GreenConviction=4`; the green-light math is retired in favor of the state machine.

---

## 8. Execution (ask: "setup que dispara + pre-orden")

On `FIRE`: light **"SETUP LONG/SHORT listo"** and **pre-stage a LIMIT in the break direction** in the Chart Trader ticket. **Never market** — the break is fast and a market order fills at the top of the spike. **The human clicks submit.** Sim/Playback only; `trading-risk-manager` VETO (F1–F17) before any real account.

**Execution-geometry reconciliation (nailed in the plan).** Today's Chart Trader wall-anchored LMT rests *at the wall for reversion* (commit `ed46c76`: BUY above wall / SELL below wall). A consumption **break** enters *with* the move, and a passive limit at the break price is **marketable** while price sits below an up-wall (it would fill instantly below resistance — wrong). The correct pre-stage is a **"join near the inside" limit** in the break direction with a small fill tolerance and a cap so it never fills far into the move. The exact tick offset (inside ± tolerance vs wall-relative) and the reconciliation with the existing reversion-LMT code are an **execution detail resolved in the implementation plan** against the live `RadarChartTrader` + the `nt8-*` order API — not guessed here.

---

## 9. Calibration — Rec capture extension (measure-first mandate)

Every new threshold is a **placeholder knob measured from Rec CSV** on ES days: `SignificanceBand`, `FireFrac (~0.7)`, `MinTradeBackedRatio`, `ReloadFrac`, `AwayTicks`, `DeltaFloor`, `ZFloor`, `K`, `ChopSlowFrac`, `ChopAltCount`, `CooldownMs`.

Extend the `lr-signals-*.csv` capture (`RadarTab` Rec path, currently mid + masses + best sizes + delta15 + wall-erosion) with, per snapshot: **dominant-wall price/peak/current per side**, **consumptionFraction**, **trade-backed fraction**, **prints/sec**, **buy vol/sec**, **sell vol/sec**, **tape z-score**, **controller state**. Then the Plan-D measurement consumer scores each candidate FIRE: P(price moves ≥ N ticks in the fire direction over 15–30s) vs a same-time baseline — the baseline-isolation protocol the prior specs established. A threshold that doesn't beat baseline gets loosened or the read is dropped. `ponytail: leave the calibration knob — the tape needs tuning a fixed model can't see.`

---

## 10. Honest weaknesses

- **Momentum scalp, false breaks.** A wall eaten to ~70% can poke through and fail ("not enough buyers, it falls back" — the video's own warning). The trade-backed delta + z-score spike + post-break follow-through are the filter, not a guarantee. **Stop is structural: 1 tick beyond the wall.** Scalp, not swing.
- **ES-first.** NQ walls are effectively inert (engine constraint). ES MBP-10 is aggregated: the read depends on the wall staying **within the top-10** as price approaches; a wall that goes blind mid-approach degrades to memory.
- **Aggressor is inferred**, not exchange-tagged (last vs bid/ask). Delta and trade-backing inherit that noise.
- **Placeholder thresholds.** Nothing is trusted until measured (§9).
- **Discarded as stock-only** (do not reconstruct on ES): named market makers, order-by-order reads, iceberg/reserve order flags. The only route to "hidden order" on ES is **absorption inference** (a later setup), never an order attribute.

---

## 11. Validation

- **Unit tests** (NT-free, assert-based, matching `Tests/`):
  - **Anti-oscillation test (the key gate):** feed a synthetic tick sequence that makes the old `PressureModel.Net` flip long↔short repeatedly; assert the Controller stays in `WAITING`/`CHOP` and emits **zero** `FireEvent`s.
  - Countdown monotonicity; trade-backed vs pull (fire vs veto); reload veto; CHOP suppression; one-shot latch + reset; per-side independence.
  - `TapeSpeed`: rate/z-score on synthetic trade streams; velocity-at-price isolation.
  - `PressureModel` `BookSkewContext` collapse (no green-light path remains).
- **`nt8c build`** compile-validation (staged `Custom/` mirror), as today.
- **Market Replay** (ES, verify the day carries L2 — `medBid/medAsk > 0`): visually confirm the state machine does **not** oscillate, the countdown reads true, CHOP lights in chop, and FIRE lands *before* the snap.
- **Signal measurement** (§9) before any threshold is trusted.
- **Chart Trader:** Sim/Playback only until `trading-risk-manager` signs off.

---

## 12. Non-goals / deferred (v1 = Consumption-Break only)

- **Absorption-then-break** (dam-break / inferred iceberg), **broken-level retest defense**, and **structural-level confluence** (swing highs/lows, VWAP, round numbers) — deferred; they hang off the same Controller spine and are added **after** Rec proves v1 doesn't oscillate.
- **Trend/VWAP agreement gate** — deferred (Javier-approved). v1 fires on consumption + delta + chop-gate alone; add as a measured toggle only if Rec shows too many false breaks.
- Manual level-pick / auto+override — deferred; v1 is **auto only** (dominant wall).
- No change to `AbsorptionScalper`. No auto-execution. No new visual style — Aurora only.

---

## 13. Open questions for the implementation plan

1. `ConsumptionTracker` as its own unit vs a surface on `WallTracker` (which already holds `PeakSize`).
2. Controller: one machine with two side-candidates (spec's model) vs two independent machines + a CHOP gate — pick the cleaner to test.
3. Exact execution geometry (§8) — the "join near inside" limit offset and the reversion-LMT reconciliation, against live `RadarChartTrader`.
4. z-score sampling cadence: per engine run (20Hz) vs per fixed 1s bucket (steadier baseline).
5. Does `CockpitVisual` host all new reads, or split a sibling `FrameworkElement` for the tape panel (cleaner render tests)?
6. Rec capture cadence for the new columns (per-snapshot vs the current 2s mid-log) — enough resolution to measure `K` and z-score.

---

## 14. Phasing (detailed in the plan)

1. **Engine, pure + tested:** `TapeSpeed`, `ConsumptionTracker`, `ControllerStateMachine` (placeholder config) + the anti-oscillation and countdown tests. `PressureModel` book-skew collapse.
2. **Rec capture extension** (§9) — so measurement can start immediately.
3. **NT glue:** `RadarTab` wires tape feed + controller; expose state + fire event.
4. **Render:** `CockpitVisual` — state + countdown + tape reads + CHOP + context strip (Aurora).
5. **Execution:** Chart Trader pre-stage on FIRE (Sim/Playback) → `trading-risk-manager` gate.
6. **Measure** thresholds on ES Rec CSVs → replace placeholders → Replay-verify no oscillation. Only then layer Setups 2–4.

Implementation runs through the `trading-*` agents (NinjaScript dev → code-reviewer → measure → **risk-manager VETO** for the order path).
