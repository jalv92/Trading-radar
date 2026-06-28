# Liquidity Radar — Pure-C# Engine Interface Contract

- **Date:** 2026-06-28
- **Status:** FROZEN — single source of truth for all drafting agents
- **Scope:** Engine layer only (`Engine/` + `Tests/`). NT8 window/visual layer is Plan 2 — excluded here except where the engine's public surface serves it.
- **Derived from:** `2026-06-28-liquidity-radar-design.md` §4, §5, §6

Drafting agents do NOT have access to the original spec. This document is complete.

---

## 1. GLOBAL CONSTRAINTS

- **Language:** C# 7.3 maximum. No `record` types. No target-typed `new()`. No nullable reference types (`?` annotations on reference types). No file-scoped namespaces. No `switch` expressions. No default interface methods. Plain `struct` and `class` only.
- **Namespace:** `LiquidityRadar.Engine` — zero `using NinjaTrader.*`, zero `using System.Windows.*`, zero WPF. `System.*` only.
- **Determinism:** All time comes from event timestamps (`DepthEvent.Time`, `TradeEvent.Time`) and an explicit `DateTime now` parameter on `Update` / `GetSnapshot`. `DateTime.Now`, `DateTime.UtcNow`, `Stopwatch`, and `Environment.TickCount` are **forbidden** inside the engine.
- **Numeric types:** Sizes are `long`. Prices are `double`. Tick arithmetic uses `RadarConfig.TickSize` (`double`). No `decimal`.
- **BCL subset (net48 + netstandard2.0 compatibility):** Use only `System.Collections.Generic`, `System.Linq`, `System.Math`, `System.Text`. No `System.Threading.Tasks`, no `Span<T>`, no `System.Memory`, no `System.Collections.Immutable` (not guaranteed in net48 without extra NuGet).
- **Repo layout:**
  - Root: `projects/Trading/LiquidityRadar/`
  - `Engine/Engine.csproj` — targets `netstandard2.0`
  - `Tests/Tests.csproj` — targets `net8.0`; `<ProjectReference>` to Engine; xunit 2.x
  - Engine `.cs` files are later **copied verbatim** into NT8's `Custom/AddOns/LiquidityRadar/Engine/` folder by Plan 2 — they must compile under net48 Roslyn (C# 7.3 subset, no language features beyond that list).
- **Thread safety:** Engine types are **not thread-safe**. The caller (`RadarTab` in NT8) owns synchronization. Engine methods must not spawn threads.
- **Immutability of snapshots:** `GetSnapshot` returns `IReadOnlyList<RadarNode>`. Callers must treat the list as immutable; the engine may reuse internal arrays between calls but must guarantee the returned reference remains valid until the next `GetSnapshot` call.

---

## 2. FILE STRUCTURE

### Engine/

| File | Single responsibility |
|---|---|
| `Primitives.cs` | All enums (`Side`, `DepthOp`, `NodeState`), all plain-data structs (`DepthEvent`, `TradeEvent`, `DepthLevel`, `RadarNode`). No logic. |
| `RadarConfig.cs` | One class holding all 21 tunable parameters with NQ defaults. No logic. |
| `BookMirror.cs` | Maintains two ordered price-level lists (bids desc, asks asc) from Add/Update/Remove/IsReset depth events. Tracks a trade ring-buffer for `TradedAt`. Exposes read queries. |
| `WallDetector.cs` | Baseline computation (level-median, temporal-median). Wall candidate promotion pipeline: relative threshold, absolute floor, persistence timer, flicker guard. Emits `WallCandidate` structs consumed by `WallTracker`. |
| `EpisodeClassifier.cs` | Episode lifecycle: open on approach or trade-at-price, resolve as Absorbed / Pulled / Consumed / timeout. Produces `EpisodeResult` consumed by `WallTracker`. |
| `LiquidityMemory.cs` | Owns the set of live `MemoryNode` records. Handles init-on-promotion, confidence decay while blind, revisit updates, eviction. Exposes the node collection for snapshot assembly. |
| `WallTracker.cs` | Orchestrator. Accepts `DepthEvent` and `TradeEvent` in order. Calls `WallDetector`, `EpisodeClassifier`, `LiquidityMemory` in the correct sequence per tick. Exposes `GetSnapshot(DateTime now)` returning `IReadOnlyList<RadarNode>`. |

