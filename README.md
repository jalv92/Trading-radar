# Liquidity Radar

A standalone **NinjaTrader 8** add-on that reads Level-2 market depth and renders a vertical **"sonar ladder"** of resting liquidity. Unlike a plain DOM or heatmap, it **tracks each large order wall as an object with memory**: it remembers walls after they scroll beyond the visible 10 levels, and when price returns it classifies what happened —

- **Absorbed** — trades hit the wall, price held, the level refilled (iceberg signature),
- **Pulled** — size vanished *without* trades (probable spoof),
- **Consumed-through** — trades ate the wall and price broke past it,

each carried with a **confidence score that decays while the level is out of view**.

It is a *secondary awareness panel*, not an execution surface. Visual identity: **"Aurora"** — deep-ink background, emerald (bid/support) / coral (ask/resistance), amber inside-market line. Explicitly *not* a Bookmap clone.

> **Status**
> - ✅ **Engine** — complete. Pure C#, deterministic, NinjaTrader-free, **34/34 unit tests, 0 warnings**.
> - 📐 **NT8 UI layer** — fully designed and planned (see [`docs/plans`](docs/plans)), **not yet implemented**. Requires a Windows machine with NinjaTrader 8 + Market Replay.

## Why it exists

A radar is blind beyond the 10 visible depth levels. The edge here is the **memory model**: large walls are detected (median baseline + persistence + flicker guards), remembered with a decaying confidence once blind, and re-evaluated on revisit. That turns "where is the meaningful liquidity, is it still there, and what happened when price hit it" into a single glance.

## Architecture

Two layers. The **engine** is isolated from NinjaTrader so it can be unit-tested with synthetic event sequences; the **NT layer** is the only place threads and the platform API cross.

### Engine (`Engine/` — pure C#, `netstandard2.0`, C# 7.3)

| Class | Responsibility |
|-------|----------------|
| `Primitives` / `RadarConfig` | DTOs (`DepthEvent`, `TradeEvent`, `DepthLevel`, `RadarNode`) + all tunable parameters (NQ defaults) |
| `BookMirror` | Positional MBP book + recent-trade ring + aggressor inference + median baselines |
| `WallDetector` | Median baselines (cross-sectional + temporal) and the 4 wall criteria: relative size, absolute floor, persistence, flicker |
| `EpisodeClassifier` | The three-outcome discriminator (absorbed / pulled / consumed) via trade↔depth cross-reference |
| `LiquidityMemory` | Confidence: init, decay-while-blind (half-life), revisit updates, eviction, snapshot |
| `WallTracker` | Orchestrator — wires the four together and emits an immutable `RadarNode[]` |

**Determinism:** the engine never reads a wall clock. Time enters only through event timestamps and an explicit `now` parameter, which is what makes it fully unit-testable.

### NT8 layer (`NinjaTrader/` — planned)

`RadarAddOn` (Control-Center menu) → `RadarWindow` + `RadarTabFactory` (floating window, workspace-persisted) → `RadarTab` (threading boundary: subscribes L2 + trades on the instrument dispatcher, maps NT event args to engine DTOs, marshals immutable snapshots to the UI) → `RadarVisual` (Aurora WPF render).

```
MarketDepth.Update ┐ (instrument thread)
MarketData.Update  ┤
                   ▼  map → DTO
            BookMirror → WallTracker.Update(now)
                   ▼  RadarNode[] snapshot (immutable)
        marshal to UI thread ──► RadarVisual.OnRender  (Aurora)
```

## Build & test the engine

Requires the .NET SDK (8 or 10).

```bash
dotnet test          # 34/34 passing, 0 warnings
dotnet build         # netstandard2.0 engine + net8.0 test project
```

## The NinjaTrader add-on

The engine is compiled **as source** into NinjaTrader's `Custom` assembly (not referenced as an external DLL), so the whole add-on builds as one unit with hot-reload during development. Because NinjaTrader does **not** replay Level-2 in the Strategy Analyzer, validation is: unit tests for the engine (here) + `nt8c` compile checks + **Market Replay** behavioral/visual passes. See [`docs/plans/2026-06-28-liquidity-radar-nt-ui.md`](docs/plans/2026-06-28-liquidity-radar-nt-ui.md).

## Documentation

- [`docs/specs/2026-06-28-liquidity-radar-design.md`](docs/specs/2026-06-28-liquidity-radar-design.md) — full design spec (microstructure logic, Aurora tokens, verified NT8 facts).
- [`docs/specs/2026-06-28-liquidity-radar-engine-contract.md`](docs/specs/2026-06-28-liquidity-radar-engine-contract.md) — frozen engine interface contract.
- [`docs/plans/2026-06-28-liquidity-radar-engine.md`](docs/plans/2026-06-28-liquidity-radar-engine.md) — engine implementation plan (built).
- [`docs/plans/2026-06-28-liquidity-radar-nt-ui.md`](docs/plans/2026-06-28-liquidity-radar-nt-ui.md) — NT8 UI layer plan (next).

## Roadmap

- [x] Engine: book mirror, wall detection, three-outcome classification, confidence/memory — unit-tested.
- [ ] NT8 add-on: the 4 NT classes + Aurora render, validated by `nt8c` + Market Replay.
- [ ] Parameter calibration from a debug data-capture path during Replay (instead of tuning blind).
- [ ] Deferred (post-v1): time×price heatmap, alerts, cross-restart memory persistence, multi-instrument.

## Disclaimer

A market-microstructure **awareness** tool. It places no orders and is not financial advice. Depth feeds are probabilistic about *why* size changes — spoof/iceberg detection is inference, not proof.
