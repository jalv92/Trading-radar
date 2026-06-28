# Liquidity Radar — Design Spec

- **Date:** 2026-06-28
- **Status:** Approved design, pre-implementation
- **Type:** NinjaTrader 8 Add-On (standalone floating window)
- **Target instruments:** NQ / ES futures (front month)
- **Author:** Javier + Claude (brainstorm grounded by a 7-agent research workflow, two claims adversarially verified against official NT8 docs)

---

## 1. Summary

A standalone, floating NinjaTrader 8 window that reads Level-2 market depth and renders a **vertical "sonar ladder"**: a price ladder where each resting-liquidity level is a glowing bar (brightness ∝ size). On top of the live book it runs a **wall-tracking engine** that does what a plain DOM/heatmap does not: it detects large resting orders (walls), **remembers them after they scroll out of the visible 10 levels**, and when price returns it classifies the outcome — **absorbed**, **pulled (spoof)**, or **consumed-through (broken)** — with a decaying confidence score.

It is a *secondary awareness panel*, not an execution surface. The trader still executes on their real DOM/chart; the radar answers "where is the meaningful liquidity, is it still there, and what happened when price hit it" at a glance.

The visual identity is **Aurora**: deep-ink background, modern emerald/coral market colors, restrained glow, amber inside-market line. Explicitly *not* a Bookmap clone.

---

## 2. Goals & non-goals

### v1 goals
- Floating, chart-independent window opened from the Control Center "New" menu, persisted in the workspace.
- Live Level-2 subscription for one instrument (user-selectable).
- Sonar-ladder render of the current book (Aurora skin).
- Wall detection within the visible ~10 levels.
- Three-outcome classification (absorbed / pulled / consumed-through) when price reaches a tracked level.
- Liquidity **memory band** (±25 ticks) that keeps remembered walls visible beyond the live 10 levels, with confidence decaying while blind.
- Tunable parameters exposed (NQ defaults).

### Non-goals (deferred — see §10)
- Bookmap-style time×price heatmap (history image).
- Sound/popup alerts.
- Persisting memory across NT restarts.
- Multiple simultaneous instruments / multi-tab radars.
- Anything drawn on the chart itself.

---

## 3. Constraints & verified facts

All NT8 platform claims below were confirmed against official docs (`developer.ninjatrader.com`) by an adversarial verification pass.

1. **Standalone window is supported.** 4-class Add-On pattern: `AddOnBase` → `NTWindow` + `IWorkspacePersistence` → `INTTabFactory` → `NTTabPage`. `NTWindow` is a real WPF `Window`. (`nt8-addon`)
2. **L2 from an Add-On is supported without a chart.** Subscribe via the instrument: `instrument.MarketDepth.Update += OnMarketDepth` with handler `(object sender, MarketDepthEventArgs e)` — **not** the indicator `OnMarketDepth` override. Official docs show a verbatim `NTTabPage` example. (`nt8-common` / `marketdepth.md`)
3. **Depth is 10 levels per side, aggregated by price (MBP)** on the NT Brokerage / Continuum / Tradovate-unified feed. Not Market-By-Order — individual order counts/queue are invisible. (A CQG-based connection may deliver more for ES/CL; do **not** hardcode ladder size — read `Asks.Count` / `Bids.Count`.)
4. **Rendering = WPF, not SharpDX.** NT8's SharpDX `RenderTarget` is bound to `ChartControl.OnRender()` and is unavailable in a chartless window. Use a custom `FrameworkElement` overriding `OnRender(DrawingContext)`, animated with a `DispatcherTimer` at ~30 fps.
5. **No historical backtest of L2.** `OnMarketDepth` only fires on **live, simulated-realtime, or Market Replay** data with recorded depth. The Strategy Analyzer does **not** replay depth. Validation is therefore unit tests (pure logic) + Market Replay, never the optimizer.
6. **Threading.** Depth/trade events arrive on the **instrument dispatcher thread**, not the WPF UI thread. Subscribe/unsubscribe on `instrument.Dispatcher` (guard with `HasShutdownStarted`); marshal snapshots to the UI thread with `Dispatcher.BeginInvoke(DispatcherPriority.Render, …)` before touching any visual.

---

## 4. Architecture — 6 units