### Tests/

| File | Covers |
|---|---|
| `BookMirrorTests.cs` | Positional Add/Update/Remove ordering; IsReset wipe + snapshot rebuild; TradedAt ring-buffer expiry; aggressor-side inference; MedianSize; best-bid/ask. |
| `WallDetectorTests.cs` | Baseline level-median and temporal-median; K_mult + MinAbsSize gate; persistence timer reset on drop; flicker-guard rejection. |
| `EpisodeClassifierTests.cs` | Episode opens on approach (D_approach ticks); opens on trade-at-price; resolves Absorbed (refill_ratio + A_absorb + price hold); resolves Pulled (cancelled-dominates + D_pull); resolves Consumed (price breaks + trades); T_episode timeout. |
| `LiquidityMemoryTests.cs` | Init confidence C0 formula; decay while blind (exponential, half-life H); decay pauses when node back in window; revisit transitions (+dC_confirm, +dC_grow, ×shrink_factor, ×pull_penalty, +dC_absorb); P_max dead node; eviction (confidence < C_floor AND age > T_evict). |
| `WallTrackerIntegrationTests.cs` | End-to-end synthetic sequences: wall detected → episode → Absorbed confidence raise; wall → Pulled → phantom → death after P_max; wall scrolls out → blind decay → revisit; IsReset clears in-window nodes without touching blind nodes; GetSnapshot RadarNode fields correct for each NodeState. |

---

## 3. FROZEN TYPE CONTRACT

### 3.1 Primitives.cs

```csharp
namespace LiquidityRadar.Engine
{
    public enum Side { Bid, Ask }

    public enum DepthOp { Add, Update, Remove }

    // NodeState progression:
    //   Live       — resting order tracked, not yet wall-qualified
    //   Wall       — confirmed wall (persistence cleared)
    //   Absorbed   — episode resolved: traded through, price held, refilled
    //   Pulled     — episode resolved: size vanished without trades (probable spoof)
    //   Consumed   — episode resolved: traded through, price broke, level removed
    //   Remembered — node scrolled beyond live window; confidence decaying
    public enum NodeState { Live, Wall, Absorbed, Pulled, Consumed, Remembered }

    public struct DepthEvent
    {
        public Side      Side;
        public DepthOp   Op;
        public int       Position;   // 0-based; 0 = best bid or best ask
        public double    Price;
        public long      Volume;     // aggregated resting contracts at this price
        public DateTime  Time;
        public bool      IsReset;    // true = wipe + rebuild; other fields still valid
    }

    public struct TradeEvent
    {
        public double   Price;
        public long     Volume;
        public DateTime Time;
    }

    public struct DepthLevel
    {
        public double Price;
        public long   Volume;
    }

    // Output DTO: contract between engine and NT8/visual layer.
    // Produced exclusively by WallTracker.GetSnapshot().
    public struct RadarNode
    {
        public double    Price;
        public Side      Side;
        public long      LastKnownSize;  // last observed Volume at this price
        public long      PeakSize;       // highest Volume ever seen
        public NodeState State;
        public double    Confidence;     // 0.0..1.0
        public bool      InWindow;       // true = currently within live 10 levels
        public double    AgeSeconds;     // elapsed since LastSeen (for "· 18s" label)
    }
}
```

### 3.2 RadarConfig.cs

