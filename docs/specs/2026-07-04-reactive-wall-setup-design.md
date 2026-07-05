# Reactive Wall Setup ("React") — Design Spec

**Date:** 2026-07-04
**Status:** Approved design, pre-implementation
**Author:** brainstormed with Javier
**Related:** `2026-07-01-consumption-break-setup-design.md` (the "Break" setup), `strategy-absorption-scalper.md` (standalone fade), `playbook-entries.md §3.2` (wall lifecycle), `decisions/2026-07-03-ml-calibration-strategy.md` + `2026-07-03-multiday-analysis-adaptation-verdict.md` (data/overfit gate)

---

## 1. Purpose & context

Add a **second, user-selectable setup** to the Liquidity Radar via a dropdown, without touching the existing Break setup. The new setup — **"React"** — mechanizes the wall-reaction pattern studied in the 2026-06-29 ES playback session: the tape **accelerates hard into a dominant wall**, then the wall either **holds (reject)** or **breaks (consume)**, and we scalp the resulting move (~12-tick target).

This is the **"absorption-then-break"** setup the Break spec (§12) already pre-named as deferred — a *reactive dual-outcome* machine, not a new market edge. The decision primitives already exist in the engine:

- **Reject vs Break** is already discriminated by `EpisodeClassifier`: `Absorbed` (traded into, held/refilled, quote did **not** cross) vs `Consumed` (quote crossed through).
- The **Break** half (consume → follow) is exactly what the shipped `ControllerStateMachine` fires today as "SETUP LONG/SHORT ready".
- The **Reject** half (hold → fade) is what the standalone `AbsorptionScalper.cs` strategy trades, and what `playbook-entries.md` Setup 1 describes.

What is **not** built is a single machine that arms on one trigger and **branches on the realized outcome**. That is what this spec adds, as an **isolated controller** so the Break setup's in-flight calibration is not disturbed.

## 2. Scope / non-goals

**In scope**
- A new isolated `ReactiveController` state machine (own file), selected via a panel dropdown.
- One new engine signal: **tape acceleration** (signed derivative of the aggressor rate).
- Minimal additive wiring: a `SetupKind` tag on `FireEvent`, dropdown ComboBox, reactive fire routing, cockpit banner text.
- Auto-aggressive execution reusing the existing Sim/Playback-gated auto-fire + ATM bracket path.

**Non-goals (explicitly deferred)**
- **No** refactor of `ControllerStateMachine` (Break) — it is frozen (in calibration).
- **No** `ISetupEvaluator` framework yet — introduce only when a 3rd setup is real (YAGNI; that was architecture option B, rejected for now).
- **No** wall-anchored dynamic stops — exit uses the fixed ATM template bracket in v1 (noted as a ceiling).
- **No** claim of realized edge — this is an experimentation harness until the data gate (§12) is met.
- **No** live-account execution — hard-gated to Sim/Playback.

## 3. Setup semantics — the direction table

React arms when the tape **accelerates toward a dominant wall that price is near**, latches that wall, then fires on the wall's resolution. Direction depends on **which side the wall is on**:

```
WALL ABOVE price (resistance / ask):
  REJECT (wall holds/absorbs)  → price bounced down off it  → SELL  (fade)
  BREAK  (wall consumed)        → price ate through it        → BUY   (follow)

WALL BELOW price (support / bid):
  REJECT (wall holds/absorbs)  → price bounced up off it     → BUY   (fade)
  BREAK  (wall consumed)        → price ate through it         → SELL  (follow)
```

