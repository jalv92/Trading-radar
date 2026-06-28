# Liquidity Radar — Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the pure-C#, NT-free, unit-tested engine that maintains the live order book, detects liquidity walls, classifies the three outcomes (absorbed / pulled / consumed-through), and keeps a decaying-confidence memory of walls beyond the visible 10 levels — emitting an immutable `RadarNode` snapshot the NT8 view layer renders.

**Architecture:** Five focused engine classes behind two NT-facing façades. `BookMirror` mirrors the positional depth stream and recent trades. `WallDetector`, `EpisodeClassifier`, and `LiquidityMemory` are the edge. `WallTracker` orchestrates them and emits `RadarNode[]`. Every class is deterministic: it takes time from event timestamps and an explicit `now` parameter — **never** `DateTime.Now` — so the whole engine is testable with synthetic event sequences and zero NinjaTrader references.

**Tech Stack:** C# 7.3, `netstandard2.0` engine assembly (consumable by both NT8 .NET Framework 4.8 and NT8 2025+ .NET 8), xUnit test project on `net8.0`, `dotnet` CLI.

## Global Constraints

- **Repo / folder:** `projects/Trading/Trading-radar/` — its own git repo (gitignored in the workspace). All paths below are relative to it.
- **Namespace:** `TradingRadar.Engine` for every engine file. Product display name is "Liquidity Radar"; assembly/folder is Trading-radar.
- **Language level:** C# **7.3** in the Engine project — **no** records, **no** target-typed `new`, **no** nullable reference types, **no** init-only setters, **no** switch expressions. (Required for NT8 .NET Framework 4.8 consumption.) The Tests project may use latest C#.
- **Determinism:** no `DateTime.Now`, `DateTime.UtcNow`, `Stopwatch`, `Environment.TickCount`, `Task.Delay`, or `Random` anywhere in the Engine project. Time enters only through `DepthEvent.Time`, `TradeEvent.Time`, and explicit `now` / `since` parameters.
- **No NinjaTrader references** in Engine or Tests. The engine never sees `MarketDepthEventArgs`; the NT layer (separate plan) maps NT events to `DepthEvent` / `TradeEvent` DTOs.
- **No UI** in any engine class.
- **Price comparison:** prices are doubles; compare with a tolerance of `TickSize / 2`. Never `==` on raw prices.
- **NQ defaults** for `RadarConfig` (TickSize `0.25`); every threshold from spec §6.6 is a config field, never a literal in logic.
- **Commit** after each task (the worker commits inside the Trading-radar repo, not the workspace).

---

## File Structure

```
projects/Trading/Trading-radar/
  TradingRadar.sln
  Engine/
    TradingRadar.Engine.csproj    # netstandard2.0, LangVersion 7.3
    Primitives.cs                 # enums + DTO structs + RadarNode contract
    RadarConfig.cs                # all tunable params (NQ defaults)
    BookMirror.cs                 # positional book + recent-trade ring + aggressor inference
    WallDetector.cs               # baselines (median), 4 wall criteria, persistence, flicker
    EpisodeClassifier.cs          # 3-outcome discriminator via trade↔depth cross-reference
    LiquidityMemory.cs            # confidence init/decay/revisit/eviction, RadarNode snapshot
    WallTracker.cs                # orchestrator: wires the four, emits RadarNode[]
  Tests/
    TradingRadar.Tests.csproj     # net8.0, xunit
    BookMirrorTests.cs
    WallDetectorTests.cs
    EpisodeClassifierTests.cs
    LiquidityMemoryTests.cs
    WallTrackerTests.cs
```

**Responsibility boundaries:** `BookMirror` knows the book and trades, nothing about walls. `WallDetector` decides *what is a wall right now*. `EpisodeClassifier` decides *what happened when price hit a tracked price*. `LiquidityMemory` owns *confidence over time and the visible node set*. `WallTracker` is the only class that knows about all four and the only public entry point the NT layer calls.

---

### Task 0: Project scaffolding

**Files:**
- Create: `TradingRadar.sln`
- Create: `Engine/TradingRadar.Engine.csproj`
- Create: `Tests/TradingRadar.Tests.csproj`
- Create: `Engine/Primitives.cs` (placeholder type so the project compiles)

**Interfaces:**
- Consumes: nothing.
- Produces: a buildable solution with an Engine assembly (`TradingRadar.Engine`, namespace `TradingRadar.Engine`) referenced by a Tests assembly that runs under `dotnet test`.

- [ ] **Step 1: Create the Engine csproj**

Create `Engine/TradingRadar.Engine.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <Nullable>disable</Nullable>
    <AssemblyName>TradingRadar.Engine</AssemblyName>
    <RootNamespace>TradingRadar.Engine</RootNamespace>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create a placeholder Primitives.cs so the project has at least one type**

Create `Engine/Primitives.cs`:

```csharp
namespace TradingRadar.Engine
{
    // Filled in by Task 1.
    public enum Side { Bid, Ask }
}
```

- [ ] **Step 3: Create the Tests csproj referencing the Engine**

Create `Tests/TradingRadar.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Engine\TradingRadar.Engine.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create the solution and add both projects**

Run:
```bash
cd projects/Trading/Trading-radar
git init -b main        # if the repo isn't initialized yet
dotnet new sln -n TradingRadar
dotnet sln add Engine/TradingRadar.Engine.csproj Tests/TradingRadar.Tests.csproj
dotnet new gitignore    # standard .NET ignore (bin/, obj/)
```

- [ ] **Step 5: Build to verify the skeleton compiles**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: scaffold TradingRadar engine + test projects"
```

---

### Task 1: Primitives & RadarConfig

**Files:**
- Modify: `Engine/Primitives.cs` (replace placeholder)
- Create: `Engine/RadarConfig.cs`
- Test: `Tests/LiquidityMemoryTests.cs` is created later; this task's check lives inline in a tiny `PrimitivesTests` we fold into `BookMirrorTests.cs` at Task 2. To keep Task 1 independently testable, add a temporary `Tests/PrimitivesTests.cs`.

**Interfaces:**
- Consumes: nothing.
- Produces (the frozen DTO contract every later task and the NT layer depend on):
  - `enum Side { Bid, Ask }`
  - `enum DepthOp { Add, Update, Remove }`
  - `enum NodeState { Live, Wall, Absorbed, Pulled, Consumed, Remembered }`
  - `enum Outcome { Absorbed, Pulled, Consumed }`
  - `struct DepthEvent { Side Side; DepthOp Op; int Position; double Price; long Volume; DateTime Time; bool IsReset; }`
  - `struct TradeEvent { double Price; long Volume; DateTime Time; }`
  - `struct DepthLevel { double Price; long Volume; }`
  - `struct RadarNode { double Price; Side Side; long LastKnownSize; long PeakSize; NodeState State; double Confidence; bool InWindow; double AgeSeconds; }`
  - `class RadarConfig` with every field from spec §6.6 + `double TickSize`, defaulted to NQ values.

- [ ] **Step 1: Write the failing test**

Create `Tests/PrimitivesTests.cs`:

```csharp
using System;
using TradingRadar.Engine;
using Xunit;

public class PrimitivesTests
{
    [Fact]
    public void RadarConfig_defaults_to_NQ()
    {
        var c = new RadarConfig();
        Assert.Equal(0.25, c.TickSize);
        Assert.Equal(4.0, c.K_mult);
        Assert.Equal(40, c.MinAbsSize);
        Assert.Equal(25, c.MemoryBandTicks);
        Assert.Equal(TimeSpan.FromSeconds(30), c.BaselineWindow);
        Assert.Equal(TimeSpan.FromSeconds(30), c.H);
    }

    [Fact]
    public void DepthEvent_carries_its_own_timestamp()
    {
        var t = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);
        var e = new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.25, Volume = 50, Time = t };
        Assert.Equal(t, e.Time);
        Assert.False(e.IsReset);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter PrimitivesTests`
Expected: FAIL — `RadarConfig` / `DepthEvent` members do not exist (compile error).

- [ ] **Step 3: Replace Primitives.cs with the full DTO set**

Replace the entire contents of `Engine/Primitives.cs`:

```csharp
using System;

namespace TradingRadar.Engine
{
    public enum Side { Bid, Ask }
    public enum DepthOp { Add, Update, Remove }
    public enum NodeState { Live, Wall, Absorbed, Pulled, Consumed, Remembered }
    public enum Outcome { Absorbed, Pulled, Consumed }

    // One depth-stream operation, mapped from NT's MarketDepthEventArgs by the NT layer.
    public struct DepthEvent
    {
        public Side Side;
        public DepthOp Op;
        public int Position;   // 0 = inside (best); ladder index on its side
        public double Price;
        public long Volume;    // price-aggregated resting size (MBP), never an order count
        public DateTime Time;
        public bool IsReset;   // true => feed reset; wipe + rebuild, do not read as real pulls
    }

