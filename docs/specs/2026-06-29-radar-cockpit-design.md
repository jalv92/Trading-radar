# Liquidity Radar — Cockpit & Chart Trader — Design Spec

- **Date:** 2026-06-29
- **Status:** Approved design (brainstorm), pre-implementation
- **Type:** Evolution of the NinjaTrader 8 Liquidity Radar Add-On (floating window)
- **Scope:** **The radar only.** The `AbsorptionScalper` strategy is **out of scope** and untouched.
- **Supersedes / extends:** `docs/specs/2026-06-28-liquidity-radar-design.md` (the v1 awareness-panel spec). This spec changes two of v1's stated non-goals on purpose (see §2).
- **Interactive mockup (approved):** `docs/mockups/radar-cockpit-demo.html` — open in a browser; this is the visual + behavioral reference for the whole spec.
- **Author:** Javier + Claude (brainstorm)

---

## 1. Summary

The current radar shows *where* resting liquidity is. By itself "this information says nothing" (Javier): the trader still has to eyeball the ladder and infer a direction. This spec turns the radar into a **directional pressure read with an integrated order ticket**:

1. **Anchored price ladder** — fix the disorienting behavior where the whole profile jumps every time price moves (the v1 render re-centers the mid each frame). The price scale holds still; the profile grows/shrinks in place; a price marker glides through it; the column only scrolls at the edges. (DOM-standard.)
2. **The Cockpit** — a confluence layer beside the ladder that fuses several order-flow reads of the **zone around the price** into a single **directional bias** (SHORT ◀──▶ LONG) with a **conviction** count and a **green-light** that only fires when enough independent signals agree. It answers "is the pressure here loaded up or down, and is there enough confluence to act."
3. **Integrated Chart Trader** — an order ticket (BUY/SELL MKT in neon green/red, Rev/Close/Flat, qty, TIF, account, ATM) docked in the radar so a position can be opened directly from the awareness surface.

Plus one engine addition that came out of the brainstorm: **wall-erosion-on-approach detection** (a wall that thins *without trades* as price approaches is currently invisible to the engine — it only flags a full vanish). That partial-pull is a real signal and feeds the Cockpit.

**Honesty mandate (carried from the playbook):** every Cockpit signal is individually weak and short-horizon (seconds). The value is in *confluence*, and **no signal's weight is trusted until it is measured against the two captured ES days** (`docs/calibration-es-day1.md`). The Cockpit ships with placeholder weights and a measurement gate, not with assumed alpha.

---

## 2. Changed non-goals (explicit)

v1 declared the radar a *secondary awareness panel, not an execution surface*, with *no alerts*. This spec deliberately changes that:

- **Execution surface — now IN.** The radar will be able to **submit real orders** (Chart Trader). This is a material change of the product's nature and carries a hard gate: **once the radar can place real orders, the `trading-risk-manager` agent VETO applies before any real-money account**, exactly as for the `AbsorptionScalper`. Sim/Playback first.
- **Directional verdict / green-light — now IN.** v1 refused to give a directional call; the Cockpit gives one — but as a *confluence summary with disclosed, measured weights*, never as a black-box oracle.

Everything else in the v1 spec (the engine, the Aurora identity, the threading model, the L2 constraints) stands.

---

## 3. Architecture — what changes, where

