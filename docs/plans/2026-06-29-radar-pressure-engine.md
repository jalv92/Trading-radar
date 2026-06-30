# Radar Pressure Engine (Plan B) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the pure-C# brain of the Cockpit — a `PressureModel` that fuses 5 order-flow signals of the zone around price into a directional bias / conviction / green-light — plus the two engine reads it needs (running aggressor delta, wall-erosion-on-approach), all unit-tested.

**Architecture:** Three additive pieces in the NT-free `Engine/` project. `BookMirror` gains a running aggressor-delta read; `EpisodeClassifier` gains a partial-erosion read (the brainstorm's wall-thins-without-trades signal); a new pure `PressureModel` takes a plain `PressureInputs` struct (assembled later by `RadarTab`, Plan C) and returns a `PressureResult`. No NT, no WPF, no clock — same discipline as the rest of `Engine/`. Fully testable here via `dotnet test`.

**Tech Stack:** C# (Engine = netstandard2.0 / LangVersion 7.3), xUnit (Tests = net8.0). No external deps added.

**Spec:** `docs/specs/2026-06-29-radar-cockpit-design.md` §5–§7, §13(#7).

## Global Constraints

- **Engine project** (`Engine/TradingRadar.Engine.csproj`): `netstandard2.0`, `LangVersion 7.3`, `Nullable disable`. **No C# 8+ syntax** — no switch-expressions, no `using` declarations, no records, no target-typed `new`. Use classic `switch`/`if`, explicit types.
- **Determinism:** never call `DateTime.Now` / `Math.Random`. All time arrives via method parameters/timestamps.
- **Additive only:** do not change the existing public behavior of `BookMirror`, `EpisodeClassifier`, `WallTracker`, or `RadarNode` that `AbsorptionScalper`/`RadarTab` already rely on. New methods/types only. The existing 34 tests must stay green.
- **Weights & thresholds are PLACEHOLDER** (measured later in Plan D). They live in `PressureConfig` fields — **never as literals in the logic.**
- **Confidence is NOT an input** (calibration: size discriminates, confidence does not).
- **Namespace:** `TradingRadar.Engine`. **Tests** project: `net8.0`, xUnit `[Fact]` + `Assert.*`.
- **Test command (all):** `dotnet test Tests/TradingRadar.Tests.csproj -v q --nologo` (baseline today: 34 passed).

---

### Task 1: `BookMirror.AggressorDelta` (running buy−sell aggressor volume)

**Files:**
- Modify: `Engine/BookMirror.cs` (add one public method; the private `Trade` struct already carries `Aggressor`)
- Test: `Tests/BookMirrorTests.cs` (add one `[Fact]`)

**Interfaces:**
- Consumes: existing `BookMirror.ApplyTrade`, `ApplyDepth`, the private `_trades` ring with `Trade.Aggressor` (`Side.Ask` = buy aggressor / lifted offer; `Side.Bid` = sell aggressor / hit bid).
- Produces: `public long AggressorDelta(DateTime since)` → `Σ vol(Aggressor==Ask) − Σ vol(Aggressor==Bid)` over retained trades with `Time >= since`. Consumed by Task 3 (Delta signal).

- [ ] **Step 1: Write the failing test**

Add to `Tests/BookMirrorTests.cs` (inside the existing test class):

```csharp
    [Fact]
    public void AggressorDelta_is_buy_volume_minus_sell_volume_since_cutoff()
    {
        var t0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(30));
        // Establish a book so InferAggressor has best bid/ask.
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 100.00, Volume = 50, Time = t0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 50, Time = t0 });
        // Buy aggressors (print at/above ask): +70 total.
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 40, Time = t0.AddMilliseconds(100) });
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 30, Time = t0.AddMilliseconds(200) });
        // Sell aggressors (print at/below bid): -25 total.
        b.ApplyTrade(new TradeEvent { Price = 100.00, Volume = 25, Time = t0.AddMilliseconds(300) });
        Assert.Equal(45L, b.AggressorDelta(t0));               // 70 buy - 25 sell
        Assert.Equal(-25L, b.AggressorDelta(t0.AddMilliseconds(250))); // only the sell after cutoff
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~BookMirrorTests.AggressorDelta_is_buy_volume_minus_sell_volume_since_cutoff" -v q --nologo`
Expected: **build error** — `'BookMirror' does not contain a definition for 'AggressorDelta'`.

- [ ] **Step 3: Write minimal implementation**

Add this method to `Engine/BookMirror.cs` (e.g. right after `TradedAt`):

```csharp
        // Running order-flow imbalance: buy-aggressor volume minus sell-aggressor volume
        // over retained trades with Time >= since. Side.Ask aggressor = buy (lifted offer).
        public long AggressorDelta(DateTime since)
        {
            long buy = 0, sell = 0;
            for (int i = 0; i < _trades.Count; i++)
            {
                Trade tr = _trades[i];
                if (tr.Time < since) continue;
                if (tr.Aggressor == Side.Ask) buy += tr.Volume; else sell += tr.Volume;
            }
            return buy - sell;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~BookMirrorTests.AggressorDelta_is_buy_volume_minus_sell_volume_since_cutoff" -v q --nologo`
Expected: **PASS** (Failed: 0, Passed: 1).

- [ ] **Step 5: Commit**

```bash
git add Engine/BookMirror.cs Tests/BookMirrorTests.cs
git commit -m "feat(engine): BookMirror.AggressorDelta running buy-minus-sell flow"
```

---

### Task 2: `EpisodeClassifier.ErosionReads` (partial pull / wall thins without trades)

**Files:**
- Modify: `Engine/EpisodeClassifier.cs` (add the `ErosionRead` struct and one public method; reuse existing private `CurrentVolume`, `QuoteTicksAway`, `QuoteCrossed`, `ConsumingAggressor`)
- Test: `Tests/EpisodeClassifierTests.cs` (add two `[Fact]`)

**Interfaces:**
- Consumes: the existing private `_open` episode dictionary, `Episode.SizeAtOpen`/`OpenTime`, and `BookMirror.TradedAt`. The episode lifecycle (`OnApproach`, `Update`) is unchanged.
- Produces:
  ```csharp
  public struct ErosionRead {
      public Side Side; public double Price;
      public long SizeAtOpen; public long Displayed; public long Traded; public long Cancelled;
      public double Frac;        // Cancelled / SizeAtOpen, 0..1 — erosion NOT explained by trades
      public bool   Approaching; // quote still >= D_pull ticks away and not crossed
  }
  public IReadOnlyList<ErosionRead> ErosionReads(BookMirror book, DateTime now)
  ```
  Consumed by Task 3 (WallErosion signal) — `Frac` while `Approaching` is the partial-pull strength.

- [ ] **Step 1: Write the failing test**

Add to `Tests/EpisodeClassifierTests.cs` (it already has `BookAskWall` + `T0`):

```csharp
    [Fact]
    public void ErosionReads_flags_cancellation_without_trades_while_quote_away()
    {
        var c = new EpisodeClassifier(new RadarConfig());
        var book = BookAskWall(200);                 // ask wall 200 @ 21000.50, bid 21000.25 (1 tick away)
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        // Wall thins 200 -> 140 with NO trades (pure cancellation), quote still a tick away.
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Update, Position = 0, Price = 21000.50, Volume = 140, Time = T0.AddMilliseconds(300) });
        var reads = c.ErosionReads(book, T0.AddMilliseconds(400));
        Assert.Single(reads);
        Assert.Equal(60L, reads[0].Cancelled);
        Assert.Equal(0L, reads[0].Traded);
        Assert.True(reads[0].Approaching);
        Assert.InRange(reads[0].Frac, 0.29, 0.31);   // 60 / 200
    }

    [Fact]
    public void ErosionReads_does_not_flag_drop_explained_by_trades()
    {
        var c = new EpisodeClassifier(new RadarConfig());
        var book = BookAskWall(200);
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        // 60 traded at the wall (buy aggressors), level shows 140 — drop is explained by trades.
        book.ApplyTrade(new TradeEvent { Price = 21000.50, Volume = 60, Time = T0.AddMilliseconds(100) });
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Update, Position = 0, Price = 21000.50, Volume = 140, Time = T0.AddMilliseconds(150) });
        var reads = c.ErosionReads(book, T0.AddMilliseconds(200));
        Assert.Single(reads);
        Assert.Equal(0L, reads[0].Cancelled);        // drop fully attributed to trading
        Assert.Equal(0.0, reads[0].Frac);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~EpisodeClassifierTests.ErosionReads" -v q --nologo`
Expected: **build error** — `'EpisodeClassifier' does not contain a definition for 'ErosionReads'`.

- [ ] **Step 3: Write minimal implementation**

Add to `Engine/EpisodeClassifier.cs`. Put the struct just above the class (still inside the namespace), and the method as a public member (it can sit next to `TryTakeResolved`):

```csharp
    public struct ErosionRead
    {
        public Side Side;
        public double Price;
        public long SizeAtOpen;
        public long Displayed;
        public long Traded;
        public long Cancelled;
        public double Frac;        // cancelled / sizeAtOpen — drop NOT explained by trades
        public bool Approaching;   // quote still >= D_pull ticks away and not crossed
    }
```

```csharp
        // Per open episode: how much of the size drop is unexplained by trades (cancellation)
        // while the quote is still approaching. Frac>0 & Approaching = partial pull (spec §7).
        public IReadOnlyList<ErosionRead> ErosionReads(BookMirror book, DateTime now)
        {
            var outl = new List<ErosionRead>();
            foreach (var ep in _open.Values)
            {
                long displayed = CurrentVolume(book, ep.Side, ep.Price);
                long drop = Math.Max(0, ep.SizeAtOpen - displayed);
                long traded = book.TradedAt(ep.Price, ep.OpenTime, ConsumingAggressor(ep.Side));
                long cancelled = Math.Max(0, drop - traded);
                bool approaching = QuoteTicksAway(book, ep.Side, ep.Price) >= _cfg.D_pull
                                   && !QuoteCrossed(book, ep.Side, ep.Price);
                double frac = ep.SizeAtOpen > 0 ? (double)cancelled / ep.SizeAtOpen : 0.0;
                outl.Add(new ErosionRead
                {
                    Side = ep.Side, Price = ep.Price, SizeAtOpen = ep.SizeAtOpen,
                    Displayed = displayed, Traded = traded, Cancelled = cancelled,
                    Frac = frac, Approaching = approaching
                });
            }
            return outl;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~EpisodeClassifierTests.ErosionReads" -v q --nologo`
Expected: **PASS** (Failed: 0, Passed: 2).

- [ ] **Step 5: Commit**

```bash
git add Engine/EpisodeClassifier.cs Tests/EpisodeClassifierTests.cs
git commit -m "feat(engine): EpisodeClassifier.ErosionReads partial-pull (wall thins w/o trades)"
```

---

### Task 3: `PressureModel` DTOs, config & the 5 signal leans

**Files:**
- Create: `Engine/PressureModel.cs`
- Test: `Tests/PressureModelTests.cs`

**Interfaces:**
- Consumes: `DepthLevel` (existing, `Primitives.cs`), `Side`. Inputs are a plain struct (assembled by `RadarTab` in Plan C from `BookMirror`/`WallTracker`/Task 1/Task 2):
  ```csharp
  public struct WallErosion { public bool Active; public double Frac; public bool Above; }
  public struct PressureInputs {
      public IReadOnlyList<DepthLevel> Bids;   // descending by price (best first), full visible band
      public IReadOnlyList<DepthLevel> Asks;   // ascending by price (best first)
      public long BestBidSize;
      public long BestAskSize;
      public long AggressorDelta;              // from BookMirror.AggressorDelta(window)
      public WallErosion Wall;                 // nearest tracked wall's erosion (Above = wall is above price)
  }
  ```
- Produces:
  ```csharp
  public enum SignalId { Imbalance, InsideThin, AirPocket, Delta, WallErosion }
  public struct SignalRead { public SignalId Id; public double Lean; public double Weight; public bool Active; }
  public class PressureConfig { /* placeholder weights/thresholds, see Step 3 */ }
  public class PressureModel {
      public PressureModel(PressureConfig cfg);
      public SignalRead[] Signals(PressureInputs inp);   // lean in [-1,+1]: - short / + long
  }
  ```
- The aggregate (`Net`/`Conviction`/`Green`) is added in Task 4.

- [ ] **Step 1: Write the failing test**

Create `Tests/PressureModelTests.cs`:

```csharp
using System.Collections.Generic;
using TradingRadar.Engine;
using Xunit;

public class PressureModelTests
{
    static PressureModel Model() => new PressureModel(new PressureConfig());
    static DepthLevel L(double p, long v) => new DepthLevel { Price = p, Volume = v };
    static SignalRead Find(SignalRead[] s, SignalId id)
    {
        for (int i = 0; i < s.Length; i++) if (s[i].Id == id) return s[i];
        return default(SignalRead);
    }

    // Heavier asks (supply overhead) => imbalance leans SHORT (negative).
    [Fact]
    public void Imbalance_leans_short_when_asks_outweigh_bids()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 10), L(99.50, 10) },
            Asks = new List<DepthLevel> { L(100.25, 40), L(100.50, 40) },
            BestBidSize = 10, BestAskSize = 40, AggressorDelta = 0,
            Wall = new WallErosion()
        };
        Assert.True(Find(Model().Signals(inp), SignalId.Imbalance).Lean < 0);
    }

    // Thin best bid vs fat best ask => inside-thin leans SHORT.
    [Fact]
    public void InsideThin_leans_short_when_best_bid_is_thin()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 9) },
            Asks = new List<DepthLevel> { L(100.25, 29) },
            BestBidSize = 9, BestAskSize = 29, AggressorDelta = 0, Wall = new WallErosion()
        };
        Assert.True(Find(Model().Signals(inp), SignalId.InsideThin).Lean < 0);
    }

    // Positive aggressor delta (buyers lifting) => delta leans LONG.
    [Fact]
    public void Delta_leans_long_when_buyers_aggress()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 20) }, Asks = new List<DepthLevel> { L(100.25, 20) },
            BestBidSize = 20, BestAskSize = 20, AggressorDelta = 12, Wall = new WallErosion()
        };
        var d = Find(Model().Signals(inp), SignalId.Delta);
        Assert.True(d.Lean > 0 && d.Active);
    }

    // An ask wall (above price) eroding without trades => wall signal leans LONG (fake ceiling).
    [Fact]
    public void WallErosion_above_leans_long_and_is_active()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 20) }, Asks = new List<DepthLevel> { L(100.25, 20) },
            BestBidSize = 20, BestAskSize = 20, AggressorDelta = 0,
            Wall = new WallErosion { Active = true, Frac = 0.5, Above = true }
        };
        var w = Find(Model().Signals(inp), SignalId.WallErosion);
        Assert.True(w.Lean > 0 && w.Active);
    }

    // No wall erosion => wall signal inactive (lean ~0).
    [Fact]
    public void WallErosion_inactive_when_not_eroding()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 20) }, Asks = new List<DepthLevel> { L(100.25, 20) },
            BestBidSize = 20, BestAskSize = 20, AggressorDelta = 0, Wall = new WallErosion { Active = false }
        };
        Assert.False(Find(Model().Signals(inp), SignalId.WallErosion).Active);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~PressureModelTests" -v q --nologo`
Expected: **build error** — `PressureModel` / `PressureInputs` / `SignalId` not found.

- [ ] **Step 3: Write minimal implementation**

Create `Engine/PressureModel.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    public enum SignalId { Imbalance, InsideThin, AirPocket, Delta, WallErosion }

    public struct WallErosion { public bool Active; public double Frac; public bool Above; }

    public struct PressureInputs
    {
        public IReadOnlyList<DepthLevel> Bids;   // descending (best first)
        public IReadOnlyList<DepthLevel> Asks;   // ascending (best first)
        public long BestBidSize;
        public long BestAskSize;
        public long AggressorDelta;
        public WallErosion Wall;
    }

    public struct SignalRead
    {
        public SignalId Id;
        public double Lean;     // -1 short .. +1 long
        public double Weight;
        public bool Active;
    }

    // PLACEHOLDER weights/thresholds (spec §6/§9 — measured in Plan D). No literal lives in logic.
    public class PressureConfig
    {
        public double WImbalance = 3.0;
        public double WInsideThin = 2.0;
        public double WAirPocket = 2.0;
        public double WDelta = 2.0;
        public double WWallErosion = 4.0;

        public double ImbalanceGain = 2.6;   // scales raw skew into lean
        public double InsideThinGain = 1.6;
        public double DeltaScale = 14.0;      // contracts mapping delta -> [-1,1]
        public int AirRange = 3;              // nearest levels per side scanned for the pocket
        public int AirThinSize = 9;           // a near level below this counts as a hole
        public double AirThinPenalty = 0.4;   // lean nudged toward the void when a hole is present

        public double ActiveFloor = 0.12;     // |lean| below this = inactive
        public double ConvictionFloor = 0.30; // |lean| to count toward conviction (Task 4)
        public double GreenNet = 0.55;        // |net| threshold for a green-light (Task 4)
        public int GreenConviction = 3;       // agreeing signals needed (Task 4)
        public double OpposingVeto = 0.55;    // an opposing active lean above this blocks green (Task 4)
    }

    // Pure: takes a snapshot of the zone + flow, emits per-signal leans. No NT, no clock.
    public class PressureModel
    {
        private readonly PressureConfig _cfg;
        public PressureModel(PressureConfig cfg) { _cfg = cfg; }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static long Mass(IReadOnlyList<DepthLevel> l)
        {
            long s = 0; if (l != null) for (int i = 0; i < l.Count; i++) s += l[i].Volume; return s;
        }
        private static long NearMass(IReadOnlyList<DepthLevel> l, int n)
        {
            long s = 0; if (l != null) for (int i = 0; i < l.Count && i < n; i++) s += l[i].Volume; return s;
        }
        private static bool HasHole(IReadOnlyList<DepthLevel> l, int n, int thin)
        {
            if (l == null) return false;
            for (int i = 0; i < l.Count && i < n; i++) if (l[i].Volume < thin) return true;
            return false;
        }

        public SignalRead[] Signals(PressureInputs inp)
        {
            long bidMass = Mass(inp.Bids), askMass = Mass(inp.Asks);
            double imb = (bidMass + askMass) > 0
                ? Clamp(((double)(bidMass - askMass) / (bidMass + askMass)) * _cfg.ImbalanceGain, -1, 1) : 0;

            double thin = (inp.BestBidSize + inp.BestAskSize) > 0
                ? Clamp(((double)(inp.BestBidSize - inp.BestAskSize) / (inp.BestBidSize + inp.BestAskSize)) * _cfg.InsideThinGain, -1, 1) : 0;

            long bidsNear = NearMass(inp.Bids, _cfg.AirRange), asksNear = NearMass(inp.Asks, _cfg.AirRange);
            bool holeBelow = HasHole(inp.Bids, _cfg.AirRange, _cfg.AirThinSize);
            double air = (bidsNear + asksNear) > 0
                ? Clamp((double)(bidsNear - asksNear) / (bidsNear + asksNear) - (holeBelow ? _cfg.AirThinPenalty : 0), -1, 1)
                : (holeBelow ? -_cfg.AirThinPenalty : 0);

            double delta = Clamp(inp.AggressorDelta / _cfg.DeltaScale, -1, 1);

            double wallLean = 0; bool wallActive = false;
            if (inp.Wall.Active)
            {
                double mag = Clamp(inp.Wall.Frac, 0, 1);
                wallLean = inp.Wall.Above ? mag : -mag;   // ask wall eroding => long; bid wall eroding => short
                wallActive = true;
            }

            return new SignalRead[]
            {
                Mk(SignalId.Imbalance,    imb,   _cfg.WImbalance,   true),
                Mk(SignalId.InsideThin,   thin,  _cfg.WInsideThin,  true),
                Mk(SignalId.AirPocket,    air,   _cfg.WAirPocket,   true),
                Mk(SignalId.Delta,        delta, _cfg.WDelta,       true),
                Mk(SignalId.WallErosion,  wallLean, _cfg.WWallErosion, wallActive)
            };
        }

        private SignalRead Mk(SignalId id, double lean, double weight, bool baseActive)
        {
            SignalRead r;
            r.Id = id; r.Lean = lean; r.Weight = weight;
            r.Active = baseActive && Math.Abs(lean) > _cfg.ActiveFloor;
            return r;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~PressureModelTests" -v q --nologo`
Expected: **PASS** (Failed: 0, Passed: 5).

- [ ] **Step 5: Commit**

```bash
git add Engine/PressureModel.cs Tests/PressureModelTests.cs
git commit -m "feat(engine): PressureModel signals (imbalance/thin/air/delta/wall-erosion)"
```

---

### Task 4: `PressureModel.Evaluate` — net bias, conviction, green-light

**Files:**
- Modify: `Engine/PressureModel.cs` (add `PressureResult` + `Evaluate`)
- Test: `Tests/PressureModelTests.cs` (add `[Fact]`)

**Interfaces:**
- Consumes: `Signals(PressureInputs)` from Task 3, and the threshold fields on `PressureConfig`.
- Produces:
  ```csharp
  public struct PressureResult {
      public SignalRead[] Signals;
      public double Net;        // -1..+1 (weighted mean of active leans)
      public int Conviction;    // active signals agreeing with the net sign
      public int Sign;          // -1 / 0 / +1
      public bool Green;        // conviction>=GreenConviction && |Net|>=GreenNet && no strong opposing
  }
  public PressureResult Evaluate(PressureInputs inp)
  ```

- [ ] **Step 1: Write the failing test**

Add to `Tests/PressureModelTests.cs`:

```csharp
    // Captured ES state (asks heavy, thin bid, no flow, wall idle) => net SHORT, NOT a green-light.
    [Fact]
    public void Evaluate_captured_state_leans_short_without_trigger()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(7589.00, 9), L(7588.75, 28), L(7588.50, 27) },
            Asks = new List<DepthLevel> { L(7589.25, 29), L(7589.75, 47), L(7590.00, 65) },
            BestBidSize = 9, BestAskSize = 29, AggressorDelta = 0, Wall = new WallErosion()
        };
        var r = Model().Evaluate(inp);
        Assert.True(r.Net < 0);
        Assert.False(r.Green);
    }

    // Ask wall erodes without trades + book lightens above + bid firms => green-light LONG.
    [Fact]
    public void Evaluate_eroding_ceiling_greenlights_long()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(7589.00, 30), L(7588.75, 32), L(7588.50, 33) },
            Asks = new List<DepthLevel> { L(7589.25, 6), L(7589.75, 10), L(7590.00, 16) },
            BestBidSize = 30, BestAskSize = 6, AggressorDelta = 0,
            Wall = new WallErosion { Active = true, Frac = 0.75, Above = true }
        };
        var r = Model().Evaluate(inp);
        Assert.True(r.Green);
        Assert.Equal(1, r.Sign);
        Assert.True(r.Conviction >= 3);
    }

    // A strong opposing active signal blocks the green-light even if net clears the magnitude bar.
    [Fact]
    public void Evaluate_strong_opposing_signal_vetoes_green()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(7589.00, 30), L(7588.75, 32), L(7588.50, 33) },
            Asks = new List<DepthLevel> { L(7589.25, 6), L(7589.75, 10), L(7590.00, 16) },
            BestBidSize = 30, BestAskSize = 6,
            AggressorDelta = -20,  // heavy selling: strong SHORT lean opposing the LONG book
            Wall = new WallErosion { Active = true, Frac = 0.75, Above = true }
        };
        Assert.False(Model().Evaluate(inp).Green);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~PressureModelTests.Evaluate" -v q --nologo`
Expected: **build error** — `'PressureModel' does not contain a definition for 'Evaluate'`.

- [ ] **Step 3: Write minimal implementation**

Add the struct (above the `PressureModel` class, inside the namespace) and the method (inside `PressureModel`):

```csharp
    public struct PressureResult
    {
        public SignalRead[] Signals;
        public double Net;
        public int Conviction;
        public int Sign;
        public bool Green;
    }
```

```csharp
        public PressureResult Evaluate(PressureInputs inp)
        {
            SignalRead[] sig = Signals(inp);

            double num = 0, den = 0;
            for (int i = 0; i < sig.Length; i++)
                if (sig[i].Active) { num += sig[i].Lean * sig[i].Weight; den += sig[i].Weight; }
            double net = den > 0 ? Clamp(num / den, -1, 1) : 0;
            int sign = net > 0 ? 1 : (net < 0 ? -1 : 0);

            int conviction = 0;
            bool opposed = false;
            for (int i = 0; i < sig.Length; i++)
            {
                if (!sig[i].Active) continue;
                int ls = sig[i].Lean > 0 ? 1 : (sig[i].Lean < 0 ? -1 : 0);
                if (sign != 0 && ls == sign && Math.Abs(sig[i].Lean) > _cfg.ConvictionFloor) conviction++;
                if (sign != 0 && ls == -sign && Math.Abs(sig[i].Lean) > _cfg.OpposingVeto) opposed = true;
            }

            bool green = sign != 0 && conviction >= _cfg.GreenConviction
                         && Math.Abs(net) >= _cfg.GreenNet && !opposed;

            PressureResult r;
            r.Signals = sig; r.Net = net; r.Conviction = conviction; r.Sign = sign; r.Green = green;
            return r;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~PressureModelTests.Evaluate" -v q --nologo`
Expected: **PASS** (Failed: 0, Passed: 3).

- [ ] **Step 5: Commit**

```bash
git add Engine/PressureModel.cs Tests/PressureModelTests.cs
git commit -m "feat(engine): PressureModel.Evaluate net bias + conviction + green-light"
```

---

### Task 5: Full-suite regression + plan-B sign-off

**Files:** none (verification only)

- [ ] **Step 1: Run the entire suite**

Run: `dotnet test Tests/TradingRadar.Tests.csproj -v q --nologo`
Expected: **PASS** — Failed: 0, Passed: **44** (34 existing + 10 new). Confirms no existing engine behavior regressed (the `AbsorptionScalper`/`RadarTab` contract is intact).

- [ ] **Step 2: Confirm the additive contract**

Verify by inspection that no existing public method signature changed in `BookMirror.cs` / `EpisodeClassifier.cs` (only additions). If any existing test changed, that is a regression — revert and fix additively.

- [ ] **Step 3: Tag the milestone (optional)**

```bash
git commit --allow-empty -m "chore(engine): pressure engine (Plan B) complete — 44/44 tests"
```

---

## Self-Review

**Spec coverage (§5–§7):**
- §5 signal catalog (5 signals) → Task 3 (`Signals`), one test per lean sign. Structural memory states are intentionally not separate votes (spec §5) — not implemented as signals here, correct.
- §6 confluence model + green-light → Task 4 (`Evaluate`): net (weighted mean of active), conviction (agreeing count), green (N/M/opposing-veto). Defaults match spec (3 / 0.55 / 0.55).
- §7 wall-erosion-on-approach → Task 2 (`ErosionReads`: cancellation-not-trades while approaching) feeding Task 3's WallErosion signal.
- §5 "confidence is not a weight" → honored (no confidence input anywhere).
- Playbook gap #1 (running delta) → Task 1.
- Weights placeholder in `PressureConfig`, no literals in logic → §6/§9 honored.
- **Out of this plan (other sub-plans):** anchored ladder (Plan A), Cockpit render (Plan C), signal measurement → real weights (Plan D), Chart Trader (Plan E). The `PressureInputs` assembler lives in `RadarTab` (Plan C) — this plan deliberately stops at the pure model + engine reads, which is the testable-here boundary.

**Placeholder scan:** no "TBD/TODO" steps; every code step shows complete code; every run step shows the exact command + expected result.

**Type consistency:** `PressureInputs`/`WallErosion`/`SignalRead`/`SignalId`/`PressureResult`/`PressureConfig` names and the methods `Signals(PressureInputs)` / `Evaluate(PressureInputs)` are used identically across Tasks 3–4 and the tests. `BookMirror.AggressorDelta(DateTime)` (Task 1) and `EpisodeClassifier.ErosionReads(BookMirror, DateTime)` + `ErosionRead` (Task 2) match the `PressureInputs` fields they feed. C# 7.3 only (classic switch/if, explicit `new`, no switch-expressions).
