# Consumption-Break Setup & Controller Spine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Delegation:** engine tasks (1–8) → `trading-ninjascript-developer` (they are pure C#/xUnit, TDD here in WSL) then `trading-code-reviewer`; NT tasks (9–12) → same pair, gated by `nt8c` build + Market Replay; the order-entry task (12) additionally requires the `trading-risk-manager` VETO before any real account. Never a generic agent.

**Goal:** Replace the flipping per-tick Cockpit Net% with a stateful **Consumption-Break** setup on an anti-flip Controller state machine, plus glanceable tape-speed reads, so the radar gives a defined buy/sell trigger that fires once on a confirmed change-of-control and latches until reset.

**Architecture:** Three new pure `Engine/` units (`TapeSpeed`, `ConsumptionTracker`, `ControllerStateMachine`) plus tape-window reads added to `BookMirror` and a vote-less `BookSkewContext` on `PressureModel`. The NT layer (`RadarTab`) feeds trades to `TapeSpeed`, assembles Controller inputs each ~20 Hz run from data it already marshals (`wallAbove`/`wallBelow`, `AggressorDelta`, `ErosionReads`), and exposes the Controller state + fire event to `CockpitVisual` (render) and `RadarChartTrader` (pre-stage a break-direction limit on fire). Every threshold is a placeholder measured from an extended Rec CSV.

**Tech Stack:** C# 7.3 / netstandard2.0 (Engine), xUnit (Tests), WPF `DrawingContext`/`FrameworkElement` (NT render), NinjaTrader 8 Account API (order entry). `dotnet test` in WSL for the engine; `nt8c` staged-`Custom/` build + NT8 Market Replay for the NT layer.

## Global Constraints

- **Engine stays NT-free:** `Engine/*.cs` reference no NinjaTrader types. Time enters only via event/parameter timestamps — no `DateTime.Now`, no clock. (Determinism is required for tests and Replay-rewind safety.)
- **Language floor:** C# 7.3, netstandard2.0 (matches `TradingRadar.Engine.csproj`). No C# 8+ syntax (no switch expressions, no nullable-reference annotations).
- **Additive only:** do not change any existing engine output the `AbsorptionScalper` consumes (`RadarNode.State` / `PeakSize` / `Confidence` / memory). New methods/types only; existing signatures preserved. `AbsorptionScalper` is untouched.
- **Tests:** xUnit `[Fact]`, assert-based, no framework beyond xUnit, class with no namespace, `using TradingRadar.Engine;` — match the existing `Tests/*.cs` idiom (static `L(p,v)` helpers, one behavior per fact).
- **Thresholds are placeholders, measured later:** every new tunable lives in a config class (never a literal in logic), documented as measured from Rec CSV (spec §9). Ship reasonable defaults; do not claim they are calibrated.
- **Visual identity:** Aurora only (deep-ink bg, emerald `#34d399` / coral `#fb7185`, amber inside-market, mono tabular numbers). Do not introduce a new style.
- **NT build gate:** the `nt8c` hook throws false CS0246 cross-file errors against the real `Custom/` dir (stock `@`-files). Validate NT changes with an **isolated staged build** of only our 11+ `.cs` files, as established. Trust `dotnet build`/`dotnet test` for the engine.
- **Order path gate:** `RadarChartTrader` order submission to a **real** account stays behind the `trading-risk-manager` VETO (F1–F17). Default account = Playback/Sim. The human always clicks the final submit.

**Aggressor convention (from `BookMirror.InferAggressor`, do not re-derive):** `Side.Ask` aggressor = a BUY (price lifted the offer); `Side.Bid` aggressor = a SELL (hit the bid). `AggressorDelta = buyVol − sellVol` (positive = buyers pressing). A wall **above** price is an **ask** wall; consuming it is a LONG break driven by `Side.Ask` (buy) aggressors. A wall **below** is a **bid** wall; consuming it is a SHORT break driven by `Side.Bid` (sell) aggressors.

---

## File Structure