| Unit | File(s) | Change |
|---|---|---|
| **Render** | `NinjaTrader/RadarVisual.cs` | Rewrite the vertical mapping from **mid-centered-per-frame** to **anchored** (§4). Add Cockpit rendering (bias gauge, conditions) — or split into a sibling element. Add in-zone anomaly overlays (erosion tags, air-pocket). |
| **Glue / threading** | `NinjaTrader/RadarTab.cs` | Host the new Cockpit + Chart Trader panels. Compute the pressure model off the same `RadarNode` snapshot it already marshals. Wire the Chart Trader to an NT8 `Account`. |
| **Pressure model** | **new** `Engine/PressureModel.cs` (pure C#) | Pure, testable. Takes a book/snapshot + recent flow, emits per-signal lean/weight + the aggregate bias/conviction/green-light. No NT, no UI — same discipline as the rest of `Engine/`. |
| **Erosion / partial-pull** | `Engine/EpisodeClassifier.cs` (+ a small surface on `WallTracker`/`RadarNode`) | Detect and expose *partial* size erosion on approach without trades, not only full vanish (§7). |
| **Cumulative delta** | `Engine/BookMirror.cs` | Already infers aggressor side; expose a running aggressor delta over a window for the Cockpit (playbook gap #1). |
| **Chart Trader** | **new** `NinjaTrader/RadarChartTrader.cs` | Order ticket UI + NT8 `Account` order submission (Account API), position/PnL readout. |

The `Engine/` stays NT-free and unit-tested. `RadarTab` remains the only threading boundary. The engine the `AbsorptionScalper` shares is **not modified in a way that changes its existing behavior** — new methods are additive; existing classification outputs are preserved (the strategy reads `RadarNode.State`/`PeakSize` and must keep seeing exactly what it sees today).

---

## 4. The anchored price ladder (the core fix)

**Problem (root cause).** `RadarVisual.OnRender` computes every row as `y = centerY − ((price − mid)/tick)·rowH` with `centerY = h/2`. The mid is re-pinned to the vertical center every frame, so a 1-tick move in price shifts *all* bars by one row. That is the "las barras se mueven con el precio, es una locura."

**Fix — anchored ladder (DOM-standard).**
- Map **screen rows to fixed prices** via an `anchorTop` price (the price of the top row), not via the live mid.
- Each price's liquidity bar is drawn at its anchored row and only **grows/shrinks in place** as size changes.
- The **inside-market marker** (best-bid/ask band + price chip) is an overlay that **glides** to `y(price) = (anchorTop − price)/tick · rowH` with a short transition — it moves through the static ladder.
- **Re-anchor only at the edges:** keep a hysteresis buffer (mockup uses **4 ticks**). When price comes within the buffer of the top/bottom row, shift `anchorTop` by the needed ticks — i.e. the **price column scrolls**, occasionally and smoothly, not every tick. (Open question 1: edge-auto vs a manual "Center" button.)
- A level's **side is derived** from its price vs the live price (`p > price` ⇒ ask, `p < price` ⇒ bid), so as price walks through a level (e.g. through the 7590 wall) it flips ask→bid naturally — the "consumed/flip" read is automatic.

Reference behavior is the mockup's **Anclado** mode; the **Centrado (actual)** toggle reproduces today's jumping behavior for contrast.

---

## 5. The Cockpit — signal catalog

Each signal reads the **zone around the price** and emits `{ lean ∈ [−1,+1] (− short / + long), weight, reason, active }`. All are derivable from data the engine already ingests (L2 + tape).

| # | Signal | Definition | Lean | Source | Honesty |
|---|---|---|---|---|---|
| 1 | **Resting imbalance** | bid mass vs ask mass across the visible band | heavier side = barrier; net skew biases the thin side | `BookMirror` levels | the one OFI-type read with documented short-horizon edge (Cont–Kukanov–Stoikov); was eyeballed |
| 2 | **Inside thin** | best-bid vs best-ask size | toward the thinner inside | `BookMirror` best quotes | tactical, 1–2 ticks |
| 3 | **Air-pocket** | thin/empty levels just beyond the inside | continuation into the void | `BookMirror` | strong *if* it breaks; benign otherwise |
| 4 | **Cumulative delta (≈15s)** | running buy-aggressor − sell-aggressor | toward who is hitting | `BookMirror` aggressor inference (new running sum) | real, short; playbook gap #1 |
| 5 | **Wall erosion on approach** | a tracked wall thinning **without trades** as price nears (partial pull) | against the wall (likely breaks) | `EpisodeClassifier` partial-pull (§7) | the brainstorm's idea; the engine gap |

**Structural memory states** already emitted by the engine (`Absorbed / Pulled / Consumed / Remembered` + size/age) remain rendered on the ladder and inform reading, but are not double-counted as separate Cockpit votes in v1 (avoid correlated double-weighting). Specifically, **Absorbed** is the inverse discriminator of signal #5: erosion *with* refilling trades = strong wall, erosion *without* trades = weak wall.

**Confidence is NOT a weight.** Calibration showed the engine's `Confidence` does not discriminate hold-rate; **size does**. Weights are size-aware; the engine `Confidence` field is not used as a Cockpit input.

---

## 6. The confluence model + green-light

- **Net bias** = weighted mean of active signals' leans, mapped to −100…+100 (SHORT…LONG).
- **Conviction** = count of active signals whose lean agrees with the net sign (|lean| above a floor).
- **Green-light** fires only when: conviction ≥ N **and** |net| ≥ M **and** no *strongly opposing* active signal. Mockup defaults: **N = 3, M = 0.55, opposing-veto |lean| > 0.55**. Below that it shows **SIN TRIGGER** with the % and conviction still visible — the tool explicitly says *when not to act*.
- The green-light is **transient by design** (these signals live seconds); it is not a persistent state.
- Always shows the **"why"**: the per-signal lean/weight/firing list. No black box.

**Default presentation (approved):** binary `SIN TRIGGER` vs `▶ SEMÁFORO LONG` / `◀ SEMÁFORO SHORT`, with the net % + conviction dots always visible. Threshold `M`, `N`, and weights are **config knobs**, not literals in logic (mirror `RadarConfig` discipline).

---

## 7. Wall-erosion-on-approach (engine addition)

**Today:** `EpisodeClassifier` opens an episode within `D_approach` ticks and only resolves `Pulled` when displayed size hits **0** with the quote still away. A wall going **73 → 40 on approach without trades** (Javier's observation) never resolves — it is invisible.

**Add:** track, per open episode, the **size trajectory vs trades** while the quote approaches. Expose a *partial-pull / erosion* read: `erosionFrac = (sizeAtOpen − displayed) / sizeAtOpen` attributable to **cancellation, not trades** (`cancelled = max(0, drop − tradedAt)`), while the quote is still ≥ `D_pull` ticks away and approaching. This is exactly the existing trade-vs-cancel discriminator (`EpisodeClassifier.TryClassify`), applied **continuously** instead of only at vanish.

- Feeds Cockpit signal #5 (lean against the eroding wall).
- Does **not** change the existing `Absorbed/Pulled/Consumed` outputs the `AbsorptionScalper` consumes — it is an additive read surfaced separately.
- Related debt: wiring `W_assoc` (per-decrease trade attribution, `EpisodeClassifier.cs:85-87` TODO) sharpens both erosion and delta. Not a blocker for v1 but called out (Open question 5).

---

## 8. The Chart Trader (execution surface)

Docked in the radar (replaces the brainstorm's scenario panel). Controls (per Javier, with the NT8 items he struck out removed):

- **BUY MKT** (neon emerald) / **SELL MKT** (neon coral) — primary, glowing, Aurora colors.
- **Rev** (reverse) · **Close** · **Flat** · **Entry**.
- **PnL** readout: position (`Long/Short qty @ entry`) + live PnL in $ and ticks, colored.
- **Instrument · TIF · Account · Order qty (stepper) · ATM Strategy**.
- **Removed** (struck out in the reference): Buy/Sell **Ask**, Buy/Sell **Bid**, and the `A:/B:` ATM target/stop lines.

**Wiring (real, post-mock):** NT8 Add-Ons can submit orders via the **Account API** — obtain an `Account` (selector / `Account.All`), build with `account.CreateOrder(...)`, submit via `account.Submit(...)`, and subscribe to order/execution/position updates for the live PnL; ATM via the ATM strategy methods. Exact API is nailed in the implementation plan with the `nt8-*` skills.

**Hard gate:** order submission to a **real** account is blocked behind the `trading-risk-manager` VETO (sizing, daily-loss, per-trade risk, "are these fills achievable live"). Default account = **Playback/Sim**. The mock simulates a position + live PnL only.

---

## 9. Honest weaknesses & the measure-first mandate

- **Weights are placeholder.** Before the Cockpit is trusted, each signal's edge is **measured on the two captured ES days** (and ≥1 more trending + ≥1 chop session, per the calibration doc's own caveat). Method: at each signal-firing timestamp, P(price moves in the signal's direction over 15–30s) vs a same-time baseline — the same baseline-isolation protocol the strategy spec already established (signal vs opposite/random). A signal that doesn't beat baseline gets weight ≈ 0 or is dropped.
- **Confluence ≠ certainty.** Correlated signals (e.g. imbalance and inside-thin both read book skew) must not be double-counted; weighting accounts for overlap.
- **Short horizon.** The green-light is a *timing* read (seconds), not a position thesis. Disclosed in the UI.
- **Execution surface raises the stakes.** A directional green-light next to a one-click BUY button invites over-trading; the SIN-TRIGGER default and the transient green-light are deliberate friction. Risk-manager gate is mandatory before real money.
- **MBP limits stand:** 10 levels, aggregated, order-count hidden; NQ remains effectively inert for walls. Cockpit is ES-first.

---

## 10. Visual design (Aurora, extended)

Reference = `docs/mockups/radar-cockpit-demo.html`. Tokens are the existing Aurora set (deep-ink bg, emerald `#34d399` / coral `#fb7185`, amber inside-market, mono tabular numbers). Extensions:

- **Cockpit:** a tug-of-war bias bar (coral↔emerald, white needle), conviction dots, a status pill (slate `SIN TRIGGER` / glowing emerald or coral when green), and a per-signal conditions list with lean chip + weight bar + firing dot + plain-language reason.
- **Chart Trader:** neon BUY/SELL with restrained glow (`box-shadow` bloom, never over the numbers — same per-bar-glow discipline as v1 §7.5), muted manage buttons, Aurora inputs.
- **In-ladder overlays:** erosion tag (`▼ −n`) on a thinning wall, air-pocket dashed gap, gliding amber price marker.
- The implementation visual-polish pass runs `frontend-design` + the `web-*` craft as needed, but the system is **Aurora — do not introduce a new style**.

---

## 11. Validation

- **Unit tests** (NT-free) for `PressureModel` (each signal's lean sign on synthetic books; aggregate/green-light thresholds; no-trigger cases) and for the erosion/partial-pull addition to `EpisodeClassifier` (erosion-without-trades vs absorbed-with-trades). Assert-based, no framework — matching the existing `Tests/`.
- **Signal measurement** on the captured ES CSVs (the weight gate, §9) before any weight is trusted.
- **`nt8c build`** compile-validation (staged `Custom/` mirror) as today.
- **Market Replay** for live-faithful behavior; verify each replay day actually carries L2 (`medBid/medAsk > 0` — the 6/23 trap).
- **Chart Trader:** Sim/Playback only until the `trading-risk-manager` signs off.

---

## 12. Non-goals / deferred

- No change to the `AbsorptionScalper` strategy.
- No time×price heatmap, no cross-restart persistence, no multi-instrument (still deferred from v1).
- No auto-execution from the green-light — the Cockpit suggests, the human clicks. (Auto-trade is the strategy's job, separately gated.)
- NQ wall-mode (engine inert there).

---

## 13. Open questions for the implementation plan

1. **Re-anchor UX:** auto at the 4-tick edge (mockup) vs a manual **Center** button vs both. Default: auto-edge.
2. **Signal set lock:** keep all 5; drop/add any after the measurement pass? (e.g. add a stacking-vs-pulling read.)
3. **Green-light thresholds** `N`/`M`/opposing-veto: defaults 3 / 0.55 / 0.55 — tune on measured data.
4. **Cockpit as part of `RadarVisual` vs a sibling `FrameworkElement`** (cleaner separation, easier to test render).
5. **`W_assoc` wiring** (per-decrease attribution) now or deferred — affects erosion + delta accuracy.
6. **Chart Trader scope:** ship MKT-only first, or include a quick **SL/TP / bracket** and an **ATM** toggle in v1? Keep "Entry"?
7. **PressureModel inputs:** does it consume `RadarNode[]` only, or also a thin live-book view for masses/microprice not currently in `RadarNode`?

---

## 14. Phasing (high-level — detailed in the plan)

1. **Anchored ladder** (`RadarVisual`) — the standalone fix; immediately usable, low risk.
2. **Engine additions** (`BookMirror` running delta, `EpisodeClassifier` erosion) + **`PressureModel`** with placeholder weights + unit tests.
3. **Cockpit render** (bias/conditions/overlays) wired to `PressureModel`.
4. **Signal measurement** on ES captures → real weights → re-tune thresholds.
5. **Chart Trader** UI → Account-API wiring (Sim/Playback) → `trading-risk-manager` gate before real.

Implementation runs through the `trading-*` agents (NinjaScript dev → code-reviewer → backtest/measure → **risk-manager VETO** for the order-entry path).
