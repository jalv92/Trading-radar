# Cockpit Signal Measurement (Plan D) — findings + enhanced-capture path

- **Date:** 2026-06-29
- **Goal:** measure each `PressureModel` signal's directional edge on real ES data → replace the placeholder weights in `PressureConfig` with measured ones (spec §9 "measure-first" mandate).
- **Data:** the two captured ES days — `lr-capture-ES-20260628-203633.csv` (6/22) + `…-205104.csv` (6/24), 918 absorbed events, ~22.8k mid snapshots (`docs/calibration-es-day1.md`).
- **Tool:** `tools/measure/measure_signals.py` (no deps; `python3 measure_signals.py <capture.csv> …`).

---

## 1. The hard constraint: the existing capture can't measure the cockpit signals

The `Rec` capture logs only **wall/absorbed events + a 2s mid snapshot + MEDIAN book sizes** (`medBid`/`medAsk`). It does **not** log the full book, best-bid/ask sizes, the trade tape, or a wall's continuous size trajectory. So of the 5 `PressureModel` signals, only **two lossy proxies** are computable here:

| Cockpit signal | Measurable from existing capture? |
|---|---|
| Imbalance (bid mass vs ask mass) | only as `sign(medBid − medAsk)` — a **lossy** median proxy |
| Inside-thin (best sizes) | **no** (best sizes not logged) |
| Air-pocket (full near-book) | **no** |
| Delta (aggressor flow) | **no** (no trade tape) |
| Wall-erosion (size trajectory) | **no** (only state *transitions*; 0 Pulled in the data) — Absorbed direction used as a wall proxy |

## 2. What was measured (metric-robust: SIGNAL dir vs OPPOSITE dir, same metric both arms)

**Absorbed-wall direction** (proxy for the wall/structural signal) — net mid move over the horizon, in the absorbed-defense direction (Bid=support→up, Ask=resistance→down), combined both days:

| Horizon | filter | n | signal | opposite |
|---|---|---|---|---|
| 15s | all | 856 | 49% | 51% |
| 30s | all | 876 | 47% | 53% |
| 60s | all | 878 | 48% | 52% |
| 60s | peak≥100 | 190 | 55% | 45% |

**Median-imbalance proxy** — `sign(medBid − medAsk)` predicting the next mid move: **51.3% / 50.9% / 50.1%** @15/30/60s (n≈20k).

## 3. Honest verdict

- **No net-directional edge is measurable from this capture.** Signal ≈ opposite ≈ 50% for the absorbed proxy; the median-imbalance proxy is ≈ coin-flip. The faint `peak≥100 @60s` bump (55%) is not robust (6/22 63% vs 6/24 45%).
- **This does NOT contradict `calibration-es-day1.md`'s 64–77% "hold-rate."** Those measure *different things*: hold-rate = "the level wasn't broken" (level-integrity), which is real but **definition-sensitive** (a strict path-touch definition gives ~25–57%, not 64–77%) and does **not** translate to net direction. The absorbed signal is a **level-hold / timing** read, exactly as the playbook §1/§7 already concluded — not a directional predictor.
- **The median-imbalance ≈ 50% is inconclusive, not a kill:** median size is a poor proxy for true book-mass imbalance (the signal `PressureModel` actually computes). The real Imbalance / Delta / Inside-thin / erosion-trajectory signals — the ones with order-flow-imbalance literature behind them — **cannot be tested without the full inputs.**

**Conclusion:** the existing data cannot validate the cockpit's directional weights. They stay **PLACEHOLDER** (`GreenConviction = 4`, the §6 defaults). This is the spec §9 "measure-first" mandate doing its job: it told us the data is insufficient *before* we trusted a weight.

## 4. The unblocking deliverable — enhanced capture

To make a real measurement possible, this plan adds (additive, nt8c-gated):
1. `WallTracker.ErosionReads(book, now)` — a passthrough surfacing the Plan B `EpisodeClassifier.ErosionReads`.
2. `RadarTab` writes a second `lr-signals-<inst>-<ts>.csv` alongside `Rec`, logging the **full `PressureInputs` per 2s snapshot**: `time,mid,bidMass,askMass,bestBid,bestAsk,delta15s,wallFrac,wallAbove,wallPx`.

**Next step (Javier, on the Windows box):** run an ES Market-Replay session with `Rec` on (verify `medBid/medAsk > 0` = L2 present). That produces `lr-signals-*.csv`. Then re-run the measurement (the script gets a per-snapshot consumer that computes each signal's lean with the same formulas as `PressureModel.Signals` and measures P(next-15/30s mid moves in the lean direction) vs opposite) → the **measured per-signal edge → real `PressureConfig` weights**, replacing the placeholders. Until then, trade nothing on the cockpit's directional read; it is provisional.