```csharp
namespace LiquidityRadar.Engine
{
    public class RadarConfig
    {
        // --- instrument tick size ---
        public double TickSize        { get; set; } = 0.25;   // NQ = 0.25, ES = 0.25

        // --- wall detection ---
        public double K_mult          { get; set; } = 4.0;    // size multiple over baseline
        public long   MinAbsSize      { get; set; } = 40;     // absolute contract floor (NQ)
        public double BaselineWindow  { get; set; } = 30.0;   // seconds for temporal-median
        public double T_persist       { get; set; } = 1500.0; // ms: candidate→wall
        public double F_flicker       { get; set; } = 6.0;    // max Add/Remove osc per second

        // --- episode ---
        public int    D_approach      { get; set; } = 1;      // ticks to open episode
        public double T_episode       { get; set; } = 3000.0; // ms: episode timeout
        public double W_assoc         { get; set; } = 250.0;  // ms: match Last↔depth window

        // --- absorption ---
        public double A_absorb        { get; set; } = 1.0;    // Traded@P / S0 threshold
        public double refill_ratio_trigger { get; set; } = 3.0; // iceberg-refill threshold

        // --- pull classifier ---
        public int    D_pull          { get; set; } = 1;      // ticks away when size vanishes → pull

        // --- confidence & memory ---
        public double H               { get; set; } = 30.0;   // seconds: blind decay half-life
        public double dC_confirm      { get; set; } = 0.15;
        public double dC_grow         { get; set; } = 0.20;
        public double dC_absorb       { get; set; } = 0.25;
        public double shrink_factor   { get; set; } = 0.6;
        public double pull_penalty    { get; set; } = 0.2;
        public int    P_max           { get; set; } = 2;      // pulls before node dead
        public double G_grow          { get; set; } = 0.25;   // 25% increase = GREW
        public double C_floor         { get; set; } = 0.05;
        public double T_evict         { get; set; } = 300.0;  // seconds

        // --- display ---
        public int    MemoryBandTicks { get; set; } = 25;     // ± ticks shown around market

        // --- vol governor ---
        // Multiplier applied to H, T_episode, T_persist in fast-market regimes.
        // Caller computes it externally and sets it here before each tick if desired.
        public double VolGovernor     { get; set; } = 1.0;    // 0.3..1.0; 1.0 = normal
    }
}
```

### 3.3 BookMirror.cs

**Internal trade ring-buffer policy:** retain trade records for `max(W_assoc, BaselineWindow) + 1 s` = effectively 31 s at NQ defaults. Older records are discarded on the next `ApplyTrade` call. Ring capacity = 2048 entries (power-of-two); if full, oldest is overwritten. No dynamic allocation after construction.

**Aggressor-side inference:** `Last >= BestAsk → Ask aggressor (buy hit the ask)`; `Last <= BestBid → Bid aggressor (sell hit the bid)`; otherwise `Unknown` (mid-cross or stale best).

```csharp
namespace LiquidityRadar.Engine
{
    public enum AggressorSide { Unknown, Bid, Ask }

    public class BookMirror
    {
        public BookMirror(RadarConfig config) { }

        // --- write path ---

        // Apply a single depth event. If e.IsReset == true, wipes both sides
        // and rebuilds from the provided snapshot lists (non-null, may be empty).
        // The non-reset fields of e are ignored when IsReset == true.
        public void ApplyDepth(DepthEvent e,
                               IEnumerable<DepthLevel> resetBids = null,
                               IEnumerable<DepthLevel> resetAsks = null);

        // Apply a single trade print. Stored in the ring-buffer for TradedAt queries.
        public void ApplyTrade(TradeEvent e);

        // Explicit snapshot rebuild (called after IsReset when the caller has already
        // assembled the full snapshot separately from the reset event).
        public void ResetFromSnapshot(IEnumerable<DepthLevel> bids,
                                      IEnumerable<DepthLevel> asks);

        // --- read path ---

        // Ordered: bids descending by price, asks ascending by price.
        public IReadOnlyList<DepthLevel> Levels(Side side);

        public bool TryBestBid(out double price);
        public bool TryBestAsk(out double price);

        // Cross-sectional median size of all levels on the given side (excluding the
        // level at `excludePrice` if it is within the book — so a wall cannot inflate
        // its own baseline). Returns 0 if fewer than 2 levels remain after exclusion.
        public double MedianSize(Side side, double excludePrice = double.NaN);

        // Sum of Volume in the trade ring-buffer where:
        //   abs(trade.Price - price) < TickSize/2   (exact price match within half tick)
        //   trade.Time >= since
        //   aggressorFilter == AggressorSide.Unknown → include all sides
        //   otherwise → include only trades whose inferred aggressor matches
        // Returns 0 if no matching trades.
        public long TradedAt(double price, DateTime since, AggressorSide aggressorFilter);
    }
}
```