    // One Last print.
    public struct TradeEvent
    {
        public double Price;
        public long Volume;
        public DateTime Time;
    }

    // One ladder row.
    public struct DepthLevel
    {
        public double Price;
        public long Volume;
    }

    // The engine→view contract. Immutable snapshot row.
    public struct RadarNode
    {
        public double Price;
        public Side Side;
        public long LastKnownSize;
        public long PeakSize;
        public NodeState State;
        public double Confidence;  // 0..1
        public bool InWindow;      // within the live 10 levels
        public double AgeSeconds;  // since lastSeen
    }
}
```

- [ ] **Step 4: Create RadarConfig.cs with NQ defaults**

Create `Engine/RadarConfig.cs`:

```csharp
using System;

namespace TradingRadar.Engine
{
    // Every tunable from spec §6.6. NQ defaults. No literal threshold lives in logic.
    public class RadarConfig
    {
        public double TickSize = 0.25;          // NQ

        // Wall detection
        public double K_mult = 4.0;             // size multiple over baseline
        public long MinAbsSize = 40;            // absolute contract floor
        public TimeSpan BaselineWindow = TimeSpan.FromSeconds(30);
        public TimeSpan T_persist = TimeSpan.FromMilliseconds(1500);
        public double F_flicker = 6.0;          // max Add/Remove osc per second before reject

        // Episode / classification
        public int D_approach = 1;              // ticks to open an episode
        public TimeSpan T_episode = TimeSpan.FromMilliseconds(3000);
        public TimeSpan W_assoc = TimeSpan.FromMilliseconds(250);
        public double A_absorb = 1.0;           // Traded@P / S0 to call absorption
        public double RefillRatioTrigger = 3.0; // iceberg refill threshold
        public int D_pull = 1;                  // ticks quote is away when size vanishes => pull

        // Confidence / memory
        public TimeSpan H = TimeSpan.FromSeconds(30);   // confidence half-life while blind
        public double dC_confirm = 0.15;
        public double dC_grow = 0.20;
        public double dC_absorb = 0.25;
        public double ShrinkFactor = 0.6;
        public double PullPenalty = 0.2;
        public int P_max = 2;                   // pulls before node dead
        public double G_grow = 0.25;            // fractional increase to count as GREW
        public double C_floor = 0.05;
        public TimeSpan T_evict = TimeSpan.FromSeconds(300);

        // Visible band
        public int MemoryBandTicks = 25;

        // VolGovernor regime multiplier applied to time windows (×0.3..1.0); 1.0 = calm.
        public double VolGovernor = 1.0;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter PrimitivesTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: engine primitives (DTOs, RadarNode contract) + RadarConfig NQ defaults"
```

---

### Task 2: BookMirror — positional book + trade ring + aggressor inference

**Files:**
- Create: `Engine/BookMirror.cs`
- Test: `Tests/BookMirrorTests.cs`
- Delete: `Tests/PrimitivesTests.cs` (fold its two asserts into `BookMirrorTests.cs` so the temp file from Task 1 doesn't linger)

**Interfaces:**
- Consumes: `DepthEvent`, `TradeEvent`, `DepthLevel`, `Side` (Task 1).
- Produces:
  - `BookMirror(double tickSize, TimeSpan tradeRetention)`
  - `void ApplyDepth(DepthEvent e)` — positional Add/Update/Remove; `e.IsReset == true` clears both sides and returns.
  - `void ApplyTrade(TradeEvent t)` — record into the recent-trade ring with inferred aggressor `Side`; prune entries older than `tradeRetention` relative to `t.Time`.
  - `void ResetFromSnapshot(IList<DepthLevel> bids, IList<DepthLevel> asks)` — replace both ladders (bids desc, asks asc).
  - `IReadOnlyList<DepthLevel> Levels(Side side)` — current ladder (bids desc, asks asc).
  - `bool TryBestBid(out DepthLevel best)` / `bool TryBestAsk(out DepthLevel best)`
  - `long MedianSize(Side side)` — cross-sectional median of current level volumes on `side` (0 if empty).
  - `long MedianSizeExcluding(Side side, double price)` — same, excluding the level at `price` (the B_level baseline).
  - `long TradedAt(double price, DateTime since, Side? aggressorFilter)` — summed trade volume at `price` (±TickSize/2) with `Time >= since`, optionally filtered to one inferred aggressor side.

- [ ] **Step 1: Write the failing test**

Create `Tests/BookMirrorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using TradingRadar.Engine;
using Xunit;

public class BookMirrorTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);
    static BookMirror NewBook() => new BookMirror(0.25, TimeSpan.FromSeconds(10));

    static DepthEvent Dep(Side s, DepthOp op, int pos, double px, long vol, double secs) =>
        new DepthEvent { Side = s, Op = op, Position = pos, Price = px, Volume = vol, Time = T0.AddSeconds(secs) };

    // ---- folded-in primitives check ----
    [Fact]
    public void RadarConfig_defaults_to_NQ()
    {
        var c = new RadarConfig();
        Assert.Equal(0.25, c.TickSize);
        Assert.Equal(4.0, c.K_mult);
        Assert.Equal(25, c.MemoryBandTicks);
    }