| File | New? | Responsibility |
|---|---|---|
| `Engine/BookMirror.cs` | modify | add tape-window reads (aggressor exposure, windowed prints/buy-vol/sell-vol, alternation count) on the existing trade ring |
| `Engine/TapeSpeed.cs` | **new** | stateful EWMA baseline of the print rate → z-score; the only unit that keeps memory longer than the trade ring |
| `Engine/ConsumptionTracker.cs` | **new** | pure read: consumption fraction + trade-backed fraction for one wall |
| `Engine/ControllerStateMachine.cs` | **new** | the anti-flip spine: per-side candidate state machine + CHOP gate → state + one-shot fire event |
| `Engine/PressureModel.cs` | modify | add vote-less `BookSkewContext` (collapse of imbalance/inside-thin/air-pocket) |
| `Tests/BookMirrorTests.cs` | modify | tape-window read tests |
| `Tests/TapeSpeedTests.cs` | **new** | z-score rising/steady/spiking |
| `Tests/ConsumptionTrackerTests.cs` | **new** | trade-backed vs pull |
| `Tests/ControllerStateMachineTests.cs` | **new** | arm/suppression/countdown/fire/chop/**anti-oscillation** |
| `Tests/PressureModelTests.cs` | modify | `BookSkewContext` sign |
| `NinjaTrader/RadarTab.cs` | modify | feed `TapeSpeed`; assemble Controller inputs; expose state+fire on `Frame`; extend Rec CSV |
| `NinjaTrader/CockpitVisual.cs` | modify | render state + countdown + tape-speed bar + z-score + CHOP + vote-less context strip |
| `NinjaTrader/RadarChartTrader.cs` | modify | on fire, pre-stage break-direction limit + "SETUP LONG/SHORT listo" |

---

## Task 1: BookMirror — tape-window reads

**Files:**
- Modify: `Engine/BookMirror.cs`
- Test: `Tests/BookMirrorTests.cs`

**Interfaces:**
- Consumes: existing `_trades` ring (`Trade { Price, Volume, Time, Aggressor }`), existing `InferAggressor`.
- Produces:
  - `Side AggressorOf(double price)` — public wrapper over `InferAggressor` (for the NT layer to forward to `TapeSpeed`).
  - `struct TapeWindow { public int Prints; public long BuyVol; public long SellVol; }`
  - `TapeWindow WindowSince(DateTime since)` — counts/sums retained trades with `Time >= since`.
  - `int RecentAlternations(int lookback)` — number of aggressor sign changes across the last `lookback` retained trades.

- [ ] **Step 1: Write the failing tests**

Add to `Tests/BookMirrorTests.cs` (match the file's existing helpers/idiom):

```csharp
[Fact]
public void WindowSince_counts_prints_and_splits_buy_sell_volume()
{
    var b = new BookMirror(0.25, System.TimeSpan.FromSeconds(10));
    // Establish an inside so aggressor inference is well-defined.
    b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 99.75, Volume = 50, Time = T(0) });
    b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 50, Time = T(0) });
    b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 3, Time = T(1) }); // buy (lifted ask)
    b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 2, Time = T(2) }); // buy
    b.ApplyTrade(new TradeEvent { Price = 99.75,  Volume = 4, Time = T(3) }); // sell (hit bid)
    var w = b.WindowSince(T(0));
    Assert.Equal(3, w.Prints);
    Assert.Equal(5, w.BuyVol);
    Assert.Equal(4, w.SellVol);
}

[Fact]
public void RecentAlternations_counts_aggressor_sign_changes()
{
    var b = new BookMirror(0.25, System.TimeSpan.FromSeconds(10));
    b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 99.75, Volume = 50, Time = T(0) });
    b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 50, Time = T(0) });
    b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(1) }); // buy
    b.ApplyTrade(new TradeEvent { Price = 99.75,  Volume = 1, Time = T(2) }); // sell  -> alt 1
    b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(3) }); // buy   -> alt 2
    Assert.Equal(2, b.RecentAlternations(3));
}
```

Add a `T` helper at the top of the class if not present:

```csharp
static System.DateTime T(int s) => new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc).AddSeconds(s);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~WindowSince_counts|FullyQualifiedName~RecentAlternations_counts"`
Expected: FAIL — `AggressorOf`/`WindowSince`/`RecentAlternations` do not exist (compile error).

- [ ] **Step 3: Implement in `BookMirror.cs`**

Make the existing inference reusable and add the reads. Change `InferAggressor` to delegate to a public method, and append the new members:

```csharp
public Side AggressorOf(double price) { return InferAggressor(price); }

public struct TapeWindow { public int Prints; public long BuyVol; public long SellVol; }

public TapeWindow WindowSince(DateTime since)
{
    TapeWindow w = new TapeWindow();
    for (int i = 0; i < _trades.Count; i++)
    {
        Trade tr = _trades[i];
        if (tr.Time < since) continue;
        w.Prints++;
        if (tr.Aggressor == Side.Ask) w.BuyVol += tr.Volume; else w.SellVol += tr.Volume;
    }
    return w;
}

// Aggressor sign changes across the last `lookback` retained trades (oldest->newest).
public int RecentAlternations(int lookback)
{
    int n = _trades.Count;
    int start = lookback <= 0 || lookback >= n ? 0 : n - lookback;
    int alts = 0;
    bool have = false; Side prev = Side.Ask;
    for (int i = start; i < n; i++)
    {
        Side a = _trades[i].Aggressor;
        if (have && a != prev) alts++;
        prev = a; have = true;
    }
    return alts;
}
```

`InferAggressor` stays private and unchanged; `AggressorOf` just exposes it. (`_trades`, `Trade`, `Side` all already exist.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~WindowSince_counts|FullyQualifiedName~RecentAlternations_counts"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Engine/BookMirror.cs Tests/BookMirrorTests.cs
git commit -m "feat(engine): BookMirror tape-window reads (prints, buy/sell vol, alternations)"
```

---

## Task 2: TapeSpeed — z-score baseline

**Files:**
- Create: `Engine/TapeSpeed.cs`
- Test: `Tests/TapeSpeedTests.cs`

**Interfaces:**
- Consumes: a print rate sampled by the caller each engine run (from `BookMirror.WindowSince`).
- Produces:
  - `class TapeSpeed { TapeSpeed(double alpha); void Sample(double rate, DateTime now); double ZScore; bool Ready; }`
  - `ZScore = (rate − mean) / std` from an EWMA mean and EWMA variance of the sampled rate; `0` until `Ready` (≥ `MinSamples`).

- [ ] **Step 1: Write the failing tests**

Create `Tests/TapeSpeedTests.cs`:

```csharp
using System;
using TradingRadar.Engine;
using Xunit;

public class TapeSpeedTests
{
    static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    [Fact]
    public void ZScore_is_positive_when_rate_spikes_above_baseline()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 60; i++) ts.Sample(10.0, T(i * 50)); // steady baseline ~10/s
        ts.Sample(40.0, T(3100));                                // spike
        Assert.True(ts.Ready);
        Assert.True(ts.ZScore > 2.0);
    }

    [Fact]
    public void ZScore_near_zero_on_steady_rate()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 60; i++) ts.Sample(10.0, T(i * 50));
        Assert.True(Math.Abs(ts.ZScore) < 0.5);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~TapeSpeedTests"`
Expected: FAIL — `TapeSpeed` not defined.

- [ ] **Step 3: Implement `Engine/TapeSpeed.cs`**

```csharp
using System;

namespace TradingRadar.Engine
{
    // Stateful EWMA baseline of a sampled print rate -> z-score. Pure: time via timestamps only.
    public class TapeSpeed
    {
        private readonly double _alpha;     // EWMA weight for the newest sample
        private const int MinSamples = 20;  // before Ready
        private double _mean;
        private double _var;
        private int _n;

        public TapeSpeed(double alpha) { _alpha = alpha <= 0 || alpha > 1 ? 0.1 : alpha; }

        public bool Ready { get { return _n >= MinSamples; } }
        public double ZScore { get; private set; }

        public void Sample(double rate, DateTime now)
        {
            if (_n == 0) { _mean = rate; _var = 0; _n = 1; ZScore = 0; return; }
            double prevMean = _mean;
            _mean = _alpha * rate + (1 - _alpha) * _mean;
            // EWMA variance (West/Welford-style incremental for exponential weighting).
            _var = (1 - _alpha) * (_var + _alpha * (rate - prevMean) * (rate - prevMean));
            _n++;
            double std = Math.Sqrt(_var);
            ZScore = _n >= MinSamples && std > 1e-9 ? (rate - _mean) / std : 0.0;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~TapeSpeedTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Engine/TapeSpeed.cs Tests/TapeSpeedTests.cs
git commit -m "feat(engine): TapeSpeed EWMA baseline + rate z-score"
```

---

## Task 3: ConsumptionTracker — consumption + trade-backed fraction

**Files:**
- Create: `Engine/ConsumptionTracker.cs`
- Test: `Tests/ConsumptionTrackerTests.cs`

**Interfaces:**
- Consumes: `BookMirror.TradedAt(price, since, Side?)` (existing).
- Produces:
  - `struct ConsumptionRead { public double Fraction; public long Drop; public long Traded; public double TradeBackedFraction; }`
  - `static class ConsumptionTracker { static ConsumptionRead Read(Side wallSide, double wallPrice, long peak, long current, DateTime armTime, BookMirror book); }`
  - `Fraction = clamp(1 − current/peak, 0, 1)`; `Drop = max(0, peak−current)`; `Traded = book.TradedAt(wallPrice, armTime, consumingAggressor)`; `TradeBackedFraction = Drop>0 ? min(1, Traded/Drop) : 0`. Consuming aggressor: ask wall → `Side.Ask` (buy), bid wall → `Side.Bid` (sell).

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConsumptionTrackerTests.cs`:

```csharp
using System;
using TradingRadar.Engine;
using Xunit;

public class ConsumptionTrackerTests
{
    static DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    static BookMirror BookWithInside()
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(30));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 99.75, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 5, Time = T(0) });
        return b;
    }

    // Ask wall at 100.25 eaten from 100 -> 30 with 70 of buy volume printed there = fully trade-backed.
    [Fact]
    public void Consumption_is_trade_backed_when_prints_explain_the_drop()
    {
        var b = BookWithInside();
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 70, Time = T(1) }); // buy aggressor (>= best ask)
        var r = ConsumptionTracker.Read(Side.Ask, 100.25, peak: 100, current: 30, armTime: T(0), book: b);
        Assert.Equal(70, r.Drop);
        Assert.True(r.Fraction > 0.69 && r.Fraction < 0.71);
        Assert.True(r.TradeBackedFraction >= 0.99); // 70 traded / 70 drop
    }

    // Same drop, but NO prints at the wall = a pull/spoof: trade-backed fraction ~0.
    [Fact]
    public void Consumption_is_not_trade_backed_when_no_prints()
    {
        var b = BookWithInside();
        var r = ConsumptionTracker.Read(Side.Ask, 100.25, peak: 100, current: 30, armTime: T(0), book: b);
        Assert.Equal(70, r.Drop);
        Assert.True(r.TradeBackedFraction < 0.01);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~ConsumptionTrackerTests"`
Expected: FAIL — `ConsumptionTracker` not defined.

- [ ] **Step 3: Implement `Engine/ConsumptionTracker.cs`**

```csharp
using System;

namespace TradingRadar.Engine
{
    public struct ConsumptionRead
    {
        public double Fraction;              // 1 - current/peak, clamped [0,1]
        public long   Drop;                  // max(0, peak - current)
        public long   Traded;                // consuming-aggressor volume at the wall since armTime
        public double TradeBackedFraction;   // Drop>0 ? min(1, Traded/Drop) : 0
    }

    // Pure read: how far a wall has been eaten, and how much of that is explained by trades
    // (vs cancellation/pull). No state, no clock.
    public static class ConsumptionTracker
    {
        public static ConsumptionRead Read(Side wallSide, double wallPrice, long peak, long current, DateTime armTime, BookMirror book)
        {
            long drop = Math.Max(0, peak - current);
            double frac = peak > 0 ? 1.0 - (double)current / peak : 0.0;
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
            Side consuming = wallSide == Side.Ask ? Side.Ask : Side.Bid; // ask wall consumed by buys; bid wall by sells
            long traded = book.TradedAt(wallPrice, armTime, consuming);
            double tbf = drop > 0 ? Math.Min(1.0, (double)traded / drop) : 0.0;
            return new ConsumptionRead { Fraction = frac, Drop = drop, Traded = traded, TradeBackedFraction = tbf };
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~ConsumptionTrackerTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Engine/ConsumptionTracker.cs Tests/ConsumptionTrackerTests.cs
git commit -m "feat(engine): ConsumptionTracker — consumption + trade-backed fraction"
```

---

## Task 4: ControllerStateMachine — arm, suppression, config, types

**Files:**
- Create: `Engine/ControllerStateMachine.cs`
- Test: `Tests/ControllerStateMachineTests.cs`

**Interfaces:**
- Produces (relied on by Tasks 5–7 and the NT layer):
  - `enum SideState { Waiting, Armed, Countdown, Fired, Cooldown }`
  - `struct FireEvent { public Side Side; public double WallPrice; public double EntryHint; public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time; }`
  - `struct ControllerInputs { public double WallAbovePrice; public long WallAboveCurrent; public double WallBelowPrice; public long WallBelowCurrent; public long AggressorDelta; public double TapeZScore; public int TapeAlternations; public double Mid; public DateTime Now; public BookMirror Book; }`
  - `struct ControllerOutput { public bool Chop; public SideState Long; public double LongFraction; public SideState Short; public double ShortFraction; public bool Fired; public FireEvent Fire; }`
  - `class ControllerConfig { ... }` (all placeholders)
  - `class ControllerStateMachine { ControllerStateMachine(ControllerConfig cfg, double tick); ControllerOutput Update(ControllerInputs inp); }`
- This task builds the class with the two `Candidate`s, config, `Update` skeleton, and only the **Waiting→Armed** transition + the **intact-wall suppression** (Armed does not advance on book skew — only Tasks 5–7 add countdown/fire). Long candidate uses the ask wall above (`WallAbovePrice`/`WallAboveCurrent`); short uses the bid wall below.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ControllerStateMachineTests.cs`:

```csharp
using System;
using TradingRadar.Engine;
using Xunit;

public class ControllerStateMachineTests
{
    static DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    static BookMirror EmptyBook() => new BookMirror(0.25, TimeSpan.FromSeconds(30));

    static ControllerInputs In(double wallAbovePrice, long wallAboveCur, double wallBelowPrice, long wallBelowCur,
                               long delta, double z, int alt, double mid, int sec, BookMirror book)
        => new ControllerInputs {
            WallAbovePrice = wallAbovePrice, WallAboveCurrent = wallAboveCur,
            WallBelowPrice = wallBelowPrice, WallBelowCurrent = wallBelowCur,
            AggressorDelta = delta, TapeZScore = z, TapeAlternations = alt, Mid = mid, Now = T(sec), Book = book };

    static ControllerStateMachine Machine() => new ControllerStateMachine(new ControllerConfig(), 0.25);

    // A big ask wall above price arms the LONG candidate.
    [Fact]
    public void Arms_long_when_dominant_ask_wall_above_meets_significance()
    {
        var m = Machine();
        var o = m.Update(In(100.25, 120, 0, 0, delta: 0, z: 0, alt: 0, mid: 100.00, sec: 1, book: EmptyBook()));
        Assert.Equal(SideState.Armed, o.Long);
    }

    // Intact wall + heavy book skew must NOT advance past Armed (no countdown, no fire) — §5b.
    [Fact]
    public void Intact_wall_does_not_advance_on_book_skew_alone()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        var o = m.Update(In(100.25, 120, 0, 0, delta: 999, z: 5, alt: 0, mid: 100.00, sec: 2, book: EmptyBook())); // size unchanged
        Assert.Equal(SideState.Armed, o.Long);
        Assert.False(o.Fired);
    }

    // A wall below significance does not arm.
    [Fact]
    public void Does_not_arm_when_wall_below_significance()
    {
        var m = Machine();
        var o = m.Update(In(100.25, 5, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        Assert.Equal(SideState.Waiting, o.Long);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~ControllerStateMachineTests"`
Expected: FAIL — `ControllerStateMachine` not defined.

- [ ] **Step 3: Implement `Engine/ControllerStateMachine.cs`** (skeleton + arm + suppression)

```csharp
using System;

namespace TradingRadar.Engine
{
    public enum SideState { Waiting, Armed, Countdown, Fired, Cooldown }

    public struct FireEvent
    {
        public Side Side; public double WallPrice; public double EntryHint;
        public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time;
    }

    public struct ControllerInputs
    {
        public double WallAbovePrice; public long WallAboveCurrent;   // dominant ask wall above (long candidate)
        public double WallBelowPrice; public long WallBelowCurrent;   // dominant bid wall below (short candidate)
        public long AggressorDelta; public double TapeZScore; public int TapeAlternations;
        public double Mid; public DateTime Now; public BookMirror Book;
    }

    public struct ControllerOutput
    {
        public bool Chop;
        public SideState Long; public double LongFraction;
        public SideState Short; public double ShortFraction;
        public bool Fired; public FireEvent Fire;
    }

    // All placeholders — measured from Rec CSV (spec §9). No literal threshold lives in logic.
    public class ControllerConfig
    {
        public long SignificanceBand = 60;       // min wall size to arm (contracts) — MEASURED
        public double MinTradeBackedRatio = 0.6; // fraction of the drop that trades must explain
        public double FireFrac = 0.7;            // consumption fraction to fire
        public long DeltaFloor = 8;              // |AggressorDelta| agreeing to confirm
        public double ZFloor = 1.5;              // tape-speed z-score to confirm
        public int K = 3;                        // consecutive snapshots meeting fire pre-conditions
        public double ReloadFrac = 0.25;         // refill above running-min (as frac of peak) => reload veto
        public int AwayTicks = 6;                // mid this far from the wall => price fell away
        public double ChopSlowZ = -0.3;          // z at/below this = quiet tape
        public int ChopAltCount = 3;             // aggressor sign changes over the window => chop
        public TimeSpan Cooldown = TimeSpan.FromSeconds(10);
    }

    // The anti-flip spine. Two per-side candidates + a global CHOP gate. Pure: time via inp.Now.
    public class ControllerStateMachine
    {
        private class Candidate
        {
            public SideState State = SideState.Waiting;
            public double WallPrice;
            public long Peak;
            public long Min;
            public DateTime ArmTime;
            public int HoldCount;
            public DateTime CooldownUntil = DateTime.MinValue;
            public double Fraction;
        }

        private readonly ControllerConfig _cfg;
        private readonly double _tick;
        private readonly Candidate _long = new Candidate();
        private readonly Candidate _short = new Candidate();

        public ControllerStateMachine(ControllerConfig cfg, double tick) { _cfg = cfg; _tick = tick; }

        public ControllerOutput Update(ControllerInputs inp)
        {
            bool chop = Chop(inp);
            FireEvent fire = default(FireEvent); bool fired = false;

            if (StepLong(inp, chop, ref fire)) fired = true;
            if (!fired && StepShort(inp, chop, ref fire)) fired = true;

            ControllerOutput o;
            o.Chop = chop;
            o.Long = _long.State; o.LongFraction = _long.Fraction;
            o.Short = _short.State; o.ShortFraction = _short.Fraction;
            o.Fired = fired; o.Fire = fire;
            return o;
        }

        private bool Chop(ControllerInputs inp)
        {
            return inp.TapeZScore <= _cfg.ChopSlowZ && inp.TapeAlternations >= _cfg.ChopAltCount;
        }

        // Long candidate = ask wall above. Task 4 implements only Waiting->Armed + intact suppression.
        // Tasks 5-7 extend the switch with Countdown/Fire/Cooldown/Reset.
        private bool StepLong(ControllerInputs inp, bool chop, ref FireEvent fire)
        {
            Candidate c = _long;
            double price = inp.WallAbovePrice; long cur = inp.WallAboveCurrent;
            switch (c.State)
            {
                case SideState.Waiting:
                    if (cur >= _cfg.SignificanceBand && inp.Now >= c.CooldownUntil)
                    {
                        c.State = SideState.Armed; c.WallPrice = price;
                        c.Peak = cur; c.Min = cur; c.ArmTime = inp.Now; c.HoldCount = 0; c.Fraction = 0;
                    }
                    break;
                case SideState.Armed:
                    // Suppression: an INTACT wall never advances on book skew. Track peak/min only.
                    if (cur <= 0) { c.State = SideState.Waiting; break; }
                    if (cur > c.Peak) c.Peak = cur;
                    if (cur < c.Min) c.Min = cur;
                    // Countdown transition added in Task 5.
                    break;
            }
            return false;
        }

        private bool StepShort(ControllerInputs inp, bool chop, ref FireEvent fire)
        {
            Candidate c = _short;
            double price = inp.WallBelowPrice; long cur = inp.WallBelowCurrent;
            switch (c.State)
            {
                case SideState.Waiting:
                    if (cur >= _cfg.SignificanceBand && inp.Now >= c.CooldownUntil)
                    {
                        c.State = SideState.Armed; c.WallPrice = price;
                        c.Peak = cur; c.Min = cur; c.ArmTime = inp.Now; c.HoldCount = 0; c.Fraction = 0;
                    }
                    break;
                case SideState.Armed:
                    if (cur <= 0) { c.State = SideState.Waiting; break; }
                    if (cur > c.Peak) c.Peak = cur;
                    if (cur < c.Min) c.Min = cur;
                    break;
            }
            return false;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~ControllerStateMachineTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add Engine/ControllerStateMachine.cs Tests/ControllerStateMachineTests.cs
git commit -m "feat(engine): Controller state machine — arm + intact-wall suppression"
```

---

## Task 5: Controller — countdown + trade-backed gate + pull-veto

**Files:**
- Modify: `Engine/ControllerStateMachine.cs`
- Test: `Tests/ControllerStateMachineTests.cs`

**Interfaces:**
- Consumes: `ConsumptionTracker.Read` (Task 3).
- Produces: `Armed → Countdown` when the size drop is trade-backed; `Armed/Countdown → Cooldown` (pull veto) when the drop is not trade-backed. `ControllerOutput.LongFraction`/`ShortFraction` now reflect the live consumption fraction.

- [ ] **Step 1: Write the failing tests**

Add to `Tests/ControllerStateMachineTests.cs`. Helper to seed a book with buy prints at the wall:

```csharp
static BookMirror BookWithBuys(double wallPrice, long vol, int sec)
{
    var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
    b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice - 0.5, Volume = 20, Time = T(0) });
    b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
    b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) }); // buy aggressor at the wall
    return b;
}

[Fact]
public void Enters_countdown_when_drop_is_trade_backed()
{
    var m = Machine();
    m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));         // arm at peak 120
    var book = BookWithBuys(100.25, 60, 2);                                    // 60 bought at the wall
    var o = m.Update(In(100.25, 60, 0, 0, 0, 0, 0, 100.00, 2, book));          // size 120->60 (drop 60, all traded)
    Assert.Equal(SideState.Countdown, o.Long);
    Assert.True(o.LongFraction > 0.49 && o.LongFraction < 0.51);
}

[Fact]
public void Pull_without_trades_vetoes_to_cooldown()
{
    var m = Machine();
    m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));          // arm
    var o = m.Update(In(100.25, 60, 0, 0, 0, 0, 0, 100.00, 2, EmptyBook()));   // size dropped, NO prints => pull
    Assert.Equal(SideState.Cooldown, o.Long);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~Enters_countdown|FullyQualifiedName~Pull_without_trades"`
Expected: FAIL — Armed never transitions to Countdown/Cooldown yet.

- [ ] **Step 3: Extend the `Armed` case in both `StepLong` and `StepShort`**

Add a shared helper and call it from each candidate's `Armed` case, replacing the `// Countdown transition added in Task 5.` line. For `StepLong` use `Side.Ask`; for `StepShort` use `Side.Bid`:

```csharp
// (add to the class)
private void AdvanceArmedOrCountdown(Candidate c, Side wallSide, long cur, ControllerInputs inp)
{
    if (cur > c.Peak) c.Peak = cur;
    if (cur < c.Min) c.Min = cur;
    ConsumptionRead r = ConsumptionTracker.Read(wallSide, c.WallPrice, c.Peak, cur, c.ArmTime, inp.Book);
    c.Fraction = r.Fraction;
    if (r.Fraction <= 0) return; // nothing eaten yet
    if (r.TradeBackedFraction >= _cfg.MinTradeBackedRatio)
        c.State = SideState.Countdown;
    else
    {
        // Thinning NOT explained by trades = pull/spoof: veto + cooldown.
        c.State = SideState.Cooldown; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.HoldCount = 0;
    }
}
```

Replace the `Armed` case body in `StepLong` with:

```csharp
case SideState.Armed:
    if (cur <= 0) { c.State = SideState.Waiting; break; }
    AdvanceArmedOrCountdown(c, Side.Ask, cur, inp);
    break;
```

and in `StepShort` with the same but `Side.Bid`. Add a `Cooldown` case to both switches:

```csharp
case SideState.Cooldown:
    if (inp.Now >= c.CooldownUntil) { c.State = SideState.Waiting; c.Fraction = 0; }
    break;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~ControllerStateMachineTests"`
Expected: PASS (5 tests total in the class).

- [ ] **Step 5: Commit**

```bash
git add Engine/ControllerStateMachine.cs Tests/ControllerStateMachineTests.cs
git commit -m "feat(engine): Controller countdown + trade-backed gate + pull veto"
```

---

## Task 6: Controller — FIRE conjunction, K-hold, latch, reset, reload veto

**Files:**
- Modify: `Engine/ControllerStateMachine.cs`
- Test: `Tests/ControllerStateMachineTests.cs`

**Interfaces:**
- Produces: `Countdown → Fired` (one-shot `FireEvent`) when `Fraction ≥ FireFrac` AND agreeing `AggressorDelta` AND `TapeZScore ≥ ZFloor` AND held `K` snapshots AND not chop; `Countdown → Cooldown` on reload; `Countdown → Waiting` when price falls away; `Fired → Waiting` on reset. Long requires `AggressorDelta ≥ +DeltaFloor`; short requires `AggressorDelta ≤ −DeltaFloor`.

- [ ] **Step 1: Write the failing tests**

Add to `Tests/ControllerStateMachineTests.cs`:

```csharp
// Drives an ask wall from 120 down to ~30 (75% eaten) with buys, delta+ and z high, for K snapshots.
[Fact]
public void Fires_long_once_on_full_confluence_then_latches()
{
    var m = Machine();
    m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook())); // arm, peak 120
    var b2 = BookWithBuys(100.25, 60, 2);
    m.Update(In(100.25, 60, 0, 0, 20, 2.0, 0, 100.00, 2, b2));        // countdown
    var b3 = BookWithBuys(100.25, 90, 3);                             // more buys at the wall (cumulative 90+)
    ControllerOutput o = default(ControllerOutput);
    int fires = 0;
    for (int s = 3; s <= 8; s++)                                      // hold several snapshots (K=3)
    {
        var b = BookWithBuys(100.25, 90, s);
        o = m.Update(In(100.25, 30, 0, 0, delta: 20, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b));
        if (o.Fired) fires++;
    }
    Assert.Equal(1, fires);                       // one-shot
    Assert.Equal(Side.Ask, o.Fire.Side);          // ask wall above => long break
    Assert.Equal(SideState.Fired, o.Long);        // latched
}

// Opposing delta blocks the fire even at high consumption.
[Fact]
public void Does_not_fire_long_when_delta_opposes()
{
    var m = Machine();
    m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
    for (int s = 2; s <= 8; s++)
    {
        var b = BookWithBuys(100.25, 90, s);
        var o = m.Update(In(100.25, 30, 0, 0, delta: -30, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b)); // sellers pressing
        Assert.False(o.Fired);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~Fires_long_once|FullyQualifiedName~Does_not_fire_long_when_delta"`
Expected: FAIL — Countdown never reaches Fired.

- [ ] **Step 3: Add the `Countdown` and `Fired` cases**

Add a shared fire-evaluator and wire both candidates. Long agreement = `inp.AggressorDelta >= _cfg.DeltaFloor`; short = `inp.AggressorDelta <= -_cfg.DeltaFloor`. Add to the class:

```csharp
private bool StepCountdown(Candidate c, Side wallSide, long cur, ControllerInputs inp, bool chop, ref FireEvent fire)
{
    if (cur <= 0) { c.State = SideState.Waiting; c.Fraction = 0; return false; }
    if (cur > c.Peak) c.Peak = cur;
    if (cur < c.Min) c.Min = cur;

    // Reload veto: refilled well above the running min (someone defending).
    if (cur - c.Min >= _cfg.ReloadFrac * c.Peak)
    { c.State = SideState.Cooldown; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.HoldCount = 0; return false; }

    // Price fell away from the wall (mid too far) => abandon.
    if (Math.Abs(inp.Mid - c.WallPrice) >= _cfg.AwayTicks * _tick)
    { c.State = SideState.Waiting; c.HoldCount = 0; c.Fraction = 0; return false; }

    ConsumptionRead r = ConsumptionTracker.Read(wallSide, c.WallPrice, c.Peak, cur, c.ArmTime, inp.Book);
    c.Fraction = r.Fraction;

    bool deltaOk = wallSide == Side.Ask ? inp.AggressorDelta >= _cfg.DeltaFloor
                                        : inp.AggressorDelta <= -_cfg.DeltaFloor;
    bool pre = r.Fraction >= _cfg.FireFrac
               && r.TradeBackedFraction >= _cfg.MinTradeBackedRatio
               && deltaOk && inp.TapeZScore >= _cfg.ZFloor && !chop;

    if (!pre) { c.HoldCount = 0; return false; }

    c.HoldCount++;
    if (c.HoldCount < _cfg.K) return false;

    // FIRE — one-shot, latch.
    c.State = SideState.Fired;
    fire = new FireEvent {
        Side = wallSide, WallPrice = c.WallPrice, EntryHint = c.WallPrice,
        Fraction = r.Fraction, DeltaAtFire = inp.AggressorDelta, ZAtFire = inp.TapeZScore, Time = inp.Now };
    return true;
}
```

In `StepLong`, add cases (return the fire bool up through `Update`):

```csharp
case SideState.Countdown:
    return StepCountdown(c, Side.Ask, cur, inp, chop, ref fire);
case SideState.Fired:
    // Reset when the wall is gone or price has clearly crossed/held past it.
    if (cur <= 0 || inp.Mid > c.WallPrice + _cfg.AwayTicks * _tick)
    { c.State = SideState.Waiting; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.Fraction = 0; }
    break;
```

In `StepShort`, mirror it with `Side.Bid` and the reset direction flipped (`inp.Mid < c.WallPrice - _cfg.AwayTicks * _tick`). Change `StepLong`/`StepShort` to return `bool` where the `Countdown` case does, and have `Update` capture it (it already assigns `fired`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~ControllerStateMachineTests"`
Expected: PASS (7 tests total).

- [ ] **Step 5: Commit**

```bash
git add Engine/ControllerStateMachine.cs Tests/ControllerStateMachineTests.cs
git commit -m "feat(engine): Controller FIRE conjunction + K-hold + latch + reset + reload veto"
```

---

## Task 7: Controller — CHOP gate + the anti-oscillation test (the key gate)

**Files:**
- Modify: `Engine/ControllerStateMachine.cs` (only if the CHOP gate needs tightening; the `Chop` helper already exists from Task 4)
- Test: `Tests/ControllerStateMachineTests.cs`

**Interfaces:**
- Produces: `ControllerOutput.Chop == true` disables all fires; the machine emits **zero** `FireEvent`s on a book-skew-oscillating input stream.

- [ ] **Step 1: Write the failing tests**

Add the CHOP test and **the anti-oscillation test** — the single most important gate in this plan:

```csharp
[Fact]
public void Chop_suppresses_fire_even_at_high_consumption()
{
    var m = Machine();
    m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
    for (int s = 2; s <= 8; s++)
    {
        var b = BookWithBuys(100.25, 90, s);
        // z below ChopSlowZ and alternations above ChopAltCount => CHOP, must not fire despite 75% eaten + buys.
        var o = m.Update(In(100.25, 30, 0, 0, delta: 20, z: -0.5, alt: 4, mid: 100.00, sec: s, book: b));
        Assert.True(o.Chop);
        Assert.False(o.Fired);
    }
}

// THE anti-flip gate: an input stream whose book skew flips every tick must produce NO fire and
// never leave a stable non-firing state. Walls are intact (no consumption), delta/skew oscillate.
[Fact]
public void Does_not_oscillate_or_fire_on_flipping_book_skew()
{
    var m = Machine();
    int fires = 0;
    for (int s = 1; s <= 40; s++)
    {
        long delta = (s % 2 == 0) ? 50 : -50;      // skew flips every snapshot
        double z = (s % 2 == 0) ? 2.0 : -0.5;      // and so does speed
        int alt = (s % 3 == 0) ? 4 : 0;
        // Walls present but INTACT (current == peak), so no consumption can arm the countdown.
        var o = m.Update(In(100.25, 120, 99.75, 120, delta, z, alt, 100.00, s, EmptyBook()));
        if (o.Fired) fires++;
        Assert.NotEqual(SideState.Countdown, o.Long);   // intact wall never enters countdown
        Assert.NotEqual(SideState.Countdown, o.Short);
    }
    Assert.Equal(0, fires);
}
```

- [ ] **Step 2: Run tests to verify they fail (or pass)**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~Chop_suppresses|FullyQualifiedName~Does_not_oscillate"`
Expected: if the Task 4/6 logic is correct these may already PASS. If either FAILS, the CHOP gate or the intact-wall suppression has a hole — fix in Step 3. (A failing anti-oscillation test here is the whole reason this task exists.)

- [ ] **Step 3: Fix any hole**

If `Chop_suppresses` fails: ensure `!chop` is in the `pre` conjunction in `StepCountdown` (it is) and that `Chop()` reads `inp.TapeZScore <= ChopSlowZ && inp.TapeAlternations >= ChopAltCount`.
If `Does_not_oscillate` fails: the intact-wall suppression is leaking — confirm `AdvanceArmedOrCountdown` only transitions to `Countdown` when `r.Fraction > 0` (an intact wall has `cur == Peak` ⇒ `Fraction == 0` ⇒ stays `Armed`). No literal change should be needed; if it is, the bug is real and this test just earned its keep.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~ControllerStateMachineTests"`
Expected: PASS (9 tests total). Then run the full engine suite: `dotnet test Tests/TradingRadar.Tests.csproj` — expected: all pass (existing 46 + new).

- [ ] **Step 5: Commit**

```bash
git add Engine/ControllerStateMachine.cs Tests/ControllerStateMachineTests.cs
git commit -m "test(engine): Controller CHOP gate + anti-oscillation gate"
```

---

## Task 8: PressureModel — vote-less BookSkewContext

**Files:**
- Modify: `Engine/PressureModel.cs`
- Test: `Tests/PressureModelTests.cs`

**Interfaces:**
- Produces: `double PressureModel.BookSkewContext(PressureInputs inp)` — one collapsed skew read in [−1,+1] (negative = ask-heavy/short-context), the demoted reference for the context strip. Does not change `Signals`/`Evaluate` (kept so nothing else breaks; the NT layer simply stops using `Evaluate` as the primary).

- [ ] **Step 1: Write the failing test**

Add to `Tests/PressureModelTests.cs`:

```csharp
[Fact]
public void BookSkewContext_is_negative_when_asks_outweigh_bids()
{
    var inp = new PressureInputs {
        Bids = new List<DepthLevel> { L(99.75, 10), L(99.50, 10) },
        Asks = new List<DepthLevel> { L(100.25, 40), L(100.50, 40) },
        BestBidSize = 10, BestAskSize = 40, AggressorDelta = 0, Wall = new WallErosion()
    };
    Assert.True(Model().BookSkewContext(inp) < 0);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~BookSkewContext_is_negative"`
Expected: FAIL — `BookSkewContext` not defined.

- [ ] **Step 3: Implement in `PressureModel.cs`**

Add a method that reuses the existing imbalance computation (the dominant of the three collapsed signals), returning a single number with no green-light involvement:

```csharp
// Vote-less "book-skew context": the collapse of imbalance/inside-thin/air-pocket into one read.
// Reference only — never fires anything (spec §7). Reuses the imbalance mass calc.
public double BookSkewContext(PressureInputs inp)
{
    long bidMass = Mass(inp.Bids), askMass = Mass(inp.Asks);
    if (bidMass + askMass <= 0) return 0;
    double raw = (double)(bidMass - askMass) / (bidMass + askMass) * _cfg.ImbalanceGain;
    return raw < -1 ? -1 : (raw > 1 ? 1 : raw);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~BookSkewContext_is_negative"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Engine/PressureModel.cs Tests/PressureModelTests.cs
git commit -m "feat(engine): PressureModel vote-less BookSkewContext (skew collapse)"
```

---

## Task 9: Rec capture — extend lr-signals CSV for calibration

**Files:**
- Modify: `NinjaTrader/RadarTab.cs` (the `_sigWriter` block, ~lines 466–487)

**Interfaces:**
- Consumes: `BookMirror.WindowSince`, `TapeSpeed.ZScore`, the dominant wall px/current already computed (`wallAbovePrice`/`wallAboveSz` etc.), the `ControllerStateMachine` (added in Task 10 — sequence Task 10 before this if the controller field isn't present yet; otherwise write the tape/wall columns now and add the controller-state column in Task 10).
- Produces: extra CSV columns per snapshot so Plan-D measurement can score fires. **No unit test** — verified by inspecting a Replay CSV.

- [ ] **Step 1: Extend the header + row**

In the `_sigWriter` initialization (where the `lr-signals-*.csv` header is written), append columns:
`wallAbovePx,wallAboveCur,wallBelowPx,wallBelowCur,consumeFracLong,tradeBackedLong,consumeFracShort,tradeBackedShort,printsPerSec,buyVolPerSec,sellVolPerSec,tapeZ,ctrlLong,ctrlShort`.

In the per-snapshot write (currently mid/masses/best/delta15/erosion), compute and append:

```csharp
var win = _book.WindowSince(now.AddSeconds(-1));           // 1s tape window
double printsPerSec = win.Prints;                          // per 1s window == per second
double buyPerSec = win.BuyVol, sellPerSec = win.SellVol;
double tapeZ = _tape != null ? _tape.ZScore : 0.0;
// dominant walls already computed this run as wallAbovePx/wallAboveSz, wallBelowPx/wallBelowSz
ConsumptionRead cL = wallAboveSz > 0
    ? ConsumptionTracker.Read(Side.Ask, wallAbovePx, wallAboveSz, wallAboveSz, now.AddSeconds(-15), _book)
    : new ConsumptionRead();
// (peak==current here is only a placeholder; the true per-wall peak lives in the Controller — Task 10
//  writes the controller's fraction. Until then log the live sizes; the measurement consumer derives
//  fraction from the size series.)
```

Write the raw sizes and tape metrics now; the authoritative consumption fractions + controller states are written by Task 10 once the controller field exists. Keep the invariant-culture `string.Format` style already in the file. `ponytail: the CSV is the calibration engine — log raw sizes + tape rate now; fractions are derivable, thresholds are not.`

- [ ] **Step 2: Build gate (nt8c staged)**

Stage the 11+ `.cs` (7 engine + 4 NT) into a temp `Custom/`-mirror dir and run the isolated `nt8c build` (NOT against the real `Custom/`).
Run: `nt8c build --custom-dir <staged>` (per the project's established staged-build recipe).
Expected: `0 errors, 0 warnings` for our files.

- [ ] **Step 3: Commit**

```bash
git add NinjaTrader/RadarTab.cs
git commit -m "feat(nt): extend lr-signals Rec capture with wall/consumption/tape columns"
```

- [ ] **Step 4: Verify in Replay (deferred to the measurement pass)**

Run a short ES Market Replay with **Rec** on; open the `lr-signals-*.csv`; confirm the new columns populate and `printsPerSec`/`tapeZ` are non-trivial during active tape. (This is the data Plan D consumes.)

---

## Task 10: RadarTab — wire TapeSpeed + Controller

**Files:**
- Modify: `NinjaTrader/RadarTab.cs`

**Interfaces:**
- Consumes: `TapeSpeed`, `ControllerStateMachine`, `ControllerInputs`, `ControllerOutput`, `FireEvent`.
- Produces: `Frame` gains `ControllerOutput Ctrl;` and `bool Fired; FireEvent Fire;` — read by `CockpitVisual` (Task 11) and `RadarChartTrader` (Task 12).

- [ ] **Step 1: Add fields + construct**

Near the existing engine fields, add:

```csharp
private TapeSpeed _tape;
private ControllerStateMachine _controller;
```

Where `_book`/`_tracker`/`_pressure` are constructed (per instrument), add:

```csharp
_tape = new TapeSpeed(0.1);
_controller = new ControllerStateMachine(new ControllerConfig(), _cfg.TickSize);
```

- [ ] **Step 2: Feed the tape in `OnMarketData`**

Inside the existing `lock (_engineLock)` block in `OnMarketData`, after `_book.ApplyTrade(te);`, forward the print with its inferred aggressor:

```csharp
Side ag = _book.AggressorOf(te.Price);
// _tape samples the 1s rate on each engine run (Step 3); here we only record the trade in the book.
```

(The tape z-score is sampled once per engine run in Step 3, from `WindowSince`, so no per-trade tape call is needed. The `AggressorOf` call is only needed if a future per-trade tape counter is added; omit if unused to avoid dead code.)

- [ ] **Step 3: Sample tape + run the controller in `MaybeRunEngine`**

After the `_latest = new Frame { ... }` assembly (which already computes `wallAbovePx/wallAboveSz`, `wallBelowPx/wallBelowSz`, `pMid`, and the 15s delta), add:

```csharp
// Tape speed: sample the 1s print rate into the EWMA baseline.
var win1s = _book.WindowSince(now.AddSeconds(-1));
_tape.Sample(win1s.Prints, now);

// Controller inputs from data already marshalled this run.
ControllerInputs cin = new ControllerInputs {
    WallAbovePrice = wallAbovePx, WallAboveCurrent = wallAboveSz,
    WallBelowPrice = wallBelowPx, WallBelowCurrent = wallBelowSz,
    AggressorDelta = _book.AggressorDelta(now.AddSeconds(-15)),
    TapeZScore = _tape.ZScore,
    TapeAlternations = _book.RecentAlternations(8),
    Mid = pMid, Now = now, Book = _book
};
ControllerOutput cout = _controller.Update(cin);
```

Add `Ctrl`, `Fired`, `Fire` to the `Frame` struct and set them:

```csharp
_latest = new Frame {
    /* existing fields */,
    Ctrl = cout, Fired = cout.Fired, Fire = cout.Fire
};
```

**Note on `wallAboveSz`/`wallBelowSz`:** today these come from `RadarNode.LastKnownSize` (the memory snapshot). Confirm they carry the *live* resting size of the dominant wall (they should, since the node is in-window). The Controller tracks its own peak from this live series, so a slightly lagged size only delays arming, never corrupts direction.

- [ ] **Step 4: On fire, notify the Chart Trader (hook only; behavior in Task 12)**

In the UI paint tick (where `_chartTrader.SetContext(...)` is already called), pass the fire event:

```csharp
if (f.Fired) _chartTrader.OnSetupFire(f.Fire);   // method added in Task 12
```

Guard against re-delivering the same latched fire: deliver only on the transition (the `ControllerOutput.Fired` bool is already one-shot per the state machine, so a single delivery per fire is correct).

- [ ] **Step 5: Build gate (nt8c staged) + commit**

Run the isolated staged `nt8c build`; expect `0/0` for our files.
```bash
git add NinjaTrader/RadarTab.cs
git commit -m "feat(nt): RadarTab wires TapeSpeed + Controller into the frame"
```

- [ ] **Step 6: Finish Task 9's controller columns**

Now that `cout` exists, append the authoritative `ctrlLong`/`ctrlShort` (and `cout.LongFraction`/`ShortFraction`) to the `lr-signals` row from Task 9. Re-run the staged build; commit `docs`/csv change with the RadarTab edit if not already.

---

## Task 11: CockpitVisual — render state, countdown, tape speed, CHOP

**Files:**
- Modify: `NinjaTrader/CockpitVisual.cs`

**Interfaces:**
- Consumes: `Frame.Ctrl` (`ControllerOutput`), `PressureModel.BookSkewContext` (via a value the NT layer can compute or pass), Aurora tokens already in the file.
- Produces: the new Cockpit render (no unit test; verified visually in Replay).

- [ ] **Step 1: Replace the 5-condition list with the state view**

In `CockpitVisual.OnRender` (or its render entry), replace the conditions list with, top-to-bottom (Aurora tokens, mono tabular numbers):
1. **Controller state banner** — one of `WAITING` / `ARMED` / `COUNTDOWN` / `SETUP LONG` / `SETUP SHORT` / `CHOP`, driven by `Ctrl.Long`/`Ctrl.Short`/`Ctrl.Chop`/`Ctrl.Fired`. `SETUP LONG/SHORT` glows emerald/coral; `CHOP` is slate; `WAITING` muted.
2. **Consumption countdown** — a bar showing `max(Ctrl.LongFraction, Ctrl.ShortFraction)` filling toward `FireFrac`, labelled e.g. `72% comido`.
3. **Signed velocity bar** — buy vol/sec (emerald, right) vs sell vol/sec (coral, left) from center. Pass `buyPerSec`/`sellPerSec` from `RadarTab` on the `Frame` (add `double BuyPerSec; double SellPerSec; double TapeZ;` to `Frame` in Task 10 if you want them here — cheap, add now).
4. **Tape-speed z-score** — a small 0..3+ gauge from `Frame.TapeZ`.
5. **CHOP light** — lit slate/amber when `Ctrl.Chop`.
6. **Book-skew context strip** — a thin, low-contrast bar from `BookSkewContext` with **no** number emphasis and no green-light; explicitly a reference, never a trigger.

Match the existing element's brush-reuse discipline (no per-frame brush allocation in the hot paint path — reuse cached `Brush`/`Pen` fields, as the current `CockpitVisual` does).

- [ ] **Step 2: Build gate (nt8c staged)**

Run the isolated staged `nt8c build`; expect `0/0`.

- [ ] **Step 3: Commit**

```bash
git add NinjaTrader/CockpitVisual.cs NinjaTrader/RadarTab.cs
git commit -m "feat(nt): CockpitVisual renders controller state + countdown + tape speed + CHOP"
```

- [ ] **Step 4: Verify in Market Replay**

Load an ES Replay day with L2 (confirm `medBid/medAsk > 0`). Watch: the state banner does **not** oscillate; `COUNTDOWN` appears only while a wall is being eaten with trades; `CHOP` lights in chop; `SETUP LONG/SHORT` fires once and latches. Screenshot for the build record. This is the acceptance test for the whole anti-flip goal.

---

## Task 12: RadarChartTrader — pre-stage break-direction limit on fire

**Files:**
- Modify: `NinjaTrader/RadarChartTrader.cs`

**Interfaces:**
- Consumes: `FireEvent` (via `OnSetupFire(FireEvent)` called from `RadarTab` Task 10 Step 4).
- Produces: a pre-staged (un-submitted) break-direction LIMIT + a "SETUP LONG/SHORT listo" indicator. The human clicks submit. **No auto-submit.**

- [ ] **Step 1: Add `OnSetupFire`**

```csharp
public void OnSetupFire(FireEvent f)
{
    // Marshal to UI thread (the fire arrives on the instrument thread via the paint tick already on UI —
    // confirm the call site; if on the instrument thread, Dispatcher.BeginInvoke like the existing order callbacks).
    // Pre-stage a JOIN-NEAR-INSIDE limit in the break direction. NEVER market (the break is fast).
    //   Long break (f.Side == Side.Ask, wall above): buy limit at min(bestAsk + tol, wallPrice) — do NOT
    //   place it above the wall (that would be marketable while price sits below). Cap so it never fills
    //   far into the move.
    //   Short break (f.Side == Side.Bid, wall below): sell limit at max(bestBid - tol, wallPrice).
    // Pre-fill the ticket qty/side/price and light "SETUP LONG/SHORT listo". Human clicks BUY/SELL LMT.
}
```

**API verification (required):** the exact order build/pre-stage calls (`account.CreateOrder`, limit price set, ticket highlight) must be verified against the live NT8 DLL with `ilspycmd`, as every prior Chart Trader change in this project has been. Reconcile with the existing **wall-anchored reversion** LMT logic (commit `ed46c76`): the break setup uses the *opposite* placement intent (join the break, not rest at the wall). Do not submit — this only pre-stages + highlights.

- [ ] **Step 2: Wire the "listo" indicator**

Light the pre-staged BUY LMT / SELL LMT button in the Aurora fire color (emerald/coral glow) when a setup is live; clear it on reset, cancel, or fill. Reuse the existing order-marker lifecycle (`SetActiveOrder`) so the ladder marker already shows the pending price.

- [ ] **Step 3: Build gate (nt8c staged)**

Run the isolated staged `nt8c build`; expect `0/0`.

- [ ] **Step 4: Commit**

```bash
git add NinjaTrader/RadarChartTrader.cs
git commit -m "feat(nt): pre-stage break-direction limit on setup fire (Sim/Playback)"
```

- [ ] **Step 5: Sim/Playback verification + risk gate**

Verify in Playback: on a fire, the ticket pre-fills the correct side/price and the "listo" indicator lights; clicking submits to `Playback101`/Sim only; the ladder marker tracks it; reset/cancel/fill clears it. **Then submit to `trading-risk-manager` for the VETO** (F1–F17: server-side stop, qty clamp, confirm-on-live, cancel-working-on-reverse, shared-account scope, connection/PnL-stale, prop rules, rejections, quote-fresh/clamp). Real account stays blocked until the VETO clears.

---

## Self-Review

**Spec coverage** (spec § → task):
- §3 architecture table → Tasks 1–12 cover every listed unit. ✅
- §4 state machine (WAITING/ARMED/COUNTDOWN/FIRE/RESET + CHOP, per-side, latch, K-hold) → Tasks 4–7. ✅
- §5 signal (arm/countdown/trade-backed/fire conjunction/invalidation) → Tasks 4–6. §5b suppression → Task 4 + the anti-oscillation test Task 7. ✅
- §6 tape speed (signed velocity bar, z-score, velocity-at-wall, CHOP) → z-score/velocity Tasks 1–2, render Task 11, CHOP Tasks 4/7/11. **Gap:** velocity-*at-the-wall* (localized print rate at the armed price) — covered by `BookMirror.TradedAt`/`WindowSince` data but not given its own render element. **Resolution:** the countdown (Task 11 item 2) already shows the wall being eaten; the localized N/sec is a Task 11 render nicety, folded into item 2's label — no new engine work. Noted, not a blocker.
- §7 Cockpit restructure (collapse 1/2/3 vote-less; delta/erosion as inputs) → Task 8 (collapse) + Tasks 6/10 (delta as controller input); erosion already feeds via `ErosionReads` in the existing `RadarTab` and is available to the controller if a later task wires it (v1 uses consumption trade-backing, which is the same discriminator). ✅
- §8 execution (pre-stage break-direction limit, human clicks, reconcile reversion LMT) → Task 12. ✅
- §9 calibration / Rec extension → Task 9 (+ Task 10 Step 6). ✅
- §11 validation (anti-oscillation unit test, nt8c gate, Replay) → Task 7 (unit), Tasks 9–12 (nt8c + Replay). ✅

**Placeholder scan:** engine tasks contain full test + impl code. NT tasks (9–12) intentionally defer exact NT8 API calls to `ilspycmd`-verified implementation (the project's established practice) — flagged explicitly, not silent TODOs. No "add error handling"/"similar to Task N" placeholders. ✅

**Type consistency:** `ControllerInputs`/`ControllerOutput`/`FireEvent`/`SideState`/`ConsumptionRead`/`TapeWindow` are defined once (Tasks 1/3/4) and used with the same field names in Tasks 5/6/9/10/11/12. `BookSkewContext` name consistent (Task 8 → Task 11). `OnSetupFire(FireEvent)` defined Task 12, called Task 10. `Frame.Ctrl/Fired/Fire` added Task 10, read Tasks 11/12. ✅

---

## Execution Handoff

**Plan complete and saved to `docs/plans/2026-07-01-consumption-break-setup.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — a fresh `trading-ninjascript-developer` per task + `trading-code-reviewer` gate between tasks; the `trading-risk-manager` VETO on Task 12. Engine tasks (1–8) run/verify here in WSL via `dotnet test`; NT tasks (9–12) require the Windows machine (`nt8c` + Market Replay). Fast iteration, review between tasks.

**2. Inline Execution** — execute tasks in this session with `executing-plans`, batching engine Tasks 1–8 (testable here) with checkpoints, and pausing at Task 9 for the Windows-only NT layer.

**Which approach?**