### 3.4 Internal DTOs (shared between WallDetector, EpisodeClassifier, WallTracker)

These are `internal` to the Engine assembly. Drafting agents must use exactly these definitions — do not invent parallel DTOs.

```csharp
namespace LiquidityRadar.Engine
{
    // Produced by WallDetector; consumed by WallTracker to create MemoryNodes.
    internal struct WallCandidate
    {
        internal double   Price;
        internal Side     Side;
        internal long     Volume;         // size at the moment persistence cleared
        internal long     PeakSize;       // peak seen during candidate window
        internal DateTime ConfirmedAt;    // timestamp when T_persist elapsed
        internal double   BaselineAtConf; // B value at confirmation time
    }

    // Produced by EpisodeClassifier; consumed by WallTracker to update LiquidityMemory.
    internal enum EpisodeOutcome { Absorbed, Pulled, Consumed, Timeout }

    internal struct EpisodeResult
    {
        internal double         Price;
        internal Side           Side;
        internal EpisodeOutcome Outcome;
        internal long           TradedVolume;   // Traded@P during episode window
        internal long           S0;             // size at episode open
        internal long           DisplayedDrop;  // S0 - size_at_close (0 if timeout)
        internal double         RefillRatio;    // TradedVolume / max(DisplayedDrop,1)
        internal DateTime       ClosedAt;
    }
}
```

### 3.5 WallDetector.cs

**Baseline policy:** `B_level` = cross-sectional median of the other N-1 visible levels on that side (caller passes current book via `BookMirror`). `B_time` = rolling median of all per-level size samples recorded within `BaselineWindow` seconds, across all price levels on that side. `B = Math.Max(B_level, B_time)`.

**Flicker guard:** tracked per price as `(addCount + removeCount) / elapsed_seconds`; if > `F_flicker`, the candidate is rejected and the timer resets.

**Persistence:** the qualifying-size timer starts when `Volume >= K_mult * B AND Volume >= MinAbsSize`. It resets to zero if Volume drops below either threshold at any subsequent depth event for that price. Promotion fires when the timer exceeds `T_persist * VolGovernor` ms.

```csharp
namespace LiquidityRadar.Engine
{
    internal class WallDetector
    {
        internal WallDetector(RadarConfig config) { }

        // Called on every DepthEvent (after BookMirror has been updated).
        // mirror is used for MedianSize baseline queries.
        // now = DepthEvent.Time.
        // Returns a non-null list (may be empty) of newly confirmed WallCandidates
        // this tick. A given price is returned at most once per promotion.
        internal IReadOnlyList<WallCandidate> Update(DepthEvent e,
                                                      BookMirror mirror,
                                                      DateTime now);

        // Remove tracking state for a price that WallTracker has already promoted
        // to a MemoryNode (avoid double-promotion on revisit).
        internal void Forget(double price, Side side);

        // Remove tracking for prices no longer in the book (called after IsReset).
        internal void Reset();
    }
}
```

### 3.6 EpisodeClassifier.cs

**Episode open conditions (either):**
1. Best bid or best ask comes within `D_approach` ticks of a tracked node's price.
2. A `TradeEvent` prints at a tracked node's price (within `TickSize/2`).

