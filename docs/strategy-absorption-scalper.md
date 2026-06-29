# Strategy + Validation Spec — Absorption Scalper (ES)

- **Date:** 2026-06-28
- **Author:** Quant Researcher (edge/spec owner) — read-only on `.cs`; this is the spec the developer implements and the criteria the backtest-analyst measures against.
- **Instrument/session:** ES front month, RTH only (09:30–16:00 ET). Not NQ (engine is inert there — see playbook §1). Not overnight.
- **Built on:** the Liquidity Radar engine (`Engine/WallTracker.cs`, `EpisodeClassifier.cs`, `LiquidityMemory.cs`, `BookMirror.cs`) — the strategy reuses the *same* pure engine the add-on runs.
- **Grounds:** `docs/calibration-es-day1.md` (918 absorbed events, 2 days), `docs/playbook-entries.md` §1–§5, order-flow literature (cited at end).

---

## 1. Honest interim edge verdict (read this first)

**The signal is real. The standalone auto-scalper, as specced for an 8-tick TP, is probably marginal-to-negative after honest fills.** Both can be true at once, and they are. Here is the reasoning, accounting for each inflation source.

### What is genuinely established
The ABSORBED-at-a-wall signal is **reproducible** (64–77% hold @15–30s across two independent ES days), and the **size filter (peak ≥100) is robust on both days** (~71–75% vs ~59–66%). That is a real, short-horizon microstructure signal with academic basis (icebergs/absorption on a deep book; playbook §1, sources). This is not in dispute.

### Why the simulated +2 to +3 t/trade is almost certainly inflated
1. **Horizon contamination (the big one).** The signal's *true* magnitude is the median favorable move at its own horizon: **+1.5 ticks @15–30s.** The sim measured whether an **8-tick** TP was *reached* over a multi-minute horizon. ES drifts ~2 pts (8 ticks) in a few minutes as a matter of course. So "8t TP reached ~75%" is mostly **random wandering of a liquid index, not the signal** — the signal predicts a *level holds for 15–30s*, it does **not** predict an 8-tick directional move. The TP is being filled by volatility, the strategy is asking the signal to do something it was never shown to do. There was **no symmetric baseline**, so none of the apparent profit has been attributed to the signal vs to drift.
2. **2-second mid snapshots hide stop-outs.** Between snapshots ES can travel 4–8 ticks. The sim therefore **overstates** BE-reached and TP-reached and **understates** stop hits. Tick-level paths will move every number against the strategy.
3. **Fills at the wall are an adverse-selection trap (the killer).** To "buy at absorbed support" with a passive limit, you **join the back of the defending wall's queue** — 100+ contracts ahead of you. Your limit fills only after all of them fill, i.e. only when the level is **traded through** — *exactly when the level fails.* The 64–75% of cases where the wall **holds and price bounces** never trade down to your queue position, so **you don't get filled on the winners and you do get filled on the losers.** Passive-limit-at-the-wall *inverts* the edge. The sim's "passive limit-in" cell assumed a fill on every signal — its single most unrealistic and most flattering assumption. (Cont/queue-imbalance & adverse-selection literature; sources.)
4. **In-sample bracket selection.** The 3/4–4/4 brackets that printed positive were chosen on the same 2 days. Overfit until proven OOS.

### Realistic expectation after honest fills + a baseline
- A passive limit at the wall: **negative** (adverse selection).
- A marketable / stop-confirmation entry after the absorb: reliable fill but pays ~1–2 t of spread+lag, which **eats the entire ~1.5 t signal** before the trade starts → net **~0 minus cost**.
- There is **no execution that cleanly captures a 1.5-tick edge** net of ES round-trip cost (~0.5–1 t) plus adverse selection.

**Verdict: a real signal that is probably too thin to stand alone as an automated 8-tick scalper.** The most likely *positive* outcome is the one the playbook already endorses (§7): this signal works as a **confirmation/timing layer on a chart thesis**, not as a standalone trigger. The one design that could rescue a standalone version is Javier's BE-asymmetry (§2 exit) **if and only if** the signal demonstrably gets price to +3 t before the initial stop **more often than a random entry does** — that single comparison is the whole ballgame (§3a). I would **green-light a Market-Replay-sim build only** (cheap — reuses the engine) to run that experiment, and **block live money** until the baseline proves signal > drift.