The design isolates the **edge (pure C#, no NT, unit-testable)** from NT8 glue and from rendering.

| Unit | Type | Single responsibility | Depends on |
|---|---|---|---|
| `RadarAddOn` | `AddOnBase` | Register the "Liquidity Radar" menu item under Control Center → New; open the window via `Core.Globals.RandomDispatcher.InvokeAsync`; remove the item in `OnWindowDestroyed` (recompile-safe). | NT8 |
| `RadarWindow` | `NTWindow`, `IWorkspacePersistence` | The floating window shell. `Caption`, size, `WorkspaceOptions` (GUID-suffixed key), Save/Restore XML. **Top-level class, default ctor, not nested.** | NT8 |
| `RadarTab` | `NTTabPage` | Glue + **threading boundary**. Hosts `InstrumentSelector` + `RadarVisual`. Subscribes/unsubscribes `MarketDepth.Update` and `MarketData.Update` on the instrument dispatcher. Feeds events to `BookMirror`, marshals node snapshots to `RadarVisual`. Calls `base.Cleanup()`. | NT8, BookMirror, WallTracker, RadarVisual |
| **`BookMirror`** | Pure C# | Maintain the live bid/ask ladder from the `Add/Update/Remove/IsReset` stream (positional). Track recent `Last` trades per price for `Traded@P`. Infer aggressor side (Last≥Ask = buy aggressor; Last≤Bid = sell aggressor). **No UI.** | nothing |
| **`WallTracker`** | Pure C# | The edge. Wall detection, classification episodes, confidence/memory nodes. Emits an immutable snapshot `IReadOnlyList<RadarNode>`. **No UI.** | nothing |
| `RadarVisual` | `FrameworkElement` | Render only: sonar ladder + memory band, Aurora skin, glow, state colors, fade, sweep/decay animation. Reads node snapshots; no trading logic. | WPF |

`BookMirror` and `WallTracker` are NT-free → testable with synthetic `MarketDepthEventArgs`/trade sequences. `RadarVisual` is render-only. `RadarTab` is the only place threads cross.

`RadarNode` (the contract between engine and view):
```
RadarNode {
  double price; Side side;            // Bid | Ask
  long   lastKnownSize; long peakSize;
  NodeState state;                    // Live | Wall | Absorbed | Pulled | Consumed | Remembered
  double confidence;                  // 0..1
  bool   inWindow;                    // within the live 10 levels
  double ageSeconds;                  // since lastSeen (for "· 18s")
}
```

---

## 5. Data flow & threading

```
instrument.MarketDepth.Update ─┐  (instrument dispatcher thread)
instrument.MarketData.Update  ─┤
                               ▼
                      BookMirror.Apply(e)          // live ladder + Traded@P
                               ▼
                      WallTracker.OnTick()         // detection + episodes + memory
                               ▼  snapshot: IReadOnlyList<RadarNode>
        Dispatcher.BeginInvoke(Render) ──────────► (WPF UI thread)
                               ▼
                      RadarVisual.SetNodes(snapshot) → InvalidateVisual → OnRender
```
- `IsReset == true` → wipe `BookMirror`, mark all in-window nodes blind, rebuild from `instrument.MarketDepth.Asks/.Bids` snapshot. Never interpret a reset's mass Remove/Add as real pulls.
- Animation runs independently on a `DispatcherTimer` (sweep angle + decay visuals); data updates only swap the node snapshot.

---

## 6. Microstructure logic (the edge)

### 6.1 Book mirror
Two ordered lists (bids desc, asks asc), indexed by `Position`. `Add` inserts at `Position`, `Update` overwrites, `Remove` deletes. `Volume` is **price-aggregated** resting size, never an order count.

### 6.2 Wall detection
Baselines use **median** (not mean) so a wall can't inflate its own benchmark:
- `B_level` = cross-sectional median of the other visible level sizes on that side **now**.
- `B_time` = rolling temporal median of per-level sizes over `BaselineWindow`.
- `B = max(B_level, B_time)`.

A price becomes a **wall candidate** only when ALL hold:
1. **Relative:** `Volume(P) ≥ K_mult · B`
2. **Absolute floor:** `Volume(P) ≥ MinAbsSize`
3. **Persistence:** qualifying size held continuously ≥ `T_persist` (timer resets if it drops)
4. **Flicker guard:** reject if the price oscillates Add/Remove > `F_flicker`/s (layered spoofs blink)

Candidate → confirmed wall once persistence clears.

### 6.3 Three-outcome classification — the shared discriminator
> The depth feed never says **why** size fell. Attribute every size decrease by cross-referencing `Last` prints at that price within `W_assoc` (the two streams are unsynchronized).

An **episode** opens when best quote comes within `D_approach` ticks of a tracked node (or a trade prints at P); it resolves on break, size→0, or `T_episode` timeout.

- **ABSORBED** — decrease explained by trades **AND** price holds **AND** level refills: `refill_ratio = Traded@P / max(displayed_drop,1) ≥ refill_ratio_trigger`, inside quote never crosses P, `Traded@P ≥ A_absorb · S0`. → strongest level, raise confidence.
- **PULLED (spoof)** — decrease **NOT** explained by trades: `Cancelled = max(0, displayed_drop − Traded@P)` dominates, and the quote was still ≥ `D_pull` ticks away when size vanished. → collapse confidence, phantom flag; dead after `P_max` pulls. *(Honest: MBP can't prove intent — probabilistic, down-weight not hard-trust.)*
- **CONSUMED-THROUGH** — decrease explained by trades **AND** price breaks (inside quote crosses P, node removed coincident with trades). → demote to a flipped S/R reference at lower confidence.

### 6.4 Confidence & memory (decay while blind)
`Node { price, side, lastKnownSize, peakSize, firstSeen, lastSeen, timesConfirmed, absorbedCount, pulledCount, consumed, confidence, inWindow, state }`

- **Init** on promotion: `C0 = clamp(0.4 + 0.1·(size/B − K_mult), 0.4, 0.8)`.
- **Decay while blind** (level scrolls to Position > 9): freeze `lastKnownSize`, `inWindow=false`, `C(t) = C_last · exp(−ln2/H · Δt)`, half-life `H`. After ~2–3 half-lives ≈ unknown. Decay runs **only** while blind; while live, confidence is observation-driven.
- **Revisit updates:** confirmed `+dC_confirm`; grew (≥ `G_grow`%) `+dC_grow`; shrank `×shrink_factor`; gone/pulled `×pull_penalty` + phantom; absorbed `+dC_absorb`; consumed → flipped level.
- **Eviction:** drop when `confidence < C_floor` AND age > `T_evict`.
- **Never** surface a blind node as live size — expose "last-known X, age Y, confidence Z".

### 6.5 Failure modes & mitigations
| Mode | Mitigation |
|---|---|
| Spoofing / layering | PULLED classifier + pull_penalty + phantom + death after `P_max`; persistence + flicker guards filter blink-spoofs. Probabilistic — never act before a wall has PERSISTED and CONFIRMED. |
| Icebergs / hidden size | `refill_ratio ≫ 1` is the signal (strongest ABSORBED evidence). Total hidden size is unknowable — confirm "more was there than shown" only after the fact. |
| Aggregated MBP (order count hidden) | No order-count inference. Trust built on persistence + behavior, not assumed composition. `MarketMaker` empty on CME — ignore. |
| Fast markets | All constants are **time-based**; a `VolGovernor` scales `H`, `T_episode`, `T_persist` by depth-update rate / ATR. Treat memory as suspect in violent moves. |
| Level leaves the 10-window | Core premise: freeze + decay; report last-known + age + confidence, never as live. |
| Feed reset (`IsReset`) | Wipe + rebuild from snapshot; do not read the reset as real pulls/adds. |
| Decrease ambiguity (trade+cancel same instant) | Attribute up to `Traded@P` to trading; only the residual counts as cancellation, matched within `W_assoc`. |

### 6.6 Tunable parameters (NQ defaults)
| Param | Purpose | Default (NQ) |
|---|---|---|
| `K_mult` | size multiple over baseline to qualify a wall | 4.0 (ES ~3.0) |
| `MinAbsSize` | absolute contract floor | 40 (ES ~300) |
| `BaselineWindow` | temporal-median window | 30 s |
| `T_persist` | rest time before candidate→confirmed | 1500 ms |
| `F_flicker` | max Add/Remove osc. before reject | 6 /s |
| `D_approach` | ticks to open an episode | 1 |
| `T_episode` | episode timeout | 3000 ms |
| `W_assoc` | match Last↔depth at same price | 250 ms |
| `A_absorb` | `Traded@P / S0` to call absorption | 1.0 |
| `refill_ratio_trigger` | iceberg-refill threshold | 3.0 |
| `D_pull` | ticks quote is away when size vanishes → pull | ≥1 |
| `H` | confidence half-life while blind | 30 s |
| `dC_confirm / dC_grow / dC_absorb` | confidence increments | 0.15 / 0.20 / 0.25 |
| `shrink_factor / pull_penalty` | confidence haircuts | 0.6 / 0.2 |
| `P_max` | pulls before node dead | 2 |
| `G_grow` | % increase to count as GREW | 25% |
| `C_floor / T_evict` | eviction thresholds | 0.05 / 300 s |
| `VolGovernor` | regime multiplier | ×0.3..1.0 |
| `MemoryBandTicks` | ± ticks shown around market | 25 |

---

## 7. Visual design — Aurora

### 7.1 Layout
Vertical sonar ladder, inside market pinned center, asks above, bids below. Per row: `price | size bar (glow) | size number | state badge`. The ladder spans `±MemoryBandTicks` (25); inner ≤10 levels are **live**, outer rows are **remembered** (dimmed). Horizontal iso-distance gridlines every 5/10/25 ticks act as the "range rings". A slow vertical sweep / outward ping from the market line is the refresh pulse. Footer legend. Title bar: instrument chip + LIVE pulse.

### 7.2 Design tokens (locked from the approved mockup)
- **Window bg:** `radial-gradient(120% 80% at 50% 0%, #121826 0%, #0a0e16 60%, #080b11 100%)`; border `rgba(255,255,255,.07)`.
- **Bid / support:** `#34d399`, bar gradient from `rgba(52,211,153,.22)`, glow `rgba(52,211,153,.50)`; size text `#6ee7b7`.
- **Ask / resistance:** `#fb7185`, bar gradient from `rgba(251,113,133,.25)`, glow `rgba(251,113,133,.55)`; size text `#fda4af`.
- **Inside-market line:** amber `rgba(255,206,92,.5)`; chip bg `#1a1d12→#23260f`, text `#ffe08a`, border `rgba(255,206,92,.35)`.
- **Price text:** `#cfd6e2`. **Bar track:** `rgba(255,255,255,.035)`.
- **Fonts:** labels = Segoe UI (NT default); **numbers = Cascadia Mono / Consolas, tabular** for column alignment.

### 7.3 State visuals
| State | Treatment |
|---|---|
| Live (normal) | side-colored bar, glow ∝ size |
| WALL | brighter bar + pill badge (side-colored: coral for ask, emerald for bid) + stronger glow |
| ABSORB | sustained bright **pulse**, emerald badge `#a7f3d0` |
| PULL | bar desaturated (`saturate .3, brightness .7`), **dashed** outline, slate badge `#94a3b8`; fade-and-retract motion |
| CONSUMED | flash then clear as the row crosses the market; demote to dim flipped-level |
| Remembered (blind) | whole row `opacity .34`, **no glow**, age tag "· 18s" |

### 7.4 Animation
`DispatcherTimer(DispatcherPriority.Render, 33 ms)` ≈ 30 fps. Drives: sweep/ping position, phosphor fade of stale rows, absorb pulse. Data snapshots swap independently. (60 fps only if needed, via `CompositionTarget.Rendering` + time-delta guard.)

### 7.5 Rendering approach (WPF)
Custom `FrameworkElement.OnRender(DrawingContext)`: `DrawRectangle` (track/bg), `DrawRoundedRectangle` (bars), `DrawText` via `FormattedText` (pass `VisualTreeHelper.GetDpi(this).PixelsPerDip`), `LinearGradientBrush` (bar fills), gridlines.
- **Glow caveat:** a global `DropShadowEffect` rasterizes the whole element (≈2× cost). Prefer **per-bar glow drawn manually** — a second, larger, semi-transparent rounded-rect behind each bar — so glow stays cheap and never bleeds over the numbers. Cap bloom; numbers always render on top, fully opaque.
- Freeze brushes/pens/geometries created per frame.

---

## 8. Validation
- **Unit tests** (NT-free) for `BookMirror` (positional Add/Update/Remove/IsReset correctness) and `WallTracker` (each classification path + decay + revisit transitions) with synthetic event sequences. assert-based, no framework needed.
- **`nt8c`** for compile validation of the Add-On.
- **Market Replay** NQ session with recorded depth for live-faithful, reproducible visual/behavior validation. Strategy Analyzer is not usable (no L2 replay).

## 9. Risks & honest weaknesses
- Blind beyond 10 levels — memory is last-known, not live (the whole confidence/decay model exists for this).
- MBP hides order count and icebergs except via absorption.
- No time-history axis (that's the heatmap's strength, deferred).
- A literal "sweep" implies periodic discrete refresh; real updates are async — keep the animation honest (it's a refresh pulse, not a scan claim).
- Glow vs legibility tension — mitigated by per-bar glow + opaque numbers on top.
- External-DLL Add-Ons need a copy-to-`bin/Custom/AddOns/` + NT restart between builds; NinjaScript-editor files hot-reload. Decide build mode in the plan.

## 10. Deferred (post-v1)
Time×price heatmap; sound/popup alerts; cross-restart memory persistence; multi-instrument; chart overlay; per-maker analysis; CQG-extended depth (>10).

## 11. Open questions for implementation
- Build as NinjaScript-editor file (hot reload) or external VS DLL (restart per build)? Default: editor file for v1.
- Instrument via `InstrumentSelector` only, or also opt into NT window-linking with a chart? Default: selector; linking optional.
- Exact `MemoryBandTicks` and row height for NQ vs ES legibility — tune in Market Replay.