**Episode close conditions (first triggers):**
- `T_episode * VolGovernor` ms elapsed → `Timeout`.
- `Size → 0` AND `TradedVolume >= A_absorb * S0` AND `RefillRatio >= refill_ratio_trigger` AND inside quote never crossed P → `Absorbed`.
- `Size → 0` AND cancelled-volume dominates (`DisplayedDrop - TradedVolume > TradedVolume`) AND best quote was >= `D_pull` ticks from P when size vanished → `Pulled`.
- `Size → 0` AND price breaks (inside quote crossed P coincident with trades) → `Consumed`.
- Caller note: "Size → 0" means the level is removed from the book OR Volume drops to 0 at that price.

At most one episode per (price, side) at a time. Opening a new episode for an already-open price is a no-op (existing episode continues).

```csharp
namespace LiquidityRadar.Engine
{
    internal class EpisodeClassifier
    {
        internal EpisodeClassifier(RadarConfig config) { }

        // Called on every DepthEvent, after BookMirror is updated.
        // trackedPrices = prices+sides currently in LiquidityMemory (non-null).
        // now = DepthEvent.Time.
        // Returns newly closed EpisodeResults this tick (may be empty).
        internal IReadOnlyList<EpisodeResult> OnDepth(DepthEvent e,
                                                       BookMirror mirror,
                                                       IEnumerable<TrackedPrice> trackedPrices,
                                                       DateTime now);

        // Called on every TradeEvent, after BookMirror.ApplyTrade.
        // Returns newly closed EpisodeResults this tick (may be empty).
        internal IReadOnlyList<EpisodeResult> OnTrade(TradeEvent e,
                                                       BookMirror mirror,
                                                       IEnumerable<TrackedPrice> trackedPrices,
                                                       DateTime now);

        // Remove episode state for a price (called when WallTracker removes the node).
        internal void CancelEpisode(double price, Side side);

        // Wipe all open episodes (called on IsReset).
        internal void Reset();
    }

    // Lightweight tuple passed from WallTracker to EpisodeClassifier each tick.
    internal struct TrackedPrice
    {
        internal double Price;
        internal Side   Side;
        internal long   S0;   // size at episode-open (or last known if already open)
    }
}
```

### 3.7 LiquidityMemory.cs

**C0 formula:** `C0 = Math.Max(0.4, Math.Min(0.8, 0.4 + 0.1 * (volume / baseline - config.K_mult)))`

**Decay while blind:** `C(t) = C_last * Math.Exp(-Math.Log(2) / (H * VolGovernor) * deltaSeconds)` where `deltaSeconds` is computed from the timestamps passed to `Tick`. Decay runs only while `InWindow == false`. When a node re-enters the window, decay stops immediately.

**Revisit update rules (applied in order, from an EpisodeResult):**
1. Outcome == Absorbed → `confidence = Math.Min(1.0, confidence + dC_absorb)`, State = Absorbed
2. Outcome == Pulled → `confidence *= pull_penalty`, `pulledCount++`; if `pulledCount >= P_max` mark node dead (remove)
3. Outcome == Consumed → `confidence = Math.Min(1.0, confidence * shrink_factor)`, State = Consumed
4. Outcome == Timeout with size grew (>= G_grow fraction) → `confidence = Math.Min(1.0, confidence + dC_grow)`
5. Outcome == Timeout with size confirmed present → `confidence = Math.Min(1.0, confidence + dC_confirm)`
6. Outcome == Timeout with size shrank (not enough to be Pulled) → `confidence *= shrink_factor`

**Eviction:** remove node when `confidence < C_floor AND ageSeconds > T_evict`.