---

## 2. Strategy spec

### 2.1 Signal (entry trigger)
- The strategy runs the **same engine** as the add-on: subscribe to `MarketDepth.Update` + `MarketData.Update`, feed `BookMirror.ApplyDepth/ApplyTrade`, call `WallTracker.Update`, then read `WallTracker.GetSnapshot(now)` each update (exactly as `RadarTab.cs` does).
- **Trigger = a node transitioning into `State==Absorbed`** (edge-detected: keep last state per `(Side,Price)` key, fire on `Live/Wall → Absorbed`), **with `PeakSize ≥ 100`** (`RadarNode.PeakSize` carries it — no engine change needed for the signal).
- **Direction:**
  - `Side.Bid` absorbed = absorbed **support** → **LONG**.
  - `Side.Ask` absorbed = absorbed **resistance** → **SHORT**.
- **Confidence is NOT a filter** (calibration: flat across buckets; size discriminates, confidence does not).

### 2.2 Entry order type — the decisive choice
Do **not** rest a limit *at* the wall price (back of the defending queue → adverse selection, §1.3). Two signal-coherent entries to test head-to-head in Replay (§3):
- **(A) Stop-confirmation entry — DEFAULT for the build.** Buy-stop **1 tick above** the absorbed bid-wall price (sell-stop 1 tick below the absorbed ask wall), valid for a **short window (~10 s)**, else cancel. It fills *only if price actually rejects off the level* — i.e. the predicted hold is happening — which sidesteps adverse selection. Cost: ~1–2 t of the move.
- **(B) Limit one tick in front of the wall** (buy-limit at wall **+1 t** inside): better price, fills on a shallow pullback into the bounce, lower fill rate, some adverse selection. Test as the alternative.
- **Explicitly rejected:** limit *at* the wall price, and any marketable chase more than ~2 t beyond the wall.
- One position at a time; **one entry per episode**; no re-entry on the same price within ~60 s.