    [Fact]
    public void Add_then_best_bid_is_position_zero()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 1, 20999.75, 30, 0));
        Assert.True(b.TryBestBid(out var best));
        Assert.Equal(21000.00, best.Price);
        Assert.Equal(50, best.Volume);
    }

    [Fact]
    public void Update_overwrites_volume_at_position()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Add, 0, 21000.25, 20, 0));
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Update, 0, 21000.25, 200, 1));
        Assert.True(b.TryBestAsk(out var best));
        Assert.Equal(200, best.Volume);
    }

    [Fact]
    public void Remove_deletes_the_level()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 1, 20999.75, 30, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Remove, 0, 21000.00, 0, 1));
        Assert.True(b.TryBestBid(out var best));
        Assert.Equal(20999.75, best.Price);
        Assert.Single(b.Levels(Side.Bid));
    }

    [Fact]
    public void Reset_event_clears_both_sides()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Add, 0, 21000.25, 50, 0));
        b.ApplyDepth(new DepthEvent { IsReset = true, Time = T0.AddSeconds(1) });
        Assert.Empty(b.Levels(Side.Bid));
        Assert.Empty(b.Levels(Side.Ask));
    }

    [Fact]
    public void Median_is_robust_to_one_wall()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 10, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 1, 20999.75, 12, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 2, 20999.50, 500, 0)); // wall
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 3, 20999.25, 11, 0));
        Assert.Equal(11, b.MedianSize(Side.Bid));                 // median(10,12,500,11)=11.5->11
        Assert.Equal(11, b.MedianSizeExcluding(Side.Bid, 20999.50)); // median(10,12,11)=11
    }

    [Fact]
    public void TradedAt_sums_only_matching_price_and_window_and_aggressor()
    {
        var b = NewBook();
        // Establish quote so aggressor can be inferred: best bid 21000.00, best ask 21000.25
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Add, 0, 21000.25, 50, 0));
        // Buy aggressor at the ask
        b.ApplyTrade(new TradeEvent { Price = 21000.25, Volume = 7, Time = T0.AddSeconds(1) });
        b.ApplyTrade(new TradeEvent { Price = 21000.25, Volume = 3, Time = T0.AddSeconds(2) });
        // A print at a different price must not count
        b.ApplyTrade(new TradeEvent { Price = 21000.00, Volume = 99, Time = T0.AddSeconds(2) });

        Assert.Equal(10, b.TradedAt(21000.25, T0, null));
        Assert.Equal(10, b.TradedAt(21000.25, T0, Side.Ask));   // buy aggressor hits ask
        Assert.Equal(0, b.TradedAt(21000.25, T0, Side.Bid));
        Assert.Equal(3, b.TradedAt(21000.25, T0.AddSeconds(1.5), null)); // window excludes the first
    }

    [Fact]
    public void TradeRing_prunes_entries_older_than_retention()
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(5));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Add, 0, 21000.25, 50, 0));
        b.ApplyTrade(new TradeEvent { Price = 21000.25, Volume = 5, Time = T0.AddSeconds(0) });
        b.ApplyTrade(new TradeEvent { Price = 21000.25, Volume = 5, Time = T0.AddSeconds(20) }); // prunes the first
        Assert.Equal(5, b.TradedAt(21000.25, T0, null));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter BookMirrorTests`
Expected: FAIL — `BookMirror` does not exist (compile error). Also delete `Tests/PrimitivesTests.cs` now (its asserts are folded in) so the suite has no duplicate `RadarConfig_defaults_to_NQ`.

```bash
git rm Tests/PrimitivesTests.cs
```

- [ ] **Step 3: Implement BookMirror**

Create `Engine/BookMirror.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    // Mirrors the positional MBP depth stream and a short ring of recent trades.
    // Pure: no NT, no clock. All time arrives via event/parameter timestamps.
    public class BookMirror
    {
        private struct Trade { public double Price; public long Volume; public DateTime Time; public Side Aggressor; }

        private readonly double _tick;
        private readonly TimeSpan _tradeRetention;
        // Bids kept descending by price, asks ascending — same order NT delivers by Position.
        private readonly List<DepthLevel> _bids = new List<DepthLevel>();
        private readonly List<DepthLevel> _asks = new List<DepthLevel>();
        private readonly List<Trade> _trades = new List<Trade>();

        public BookMirror(double tickSize, TimeSpan tradeRetention)
        {
            _tick = tickSize;
            _tradeRetention = tradeRetention;
        }

        private List<DepthLevel> SideList(Side s) { return s == Side.Bid ? _bids : _asks; }
        private bool SamePrice(double a, double b) { return Math.Abs(a - b) < _tick / 2.0; }

        public void ApplyDepth(DepthEvent e)
        {
            if (e.IsReset) { _bids.Clear(); _asks.Clear(); return; }
            var list = SideList(e.Side);
            switch (e.Op)
            {
                case DepthOp.Add:
                    {
                        int pos = e.Position < 0 ? 0 : (e.Position > list.Count ? list.Count : e.Position);
                        list.Insert(pos, new DepthLevel { Price = e.Price, Volume = e.Volume });
                        break;
                    }
                case DepthOp.Update:
                    {
                        if (e.Position >= 0 && e.Position < list.Count)
                            list[e.Position] = new DepthLevel { Price = e.Price, Volume = e.Volume };
                        break;
                    }
                case DepthOp.Remove:
                    {
                        if (e.Position >= 0 && e.Position < list.Count)
                            list.RemoveAt(e.Position);
                        break;
                    }
            }
        }

        public void ApplyTrade(TradeEvent t)
        {
            Side aggressor = InferAggressor(t.Price);
            _trades.Add(new Trade { Price = t.Price, Volume = t.Volume, Time = t.Time, Aggressor = aggressor });
            // Prune relative to the newest trade time (deterministic, no clock).
            DateTime cutoff = t.Time - _tradeRetention;
            int i = 0;
            while (i < _trades.Count && _trades[i].Time < cutoff) i++;
            if (i > 0) _trades.RemoveRange(0, i);
        }

        // Last >= best ask => buy aggressor (lifted the offer); Last <= best bid => sell aggressor.
        private Side InferAggressor(double price)
        {
            if (_asks.Count > 0 && price >= _asks[0].Price - _tick / 2.0) return Side.Ask; // hit the ask = buy aggressor
            if (_bids.Count > 0 && price <= _bids[0].Price + _tick / 2.0) return Side.Bid; // hit the bid = sell aggressor
            // Inside the spread / unknown: attribute by nearest touch.
            if (_asks.Count > 0 && _bids.Count > 0)
                return Math.Abs(price - _asks[0].Price) <= Math.Abs(price - _bids[0].Price) ? Side.Ask : Side.Bid;
            return Side.Ask;
        }

        public void ResetFromSnapshot(IList<DepthLevel> bids, IList<DepthLevel> asks)
        {
            _bids.Clear(); _asks.Clear();
            if (bids != null) _bids.AddRange(bids);
            if (asks != null) _asks.AddRange(asks);
        }

        public IReadOnlyList<DepthLevel> Levels(Side side) { return SideList(side); }

        public bool TryBestBid(out DepthLevel best)
        {
            if (_bids.Count > 0) { best = _bids[0]; return true; }
            best = default(DepthLevel); return false;
        }

        public bool TryBestAsk(out DepthLevel best)
        {
            if (_asks.Count > 0) { best = _asks[0]; return true; }
            best = default(DepthLevel); return false;
        }

        public long MedianSize(Side side) { return MedianOf(SideList(side), double.NaN); }

        public long MedianSizeExcluding(Side side, double price) { return MedianOf(SideList(side), price); }

        private long MedianOf(List<DepthLevel> list, double excludePrice)
        {
            var v = new List<long>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                if (!double.IsNaN(excludePrice) && SamePrice(list[i].Price, excludePrice)) continue;
                v.Add(list[i].Volume);
            }
            if (v.Count == 0) return 0;
            v.Sort();
            int mid = v.Count / 2;
            // Even count: lower-middle (floor) — conservative baseline, matches test expectations.
            return (v.Count % 2 == 1) ? v[mid] : v[mid - 1];
        }

        public long TradedAt(double price, DateTime since, Side? aggressorFilter)
        {
            long sum = 0;
            for (int i = 0; i < _trades.Count; i++)
            {
                var tr = _trades[i];
                if (tr.Time < since) continue;
                if (!SamePrice(tr.Price, price)) continue;
                if (aggressorFilter.HasValue && tr.Aggressor != aggressorFilter.Value) continue;
                sum += tr.Volume;
            }
            return sum;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter BookMirrorTests`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: BookMirror positional book, trade ring, median baselines, aggressor inference"
```

---

### Task 3: WallDetector — baselines, the 4 criteria, persistence, flicker

**Files:**
- Create: `Engine/WallDetector.cs`
- Test: `Tests/WallDetectorTests.cs`

**Interfaces:**
- Consumes: `BookMirror` (Task 2), `RadarConfig`, `Side` (Task 1).
- Produces:
  - `WallDetector(RadarConfig cfg)`
  - `void Update(BookMirror book, DateTime now)` — ingest the current book: append temporal size samples (for B_time), track per-price persistence start, and count Add/Remove flicker transitions. Call once per processed depth batch.
  - `long Baseline(Side side, double price, BookMirror book, DateTime now)` — `B = max(B_level, B_time)`: `B_level = book.MedianSizeExcluding(side, price)`; `B_time = ` temporal median of that side's size samples within `BaselineWindow`.
  - `bool IsConfirmed(Side side, double price, BookMirror book, DateTime now)` — true only when all four hold: relative (`Volume ≥ K_mult·B`), absolute (`Volume ≥ MinAbsSize`), persistence (`now − qualifyingSince ≥ T_persist·VolGovernor`), flicker (`rate ≤ F_flicker`).

- [ ] **Step 1: Write the failing test**

Create `Tests/WallDetectorTests.cs`:

```csharp
using System;
using TradingRadar.Engine;
using Xunit;

public class WallDetectorTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);

    // Build a bid book: small levels + one candidate wall at index 2.
    static BookMirror BookWithBidWall(long wallSize)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(10));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.00, Volume = 10, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 1, Price = 20999.75, Volume = 12, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 2, Price = 20999.50, Volume = wallSize, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 3, Price = 20999.25, Volume = 11, Time = T0 });
        return b;
    }

    [Fact]
    public void Baseline_is_median_of_other_levels_not_inflated_by_wall()
    {
        var cfg = new RadarConfig();
        var d = new WallDetector(cfg);
        var book = BookWithBidWall(500);
        d.Update(book, T0);
        // median of {10,12,11} = 11
        Assert.Equal(11, d.Baseline(Side.Bid, 20999.50, book, T0));
    }

    [Fact]
    public void Not_confirmed_before_persistence_elapsed()
    {
        var cfg = new RadarConfig(); // K_mult 4, MinAbsSize 40, T_persist 1500ms
        var d = new WallDetector(cfg);
        var book = BookWithBidWall(500); // 500 >= 4*11 and >= 40
        d.Update(book, T0);
        Assert.False(d.IsConfirmed(Side.Bid, 20999.50, book, T0)); // 0ms elapsed
    }

    [Fact]
    public void Confirmed_after_persistence_when_all_criteria_hold()
    {
        var cfg = new RadarConfig();
        var d = new WallDetector(cfg);
        var book = BookWithBidWall(500);
        d.Update(book, T0);
        d.Update(book, T0.AddMilliseconds(1600)); // > T_persist
        Assert.True(d.IsConfirmed(Side.Bid, 20999.50, book, T0.AddMilliseconds(1600)));
    }

    [Fact]
    public void Fails_absolute_floor_even_if_relatively_large()
    {
        var cfg = new RadarConfig { MinAbsSize = 40 };
        var d = new WallDetector(cfg);
        // Tiny book where 30 is 4x the median(2,3,2)=2 but below the 40 floor.
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(10));
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21000.25, Volume = 2, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 1, Price = 21000.50, Volume = 30, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 2, Price = 21000.75, Volume = 3, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 3, Price = 21001.00, Volume = 2, Time = T0 });
        d.Update(b, T0);
        d.Update(b, T0.AddMilliseconds(1600));
        Assert.False(d.IsConfirmed(Side.Ask, 21000.50, b, T0.AddMilliseconds(1600)));
    }

    [Fact]
    public void Persistence_resets_if_size_drops_below_threshold()
    {
        var cfg = new RadarConfig();
        var d = new WallDetector(cfg);
        var big = BookWithBidWall(500);
        d.Update(big, T0);
        // Wall shrinks below threshold -> persistence clock resets.
        var small = BookWithBidWall(20);
        d.Update(small, T0.AddMilliseconds(800));
        var bigAgain = BookWithBidWall(500);
        d.Update(bigAgain, T0.AddMilliseconds(900));
        // Only ~100ms since re-qualify, not confirmed yet.
        Assert.False(d.IsConfirmed(Side.Bid, 20999.50, bigAgain, T0.AddMilliseconds(900)));
    }

    [Fact]
    public void Flicker_above_threshold_rejects_the_wall()
    {
        var cfg = new RadarConfig { F_flicker = 6.0 };
        var d = new WallDetector(cfg);
        double px = 20999.50;
        // Oscillate present/absent many times within one second at the wall price.
        for (int i = 0; i < 10; i++)
        {
            var present = BookWithBidWall(500);
            d.Update(present, T0.AddMilliseconds(i * 100));
            var absent = new BookMirror(0.25, TimeSpan.FromSeconds(10));
            absent.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.00, Volume = 10, Time = T0 });
            d.Update(absent, T0.AddMilliseconds(i * 100 + 50));
        }
        var final = BookWithBidWall(500);
        d.Update(final, T0.AddMilliseconds(2000));
        Assert.False(d.IsConfirmed(Side.Bid, px, final, T0.AddMilliseconds(2000)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter WallDetectorTests`
Expected: FAIL — `WallDetector` does not exist (compile error).

- [ ] **Step 3: Implement WallDetector**

Create `Engine/WallDetector.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    // Decides whether a price is a confirmed wall *right now*. Stateful across Update() calls
    // for persistence and flicker; pure (no clock, no NT).
    public class WallDetector
    {
        private class LevelState
        {
            public DateTime? QualifyingSince;     // start of continuous qualification
            public bool PresentLastUpdate;        // for flicker transition detection
            public readonly List<DateTime> Transitions = new List<DateTime>(); // appear/disappear stamps
        }

        private struct Sample { public long Size; public DateTime Time; }

        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly Dictionary<long, LevelState> _bid = new Dictionary<long, LevelState>();
        private readonly Dictionary<long, LevelState> _ask = new Dictionary<long, LevelState>();
        private readonly List<Sample> _bidSamples = new List<Sample>();
        private readonly List<Sample> _askSamples = new List<Sample>();

        public WallDetector(RadarConfig cfg) { _cfg = cfg; _tick = cfg.TickSize; }

        private long Key(double price) { return (long)Math.Round(price / _tick); }
        private Dictionary<long, LevelState> StateMap(Side s) { return s == Side.Bid ? _bid : _ask; }
        private List<Sample> SampleList(Side s) { return s == Side.Bid ? _bidSamples : _askSamples; }
        private TimeSpan Scaled(TimeSpan ts) { return TimeSpan.FromTicks((long)(ts.Ticks * _cfg.VolGovernor)); }

        public void Update(BookMirror book, DateTime now)
        {
            UpdateSide(book, Side.Bid, now);
            UpdateSide(book, Side.Ask, now);
        }

        private void UpdateSide(BookMirror book, Side side, DateTime now)
        {
            var levels = book.Levels(side);
            var map = StateMap(side);
            var samples = SampleList(side);

            // Record presence set this update.
            var presentNow = new HashSet<long>();
            for (int i = 0; i < levels.Count; i++)
            {
                long k = Key(levels[i].Price);
                presentNow.Add(k);
                samples.Add(new Sample { Size = levels[i].Volume, Time = now });

                LevelState st;
                if (!map.TryGetValue(k, out st)) { st = new LevelState(); map[k] = st; }

                // Flicker: a transition is an appear after an absence.
                if (!st.PresentLastUpdate) st.Transitions.Add(now);
                st.PresentLastUpdate = true;

                // Persistence: relative + absolute must hold continuously.
                long b = Baseline(side, levels[i].Price, book, now);
                bool qualifies = levels[i].Volume >= _cfg.K_mult * b && levels[i].Volume >= _cfg.MinAbsSize;
                if (qualifies) { if (st.QualifyingSince == null) st.QualifyingSince = now; }
                else st.QualifyingSince = null;

                PruneTransitions(st, now);
            }

            // Mark levels that disappeared this update (also a flicker transition).
            foreach (var kv in map)
            {
                if (!presentNow.Contains(kv.Key))
                {
                    if (kv.Value.PresentLastUpdate) kv.Value.Transitions.Add(now);
                    kv.Value.PresentLastUpdate = false;
                    kv.Value.QualifyingSince = null;
                    PruneTransitions(kv.Value, now);
                }
            }

            PruneSamples(samples, now);
        }

        private void PruneTransitions(LevelState st, DateTime now)
        {
            DateTime cutoff = now - TimeSpan.FromSeconds(1);
            int i = 0;
            while (i < st.Transitions.Count && st.Transitions[i] < cutoff) i++;
            if (i > 0) st.Transitions.RemoveRange(0, i);
        }

        private void PruneSamples(List<Sample> samples, DateTime now)
        {
            DateTime cutoff = now - _cfg.BaselineWindow;
            int i = 0;
            while (i < samples.Count && samples[i].Time < cutoff) i++;
            if (i > 0) samples.RemoveRange(0, i);
        }

        public long Baseline(Side side, double price, BookMirror book, DateTime now)
        {
            long bLevel = book.MedianSizeExcluding(side, price);
            long bTime = TemporalMedian(SampleList(side), now);
            return Math.Max(bLevel, bTime);
        }

        private long TemporalMedian(List<Sample> samples, DateTime now)
        {
            DateTime cutoff = now - _cfg.BaselineWindow;
            var v = new List<long>();
            for (int i = 0; i < samples.Count; i++)
                if (samples[i].Time >= cutoff) v.Add(samples[i].Size);
            if (v.Count == 0) return 0;
            v.Sort();
            int mid = v.Count / 2;
            return (v.Count % 2 == 1) ? v[mid] : v[mid - 1];
        }

        public bool IsConfirmed(Side side, double price, BookMirror book, DateTime now)
        {
            long k = Key(price);
            LevelState st;
            if (!StateMap(side).TryGetValue(k, out st)) return false;
            if (st.QualifyingSince == null) return false;
            if (now - st.QualifyingSince.Value < Scaled(_cfg.T_persist)) return false;

            // Flicker rate over the last second.
            PruneTransitions(st, now);
            if (st.Transitions.Count > _cfg.F_flicker) return false;

            // Re-check current size still qualifies (defensive; persistence implies it).
            long vol = CurrentVolume(book, side, price);
            long b = Baseline(side, price, book, now);
            return vol >= _cfg.K_mult * b && vol >= _cfg.MinAbsSize;
        }

        private long CurrentVolume(BookMirror book, Side side, double price)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
                if (Math.Abs(levels[i].Price - price) < _tick / 2.0) return levels[i].Volume;
            return 0;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter WallDetectorTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: WallDetector median baselines + 4 wall criteria (relative/absolute/persistence/flicker)"
```

---

### Task 4: EpisodeClassifier — the three-outcome discriminator

**Files:**
- Create: `Engine/EpisodeClassifier.cs`
- Test: `Tests/EpisodeClassifierTests.cs`

**Interfaces:**
- Consumes: `BookMirror` (Task 2), `RadarConfig`, `Side`, `Outcome` (Task 1).
- Produces:
  - `struct EpisodeResult { Side Side; double Price; Outcome Outcome; long Traded; long Cancelled; DateTime ResolvedAt; }`
  - `EpisodeClassifier(RadarConfig cfg)`
  - `bool HasOpenEpisode(Side side, double price)`
  - `void OnApproach(Side side, double price, long sizeAtOpen, DateTime now)` — open an episode (no-op if one is already open for that price).
  - `void Update(BookMirror book, DateTime now)` — advance every open episode; resolve on break / size→0 / `T_episode·VolGovernor` timeout, classifying into one `Outcome`.
  - `bool TryTakeResolved(out EpisodeResult r)` — drain one resolved result (FIFO).

**Classification rule (spec §6.3), applied at resolution:**
- `traded = book.TradedAt(price, openTime, aggressorThatConsumesThisSide)` — a Bid wall is consumed by **sell** aggressors, an Ask wall by **buy** aggressors.
- `displayed_drop = max(0, sizeAtOpen − currentDisplayed)`; `cancelled = max(0, displayed_drop − traded)`.
- `refill_ratio = traded / max(displayed_drop, 1)`.
- **CONSUMED** if the inside quote crossed P during the episode (price broke through).
- else **ABSORBED** if `traded ≥ A_absorb·sizeAtOpen` AND `refill_ratio ≥ RefillRatioTrigger` AND quote never crossed P.
- else **PULLED** if `cancelled > traded` AND the quote was ≥ `D_pull` ticks away at the moment size vanished.
- else **ABSORBED** if trades explain most of the drop (`traded ≥ cancelled`), otherwise **PULLED** (cancellation-dominant fallback).

- [ ] **Step 1: Write the failing test**

Create `Tests/EpisodeClassifierTests.cs`:

```csharp
using System;
using TradingRadar.Engine;
using Xunit;

public class EpisodeClassifierTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);

    // Ask wall at 21000.50; bids below. Buy aggressors (prints at/above ask) consume an ask wall.
    static BookMirror BookAskWall(long askWallSize, double bestAsk = 21000.50, double bestBid = 21000.25)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(30));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = bestBid, Volume = 20, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = bestAsk, Volume = askWallSize, Time = T0 });
        return b;
    }

    [Fact]
    public void Absorbed_when_trades_explain_drop_price_holds_and_level_refills()
    {
        var cfg = new RadarConfig();
        var c = new EpisodeClassifier(cfg);
        var book = BookAskWall(200);
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        // Heavy buying lifts the offer but it refills: displayed stays ~200, big Traded@P.
        for (int i = 1; i <= 5; i++)
            book.ApplyTrade(new TradeEvent { Price = 21000.50, Volume = 120, Time = T0.AddMilliseconds(i * 100) });
        // Still showing 200 (iceberg refill), price held at the ask.
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Update, Position = 0, Price = 21000.50, Volume = 200, Time = T0.AddMilliseconds(600) });
        c.Update(book, T0.AddMilliseconds(700));
        c.Update(book, T0.AddSeconds(4)); // timeout resolves
        Assert.True(c.TryTakeResolved(out var r));
        Assert.Equal(Outcome.Absorbed, r.Outcome);
    }

    [Fact]
    public void Pulled_when_size_vanishes_without_trades_and_quote_away()
    {
        var cfg = new RadarConfig();
        var c = new EpisodeClassifier(cfg);
        var book = BookAskWall(200); // quote at 21000.50, approach within 1 tick from bid 21000.25
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        // No trades at the wall. Size disappears while bid is still a tick away.
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Remove, Position = 0, Price = 21000.50, Volume = 0, Time = T0.AddMilliseconds(300) });
        c.Update(book, T0.AddMilliseconds(400));
        Assert.True(c.TryTakeResolved(out var r));
        Assert.Equal(Outcome.Pulled, r.Outcome);
        Assert.True(r.Cancelled > r.Traded);
    }

    [Fact]
    public void Consumed_when_price_breaks_through_with_trades()
    {
        var cfg = new RadarConfig();
        var c = new EpisodeClassifier(cfg);
        var book = BookAskWall(200);
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        // Trades eat the wall...
        for (int i = 1; i <= 2; i++)
            book.ApplyTrade(new TradeEvent { Price = 21000.50, Volume = 100, Time = T0.AddMilliseconds(i * 100) });
        // ...wall removed AND price breaks above: new best ask is higher.
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Remove, Position = 0, Price = 21000.50, Volume = 0, Time = T0.AddMilliseconds(250) });
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21000.75, Volume = 15, Time = T0.AddMilliseconds(250) });
        c.Update(book, T0.AddMilliseconds(300));
        Assert.True(c.TryTakeResolved(out var r));
        Assert.Equal(Outcome.Consumed, r.Outcome);
    }

    [Fact]
    public void OnApproach_is_idempotent_while_open()
    {
        var cfg = new RadarConfig();
        var c = new EpisodeClassifier(cfg);
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        Assert.True(c.HasOpenEpisode(Side.Ask, 21000.50));
        c.OnApproach(Side.Ask, 21000.50, 999, T0.AddMilliseconds(10)); // ignored
        Assert.True(c.HasOpenEpisode(Side.Ask, 21000.50));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter EpisodeClassifierTests`
Expected: FAIL — `EpisodeClassifier` does not exist (compile error).

- [ ] **Step 3: Implement EpisodeClassifier**

Create `Engine/EpisodeClassifier.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    public struct EpisodeResult
    {
        public Side Side;
        public double Price;
        public Outcome Outcome;
        public long Traded;
        public long Cancelled;
        public DateTime ResolvedAt;
    }

    // Attributes every size decrease at a tracked price by cross-referencing Last prints.
    // Stateful per open episode; pure (no clock, no NT).
    public class EpisodeClassifier
    {
        private class Episode
        {
            public Side Side;
            public double Price;
            public long SizeAtOpen;
            public DateTime OpenTime;
            public bool Crossed;          // inside quote ever crossed P
            public bool QuoteAwayAtVanish; // quote was >= D_pull ticks away when size hit 0
            public bool Vanished;
        }

        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly Dictionary<long, Episode> _open = new Dictionary<long, Episode>();
        private readonly Queue<EpisodeResult> _resolved = new Queue<EpisodeResult>();

        public EpisodeClassifier(RadarConfig cfg) { _cfg = cfg; _tick = cfg.TickSize; }

        private long Key(Side s, double price) { return ((long)Math.Round(price / _tick)) * 2 + (s == Side.Ask ? 1 : 0); }
        private TimeSpan Scaled(TimeSpan ts) { return TimeSpan.FromTicks((long)(ts.Ticks * _cfg.VolGovernor)); }
        private Side ConsumingAggressor(Side wallSide) { return wallSide == Side.Ask ? Side.Ask : Side.Bid; }

        public bool HasOpenEpisode(Side side, double price) { return _open.ContainsKey(Key(side, price)); }

        public void OnApproach(Side side, double price, long sizeAtOpen, DateTime now)
        {
            long k = Key(side, price);
            if (_open.ContainsKey(k)) return;
            _open[k] = new Episode { Side = side, Price = price, SizeAtOpen = sizeAtOpen, OpenTime = now };
        }

        public void Update(BookMirror book, DateTime now)
        {
            if (_open.Count == 0) return;
            var toResolve = new List<Episode>();
            foreach (var ep in _open.Values)
            {
                long displayed = CurrentVolume(book, ep.Side, ep.Price);
                bool crossedNow = QuoteCrossed(book, ep.Side, ep.Price);
                if (crossedNow) ep.Crossed = true;

                if (displayed == 0 && !ep.Vanished)
                {
                    ep.Vanished = true;
                    ep.QuoteAwayAtVanish = QuoteTicksAway(book, ep.Side, ep.Price) >= _cfg.D_pull;
                }

                bool timedOut = now - ep.OpenTime >= Scaled(_cfg.T_episode);
                if (displayed == 0 || ep.Crossed || timedOut) toResolve.Add(ep);
            }

            foreach (var ep in toResolve)
            {
                _resolved.Enqueue(Classify(ep, book, now));
                _open.Remove(Key(ep.Side, ep.Price));
            }
        }

        private EpisodeResult Classify(Episode ep, BookMirror book, DateTime now)
        {
            long displayed = CurrentVolume(book, ep.Side, ep.Price);
            long drop = Math.Max(0, ep.SizeAtOpen - displayed);
            long traded = book.TradedAt(ep.Price, ep.OpenTime, ConsumingAggressor(ep.Side));
            long cancelled = Math.Max(0, drop - traded);
            double refillRatio = traded / (double)Math.Max(drop, 1);

            Outcome o;
            if (ep.Crossed)
                o = Outcome.Consumed;
            else if (traded >= _cfg.A_absorb * ep.SizeAtOpen && refillRatio >= _cfg.RefillRatioTrigger)
                o = Outcome.Absorbed;
            else if (cancelled > traded && ep.QuoteAwayAtVanish)
                o = Outcome.Pulled;
            else
                o = traded >= cancelled ? Outcome.Absorbed : Outcome.Pulled;

            return new EpisodeResult { Side = ep.Side, Price = ep.Price, Outcome = o, Traded = traded, Cancelled = cancelled, ResolvedAt = now };
        }

        public bool TryTakeResolved(out EpisodeResult r)
        {
            if (_resolved.Count > 0) { r = _resolved.Dequeue(); return true; }
            r = default(EpisodeResult); return false;
        }

        private long CurrentVolume(BookMirror book, Side side, double price)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
                if (Math.Abs(levels[i].Price - price) < _tick / 2.0) return levels[i].Volume;
            return 0;
        }

        // For an Ask wall: crossed if best ask moved above P. For a Bid wall: best bid below P.
        private bool QuoteCrossed(BookMirror book, Side side, double price)
        {
            if (side == Side.Ask)
                return book.TryBestAsk(out var a) && a.Price > price + _tick / 2.0;
            return book.TryBestBid(out var b) && b.Price < price - _tick / 2.0;
        }

        private int QuoteTicksAway(BookMirror book, Side side, double price)
        {
            DepthLevel q;
            bool has = side == Side.Ask ? book.TryBestBid(out q) : book.TryBestAsk(out q);
            if (!has) return int.MaxValue;
            return (int)Math.Round(Math.Abs(price - q.Price) / _tick);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter EpisodeClassifierTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: EpisodeClassifier 3-outcome discriminator (absorbed/pulled/consumed)"
```

---

### Task 5: LiquidityMemory — confidence init/decay/revisit/eviction + snapshot

**Files:**
- Create: `Engine/LiquidityMemory.cs`
- Test: `Tests/LiquidityMemoryTests.cs`

**Interfaces:**
- Consumes: `RadarConfig`, `Side`, `NodeState`, `RadarNode`, `EpisodeResult`, `Outcome` (Tasks 1, 4).
- Produces:
  - `LiquidityMemory(RadarConfig cfg)`
  - `bool Contains(Side side, double price)`
  - `void Promote(Side side, double price, long size, long baseline, DateTime now)` — create a node; `C0 = clamp(0.4 + 0.1·(size/baseline − K_mult), 0.4, 0.8)`; `State = Wall`.
  - `void ObserveLive(Side side, double price, long size, bool stillConfirmedWall, DateTime now)` — node visible: refresh `LastKnownSize`/`PeakSize`/`LastSeen`, `InWindow = true`; grew (`≥ G_grow`) → `+dC_grow`; shrank (`≤ −G_grow`) → `×ShrinkFactor`; if `stillConfirmedWall` → `TimesConfirmed++`, `+dC_confirm`, `State = Wall`, else `State = Live`.
  - `void MarkBlind(Side side, double price)` — `InWindow = false` (freeze; decay applies in snapshot).
  - `void MarkAllBlind()` — feed-reset path: every node `InWindow = false`.
  - `void ApplyOutcome(EpisodeResult r, DateTime now)` — Absorbed `+dC_absorb`, `State = Absorbed`; Pulled `×PullPenalty`, `Phantom = true`, `State = Pulled` (dead at `P_max` pulls); Consumed `Consumed = true`, `State = Consumed`, confidence halved (flipped-level demotion).
  - `void Evict(DateTime now)` — drop nodes where (`DecayedConfidence < C_floor` AND age `> T_evict`) OR `PulledCount ≥ P_max`.
  - `IReadOnlyList<RadarNode> Snapshot(double bestBid, double bestAsk, DateTime now)` — every node within `MemoryBandTicks` of mid; confidence decayed while blind (`C·exp(−ln2/H · Δt)`), `AgeSeconds` since `LastSeen`, `State = Remembered` when blind.

- [ ] **Step 1: Write the failing test**

Create `Tests/LiquidityMemoryTests.cs`:

```csharp
using System;
using System.Linq;
using TradingRadar.Engine;
using Xunit;

public class LiquidityMemoryTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);

    static RadarNode NodeAt(System.Collections.Generic.IReadOnlyList<RadarNode> s, double price)
        => s.First(n => Math.Abs(n.Price - price) < 0.01);

    [Fact]
    public void Promote_sets_C0_in_band_and_wall_state()
    {
        var m = new LiquidityMemory(new RadarConfig());
        m.Promote(Side.Bid, 21000.00, size: 500, baseline: 11, now: T0); // size/B=45.5, K=4 -> 0.4+0.1*41.5 clamps to 0.8
        var s = m.Snapshot(21000.00, 21000.25, T0);
        var n = NodeAt(s, 21000.00);
        Assert.Equal(NodeState.Wall, n.State);
        Assert.InRange(n.Confidence, 0.4, 0.8);
        Assert.True(n.InWindow);
        Assert.Equal(500, n.LastKnownSize);
    }

    [Fact]
    public void Confidence_decays_by_half_after_one_half_life_while_blind()
    {
        var cfg = new RadarConfig { H = TimeSpan.FromSeconds(30) };
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Bid, 21000.00, 500, 11, T0); // C0 = 0.8
        m.MarkBlind(Side.Bid, 21000.00);
        var s = m.Snapshot(21000.00, 21000.25, T0.AddSeconds(30)); // one half-life
        var n = NodeAt(s, 21000.00);
        Assert.False(n.InWindow);
        Assert.Equal(NodeState.Remembered, n.State);
        Assert.InRange(n.Confidence, 0.38, 0.42); // ~0.4
        Assert.InRange(n.AgeSeconds, 29.9, 30.1);
    }

    [Fact]
    public void Live_node_does_not_decay()
    {
        var m = new LiquidityMemory(new RadarConfig());
        m.Promote(Side.Bid, 21000.00, 500, 11, T0);
        double c0 = NodeAt(m.Snapshot(21000.00, 21000.25, T0), 21000.00).Confidence;
        // still live 60s later (kept observed)
        m.ObserveLive(Side.Bid, 21000.00, 500, true, T0.AddSeconds(60));
        double c1 = NodeAt(m.Snapshot(21000.00, 21000.25, T0.AddSeconds(60)), 21000.00).Confidence;
        Assert.True(c1 >= c0); // confirmed raised it, never decayed
    }

    [Fact]
    public void Absorbed_outcome_raises_confidence()
    {
        var cfg = new RadarConfig();
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Ask, 21000.50, 200, 20, T0); // C0 ~ 0.4+0.1*(10-4)=clamp .8
        double before = NodeAt(m.Snapshot(21000.25, 21000.50, T0), 21000.50).Confidence;
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Absorbed, Traded = 600, Cancelled = 0, ResolvedAt = T0.AddSeconds(1) }, T0.AddSeconds(1));
        var n = NodeAt(m.Snapshot(21000.25, 21000.50, T0.AddSeconds(1)), 21000.50);
        Assert.Equal(NodeState.Absorbed, n.State);
        Assert.True(n.Confidence >= before);
    }

    [Fact]
    public void Pulled_outcome_collapses_confidence_and_flags_phantom()
    {
        var cfg = new RadarConfig { PullPenalty = 0.2 };
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Ask, 21000.50, 200, 20, T0);
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Pulled, Traded = 0, Cancelled = 200, ResolvedAt = T0.AddSeconds(1) }, T0.AddSeconds(1));
        var n = NodeAt(m.Snapshot(21000.25, 21000.50, T0.AddSeconds(1)), 21000.50);
        Assert.Equal(NodeState.Pulled, n.State);
        Assert.True(n.Confidence < 0.4);
    }

    [Fact]
    public void Node_dies_after_P_max_pulls()
    {
        var cfg = new RadarConfig { P_max = 2 };
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Ask, 21000.50, 200, 20, T0);
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Pulled, ResolvedAt = T0 }, T0);
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Pulled, ResolvedAt = T0.AddSeconds(1) }, T0.AddSeconds(1));
        m.Evict(T0.AddSeconds(1));
        Assert.False(m.Contains(Side.Ask, 21000.50));
    }

    [Fact]
    public void Snapshot_excludes_nodes_outside_the_memory_band()
    {
        var cfg = new RadarConfig { MemoryBandTicks = 25, TickSize = 0.25 }; // band = 6.25 price units
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Bid, 21000.00, 500, 11, T0);   // inside band
        m.MarkBlind(Side.Bid, 21000.00);
        m.Promote(Side.Bid, 20990.00, 500, 11, T0);   // 40 ticks away -> outside
        m.MarkBlind(Side.Bid, 20990.00);
        var s = m.Snapshot(21000.00, 21000.25, T0);
        Assert.Contains(s, n => Math.Abs(n.Price - 21000.00) < 0.01);
        Assert.DoesNotContain(s, n => Math.Abs(n.Price - 20990.00) < 0.01);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter LiquidityMemoryTests`
Expected: FAIL — `LiquidityMemory` does not exist (compile error).

- [ ] **Step 3: Implement LiquidityMemory**

Create `Engine/LiquidityMemory.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    public class LiquidityMemory
    {
        private class MemoryNode
        {
            public Side Side;
            public double Price;
            public long LastKnownSize;
            public long PeakSize;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public int TimesConfirmed;
            public int AbsorbedCount;
            public int PulledCount;
            public bool Consumed;
            public double Confidence;   // last observed value, pre-decay
            public bool InWindow;
            public NodeState State;
            public bool Phantom;
        }

        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly Dictionary<long, MemoryNode> _nodes = new Dictionary<long, MemoryNode>();

        public LiquidityMemory(RadarConfig cfg) { _cfg = cfg; _tick = cfg.TickSize; }

        private long Key(Side s, double price) { return ((long)Math.Round(price / _tick)) * 2 + (s == Side.Ask ? 1 : 0); }
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private TimeSpan ScaledH() { return TimeSpan.FromTicks((long)(_cfg.H.Ticks * _cfg.VolGovernor)); }

        public bool Contains(Side side, double price) { return _nodes.ContainsKey(Key(side, price)); }

        public void Promote(Side side, double price, long size, long baseline, DateTime now)
        {
            long k = Key(side, price);
            if (_nodes.ContainsKey(k)) { ObserveLive(side, price, size, true, now); return; }
            double ratio = baseline > 0 ? size / (double)baseline : _cfg.K_mult;
            double c0 = Clamp(0.4 + 0.1 * (ratio - _cfg.K_mult), 0.4, 0.8);
            _nodes[k] = new MemoryNode
            {
                Side = side, Price = price, LastKnownSize = size, PeakSize = size,
                FirstSeen = now, LastSeen = now, TimesConfirmed = 1,
                Confidence = c0, InWindow = true, State = NodeState.Wall
            };
        }

        public void ObserveLive(Side side, double price, long size, bool stillConfirmedWall, DateTime now)
        {
            MemoryNode n;
            if (!_nodes.TryGetValue(Key(side, price), out n)) return;
            if (n.LastKnownSize > 0)
            {
                double change = (size - n.LastKnownSize) / (double)n.LastKnownSize;
                if (change >= _cfg.G_grow) n.Confidence += _cfg.dC_grow;
                else if (change <= -_cfg.G_grow) n.Confidence *= _cfg.ShrinkFactor;
            }
            n.LastKnownSize = size;
            if (size > n.PeakSize) n.PeakSize = size;
            n.LastSeen = now;
            n.InWindow = true;
            if (stillConfirmedWall) { n.TimesConfirmed++; n.Confidence += _cfg.dC_confirm; n.State = NodeState.Wall; }
            else n.State = NodeState.Live;
            n.Confidence = Clamp(n.Confidence, 0.0, 1.0);
        }

        public void MarkBlind(Side side, double price)
        {
            MemoryNode n;
            if (_nodes.TryGetValue(Key(side, price), out n)) n.InWindow = false;
        }

        public void MarkAllBlind() { foreach (var n in _nodes.Values) n.InWindow = false; }

        public void ApplyOutcome(EpisodeResult r, DateTime now)
        {
            MemoryNode n;
            if (!_nodes.TryGetValue(Key(r.Side, r.Price), out n)) return;
            n.LastSeen = now;
            switch (r.Outcome)
            {
                case Outcome.Absorbed:
                    n.AbsorbedCount++;
                    n.Confidence = Clamp(n.Confidence + _cfg.dC_absorb, 0.0, 1.0);
                    n.State = NodeState.Absorbed;
                    break;
                case Outcome.Pulled:
                    n.PulledCount++;
                    n.Confidence *= _cfg.PullPenalty;
                    n.Phantom = true;
                    n.State = NodeState.Pulled;
                    break;
                case Outcome.Consumed:
                    n.Consumed = true;
                    n.Confidence *= 0.5; // demote to a flipped S/R reference
                    n.State = NodeState.Consumed;
                    break;
            }
        }

        public void Evict(DateTime now)
        {
            var dead = new List<long>();
            foreach (var kv in _nodes)
            {
                var n = kv.Value;
                if (n.PulledCount >= _cfg.P_max) { dead.Add(kv.Key); continue; }
                double c = DecayedConfidence(n, now);
                double ageSec = (now - n.LastSeen).TotalSeconds;
                if (c < _cfg.C_floor && ageSec > _cfg.T_evict.TotalSeconds) dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++) _nodes.Remove(dead[i]);
        }

        private double DecayedConfidence(MemoryNode n, DateTime now)
        {
            if (n.InWindow) return n.Confidence;
            double dt = (now - n.LastSeen).TotalSeconds;
            double h = ScaledH().TotalSeconds;
            if (h <= 0) return n.Confidence;
            return n.Confidence * Math.Exp(-Math.Log(2.0) / h * dt);
        }

        public IReadOnlyList<RadarNode> Snapshot(double bestBid, double bestAsk, DateTime now)
        {
            double mid = (bestBid > 0 && bestAsk > 0) ? (bestBid + bestAsk) / 2.0
                       : (bestBid > 0 ? bestBid : bestAsk);
            double band = _cfg.MemoryBandTicks * _tick;
            var outList = new List<RadarNode>();
            foreach (var n in _nodes.Values)
            {
                if (mid > 0 && Math.Abs(n.Price - mid) > band + _tick / 2.0) continue;
                outList.Add(new RadarNode
                {
                    Price = n.Price,
                    Side = n.Side,
                    LastKnownSize = n.LastKnownSize,
                    PeakSize = n.PeakSize,
                    State = n.InWindow ? n.State : NodeState.Remembered,
                    Confidence = DecayedConfidence(n, now),
                    InWindow = n.InWindow,
                    AgeSeconds = (now - n.LastSeen).TotalSeconds
                });
            }
            return outList;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter LiquidityMemoryTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: LiquidityMemory confidence init/decay/revisit/eviction + RadarNode snapshot"
```

---

### Task 6: WallTracker — orchestrator (the single public engine entry point)

**Files:**
- Create: `Engine/WallTracker.cs`
- Test: `Tests/WallTrackerTests.cs`

**Interfaces:**
- Consumes: `BookMirror`, `WallDetector`, `EpisodeClassifier`, `LiquidityMemory`, `RadarConfig`, `RadarNode`, `Side` (Tasks 1–5).
- Produces (this is exactly what the NT `RadarTab` layer — separate plan — calls):
  - `WallTracker(RadarConfig cfg)`
  - `void Update(BookMirror book, DateTime now)` — run detection, promote/observe confirmed walls, open & resolve episodes, apply outcomes, mark blind, evict. Caches best quotes for `GetSnapshot`.
  - `void OnReset(DateTime now)` — feed-reset path: mark every node blind, do not classify the reset's removes.
  - `IReadOnlyList<RadarNode> GetSnapshot(DateTime now)` — the immutable node list for the view (band-filtered, decay-applied), centered on the last cached quotes.

- [ ] **Step 1: Write the failing test**

Create `Tests/WallTrackerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using TradingRadar.Engine;
using Xunit;

public class WallTrackerTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);

    static BookMirror NewBook() => new BookMirror(0.25, TimeSpan.FromSeconds(30));

    static void AddBidWallBook(BookMirror b, long wall, DateTime t)
    {
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.00, Volume = 10, Time = t });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 1, Price = 20999.75, Volume = 12, Time = t });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 2, Price = 20999.50, Volume = wall, Time = t });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21000.25, Volume = 11, Time = t });
    }

    [Fact]
    public void Confirmed_wall_appears_as_a_node_after_persistence()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 500, T0);
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600));
        var s = wt.GetSnapshot(T0.AddMilliseconds(1600));
        Assert.Contains(s, n => Math.Abs(n.Price - 20999.50) < 0.01 &&
                                (n.State == NodeState.Wall || n.State == NodeState.Live));
    }

    [Fact]
    public void Small_levels_never_become_nodes()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 15, T0); // below threshold
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600));
        var s = wt.GetSnapshot(T0.AddMilliseconds(1600));
        Assert.Empty(s);
    }

    [Fact]
    public void Wall_persists_in_memory_after_scrolling_out_of_window()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 500, T0);
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600)); // confirmed & remembered

        // Price moves up; the wall level is removed from the visible book (blind).
        var book2 = NewBook();
        book2.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.50, Volume = 10, Time = T0.AddSeconds(2) });
        book2.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21000.75, Volume = 11, Time = T0.AddSeconds(2) });
        wt.Update(book2, T0.AddSeconds(2));
        var s = wt.GetSnapshot(T0.AddSeconds(2));
        var node = s.FirstOrDefault(n => Math.Abs(n.Price - 20999.50) < 0.01);
        Assert.False(node.InWindow);
        Assert.Equal(NodeState.Remembered, node.State);
    }

    [Fact]
    public void OnReset_marks_all_nodes_blind()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 500, T0);
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600));
        wt.OnReset(T0.AddMilliseconds(1700));
        var s = wt.GetSnapshot(T0.AddMilliseconds(1700));
        Assert.All(s, n => Assert.False(n.InWindow));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter WallTrackerTests`
Expected: FAIL — `WallTracker` does not exist (compile error).

- [ ] **Step 3: Implement WallTracker**

Create `Engine/WallTracker.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    // The single public engine entry point. Wires BookMirror (passed in) through
    // WallDetector -> EpisodeClassifier -> LiquidityMemory and emits RadarNode[].
    public class WallTracker
    {
        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly WallDetector _detector;
        private readonly EpisodeClassifier _classifier;
        private readonly LiquidityMemory _memory;
        private double _lastBestBid;
        private double _lastBestAsk;

        public WallTracker(RadarConfig cfg)
        {
            _cfg = cfg; _tick = cfg.TickSize;
            _detector = new WallDetector(cfg);
            _classifier = new EpisodeClassifier(cfg);
            _memory = new LiquidityMemory(cfg);
        }

        public void Update(BookMirror book, DateTime now)
        {
            _detector.Update(book, now);

            DepthLevel bb, ba;
            bool hasBid = book.TryBestBid(out bb);
            bool hasAsk = book.TryBestAsk(out ba);
            _lastBestBid = hasBid ? bb.Price : 0;
            _lastBestAsk = hasAsk ? ba.Price : 0;

            // 1) Promote / observe confirmed walls; track which prices are currently visible.
            var visible = new HashSet<long>();
            UpdateSide(book, Side.Bid, now, visible);
            UpdateSide(book, Side.Ask, now, visible);

            // 2) Mark blind every tracked node not in the current visible set.
            MarkAbsentBlind(book, Side.Bid, visible);
            MarkAbsentBlind(book, Side.Ask, visible);

            // 3) Approach detection -> open episodes for tracked, visible walls near the inside.
            OpenApproaching(book, Side.Bid, now);
            OpenApproaching(book, Side.Ask, now);

            // 4) Advance & resolve episodes -> feed outcomes to memory.
            _classifier.Update(book, now);
            EpisodeResult r;
            while (_classifier.TryTakeResolved(out r)) _memory.ApplyOutcome(r, now);

            // 5) Eviction.
            _memory.Evict(now);
        }

        private void UpdateSide(BookMirror book, Side side, DateTime now, HashSet<long> visible)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
            {
                long k = Key(side, levels[i].Price);
                visible.Add(k);
                bool confirmed = _detector.IsConfirmed(side, levels[i].Price, book, now);
                if (_memory.Contains(side, levels[i].Price))
                    _memory.ObserveLive(side, levels[i].Price, levels[i].Volume, confirmed, now);
                else if (confirmed)
                {
                    long baseline = _detector.Baseline(side, levels[i].Price, book, now);
                    _memory.Promote(side, levels[i].Price, levels[i].Volume, baseline, now);
                }
            }
        }

        private void MarkAbsentBlind(BookMirror book, Side side, HashSet<long> visible)
        {
            // A tracked node whose price is not in the visible set is blind. We can only test
            // prices the memory knows; iterate the visible book to mark present, blind the rest
            // via the memory's own knowledge by re-walking known prices is not exposed, so we
            // rely on MarkBlind being idempotent: any node we did NOT ObserveLive this tick
            // stays at its prior InWindow. To force-blind, mark blind for every price within the
            // band that is not visible.
            double center = _lastBestBid > 0 && _lastBestAsk > 0 ? (_lastBestBid + _lastBestAsk) / 2.0
                          : (_lastBestBid > 0 ? _lastBestBid : _lastBestAsk);
            if (center <= 0) return;
            int band = _cfg.MemoryBandTicks;
            for (int t = -band; t <= band; t++)
            {
                double price = RoundToTick(center) + t * _tick;
                long k = Key(side, price);
                if (visible.Contains(k)) continue;
                if (_memory.Contains(side, price)) _memory.MarkBlind(side, price);
            }
        }

        private void OpenApproaching(BookMirror book, Side side, DateTime now)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
            {
                double price = levels[i].Price;
                if (!_memory.Contains(side, price)) continue;
                if (_classifier.HasOpenEpisode(side, price)) continue;
                if (NearInside(book, side, price))
                    _classifier.OnApproach(side, price, levels[i].Volume, now);
            }
        }

        // Ask wall tested when the bid is within D_approach ticks; bid wall when the ask is.
        private bool NearInside(BookMirror book, Side side, double price)
        {
            if (side == Side.Ask)
            {
                DepthLevel bid;
                if (!book.TryBestBid(out bid)) return false;
                return (price - bid.Price) <= _cfg.D_approach * _tick + _tick / 2.0;
            }
            else
            {
                DepthLevel ask;
                if (!book.TryBestAsk(out ask)) return false;
                return (ask.Price - price) <= _cfg.D_approach * _tick + _tick / 2.0;
            }
        }

        public void OnReset(DateTime now) { _memory.MarkAllBlind(); }

        public IReadOnlyList<RadarNode> GetSnapshot(DateTime now)
        {
            return _memory.Snapshot(_lastBestBid, _lastBestAsk, now);
        }

        private long Key(Side s, double price) { return ((long)Math.Round(price / _tick)) * 2 + (s == Side.Ask ? 1 : 0); }
        private double RoundToTick(double price) { return Math.Round(price / _tick) * _tick; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter WallTrackerTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS — all tasks' tests green (Primitives folded into BookMirror: 9 + WallDetector 6 + EpisodeClassifier 4 + LiquidityMemory 7 + WallTracker 4 = 30).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: WallTracker orchestrator — single engine entry point emitting RadarNode[]"
```

---

## Engine complete — next

The pure engine is done and fully unit-tested. The NT8 layer is a **separate plan** (`2026-06-28-liquidity-radar-nt-ui.md`, to be written): `RadarAddOn`, `RadarWindow`, `RadarTab` (the threading boundary that maps `MarketDepthEventArgs`/`MarketDataEventArgs` → `DepthEvent`/`TradeEvent` and calls this `WallTracker`), and `RadarVisual` (Aurora WPF render of `GetSnapshot`). That plan validates with `nt8c` + Market Replay, not unit tests.

**Engine→NT contract the UI plan must honor (already frozen here):** feed via `BookMirror.ApplyDepth/ApplyTrade`; on each processed batch call `WallTracker.Update(book, eventTime)`; on `IsReset` call `book.ApplyDepth(resetEvent)` then `WallTracker.OnReset(eventTime)`; render `WallTracker.GetSnapshot(now)`.

---

## Self-Review

**1. Spec coverage** (spec §-by-§ → task):
- §4 architecture (BookMirror, WallTracker, RadarNode contract) → Tasks 1, 2, 6. ✅ (RadarAddOn/RadarWindow/RadarTab/RadarVisual are explicitly the NT-UI plan, noted above.)
- §6.1 book mirror (positional Add/Update/Remove, aggregated volume) → Task 2. ✅
- §6.2 wall detection (median B_level + B_time, K_mult, MinAbsSize, T_persist, F_flicker) → Task 3. ✅
- §6.3 three outcomes (absorbed/pulled/consumed via trade↔depth cross-ref, W_assoc, A_absorb, refill, D_pull) → Task 4. ✅
- §6.4 confidence & memory (C0 formula, decay-while-blind half-life, revisit increments, eviction) → Task 5. ✅
- §6.5 failure modes: reset (Task 2 `IsReset` + Task 6 `OnReset`), spoof (Task 4 PULLED + Task 5 P_max death), iceberg (Task 4 refill_ratio), MBP no order-count (no inference anywhere), fast markets (`VolGovernor` scales T_persist/T_episode/H), level leaves window (Task 5 decay). ✅
- §6.6 all 20 params → `RadarConfig` (Task 1). ✅
- §8 validation: unit tests for BookMirror + WallTracker family → Tasks 2–6. ✅ (`nt8c` + Market Replay belong to the NT-UI plan.)
- §6.3 determinism / no clock → Global Constraints + every class takes `now`. ✅

**2. Placeholder scan:** No `TBD`/`TODO`/"handle edge cases"/"similar to Task N". Every step has complete, runnable code. The long comment in `MarkAbsentBlind` documents a real design choice (band-scan blinding because memory doesn't expose price enumeration), not a placeholder. ✅

**3. Type consistency:** `Side`, `DepthOp`, `NodeState`, `Outcome`, `DepthEvent`, `TradeEvent`, `DepthLevel`, `RadarNode`, `EpisodeResult`, `RadarConfig` field names are identical across Tasks 1–6. `BookMirror.MedianSizeExcluding`, `TradedAt`, `TryBestBid/Ask`; `WallDetector.IsConfirmed/Baseline/Update`; `EpisodeClassifier.OnApproach/Update/TryTakeResolved/HasOpenEpisode`; `LiquidityMemory.Promote/ObserveLive/MarkBlind/MarkAllBlind/ApplyOutcome/Evict/Snapshot/Contains`; `WallTracker.Update/OnReset/GetSnapshot` — all referenced consistently with their defining task. ✅

**Known v1 simplifications (deliberate, ponytail-marked for the implementer):**
- `VolGovernor` is a manual `RadarConfig` field (default 1.0). Auto-deriving it from depth-update-rate/ATR is deferred — the scaling hooks (`Scaled()`/`ScaledH()`) are already in place, so wiring an estimator later is additive.
- `MarkAbsentBlind` band-scans ±`MemoryBandTicks` to blind absent nodes because `LiquidityMemory` doesn't expose price enumeration. Cheap at 51 prices/side; if the band grows large, add a `MemoryMarkBlindExcept(visibleSet)` method.
- Even-count median uses the lower-middle element (floor) — a conservative baseline that can't be inflated by a single wall, and it keeps the test expectations exact.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-28-liquidity-radar-engine.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration. For this engine the right worker is **`trading-ninjascript-developer`** (C#/NT8 domain), with **`trading-code-reviewer`** as the between-task gate.

**2. Inline Execution** — I execute the tasks in this session using executing-plans, batch execution with checkpoints for your review.

Which approach?