```csharp
namespace LiquidityRadar.Engine
{
    internal class LiquidityMemory
    {
        internal LiquidityMemory(RadarConfig config) { }

        // Promote a confirmed WallCandidate to a memory node.
        // No-op if a node for (price, side) already exists.
        internal void Promote(WallCandidate candidate, double baseline, DateTime now);

        // Called once per WallTracker tick.
        // now = current event timestamp.
        // inWindowPrices = set of prices currently visible in the live book (both sides).
        // Updates InWindow flags, runs blind decay, applies eviction.
        internal void Tick(DateTime now, IEnumerable<DepthLevel> allLiveLevels);

        // Apply an EpisodeResult to the matching node. No-op if node not found.
        internal void ApplyEpisode(EpisodeResult result, DateTime now);

        // Update LastKnownSize when a node is in-window and its level changes.
        // Called by WallTracker after each DepthEvent for prices that have nodes.
        internal void UpdateSize(double price, Side side, long newVolume, DateTime now);

        // Enumerate all live (non-dead) nodes. Used by WallTracker to build
        // TrackedPrice list for EpisodeClassifier and to assemble the snapshot.
        internal IEnumerable<MemoryNode> AllNodes { get; }

        // Wipe in-window nodes (called on IsReset). Blind nodes are kept.
        internal void ResetInWindow();
    }

    // Internal mutable record. Exposed read-only fields for snapshot assembly.
    internal class MemoryNode
    {
        internal double    Price;
        internal Side      Side;
        internal long      LastKnownSize;
        internal long      PeakSize;
        internal NodeState State;
        internal double    Confidence;
        internal bool      InWindow;
        internal DateTime  FirstSeen;
        internal DateTime  LastSeen;
        internal int       PulledCount;
        internal bool      IsPhantom;    // true after first pull; used by visual layer
        internal bool      IsDead;       // pending removal; Tick() evicts after setting
    }
}
```

### 3.8 WallTracker.cs

**Tick ordering per `DepthEvent`:**
1. If `e.IsReset`: call `BookMirror.ApplyDepth(e, bids, asks)` (caller must supply snapshot), then `WallDetector.Reset()`, `EpisodeClassifier.Reset()`, `LiquidityMemory.ResetInWindow()`.
2. Otherwise: `BookMirror.ApplyDepth(e)`.
3. `LiquidityMemory.UpdateSize(e.Price, e.Side, e.Volume, e.Time)` if a node exists for this price+side.
4. `LiquidityMemory.Tick(e.Time, mirror.Levels(Side.Bid).Concat(mirror.Levels(Side.Ask)))`.
5. `candidates = WallDetector.Update(e, mirror, e.Time)` → for each candidate: `LiquidityMemory.Promote(candidate, baseline, e.Time)`, `WallDetector.Forget(candidate.Price, candidate.Side)`.
6. `trackedPrices = LiquidityMemory.AllNodes.Select(n => new TrackedPrice{...})`.
7. `results = EpisodeClassifier.OnDepth(e, mirror, trackedPrices, e.Time)` → for each: `LiquidityMemory.ApplyEpisode(result, e.Time)`.

**Tick ordering per `TradeEvent`:**
1. `BookMirror.ApplyTrade(e)`.
2. `trackedPrices = LiquidityMemory.AllNodes.Select(...)`.
3. `results = EpisodeClassifier.OnTrade(e, mirror, trackedPrices, e.Time)` → for each: `LiquidityMemory.ApplyEpisode(result, e.Time)`.

**`GetSnapshot` assembly:**
- For each non-dead node in `LiquidityMemory.AllNodes`:
  - `AgeSeconds = (now - node.LastSeen).TotalSeconds`
  - `State`: if `node.IsDead` skip; if `node.InWindow && node.State == NodeState.Remembered` promote back to `Live`; else `node.State`.
  - Blind nodes (InWindow=false, State not terminal): State = `Remembered`.
  - Terminal states (Absorbed, Pulled, Consumed) are preserved and still emitted until eviction.
- Returns a **new** `List<RadarNode>` each call (callers may hold the reference; the engine does not mutate it after return).

```csharp
namespace LiquidityRadar.Engine
{
    public class WallTracker
    {
        public WallTracker(RadarConfig config) { }

        // Call BEFORE ApplyDepth when e.IsReset == true.
        // Provides the full book snapshot the platform delivers alongside a reset.
        // If the platform doesn't deliver it, pass empty enumerables.
        public void SetResetSnapshot(IEnumerable<DepthLevel> bids,
                                     IEnumerable<DepthLevel> asks);

        // Feed one depth event. Thread-unsafe; caller serializes.
        public void ApplyDepth(DepthEvent e);

        // Feed one trade event. Thread-unsafe; caller serializes.
        public void ApplyTrade(TradeEvent e);

        // Assemble and return the current node snapshot.
        // 'now' must be >= the last event timestamp; caller passes e.g. the
        // timestamp of the most recent event so the engine stays deterministic.
        public IReadOnlyList<RadarNode> GetSnapshot(DateTime now);
    }
}
```