### 2.3 Exit
- **Initial stop:** structural — a few ticks **beyond the wall price** (the thesis is the wall holding; if it's consumed, the thesis is dead). ES: ~**6 ticks** of risk from entry (entry at wall+1 t → stop at wall−5 t). Plus a **hard invalidation:** if the same node resolves `State==Consumed`, **exit at market immediately** (don't wait for the price stop).
- **Breakeven:** move stop to **entry − 1 t (breakeven + commission)** once MFE reaches **+3 ticks**.
- **Time-stop (critical, and it resolves the horizon tension):** the signal lives 15–30 s. **Until BE is set,** if the trade hasn't reached +3 t within **~30–45 s**, exit at market — this keeps the trade tied to the signal's actual horizon instead of converting a 15–30 s edge into an edgeless multi-minute drift bet. **Once BE is set (risk-free), drop the time-stop** and let the runner go.
- **Target:** **+8 ticks** (Javier's floor, clears commissions). Single TP for v1 (no scaling). The 8-t target is reached, if at all, by post-bounce continuation/drift *after* the trade is already risk-free at BE — that is the only honest way an 8-t TP coexists with a 1.5-t signal.

### 2.4 Sizing & guardrails
- **Size:** fixed **1 ES contract** for the whole validation. No scaling, no martingale.
- **Daily loss limit:** halt + flatten for the day after **3 stop-losses** or a **−$ daily cap** (risk-manager sets the dollar figure).
- **Max trades/day:** cap (~10–15) to refuse to grind chop.
- **Max concurrent:** 1.
- **Session guards:** skip the **first 5 minutes** of RTH and **macro releases** (book resets — `IsReset`; let it settle); RTH only; front month.
- **Cold start / no signal:** **flat and idle.** This is an *event* strategy — most of the day it does nothing. That is correct, not a bug.

---

## 3. Validation protocol — BEFORE any real money

The build's *purpose* is to run this protocol. Nothing here advances to live without passing every gate.

### (a) Baseline isolation — proves the signal, not the drift (the #1 requirement)
For every `ABSORBED & PeakSize≥100` entry timestamp, also simulate two controls at the **same time**:
- **(i) random-entry** (same time, randomized side),
- **(ii) opposite-side entry** (short the absorbed support).

Measure all three on the metrics that match the *signal's* horizon, not a 5-minute drift window:
- **P(reach +3 t before the initial stop)** — the BE-trigger rate. *This is the single number that decides go/no-go.*
- P(reach +8 t TP), MFE/MAE distribution, net expectancy.

**If the signal's +3-t-before-stop rate is not materially above random's, there is no tradeable edge → kill the standalone strategy** (fall back to the confirmation-layer use, playbook §7).

### (b) Real-fill Market-Replay sim (not Strategy Analyzer)
Strategy Analyzer **cannot replay L2 depth**, so the strategy must run **live in Market Replay**, exactly like the radar add-on: real depth, **tick-level price paths**, real bracket fills (NT models stop slippage). **N ≥ 5 RTH days, including ≥1 trending and ≥1 chop day** (calibration has only 2 quiet-ish days). Verify each replay day actually carries depth (`medBid/medAsk > 0` — the 6/23 "no L2" trap).

### (c) Out-of-sample
Pick entry type (A vs B), brackets, and timers on **days 1–3**; **lock them**; validate untouched on **days 4–7**. No peeking, no re-tuning on the OOS set.

### (d) Advance thresholds (go/no-go, measured on OOS)
- Net expectancy **≥ +1.0 tick/trade after realistic fills + commission** (clears the ~0.5–1 t cost with margin).
- Profit factor **≥ 1.3**.
- **Signal expectancy − baseline expectancy ≥ +0.75 t/trade** (proves it's the signal).
- Max intraday drawdown within the risk budget.
- **Win-rate is NOT a gate** (Javier accepts low WR for a bigger TP) — expectancy and signal-vs-baseline are.

### (e) Risk-manager VETO gate
Before any sim→live promotion the **risk-manager agent** reviews and signs off (or vetoes): per-trade risk vs account, daily-loss enforcement actually triggering in sim, and — critically — **whether the sim fills are achievable live** (did passive/stop fills in Replay match what a real seat behind the queue would get?). **No live money without that sign-off.**

---

## 4. What to capture next (to make validation possible)

- **Per-trade logging:** entry type, **fill price vs wall price** (this directly measures the adverse-selection gap — fill rate on holds vs breaks), MFE/MAE in ticks, time-to-+3 t, time-to-stop, exit reason, **wall PeakSize**, regime tag (trend/chop).
- **Surface the episode stream (nice-to-have, not a blocker):** `WallTracker.Update` currently drains `EpisodeClassifier.TryTakeResolved` straight into memory and does not expose `EpisodeResult` to callers. The signal works off `GetSnapshot` `State==Absorbed`, but exposing the resolved-episode stream (price, side, traded, cancelled, peak) would give cleaner logging and is the same thing as the playbook's gap #2 (persistent absorption log).
- **More ES days:** the 2 existing + ≥1 deliberately trending + ≥1 chop. Confirm L2 present each day.
- **Wire `W_assoc` (engine gap #3, `EpisodeClassifier.cs:85-87`):** windowed trade attribution. Until then ABSORBED is "likely," not "proven" → keep size at 1 and don't over-trust.

---

## Sources
- Order-flow imbalance ↔ short-horizon price (sensitivity ∝ 1/depth): https://arxiv.org/pdf/1707.01167
- Queue imbalance / adverse selection of resting limit orders: https://arxiv.org/pdf/1610.00261 , https://www.sciencedirect.com/science/article/pii/S1386418125000229
- Iceberg/absorption (refill = hidden size): https://bookmap.com/blog/how-to-read-and-trade-iceberg-orders-hidden-liquidity-in-plain-sight
- Why DOM scalping fails retail (latency, front-running, pulled walls): https://bookmap.com/blog/can-real-time-order-flow-give-you-an-edge-in-scalp-trading