The studied case (wall above): **reject → sell, break → buy.** `Pulled` (spoof: cancelled, no trades, quote didn't cross) → **abstain** (no trade).

## 4. Architecture — isolated controller (option C)

```
                      ┌─────────────────────────────────────────┐
 market data ──▶ BookMirror ──▶ RadarTab (per-frame) builds:    │
                      │           • dominant walls (WallDetector)│
                      │           • aggressor delta, tape z       │
                      │           • NEW tape acceleration (§5)    │
                      │           • NEW dominant-wall outcome     │
                      │                    │                      │
                      │      ┌─────────────┴─────────────┐        │
                      │  SetupKind.Break            SetupKind.Reactive
                      │  ControllerStateMachine     ReactiveController  ◀── dropdown
                      │  (FROZEN, unchanged)        (NEW, this spec)   selects active
                      │      └─────────────┬─────────────┘        │
                      │                    ▼                      │
                      │            ControllerOutput + FireEvent{SetupKind}
                      └────────────────────┼──────────────────────┘
                                           ▼
                       CockpitVisual banner  +  RadarChartTrader auto-fire
                                                 (Sim/Playback-gated) + ATM bracket
```

`RadarTab` holds `_activeController` and swaps it under `_engineLock` on dropdown change — the **same rebuild-under-lock pattern already used** at instrument switch (`RadarTab.cs:336`) and replay reset (`RadarTab.cs:909`). The selection must be **re-applied at those two reset sites** or an instrument switch silently reverts to default. Both controllers consume the identical `ControllerInputs` bundle; only the active one is `Update()`-ed each frame.

## 5. New engine signal — tape acceleration

`TapeSpeed` today exposes only a **level** z-score of the print rate (`TapeSpeed.ZScore`), which was only 0.8–1.1 in the studied bursts — too weak to arm on. We add a **signed derivative** term.

- **Definition:** `accel = d(netRate)/dt`, where `netRate = BuyVol − SellVol` per second (from `BookMirror.WindowSince`, `BuyVol`/`SellVol`). Smoothed (EWMA of the frame-to-frame delta of `netRate`) to reject single-frame noise.
- **Sign:** positive = buyers accelerating (arms a wall **above**); negative = sellers accelerating (arms a wall **below**).
- **Home:** new `Engine/TapeAcceleration.cs` (mirrors `TapeSpeed`'s EWMA-with-warmup shape, `MinSamples` before `Ready`), surfaced into `ControllerInputs` as a signed `TapeAccel`.
- **Arm test:** `sign(TapeAccel)` points at the latched wall's side **and** `|TapeAccel| ≥ AccelFloor`.

## 6. ReactiveController state machine

States: `Waiting → Watching → Fired → Cooldown` (own `enum`, not shared with Break).

**Waiting → Watching (ARM)** — all three:
1. a **dominant wall** exists on one side within `WatchProximityTicks` of `Mid`, size meets the same significance bar the Break setup uses (`max(SignificanceBand, AdaptiveSignificance)`), and
2. `|TapeAccel| ≥ AccelFloor` with `sign(TapeAccel)` toward that wall, and
3. `AggressorDelta` agrees (points at the wall).
On arm: **latch** wall price + side + identity, stamp `WatchStart`.

**Watching → Fired (RESOLVE)** — evaluate the latched wall each tick:
- **REJECT** — wall `Absorbed`: `ConsumptionTracker` trade-backed drop into the wall **without** `QuoteCrossed`, i.e. `EpisodeClassifier.Outcome == Absorbed` (held/refilled). → fire the **fade** side (§3).
- **BREAK** — wall `Consumed`: `r.Fraction ≥ BreakFireFrac` **and** `r.TradeBackedFraction ≥ MinTradeBackedRatio` **and** `QuoteCrossed`. → fire the **follow** side (§3).
- Emit `FireEvent{ SetupKind = Reactive, ReactKind = Reject|Break, Side }`.

**Watching → Waiting/Cooldown (ABANDON)** — any of:
- latched wall vanishes or dominant-wall identity hops,
- `|Mid − wallPrice| ≥ AwayTicks` without resolving (acceleration fizzled, price left),
- `Now − WatchStart ≥ MaxWatchSeconds` (wait-and-see timeout),
- wall `Pulled` (spoof) → abstain.

**Fired → Cooldown → Waiting** — cool `ReactCooldownSeconds` after a fire (and after abandon) so the same event can't re-arm/re-fire.

The reject/break tests **reuse** `ConsumptionTracker.Read(book)` and the `EpisodeClassifier` outcomes already computed upstream — no new consumption math.

## 7. Inputs consumed

Existing `ControllerInputs` fields (walls above/below, `AggressorDelta`, `TapeZScore`, `Mid`, `Now`, `Book`, `AdaptiveSignificance`) plus **two additions**:
- `TapeAccel` (signed) — from §5.
- The latched/dominant wall's **episode outcome** (`Absorbed`/`Consumed`/`Pulled`) — currently computed in the upstream `WallTracker`/`EpisodeClassifier` layer in `RadarTab`; pass the dominant wall's live outcome into the inputs (or have `ReactiveController` read it from the same source). Small additive change; does not affect Break.

## 8. Execution & exit

**Auto-aggressive** (user's choice): on a reactive fire, the panel auto-submits **regardless of the AUTO checkbox** — but the existing hard guards remain:
- **Sim/Playback accounts only** (`RadarChartTrader` guard 1b, `:709`) — never fires on a live account,
- hours window, daily cap, busy/one-trade, stale-quote, ATM-selected guards (`TryAutoFire`, `:691`).

Entry mechanics by branch:
- **REJECT / fade** → **marketable** entry (`BUY/SELL MKT`): the rejection already printed, so chase it rather than rest a limit the price has left.
- **BREAK / follow** → **wall-anchored limit** (`BUY/SELL LMT`), identical to the Break setup's pre-staged limit.

Both attach the **ATM bracket**. **The 12-tick target and the structural stop live in the ATM template** (`ES_2C`), not in the controller. v1 uses the ATM's fixed-tick bracket for both branches (see ceiling in §12: the ideal fade stop is wall-anchored and tighter than a break stop; fixed ATM is the lazy-correct v1).

## 9. UI — dropdown

- New WPF `ComboBox _setupCombo` in `RadarChartTrader`, placed in the **empty col1/row3** box beside AUTO: drop `Grid.SetColumnSpan(autoGroup, 2)` (`:432`) so AUTO stays in col0, then `Grid.SetColumn(_setupCombo,1); Grid.SetRow(_setupCombo,3)` — mirrors how `capGroup` fills col1/row1. Styled like `_accountCombo` (`:267-270`).
- Items: `Break`, `React`. Expose `public event Action<SetupKind> SetupChanged;` fired from `SelectionChanged` (stored-handler pattern, `:273`).
- `RadarTab` subscribes: on change, rebuild `_activeController` under `_engineLock`; re-apply at instrument-switch (`:336`) and replay-reset (`:909`).

## 10. Cockpit banner

`CockpitVisual.ComputeBanner`/`DrawBannerCard` gain reactive states, routed by the active `SetupKind`:
- `WATCHING WALL · waiting for resolution` (Watching),
- `REJECT · FADE` / `BREAK · FOLLOW` (Fired, latched),
- `WAITING` (default).
Market cards (WALL CONSUMPTION, TAPE SPEED, TAPE Z-SCORE, and a new **TAPE ACCEL** readout) render from the frame regardless of setup.

## 11. Config / tunables (all placeholders — calibrate later)

New `ReactiveConfig` (mirrors `ControllerConfig`'s "MEASURED-later" discipline):
- `AccelFloor` — arm threshold on `|TapeAccel|`.
- `WatchProximityTicks` — arm proximity to wall (~2–3).
- `MaxWatchSeconds` — wait-and-see timeout (~10–20 s).
- `BreakFireFrac` — reuse Break's `FireFrac` (0.6) for the consume branch.
- reuse `Absorbed` criteria / `MinTradeBackedRatio` (0.6) for the reject branch.
- `ReactCooldownSeconds`.
- Target/stop: **not here** — in the ATM template (12t target).

## 12. Honest ceilings & calibration gate

Recorded so future-me is not fooled:
1. **12t is ambitious** vs the ~1.5–8t realistic reaction magnitude (`strategy-absorption-scalper.md §1`). Fine for playback learning; **not** a proven edge.
2. **Reactive enters late by design** — it waits for the resolution. Auto-aggressive + late entry → **worse fills than playback shows**.
3. **New knobs are uncalibrated.** Per `decisions/2026-07-03-ml-calibration-strategy.md` and the multiday verdict: ~1 distinct ES day, 0 graded exit legs. Any "is React real?" claim stays **gated behind ≥10 distinct days + ≥15–20 real fires with realized-fill outcomes + exit-leg logging.** Until then React is an *experimentation harness*, not an edge.
4. **Fixed ATM stop** (v1) is not wall-anchored; the ideal fade stop hugs the wall and is tighter. Upgrade path: wall-anchored bracket after v1 proves the trigger fires cleanly.

## 13. Components + testing

**New files**
- `Engine/ReactiveController.cs` — the state machine (§6).
- `Engine/TapeAcceleration.cs` — the derivative signal (§5).
- `Tests/ReactiveControllerTests.cs`, `Tests/TapeAccelerationTests.cs`.

**Changed (minimal, additive)**
- `Engine/ControllerStateMachine.cs` — `FireEvent` gains `SetupKind Kind` + `ReactKind` (additive default; Break logic untouched).
- `RadarTab.cs` — build `TapeAccel` + dominant-wall outcome into inputs; hold + swap `_activeController`; re-apply on instrument/replay reset; route reactive fire.
- `RadarChartTrader.cs` — `_setupCombo` ComboBox; `SetupChanged`; reactive fire → MKT (fade) / LMT (break); auto-aggressive path (fire even if AUTO unchecked, still Sim/Playback-gated).
- `CockpitVisual.cs` — reactive banner text + TAPE ACCEL readout.

**Tests cover**
- arm on accel-into-wall (both signs → correct side);
- reject → correct fade side (wall above → SELL, wall below → BUY);
- break → correct follow side (wall above → BUY, wall below → SELL);
- timeout / wall-vanish / away-drift abandonment; pulled → abstain;
- cooldown blocks re-fire;
- `TapeAcceleration` sign + magnitude correctness (rising rate → positive, flat → ~0).

**Implementation ownership:** the `.cs` work is delegated to the **`trading-*` agent team** (quant-researcher → ninjascript-developer → code-reviewer → risk-manager veto before anything touches real), with the relevant `nt8-*` skills loaded. The `nt8c` PostToolUse hook validates compilation on each `.cs` edit.

## 14. Open questions (resolve during planning)

- Exact `TapeAccel` window/EWMA α and warmup `MinSamples` (start by mirroring `TapeSpeed`).
- Whether the dominant-wall outcome is pushed into `ControllerInputs` or read by `ReactiveController` from the shared upstream source (prefer the smaller diff).
- Marketable-entry mechanics for the fade branch (market vs marketable-limit with a tick of protection).