---

## 4. TASK MAP

Tasks are ordered. Each agent owns exactly one task. Numbering is the agreed sequence; later tasks may start as soon as their dependencies (noted) are complete.

| # | Component | Task title | Delivers | Depends on |
|---|---|---|---|---|
| T1 | Scaffold | Repo + toolchain smoke test | `liquidity-radar/` git repo; `Engine/Engine.csproj` (netstandard2.0); `Tests/Tests.csproj` (net8.0 + xunit + ProjectReference); `Primitives.cs` and `RadarConfig.cs` with exact signatures from §3.1–3.2; one trivial passing xunit test confirming the reference compiles. `nt8c` smoke: `nt8c build` on Engine succeeds. | — |
| T2 | BookMirror | Depth operations | `BookMirror.cs` fully implemented. Covers: `ApplyDepth` positional Add/Update/Remove (bids desc, asks asc); `ResetFromSnapshot`; `TryBestBid`/`TryBestAsk`; `Levels(side)`. All `BookMirrorTests` for depth ops green. | T1 |
| T3 | BookMirror | IsReset + snapshot rebuild | `ApplyDepth` `IsReset==true` path: wipes both sides, calls `ResetFromSnapshot` with provided lists. Tests: reset clears book; subsequent events apply to rebuilt state; reset with empty lists leaves book empty. | T2 |
| T4 | BookMirror | Trade ring-buffer + TradedAt + aggressor + MedianSize | Ring-buffer implementation (capacity 2048, oldest-overwrite); `ApplyTrade`; `TradedAt(price, since, aggressorFilter)` with aggressor-side inference (Last>=BestAsk → Ask; Last<=BestBid → Bid; else Unknown); `MedianSize(side, excludePrice)`. All remaining `BookMirrorTests` green. | T3 |
| T5 | WallDetector | Baselines | `WallDetector.cs` internal class. Temporal-median rolling storage (per-level size samples with timestamp). Level-median via `BookMirror.MedianSize`. `B = max(B_level, B_time)`. Tests in `WallDetectorTests`: B_level excludes the candidate price; B_time window expires old samples; B picks max. | T4 |
| T6 | WallDetector | Candidate qualify + persistence + flicker | Per-price candidate state machine: relative gate (K_mult·B), absolute gate (MinAbsSize), persistence timer (resets on drop), flicker counter (oscillation rate). `Forget` and `Reset`. Tests: candidate promoted only after T_persist; drop below threshold resets timer; flicker rate > F_flicker rejects; `Reset` clears all state. | T5 |
| T7 | EpisodeClassifier | Episode lifecycle — open and timeout | `EpisodeClassifier.cs`. Open on approach (D_approach ticks from best quote) and open on trade-at-price. `T_episode` timeout path → `EpisodeOutcome.Timeout`. `CancelEpisode`, `Reset`. Tests: episode opens on approach; episode opens on trade print; timeout returns Timeout result; no duplicate open for same price. | T4 |
| T8 | EpisodeClassifier | Absorbed resolution | `Outcome.Absorbed` path: size drop with `TradedVolume >= A_absorb * S0` AND `RefillRatio >= refill_ratio_trigger` AND inside quote never crossed P. Tests: exact threshold (at-boundary and just-below); RefillRatio < trigger → not Absorbed; quote crossed P → not Absorbed. | T7 |
| T9 | EpisodeClassifier | Pulled + Consumed resolution | `Outcome.Pulled` path: cancelled volume dominates AND best quote >= D_pull ticks from P when size vanished. `Outcome.Consumed` path: price breaks (quote crossed P) coincident with trades. Tests: each path independently; ambiguous case (trade+cancel same instant) follows §6.3 attribution rule (up to TradedAt is trading, residual is cancel). | T8 |
| T10 | LiquidityMemory | Init + C0 formula | `LiquidityMemory.cs` and `MemoryNode`. `Promote`: no-op on duplicate; C0 formula from §3.7. `AllNodes` enumerator (excludes dead). Tests: C0 clamps to [0.4, 0.8]; duplicate Promote is no-op; AllNodes excludes dead. | T6 |
| T11 | LiquidityMemory | Blind decay while blind + Tick inWindow update | `Tick(now, allLiveLevels)`: sets `InWindow` from allLiveLevels membership; runs exponential decay only on blind nodes; updates `LastSeen` for in-window nodes. Tests: decay matches `C * exp(-ln2/H * dt)` to 4 decimal places; in-window node confidence unchanged by Tick alone; node that returns to window stops decaying; VolGovernor scales H correctly. | T10 |
| T12 | LiquidityMemory | Revisit transitions + eviction + snapshot | `ApplyEpisode` all six revisit rules from §3.7. `UpdateSize` (LastKnownSize, PeakSize). Eviction in `Tick` (C_floor AND age > T_evict). `ResetInWindow`. Tests: each revisit rule in isolation; P_max dead node removed; eviction fires only when both conditions met; ResetInWindow keeps blind nodes. | T11 |
| T13 | WallTracker | Orchestration integration test | `WallTracker.cs` implementing the tick-ordering from §3.8 (`SetResetSnapshot`, `ApplyDepth`, `ApplyTrade`, `GetSnapshot`). `WallTrackerIntegrationTests.cs`: (a) wall detected → Absorbed → confidence raised → RadarNode.State == Absorbed; (b) wall → Pulled × P_max → node gone from snapshot; (c) wall scrolls out → blind decay visible in AgeSeconds/Confidence; (d) IsReset wipes in-window, blind nodes survive; (e) GetSnapshot RadarNode fields correct for each NodeState enum value. All test files green. `nt8c build` on Engine still passes. | T9 + T12 |

---

## 5. CONVENTIONS FOR DRAFTING AGENTS

- **No file creates `RadarConfig` or primitive types** other than `Primitives.cs` and `RadarConfig.cs`. Import via the same assembly.
- **Internal DTOs** (`WallCandidate`, `EpisodeResult`, `TrackedPrice`, `MemoryNode`) live in their respective implementation files (`WallDetector.cs`, `EpisodeClassifier.cs`, `WallTracker.cs`, `LiquidityMemory.cs`). Do not move them to `Primitives.cs`.
- **DepthLevel equality for `allLiveLevels` membership:** match on `Price` within `TickSize / 2` (floating-point safe). Never use `==` on doubles for price comparison anywhere in the engine; always use `Math.Abs(a - b) < config.TickSize * 0.5`.
- **Ring-buffer in BookMirror:** use a fixed-size `TradeRecord[]` array allocated at construction. `TradeRecord` is a private struct `{ double Price; long Volume; DateTime Time; AggressorSide Aggressor; }`. No `List<T>` growth allowed for the trade buffer.
- **Temporal-median in WallDetector:** use a `List<SizeSample>` per price (private struct `{ long Volume; DateTime Time; }`), purge on each `Update` call before computing median. Max expected size is `BaselineWindow(30s) * max_events_per_second` — bounded in practice; no hard cap needed.
- **Test project:** each `*Tests.cs` file must be self-contained (no shared test helpers unless extracted into `Tests/TestHelpers.cs` in T1). Use `Assert.Equal`, `Assert.True`, `Assert.False`, `Assert.Throws` from xunit. No Moq or other mocking frameworks — pass real `BookMirror` / `RadarConfig` instances with synthetic data.
- **Compile check:** after every task, the agent must confirm `dotnet build` on both projects returns exit code 0 with zero warnings (treat `CS0168`, `CS0219`, `CS8600-CS8603` as errors via `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in both csproj files).
