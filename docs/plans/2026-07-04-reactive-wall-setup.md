# Reactive Wall Setup ("React") Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **All NinjaScript/engine `.cs` work is delegated to the `trading-*` agent team** (ninjascript-developer -> code-reviewer -> risk-manager veto before anything reaches real), with the relevant `nt8-*` skills loaded. The `nt8c` PostToolUse hook validates compilation on each `.cs` edit.

**Goal:** Add a second, user-selectable "React" setup to the Liquidity Radar — arms on tape acceleration into a dominant wall, waits, and fires on the wall's resolution (Absorbed -> fade / Consumed -> follow) — without touching the frozen Break setup.

**Architecture:** An isolated `ReactiveController` state machine (new file) selected via a WPF dropdown; the existing `ControllerStateMachine` (Break) is frozen and untouched. One new pure-engine signal (`TapeAcceleration`). Additive-only contract changes to shared structs (`FireEvent`, `ControllerInputs`). Auto-aggressive execution reusing the existing Sim/Playback-gated auto-fire + ATM bracket path.

**Tech Stack:** C# — engine `netstandard2.0` / `LangVersion 7.3`; NT/WPF add-on layer `net8.0`; xUnit tests via `dotnet test`; NinjaTrader 8 add-on; full-assembly compile via `nt8c`.

**Design spec:** `docs/specs/2026-07-04-reactive-wall-setup-design.md` (source of truth). Section refs (§N) below point at it.

## Global Constraints

_Every task's requirements implicitly include this section. Values copied verbatim from the recon of the real code._

- **Run all commands from** the sub-repo root: `projects/Trading/Trading-radar/`.
- **Engine language floor:** `netstandard2.0`, **`LangVersion 7.3`** — no C# 8+ syntax (no switch-expressions, target-typed `new`, records, ranges). Enums + struct fields are fine.
- **Tests:** xUnit via `dotnet test` from the repo root. Test classes are `public class <Name>Tests` with **NO namespace**, `using TradingRadar.Engine; using Xunit;`, their own `static DateTime T(...)` clock helper and local book/inputs builders (files are self-contained — no shared cross-class helpers). Assertions: `Assert.Equal/True/False/NotEqual`. **Green baseline before any work: `Passed: 117`.** Scope a cluster with `dotnet test --filter FullyQualifiedName~<Name>`.
- **csproj auto-globs** `**/*.cs` under `Engine/` and `Tests/` — **new `.cs` files need NO `.csproj` edit.**
- **Full add-on compile check** (engine + NT/WPF together): `bash build/stage-custom.sh && nt8c build --custom-dir build/.stage/Custom` -> expect **`0 errors`**. A per-file `nt8c check` reports **FALSE** `CS0246`/`CS0234` for engine types (they only resolve when the whole assembly links) — **trust `dotnet build`/`dotnet test`/`nt8c build`, not the per-file hook.**
- **Break is FROZEN.** The `ControllerStateMachine` class body (Break setup, in live calibration) is never modified. Only additive changes (new enums/fields with defaults, new files) are allowed. Every prior Break test must stay green — that is the invariance proof.
- **Execution safety:** the reactive auto-fire path stays **hard-gated to Sim/Playback accounts** (existing guard `RadarChartTrader.cs:709`) — it must NEVER be able to auto-submit on a live account, even in "auto-aggressive" mode.
- **Canonical contract names (RECONCILED — use these exact names everywhere).** Defined once in cluster B, consumed by C/D/E/F:
  - `FireEvent.Kind : SetupKind { Break = 0, Reactive }`
  - `FireEvent.React : ReactKind { None = 0, Reject, Break }`
  - `ControllerInputs.TapeAccel : double` (signed; default `0.0`)
  - `ControllerInputs.DominantWallOutcome : Outcome` (reuses `Primitives.cs` `Outcome { Absorbed, Pulled, Consumed }`; `default(Outcome) == Absorbed` — **meaningless until the flag below**)
  - `ControllerInputs.DominantWallOutcomeValid : bool` (default `false`) — **gate every read of `DominantWallOutcome` on this flag**, or a warmup frame reads a phantom `Absorbed` and false-fires a fade (a real trade). Cluster C's `Reject` test (`DominantWallOutcome == Absorbed`) MUST be guarded by it.
  - _(Cluster C drafted these as `DominantWallOutcome`/`DominantWallOutcomeValid`; this document has been normalized to the `DominantWallOutcome*` names above. If you see the short form anywhere, it means these.)_
- **Reuse, don't duplicate logic:** the reject/break discriminator is the existing `EpisodeClassifier` `Absorbed`/`Consumed` outcome + `ConsumptionTracker.Read`. `ReactiveConfig` mirrors some `ControllerConfig` values by design but as **separate fields** so React recalibrates without disturbing Break.
- **Commits:** frequent, one per task, ending with the trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

## §0 — Contract reconciliation (AUTHORITATIVE — read before any task)

The six task groups below were drafted in parallel and each invented its own version of the shared contract. **Where a cluster block conflicts with a rule here, THIS SECTION WINS.** These are the only shared types; get them right once and the clusters compose.

- **R1 — the two enums are defined EXACTLY ONCE, in `Engine/Primitives.cs`** (the existing home of `Outcome`/`Side`/`NodeState`), immediately after `public enum Outcome { Absorbed, Pulled, Consumed }`:
  ```csharp
  public enum SetupKind { Break, Reactive }    // Break=0 => default(FireEvent) self-tags Break
  public enum ReactKind { None, Reject, Break } // None=0  => a Break fire carries no reactive verdict
  ```
  Add them here in **Task B only**. Cluster B's "enum in ControllerStateMachine.cs", cluster D's Step D-1.3 re-add, and cluster E's "SHARED hunk A in Primitives.cs" all refer to **this single definition** — never define `SetupKind`/`ReactKind` in more than one file (that is a `CS0101` duplicate / cross-namespace type-identity bug). `FireEvent` and the NT layer both see them via `namespace TradingRadar.Engine` / `using TradingRadar.Engine`.

- **R2 — `FireEvent` gains exactly two fields**, added once in **Task B** (`ControllerStateMachine.cs`): `public SetupKind Kind;` + `public ReactKind React;`. Cluster D **consumes** these; it does NOT re-add them (ignore D's Step D-1.3 FireEvent edit — Task B already did it).

- **R3 — wall-outcome input = PER-SIDE with valid flags (4 fields on `ControllerInputs`)**, added once in **Task B**:
  ```csharp
  public Outcome WallAboveOutcome; public bool WallAboveOutcomeValid;
  public Outcome WallBelowOutcome; public bool WallBelowOutcomeValid;
  ```
  This **supersedes** both B/C's single `DominantWallOutcome`(+`Valid`) and D's flagless `WallAboveOutcome`/`WallBelowOutcome`. Rationale: `ReactiveController` latches ONE side and must read THAT side's resolution; a single "dominant" outcome is ambiguous when walls sit on both sides, and the valid flag is mandatory because `default(Outcome) == Absorbed` (`Primitives.cs:8`) would phantom-fire a fade on a warmup frame. **Every `DominantWallOutcome` / `DominantWallOutcomeValid` in the blocks below maps to the latched side's pair** — Cluster C reads `WallAboveOutcome`/`WallAboveOutcomeValid` when it latched the ask wall above, `WallBelow*` when it latched the bid wall below.

- **R4 — `ControllerInputs.TapeAccel : double`** (signed) added once in **Task B**; **populated by Task D** each frame from `TapeAcceleration.Acceleration` (Task A's public surface: ctor `(double alpha)`, `bool Ready`, `double Acceleration`, `void Sample(double netRate, DateTime now)`).

- **R5 — `RadarTab` (Task D) owns the `CockpitVisual.SetFrame` call update** — Cluster F's note that "cluster E updated SetFrame" is a mislabel (RadarTab is D, RadarChartTrader is E). Cluster F's `nt8c build` goes green only AFTER Task B (enums) and Task D (new `SetFrame` arity + `TapeAccel`/`ReactBanner` on the frame) land.

- **R6 — de-dup task ownership:** Task B is the sole owner of the shared contract (R1–R4). Task D's real, non-overlapping work is: sample `TapeAcceleration`; populate the 4 outcome fields + `TapeAccel`; hold `_reactive` + `_activeSetup`; swap on `SetupChanged`; re-apply the selection at the instrument-switch and replay-reset rebuild sites; route the reactive fire to the panel. Skip any D step that merely re-adds B's contract.

---

## Build order & dependencies

Land clusters in this order (each is independently testable; later clusters consume earlier contracts):

1. **A — `TapeAcceleration` signal** (pure engine, standalone).
2. **B — additive `FireEvent`/`ControllerInputs` contract** (must precede C/D/E/F).
3. **C — `ReactiveController` state machine** (consumes B's contract; the core).
4. **D — `RadarTab` wiring** (consumes A's signal + B's inputs + C's controller; holds/swaps the active controller).
5. **E — `RadarChartTrader` dropdown + reactive fire routing** (consumes B's `SetupKind`/`ReactKind`).
6. **F — `CockpitVisual` reactive banner + TAPE ACCEL readout** (consumes B/C state).

**Preamble (once, before Task A1):** create the feature branch off the sub-repo's `main`:
```bash
cd "projects/Trading/Trading-radar" && git checkout -b feature/react-setup
```

## Ceilings carried from the spec (§12)

Do not let a good playback run overwrite these: **12t target is ambitious** vs a ~1.5-8t realistic reaction; **React enters late by design** so live fills are worse than playback shows; the new knobs (`AccelFloor`, `MaxWatchSeconds`, ...) are **uncalibrated** — React is an experimentation harness until the data gate (>=10 distinct days + >=15-20 real fires with logged realized-fill exits) is met. v1 exit uses the fixed ATM template bracket (not a wall-anchored stop).

---


---

# Task Group A — `TapeAcceleration` signal

Baseline confirmed: .NET SDK 10.0.300, `dotnet test` → **117 passing** (README's "111" is stale). Output format captured. I have everything I need. Here is the cluster plan.

---

# Cluster A — Engine signal `TapeAcceleration` (spec §5)

## 1. Shared conventions observed (for cross-cluster reconciliation)

- **(a) How tests are RUN:** `dotnet test` from the repo root `projects/Trading/Trading-radar/` (drives the `TradingRadar.slnx` → `Tests/TradingRadar.Tests.csproj`, which `ProjectReference`s the engine). Verified live: `Passed!  - Failed: 0, Passed: 117, Skipped: 0, Total: 117, Duration: 35 ms`. (README says 111; actual is **117** as of today — use 117 as the baseline count.) Scope to one cluster with `dotnet test --filter FullyQualifiedName~TapeAcceleration`.
- **(b) How the engine is COMPILED / validated:** `dotnet build` compiles the pure engine (`Engine/TradingRadar.Engine.csproj`, **`netstandard2.0`, `LangVersion 7.3`** — no C#8+ syntax) + the `net8.0` test project. Both csproj use SDK-style default globs, so **new `.cs` files under `Engine/` and `Tests/` are auto-included — no csproj edit needed.** The full add-on (engine + NT/WPF layer) is compile-checked with `bash build/stage-custom.sh && nt8c build --custom-dir build/.stage/Custom` — **not needed for this cluster** (TapeAcceleration is pure-engine, zero NT deps). Note the repo's documented gotcha: a per-file `nt8c check` throws **false** `CS0246`/`CS0234` for engine types (they only resolve when the whole assembly compiles together) — trust `dotnet build`/`dotnet test`, not the per-file hook, for this file.
- **(c) Exact test assertion idiom** (copied from `Tests/TapeSpeedTests.cs`): plain **xUnit** — a `public class <Name>Tests` with **no namespace**, `using TradingRadar.Engine; using Xunit;`, a `static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);` clock helper, and `[Fact]` methods using `Assert.True(...)` / `Assert.False(...)` / `Assert.NotEqual(...)`. No fixtures, no `[Theory]`, no test base class.

## 2. Files

| File | New/Modified | Notes |
|------|--------------|-------|
| `Engine/TapeAcceleration.cs` | **NEW** | The signed derivative signal. Pure, C# 7.3, no NT deps. Auto-globbed by `Engine/TradingRadar.Engine.csproj`. |
| `Tests/TapeAccelerationTests.cs` | **NEW** | 4 `[Fact]`s. Auto-globbed by `Tests/TradingRadar.Tests.csproj`. |

**Not touched by this cluster** (owned by the controller/wiring clusters, flagged for reconciliation): `ControllerStateMachine.cs`'s `ControllerInputs` struct gains `public double TapeAccel;` (spec §7) — **do NOT add it here**; a struct field no code in this cluster reads is dead weight. `RadarTab.cs` samples this class each frame and feeds the value into that field. Break logic is frozen and untouched.

## 3. Interfaces (consumes / produces) — real current signatures

**Consumes (upstream net-rate source — `Engine/BookMirror.cs`):**
- `BookMirror.cs:152` — `public struct TapeWindow { public int Prints; public long BuyVol; public long SellVol; }`
- `BookMirror.cs:154` — `public TapeWindow WindowSince(DateTime since)`

The wiring cluster (RadarTab) computes the scalar `netRate = (WindowSince(now-w).BuyVol − WindowSince(now-w).SellVol) / w.TotalSeconds` (BuyVol−SellVol **per second**, spec §5) and calls `Sample(netRate, now)` once per frame — the same frame where it already samples `TapeSpeed`. `TapeAcceleration` itself is agnostic to how `netRate` is produced; it differentiates whatever scalar it is fed.

**Mirror target (shape to copy — `Engine/TapeSpeed.cs`):**
- `TapeSpeed.cs:9` — `private const int MinSamples = 20;`
- `TapeSpeed.cs:14` — `public TapeSpeed(double alpha) { _alpha = alpha <= 0 || alpha >= 1 ? 0.1 : alpha; }`
- `TapeSpeed.cs:16` — `public bool Ready { get { return _n >= MinSamples; } }`
- `TapeSpeed.cs:17` — `public double ZScore { get; private set; }`
- `TapeSpeed.cs:19` — `public void Sample(double rate, DateTime now)` (with the `IsNaN/IsInfinity` poison guard at `:21` and the "first sample sets baseline, return" pattern at `:22`)

**Produces (this cluster's public surface):**
```csharp
public TapeAcceleration(double alpha)     // alpha clamped like TapeSpeed
public bool   Ready         { get; }      // _n >= MinSamples (20)
public double Acceleration  { get; }      // signed d(netRate)/dt, EWMA-smoothed; 0 until Ready
public void   Sample(double netRate, DateTime now)
```
Downstream reconciliation: the wiring cluster surfaces `Acceleration` into `ControllerInputs.TapeAccel` (signed); `ReactiveController` arms when `sign(TapeAccel)` points at the latched wall and `|TapeAccel| ≥ AccelFloor` (spec §5 arm test; `AccelFloor` is a `ReactiveConfig` concern — cluster C, not here).

---

## 4. TDD steps

### Step A1 — Write the failing test

**New file `Tests/TapeAccelerationTests.cs`** (full contents; idiom copied verbatim from `Tests/TapeSpeedTests.cs:1-7`):

```csharp
using System;
using TradingRadar.Engine;
using Xunit;

public class TapeAccelerationTests
{
    static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    // Rising aggressor net rate (buyers accelerating) => positive acceleration.
    [Fact]
    public void Acceleration_is_positive_when_net_rate_is_rising()
    {
        var ta = new TapeAcceleration(0.1);
        // netRate climbs +50 every 100 ms => a steady +500 / s derivative.
        for (int i = 0; i < 40; i++) ta.Sample(i * 50.0, T(i * 100));
        Assert.True(ta.Ready);
        Assert.True(ta.Acceleration > 0.0);
    }

    // Falling aggressor net rate (sellers accelerating) => negative acceleration.
    [Fact]
    public void Acceleration_is_negative_when_net_rate_is_falling()
    {
        var ta = new TapeAcceleration(0.1);
        // netRate drops -50 every 100 ms => a steady -500 / s derivative.
        for (int i = 0; i < 40; i++) ta.Sample(-i * 50.0, T(i * 100));
        Assert.True(ta.Ready);
        Assert.True(ta.Acceleration < 0.0);
    }

    // Constant net rate => zero frame-to-frame derivative => EWMA stays ~0.
    [Fact]
    public void Acceleration_is_near_zero_when_net_rate_is_flat()
    {
        var ta = new TapeAcceleration(0.1);
        for (int i = 0; i < 40; i++) ta.Sample(120.0, T(i * 100));
        Assert.True(ta.Ready);
        Assert.True(Math.Abs(ta.Acceleration) < 1e-6);
    }

    // Ready gate mirrors TapeSpeed: not Ready until MinSamples (20) samples have arrived.
    [Fact]
    public void Not_ready_before_MinSamples_then_ready_on_the_20th_sample()
    {
        var ta = new TapeAcceleration(0.1);
        for (int i = 0; i < 19; i++) ta.Sample(i * 10.0, T(i * 100)); // 19 samples
        Assert.False(ta.Ready);            // not yet warmed up
        ta.Sample(190.0, T(1900));         // 20th sample -> Ready flips true this call
        Assert.True(ta.Ready);
    }
}
```

### Step A2 — Run it, expect FAIL (compile / red)

Command:
```bash
cd projects/Trading/Trading-radar && dotnet test --filter FullyQualifiedName~TapeAcceleration
```
Expected output (build fails because the type does not exist yet — **no tests execute**):
```
Tests/TapeAccelerationTests.cs(11,22): error CS0246: The type or namespace name 'TapeAcceleration' could not be found (are you missing a using directive or an assembly reference?)
...
Build FAILED.
```
(The `error CS0246` repeats on every `new TapeAcceleration(0.1)` line — 4 occurrences. `dotnet test` returns non-zero; `Passed: 0`.)

### Step A3 — Minimal implementation

**New file `Engine/TapeAcceleration.cs`** (full contents; C# 7.3 / netstandard2.0-safe, mirrors `TapeSpeed`'s warmup+guard shape):

```csharp
using System;

namespace TradingRadar.Engine
{
    // Signed derivative d(netRate)/dt of the aggressor net rate (BuyVol - SellVol per second,
    // from BookMirror.WindowSince), EWMA-smoothed to reject single-frame noise. Pure: time via
    // timestamps only. Mirrors TapeSpeed's EWMA-with-warmup shape + MinSamples Ready gate (spec §5).
    // Sign: positive = buyers accelerating (arms a wall ABOVE); negative = sellers accelerating (arms BELOW).
    public class TapeAcceleration
    {
        private readonly double _alpha;     // EWMA weight for the newest derivative
        private const int MinSamples = 20;  // total samples before Ready (mirrors TapeSpeed)
        private double _accel;              // EWMA of the frame-to-frame d(netRate)/dt
        private double _prevRate;
        private DateTime _prevTime;
        private int _n;

        public TapeAcceleration(double alpha) { _alpha = alpha <= 0 || alpha >= 1 ? 0.1 : alpha; }

        public bool Ready { get { return _n >= MinSamples; } }

        // Gated to 0 until warmed up, exactly like TapeSpeed.ZScore — the EWMA still smooths
        // internally through the warmup so the first Ready read is already converged.
        public double Acceleration { get { return _n >= MinSamples ? _accel : 0.0; } }

        public void Sample(double netRate, DateTime now)
        {
            if (double.IsNaN(netRate) || double.IsInfinity(netRate)) return; // same poison guard as TapeSpeed
            if (_n == 0) { _prevRate = netRate; _prevTime = now; _n = 1; return; } // first sample: baseline only, no derivative yet
            double dt = (now - _prevTime).TotalSeconds;
            if (dt <= 0) return; // drop non-forward samples: no divide-by-zero / negative-dt derivative
            _n++;                // count only samples that actually produce a derivative (after the baseline)
            double deriv = (netRate - _prevRate) / dt;
            _accel = _alpha * deriv + (1 - _alpha) * _accel;
            _prevRate = netRate; _prevTime = now;
        }
    }
}
```

Behavior trace (why each test passes): first `Sample` sets `_prevRate`/`_prevTime`, `_n=1`, no derivative. Each later forward sample computes `(netRate−_prevRate)/dt` and EWMA-folds it. **Rising** ramp → every `deriv = +500` → `_accel → +500 > 0`. **Falling** ramp → `deriv = −500` → `_accel → −500 < 0`. **Flat** → `deriv = 0` → `_accel` stays exactly `0` (< 1e-6). **Ready:** after 19 samples `_n=19` (< 20) → `false`; the 20th makes `_n=20` → `true`. `dt <= 0` and NaN/Inf samples are dropped without advancing `_n` (deterministic, no clock).

### Step A4 — Run it, expect PASS (green)

Command:
```bash
cd projects/Trading/Trading-radar && dotnet test --filter FullyQualifiedName~TapeAcceleration
```
Expected output:
```
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: <ms> ms - TradingRadar.Tests.dll (net8.0)
```
Then confirm no regression across the whole suite:
```bash
dotnet test
```
Expected: `Passed!  - Failed: 0, Passed: 121, Skipped: 0, Total: 121` (117 baseline + 4 new).

### Step A5 — Commit

```bash
git add Engine/TapeAcceleration.cs Tests/TapeAccelerationTests.cs
git commit -m "$(cat <<'EOF'
feat(engine): TapeAcceleration signed derivative signal (React §5)

New pure-engine signal: EWMA-smoothed d(netRate)/dt of the aggressor
net rate (BuyVol-SellVol/s). Positive=buyers accelerating (wall above),
negative=sellers accelerating (wall below). Mirrors TapeSpeed's
EWMA-with-warmup + MinSamples Ready gate. 4 tests: rising>0, falling<0,
flat~0, not-Ready-before-MinSamples. Additive only; Break untouched.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

**Cross-cluster note for the reconciler:** this cluster ships the signal in isolation. The field `ControllerInputs.TapeAccel` (additive, default `0.0`) and the per-frame `Sample(...)` wiring in `RadarTab.cs` are intentionally **left to the controller/wiring cluster** — adding an unread struct field here would be dead code. `AccelFloor` lives in `ReactiveConfig` (cluster C), not `RadarConfig`.

---

# Task Group B — additive `FireEvent` / `ControllerInputs` contract

# Cluster B — Additive contract changes in `Engine/ControllerStateMachine.cs`

## (1) Shared conventions observed (for cross-cluster reconciliation)

- **(a) How tests are RUN:** xUnit via `dotnet test` from the repo root `projects/Trading/Trading-radar/`. There is **no** custom runner `Main` and **no** nt8c-for-tests. Current baseline verified this session: `Passed! - Failed: 0, Passed: 117, Skipped: 0, Total: 117`. The test project (`Tests/TradingRadar.Tests.csproj`) is SDK-style (`Microsoft.NET.Sdk`, `net8.0`, xunit 2.9.2) and **auto-globs `**/*.cs`**, so a new `Tests/*.cs` file needs **no** `.csproj` edit.
- **(b) How the engine/add-on is COMPILED/validated:** the engine (`Engine/TradingRadar.Engine.csproj`) targets **`netstandard2.0`, `LangVersion 7.3`** — additive code must be C# 7.3-valid (enums + struct fields are; no switch-expressions / target-typed `new` / records). `dotnet build` compiles the engine. The **full add-on** (engine + NT/WPF) is compile-checked outside the NinjaScript editor with the staged `nt8c` mirror: `bash build/stage-custom.sh` then `nt8c build --custom-dir build/.stage/Custom` (expect `0 errors`). Per README §"Build & test": a per-file `nt8c check` reports **false** `CS0246`/`CS0234` for engine types — trust the project-wide `nt8c build`. The `nt8c` PostToolUse hook auto-validates on each `.cs` edit.
- **(c) Test assertion idiom in `Tests/`:** plain **public class, no namespace**, `using TradingRadar.Engine; using Xunit;`. Methods are `[Fact]`. Assertions are `Assert.Equal(expected, actual)`, `Assert.True(cond)`, `Assert.False(cond)`, `Assert.NotEqual(...)`. Each file declares its own `static DateTime T(...)` helper and its own local book/inputs builders (see `Tests/ConsumptionTrackerTests.cs`, `Tests/TapeSpeedTests.cs`) — test files are self-contained, they do **not** share private helpers across classes.

## (2) Files

- **Modified:** `Engine/ControllerStateMachine.cs` — add two enums above `FireEvent` (before line 7), two fields inside `FireEvent` (lines 7-11), three fields inside `ControllerInputs` (lines 13-24). **Break logic (lines 95-387) untouched.**
- **New:** `Tests/FireEventContractTests.cs` — 3 `[Fact]`s proving defaults keep existing Break construction compiling and behaving unchanged. No `.csproj` change (auto-globbed).

## (3) Interfaces (consumes / produces) — real current signatures

Consumed as-is (quoted verbatim):

- `Engine/Primitives.cs:8` — `public enum Outcome { Absorbed, Pulled, Consumed }` → **`default(Outcome) == Outcome.Absorbed`** (the footgun this cluster guards).
- `Engine/ControllerStateMachine.cs:7-11` (FireEvent, current):
  ```csharp
  public struct FireEvent
  {
      public Side Side; public double WallPrice; public double EntryHint;
      public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time;
  }
  ```
- `Engine/ControllerStateMachine.cs:13-24` (ControllerInputs, current tail):
  ```csharp
  public long AdaptiveSignificance;
  ```
- `Engine/ControllerStateMachine.cs:300-302` — the **only** `FireEvent` construction (Break path), object-initializer, sets only the original 7 fields; it stays unchanged and will default the new fields to `Kind=Break(0)/React=None(0)`.
- `Engine/ControllerStateMachine.cs:130` — `public ControllerOutput Update(ControllerInputs inp)` never reads any new field.

Produced (verbatim names for clusters C/D to consume):

- `FireEvent.Kind` : `SetupKind` (`enum SetupKind { Break, Reactive }`, default `Break`).
- `FireEvent.React` : `ReactKind` (`enum ReactKind { None, Reject, Break }`, default `None`).
- `ControllerInputs.TapeAccel` : `double` (signed, default `0.0`).
- `ControllerInputs.DominantWallOutcome` : `Outcome` (reuses `Primitives.Outcome`; default `Absorbed` — **meaningless until the flag below**).
- `ControllerInputs.DominantWallOutcomeValid` : `bool` (default `false`). **Contract for cluster C/D: gate any read of `DominantWallOutcome` on this flag** (the reject test `Outcome == Absorbed` would phantom-fire on a warmup frame otherwise, because `default(Outcome) == Absorbed`). This is the "sane default" — it is the third additive field on top of the two the spec names, and it is the smallest thing that stops a phantom Reject fire.

---

## (4) TDD steps

### Step B1 — Write the failing test (real code)

Create **new file** `Tests/FireEventContractTests.cs`:

```csharp
using System;
using TradingRadar.Engine;
using Xunit;

// Cluster B: additive discriminators on FireEvent + reactive fields on ControllerInputs.
// Proves the defaults leave every existing Break-path construction compiling AND behaving unchanged.
public class FireEventContractTests
{
    static DateTime T(double s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    // Break-style FireEvent built exactly as StepCountdown does today (the original 7 fields only) must
    // report Kind=Break, React=None — so the frozen fire path is tagged correctly with zero logic change.
    [Fact]
    public void FireEvent_discriminators_default_to_break_and_none()
    {
        var f = new FireEvent {
            Side = Side.Ask, WallPrice = 100.25, EntryHint = 100.25,
            Fraction = 0.75, DeltaAtFire = 40, ZAtFire = 2.0, Time = T(1) };
        Assert.Equal(SetupKind.Break, f.Kind);
        Assert.Equal(ReactKind.None, f.React);
        Assert.Equal(SetupKind.Break, default(FireEvent).Kind);
        Assert.Equal(ReactKind.None, default(FireEvent).React);
    }

    // A Break-style ControllerInputs object-initializer sets none of the reactive fields — they must
    // default to 0 / invalid so no consumer mistakes default(Outcome)==Absorbed for a real resolution.
    [Fact]
    public void ControllerInputs_reactive_fields_default_sane()
    {
        var inp = new ControllerInputs {
            WallAbovePrice = 100.25, WallAboveCurrent = 120,
            Mid = 100.00, Now = T(1), Book = new BookMirror(0.25, TimeSpan.FromSeconds(30)) };
        Assert.Equal(0.0, inp.TapeAccel);
        Assert.False(inp.DominantWallOutcomeValid);                 // guard: Outcome meaningless until true
        Assert.Equal(default(ControllerInputs).TapeAccel, inp.TapeAccel);
        Assert.False(default(ControllerInputs).DominantWallOutcomeValid);
    }

    // The real, FROZEN Break state machine still fires on full confluence (mirrors the shipped
    // Fires_long_once_on_full_confluence_then_latches scenario) and the emitted FireEvent now carries
    // the additive tags Break/None with its original Side semantics intact — proof Break logic is untouched.
    [Fact]
    public void Break_setup_fire_is_tagged_break_none_without_touching_break_logic()
    {
        var m = new ControllerStateMachine(new ControllerConfig(), 0.25);
        m.Update(In(100.25, 120, 0, 0.0, 100.00, 1, new BookMirror(0.25, TimeSpan.FromSeconds(30)))); // arm, peak 120
        m.Update(In(100.25, 60, 20, 2.0, 100.00, 2, BookWithBuys(100.25, 60, 2)));                    // -> Countdown
        ControllerOutput o = default(ControllerOutput);
        for (int s = 3; s <= 8; s++)                                                                  // full confluence -> fire+latch
            o = m.Update(In(100.25, 30, 40, 2.0, 100.00, s, BookWithBuys(100.25, 90, s)));
        Assert.Equal(SideState.Fired, o.Long);        // Break machine latched a fire (frozen logic)
        Assert.Equal(Side.Ask, o.Fire.Side);          // ask wall above => long break; original semantics
        Assert.Equal(SetupKind.Break, o.Fire.Kind);   // now additively tagged
        Assert.Equal(ReactKind.None, o.Fire.React);
    }

    static ControllerInputs In(double wallAbovePrice, long wallAboveCur, long delta, double z,
                               double mid, double sec, BookMirror book)
        => new ControllerInputs {
            WallAbovePrice = wallAbovePrice, WallAboveCurrent = wallAboveCur,
            AggressorDelta = delta, TapeZScore = z, Mid = mid, Now = T(sec), Book = book };

    static BookMirror BookWithBuys(double wallPrice, long vol, int sec)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice - 0.5, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) }); // buy aggressor at the wall
        return b;
    }
}
```

### Step B2 — Run it, expect FAIL (exact command + expected output)

```bash
cd "projects/Trading/Trading-radar" && dotnet test
```

Expected: **build failure** (the new symbols don't exist yet), test run aborts:

```
Tests/FireEventContractTests.cs(...): error CS0246: The type or namespace name 'SetupKind' could not be found ...
Tests/FireEventContractTests.cs(...): error CS0246: The type or namespace name 'ReactKind' could not be found ...
Tests/FireEventContractTests.cs(...): error CS1061: 'FireEvent' does not contain a definition for 'Kind' ...
Tests/FireEventContractTests.cs(...): error CS1061: 'FireEvent' does not contain a definition for 'React' ...
Tests/FireEventContractTests.cs(...): error CS1061: 'ControllerInputs' does not contain a definition for 'TapeAccel' ...
Tests/FireEventContractTests.cs(...): error CS1061: 'ControllerInputs' does not contain a definition for 'DominantWallOutcomeValid' ...

Build FAILED.
```

### Step B3 — Minimal implementation (real code)

**Edit `Engine/ControllerStateMachine.cs`.**

Edit 1 — replace the current `FireEvent` block (lines 7-11) with the two enums + expanded struct:

*Before:*
```csharp
    public struct FireEvent
    {
        public Side Side; public double WallPrice; public double EntryHint;
        public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time;
    }
```

*After:*
```csharp
    // Spec §2/§13. Which setup produced this fire. Break = the frozen ControllerStateMachine (in
    // calibration); Reactive = the new ReactiveController (spec §6). Break is value 0 so every existing
    // Break fire — built by object-initializer in StepCountdown WITHOUT setting Kind — is tagged correctly.
    public enum SetupKind { Break, Reactive }

    // Spec §3/§6. For a Reactive fire, which branch resolved: Reject = wall held/absorbed -> fade,
    // Break = wall consumed -> follow. None is value 0 and the value carried by every Break-setup fire.
    public enum ReactKind { None, Reject, Break }

    public struct FireEvent
    {
        public Side Side; public double WallPrice; public double EntryHint;
        public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time;
        // Additive discriminators (spec §2/§13). default(FireEvent) and the StepCountdown object-initializer
        // (which sets neither) leave these at Kind=Break(0)/React=None(0) — Break logic is untouched.
        public SetupKind Kind; public ReactKind React;
    }
```

Edit 2 — replace the `ControllerInputs` tail (lines 22-24) to append the three reactive fields:

*Before:*
```csharp
        // bounded above by the max observed depth, so "never arm" is structurally unreachable.
        public long AdaptiveSignificance;
    }
```

*After:*
```csharp
        // bounded above by the max observed depth, so "never arm" is structurally unreachable.
        public long AdaptiveSignificance;
        // Spec §5/§7 (Reactive setup). Additive: the frozen Break ControllerStateMachine.Update never reads
        // these, and every existing ControllerInputs object-initializer omits them, so Break behaviour is
        // byte-for-byte unchanged.
        //
        // Signed tape acceleration = d(netRate)/dt (§5): +ve = buyers accelerating (arms a wall above),
        // -ve = sellers accelerating (arms a wall below). Fed by TapeAcceleration.cs via the NT layer.
        public double TapeAccel;
        // The latched/dominant wall's live episode outcome (§7), reusing Outcome from Primitives.cs.
        // WARNING (clusters C/D): default(Outcome) == Outcome.Absorbed, so this is meaningless until
        // DominantWallOutcomeValid is true — ReactiveController MUST gate its Reject test on the flag,
        // or a warming-up frame reads a phantom Absorbed and false-fires a fade.
        public Outcome DominantWallOutcome;
        public bool DominantWallOutcomeValid;
    }
```

No other edits — the Break state machine (`ControllerStateMachine` class, lines 95-387) and its lone `FireEvent { ... }` at 300-302 are unchanged; the new `FireEvent` fields default into that construction automatically.

### Step B4 — Run, expect PASS (exact commands)

```bash
cd "projects/Trading/Trading-radar" && dotnet test
```

Expected: `Passed!  - Failed:     0, Passed:   120, Skipped:     0, Total:   120` (117 prior + 3 new; every prior Break test still green, proving behavioral invariance).

Then confirm the full add-on still compiles with the new enums (the `nt8c` PostToolUse hook also fires on the edit, but run it explicitly):

```bash
cd "projects/Trading/Trading-radar" && bash build/stage-custom.sh && nt8c build --custom-dir build/.stage/Custom
```

Expected: `0 errors`.

### Step B5 — Commit (exact git message)

```bash
cd "projects/Trading/Trading-radar" && git add Engine/ControllerStateMachine.cs Tests/FireEventContractTests.cs && git commit -m "feat(reactive): additive FireEvent/ControllerInputs contract for React setup

Add SetupKind{Break,Reactive} + ReactKind{None,Reject,Break} discriminators to
FireEvent, and TapeAccel + DominantWallOutcome(+Valid flag) to ControllerInputs
(spec 2026-07-04-reactive-wall-setup-design §2/§5/§7/§13). All fields default so
existing Break construction compiles and behaves unchanged; Break logic untouched.
DominantWallOutcomeValid guards default(Outcome)==Absorbed for clusters C/D.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

*(Assumes the feature branch is already checked out — branching is handled at the plan/orchestrator level, not inside this cluster.)*

---

**Ponytail note:** the spec/task name two ControllerInputs fields; I ship a third — `DominantWallOutcomeValid`. It is not scaffolding: `default(Outcome) == Absorbed` (Primitives.cs:8), and cluster C/D's Reject test is literally `Outcome == Absorbed`, so without the flag a warmup/no-episode frame silently fires a phantom fade (a real trade). That is the money-path check the ladder says never to skip. If the orchestrator prefers exactly two fields, drop the bool and cluster C must special-case "no resolved episode yet" itself — larger diff, in the wrong place.

---

# Task Group C — `ReactiveController` state machine

```
Expected: `Passed! - Failed: 0, Passed: 135` (117 pre-existing Break/engine tests + 18 new React tests).
```

Also validate the NT8 single-assembly compile (the file ships into `Custom/` via `stage-custom.sh`):
```bash
bash build/stage-custom.sh && nt8c build --custom-dir build/.stage/Custom
```
Expected: `0 errors` (trust `nt8c build`, not the per-file hook's cross-file `CS0246` false positives).

**Step C5.5 — commit.**
```
feat(engine): ReactiveController cooldown blocks re-fire (spec §6)

Fired latches the fire result and blocks re-arm/re-fire for ReactCooldownSeconds,
then returns to Waiting; the same event can re-arm only after it elapses.
Completes the Waiting->Watching->Fired->Cooldown machine (spec §6, §13 matrix).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

---

## 5. §13 matrix coverage map (verification the plan is complete)

| Spec §13 requirement | Test(s) | Task |
|---|---|---|
| arm both signs → correct side | `Arms_watching_when_buyers_accelerate_into_wall_above`, `Arms_watching_when_sellers_accelerate_into_wall_below` | C1 |
| arm rejects (significance / accel floor / sign / delta / proximity) | 5 `Does_not_arm_*` facts | C1 |
| wall-above reject → SELL | `Reject_fires_sell_when_wall_above_absorbed` | C2 |
| wall-below reject → BUY | `Reject_fires_buy_when_wall_below_absorbed` | C2 |
| wall-above break → BUY | `Break_fires_buy_when_wall_above_consumed_and_trade_backed` | C3 |
| wall-below break → SELL | `Break_fires_sell_when_wall_below_consumed_and_trade_backed` | C3 |
| consumed-but-untraded does not follow | `Consumed_wall_without_trade_backing_does_not_fire` | C3 |
| timeout abandon | `Abandons_to_cooldown_on_watch_timeout_then_can_rearm` | C4 |
| wall-vanish abandon | `Abandons_to_cooldown_when_wall_vanishes` | C4 |
| wall-hop abandon | `Abandons_to_cooldown_when_wall_identity_hops` | C4 |
| away-drift abandon | `Abandons_to_cooldown_on_away_drift` | C4 |
| pulled → abstain | `Pulled_wall_abstains_no_fire` | C4 |
| cooldown blocks re-fire | `Cooldown_blocks_refire_then_rearms_after_it_elapses` | C5 |

`TapeAcceleration` sign/magnitude (spec §13 last bullet) is **out of scope for this cluster** — it lives in `Engine/TapeAcceleration.cs` + `Tests/TapeAccelerationTests.cs` (the cluster that produces `ControllerInputs.TapeAccel`). This cluster consumes `inp.TapeAccel` as a given.

## 6. Cross-cluster dependencies & reconciliation notes (for the integrator)

1. **Ordering:** Cluster C must land **after** cluster B. C's tests reference `SetupKind.Reactive`, `ReactKind.Reject`, `ReactKind.Break`, `FireEvent.Kind`, `FireEvent.React`, `ControllerInputs.TapeAccel`, `ControllerInputs.DominantWallOutcome`, and (my assumption) `ControllerInputs.DominantWallOutcomeValid`. If B is not merged, C1.2's RED is a *different* compile error than the one shown (extra `CS0117`/`CS1061` for the missing members) — still RED, but for the wrong reason. Sequence B→C.
2. **`DominantWallOutcomeValid` assumption:** if cluster B modeled the unresolved case as `Outcome? DominantWallOutcome` instead of a companion `bool`, apply the one-line-per-site swap noted in §3 (three call sites: the `if (inp.DominantWallOutcomeValid)` guard in `StepWatching`, and the `In(...)` helper's two params). No logic change.
3. **`FireEvent.Side` = direction (not wall side):** confirm the NT execution layer (cluster that owns `RadarChartTrader` routing) reads `FireEvent.Side` as `Ask`⇒BUY / `Bid`⇒SELL for React fires, and branches MKT (fade, `React==Reject`) vs LMT (follow, `React==Break`) off `FireEvent.React` per spec §8. `ReactiveOutput.WallSide` carries the wall's own side for the cockpit banner.
4. **Config duplication is intentional:** `ReactiveConfig.SignificanceBand`/`AwayTicks`/`BreakFireFrac`/`MinTradeBackedRatio` mirror `ControllerConfig` values by design (spec §11 "reuse"), but as **separate fields** so React can be recalibrated without disturbing Break's in-flight calibration (spec §2/§4). Do not fold them into a shared config.
5. **No retention re-baseline (deliberate):** unlike `ControllerStateMachine.StepCountdown` (`ControllerStateMachine.cs:239`), `ReactiveController` has no `inp.Now - _watchStart >= inp.Book.TradeRetention` guard — `MaxWatchSeconds` (~15s) is bounded well below typical `TradeRetention` (30–60s), so the watch always times out first. Flagged in case a future longer `MaxWatchSeconds` re-opens that gap.

## 7. Final deliverable file paths

- **NEW** `/home/javlo/Code Projects/main-project/projects/Trading/Trading-radar/Engine/ReactiveController.cs`
- **NEW** `/home/javlo/Code Projects/main-project/projects/Trading/Trading-radar/Tests/ReactiveControllerTests.cs`

---

# Task Group D — `RadarTab` wiring (build inputs, swap active controller)

# Cluster D — RadarTab wiring (spec §4, §7, §9, §10)

## 1. Shared conventions observed (for cross-cluster reconciliation)

- **How tests are RUN:** `dotnet test Tests/TradingRadar.Tests.csproj` (xUnit 2.9.2 on `net8.0`, referencing the pure `Engine` `netstandard2.0` project). Reports read `Passed! — Failed: 0, Passed: 46, …`. Single test: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- **How the add-on is COMPILED/validated:** `bash build/stage-custom.sh && nt8c build --custom-dir build/.stage/Custom`. `stage-custom.sh` mirrors `Engine/*.cs` + `NinjaTrader/*.cs` into `build/.stage/Custom/AddOns/LiquidityRadar/` and `NinjaTrader/strategy/*.cs` into `Strategies/`. Expected tail: `Compiled N files in ~1200 ms.` / `OK → …/NinjaTrader.Custom.dll (0 warnings)`. The per-file PostToolUse hook throws CS0246/CS0234 cross-assembly false positives while editing a single `.cs` (types like `Side`, `BookMirror`, `NodeState` aren't visible to single-file Roslyn) — the `--custom-dir` build is the authoritative gate.
- **Test assertion idiom (`Tests/*.cs`):** xUnit `[Fact]` + `Assert.Equal(expected, actual)` / `Assert.True` / `Assert.False` / `Assert.NotEqual`. `ControllerStateMachineTests.cs` helpers: `static DateTime T(double s)` (`:9`), `static BookMirror EmptyBook()` (`:11`), `static ControllerInputs In(...)` (`:13`), `static ControllerStateMachine Machine()` (`:20`), `static BookMirror BookWithBuys(price, vol, sec)` (`:109`). No custom runner `Main`, no nt8c-driven tests — engine only.
- **Conventions cluster D DEPENDS ON (parent to reconcile):**
  - **`FireEvent.Side` semantics:** `RadarChartTrader.OnSetupFire` maps `bool isBuy = f.Side == Side.Ask;` (`RadarChartTrader.cs:673`). So `ReactiveController` MUST emit `FireEvent.Side` as the intended **trade** side (`Ask`⇒buy/long, `Bid`⇒sell/short) per the §3 direction table — e.g. reject-of-wall-above (SELL) must emit `Side.Bid`. Then the existing routing yields the correct direction with **zero** cluster-C change to the direction math.
  - **`SetupKind` enum + `FireEvent.Kind`:** cluster D owns the additive default (Break=0 ⇒ existing fires carry `Kind=Break` free); **cluster B** adds `ReactKind` and sets `Kind=Reactive`. Declare the enum/field ONCE.
  - **`ControllerInputs.TapeAccel`** (double, signed) — cluster **A** owns this field (spec §5). **`WallAboveOutcome`/`WallBelowOutcome`** (`NodeState`) — spec §7 open question; cluster D populates them, whoever declares them. I include the additive hunk; drop the dup if A/B already declares.
  - **`ReactiveController` contract** (cluster B, assumed — see §3 below).
  - **`InstrumentPreset.Reactive`** (`ReactiveConfig`) — not present today (`InstrumentPresets.cs:9-12` has only `Controller/Pressure/MinAbsSize/Label`). If presets don't expose it, cluster D falls back to `new ReactiveConfig()`.

## 2. Files

**MODIFIED**
- `Engine/ControllerStateMachine.cs` — additive only: (a) `enum SetupKind { Break, Reactive }` + `public SetupKind Kind;` on `FireEvent` (struct at `:7-11`); (b) 3 fields on `ControllerInputs` (struct at `:13-24`): `TapeAccel`, `WallAboveOutcome`, `WallBelowOutcome`. **Break logic (StepLong/StepShort/StepCountdown/AdvanceArmedOrCountdown, the fire at `:300-303`) is untouched** — Break's `FireEvent` omits `Kind` so it defaults to `Break` (value 0).
- `Tests/ControllerStateMachineTests.cs` — one new `[Fact]` guarding the `Kind=Break` default (the sole pure-unit-testable slice of cluster D).
- `NinjaTrader/RadarTab.cs` — fields (`~:49-50`), ctor init (`~:123`, `~:220`), instrument-switch rebuild (`~:335`), replay-reset rebuild (`~:903`), new `OnSetupChanged` handler, and MaybeRunEngine edits (accel sample after `:584`, outcome capture in the `:557-567` loop, `cin` fields at `:586-595`, active-controller select at `:596`).

**CONSUMED (owned by other clusters — cluster D does not create):** `Engine/TapeAcceleration.cs` (A), `Engine/ReactiveController.cs` + `ReactiveConfig` (B), `RadarChartTrader.SetupChanged` event + reactive MKT/LMT routing (C), `CockpitVisual` reactive banner (C).

## 3. Interfaces (consumes / produces) — real signatures

**Consumes (real, current):**
- `ControllerStateMachine.Update(ControllerInputs inp) → ControllerOutput` — `Engine/ControllerStateMachine.cs:130`.
- `FireEvent { Side Side; double WallPrice; double EntryHint; double Fraction; long DeltaAtFire; double ZAtFire; DateTime Time; }` — `:7-11`.
- `ControllerInputs { double WallAbovePrice; long WallAboveCurrent; double WallBelowPrice; long WallBelowCurrent; long AggressorDelta; double TapeZScore; int TapeAlternations; double Mid; DateTime Now; BookMirror Book; long AdaptiveSignificance; }` — `:13-24`.
- `ControllerOutput { bool Chop; SideState Long; …; bool Fired; FireEvent Fire; double LongWallPrice; double ShortWallPrice; … }` — `:26-61`.
- `BookMirror.WindowSince(DateTime since) → TapeWindow { int Prints; long BuyVol; long SellVol; }` — `Engine/BookMirror.cs:152-165`. (`win1s` already computed at `RadarTab.cs:583`.)
- `TapeSpeed(double alpha)` / `.Sample(double rate, DateTime now)` / `.ZScore` — `Engine/TapeSpeed.cs:14,19,17`. Sampled at `RadarTab.cs:584`.
- `WallTracker.GetSnapshot(DateTime now) → IReadOnlyList<RadarNode>` — `Engine/WallTracker.cs:126`. `RadarNode.State` is `NodeState` — `Engine/Primitives.cs:44`.
- `NodeState { Live, Wall, Absorbed, Pulled, Consumed, Remembered }` — `Engine/Primitives.cs:7`. (LiquidityMemory maps resolved episode `Outcome`→these terminal states; `Absorbed`/`Consumed`/`Pulled` ARE the surfaced §7 outcomes.)
- `RadarChartTrader.OnSetupFire(FireEvent f)` — `RadarChartTrader.cs:668` (reads `f.Time` dedupe `:670`, `f.Side` `:673`, `f.WallPrice`/`f.Fraction`; `TryAutoFire` `:701`; Sim/Playback guard 1b `:709`). `RadarChartTrader.OnReplayReset()` — `:1026`.
- `InstrumentPresets.For(string) → InstrumentPreset { ControllerConfig Controller; PressureConfig Pressure; long MinAbsSize; string Label; }` — `Engine/InstrumentPresets.cs:9-27`.

**Assumed (cluster B/A/C — state assumption, reconcile):**
- `TapeAcceleration(double alpha)` / `.Sample(double netRate, DateTime now)` / signed `.Value` — mirrors `TapeSpeed`'s EWMA-with-warmup shape (spec §5). *Assumption:* the public read is `.Value`; if cluster A named it `.Accel`, swap the one read site.
- `ReactiveController(ReactiveConfig cfg, double tick)` with `ControllerOutput Update(ControllerInputs inp)` — same shape as `ControllerStateMachine.Update`. *Assumptions:* (i) sets `FireEvent.Kind = SetupKind.Reactive` and `FireEvent.Side` = the trade side (§3); (ii) leaves `ControllerOutput.Long/Short = Waiting` and `LongWallPrice/ShortWallPrice = 0`, so RadarTab's existing `_lastCtrl` identity-pin (`ResolveWallFeed`, `:579-580`) naturally feeds the **raw dominant** wall while React is active (React does its own wall-latching per §6). If B routes identity differently, reconcile the feed.
- `SetupKind { Break, Reactive }` (Break = 0). `FireEvent.Kind` default `Break`.

**Produces (cluster D):** each frame, a fully-built `ControllerInputs` carrying `TapeAccel` + `WallAboveOutcome`/`WallBelowOutcome`; a `ControllerOutput` from whichever controller `_activeSetup` selects; the latched `FireEvent` (with `Kind`) routed unchanged through `_pendingFire`→`OnSetupFire`.

---

## 4. TDD / implementation steps

### D-1 — Pure TDD: `FireEvent.Kind` defaults to `Break` (the routing contract cluster D depends on)

This is the **only** slice of cluster D exercisable without NT (RadarTab is `NTTabPage`, NT-bound, not referenced by the `net8.0` test project). Everything downstream routes by `f.Kind`, so this default is load-bearing.

**Step D-1.1 — write the failing test.** Add to `Tests/ControllerStateMachineTests.cs` (after `Fires_long_once_on_full_confluence_then_latches`, i.e. after `:161`). It reuses the exact firing sequence from that test (`:143-160`):

```csharp
    // Cluster D routing contract: a Break fire's FireEvent carries SetupKind.Break by DEFAULT
    // (the struct field is never set in StepCountdown's fire) — the panel routes MKT/LMT by f.Kind,
    // so Break must self-tag Break with zero change to the frozen Break logic.
    [Fact]
    public void Break_fire_carries_SetupKind_Break()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook())); // arm, peak 120
        m.Update(In(100.25, 60, 0, 0, 20, 2.0, 0, 100.00, 2, BookWithBuys(100.25, 60, 2))); // countdown
        ControllerOutput o = default(ControllerOutput);
        int fires = 0;
        for (int s = 3; s <= 8; s++)
        {
            var b = BookWithBuys(100.25, 90, s);
            o = m.Update(In(100.25, 30, 0, 0, delta: 40, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b));
            if (o.Fired) fires++;
        }
        Assert.Equal(1, fires);                       // sanity: the sequence actually fires
        Assert.Equal(SideState.Fired, o.Long);
        Assert.Equal(SetupKind.Break, o.Fire.Kind);   // the contract under test
    }
```

**Step D-1.2 — run it, expect FAIL.**
```
dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~Break_fire_carries_SetupKind_Break"
```
Expected: **build failure** (RED) — `error CS0246: The type or namespace name 'SetupKind' could not be found` and `error CS1061: 'FireEvent' does not contain a definition for 'Kind'`. (First red is a compile error: the symbol doesn't exist yet — honest TDD red for a new type.)

**Step D-1.3 — minimal implementation.** In `Engine/ControllerStateMachine.cs`, add the enum immediately above the `FireEvent` struct, and one field inside it. Replace the current `:5-11`:

```csharp
    public enum SideState { Waiting, Armed, Countdown, Fired, Cooldown }

    // Which setup produced a fire. Break = 0 so every existing Break fire self-tags Break with no
    // code change (StepCountdown's fire never sets Kind). Reactive fires are tagged by ReactiveController.
    public enum SetupKind { Break, Reactive }

    public struct FireEvent
    {
        public Side Side; public double WallPrice; public double EntryHint;
        public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time;
        public SetupKind Kind;   // additive; defaults to Break (0) for the frozen Break controller
    }
```

(No change to `StepCountdown`'s `fire = new FireEvent { … }` at `:300-303` — `Kind` is left at its `Break` default. Break logic untouched.)

**Step D-1.4 — run it, expect PASS.**
```
dotnet test Tests/TradingRadar.Tests.csproj
```
Expected (GREEN): `Passed! — Failed: 0, Passed: 47, Skipped: 0, Total: 47`. (The 46 existing + the new one; additive field breaks no existing `FireEvent`/`ControllerOutput` consumer.)

**Step D-1.5 — commit.**
```
git commit -am "feat(engine): FireEvent.Kind (SetupKind) additive default Break — reactive-setup routing contract

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### D-2 — Additive `ControllerInputs` fields (plumbing; no behavior to TDD, regression-guarded)

These fields carry the two new §7 inputs. Their *behavior* is tested in cluster A (TapeAccel) and cluster B (React reads the outcomes); for cluster D they are additive plumbing whose only obligation is "don't break Break." Guarded by the existing suite staying green + nt8c.

**Step D-2.1 — implementation.** In `Engine/ControllerStateMachine.cs`, extend the `ControllerInputs` struct — append after `AdaptiveSignificance;` (`:23`), inside the struct:

```csharp
        public long AdaptiveSignificance;
        // ---- Reactive setup (spec §5, §7). Ignored by the frozen Break controller. ----
        public double TapeAccel;            // signed net-aggressor acceleration (cluster A). +=buyers accel (wall above), -=sellers (below)
        public NodeState WallAboveOutcome;  // dominant ask-wall episode state; Live/Wall until resolved -> Absorbed/Consumed/Pulled
        public NodeState WallBelowOutcome;  // dominant bid-wall episode state
```

> Reconcile: if cluster A already declares `TapeAccel` and/or cluster B declares the outcome fields, drop the duplicate line(s) — keep the struct's field set unioned, declared once.

**Step D-2.2 — run existing suite, expect PASS (regression gate).**
```
dotnet test Tests/TradingRadar.Tests.csproj
```
Expected: `Passed! — Failed: 0, Passed: 47`. Additive struct fields default to `0` / `NodeState.Live` and are read by no existing test or Break code path — proves the additive change is inert for Break.

**Step D-2.3 — commit.**
```
git commit -am "feat(engine): ControllerInputs gains TapeAccel + WallAbove/BelowOutcome (reactive inputs, additive)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### D-3 — RadarTab wiring (NT-bound; validated by the nt8c gate, not unit tests)

`RadarTab.cs` derives from `NTTabPage` and references `NinjaTrader.*`, so it is not instantiable in the `net8.0` test project — **honestly, none of D-3 is unit-testable.** It is validated by the nt8c compile gate at each step and by the manual playback checklist (D-4). Each edit below is a minimal diff with exact anchors.

**Step D-3.1 — fields.** In `RadarTab.cs`, after `_controller` (`:50`) and `_lastCtrl` (`:54`), add the two new engine handles and the selection flag:

```csharp
        private ControllerStateMachine _controller;
        private ReactiveController _reactive;                    // spec §4: the swappable second setup
        private TapeAcceleration _accel;                         // spec §5: signed net-aggressor acceleration
        // The user's dropdown choice (RadarChartTrader.SetupChanged). NOT reset on instrument-switch /
        // replay-reset — that is the "re-apply the selection" contract (spec §4/§9); those sites rebuild
        // BOTH controllers but leave this field alone, so a switch can't silently revert to Break.
        private SetupKind _activeSetup = SetupKind.Break;
```

**Step D-3.2 — ctor init.** In the ctor, replace the `_tape`/`_controller` construction lines (`:123-124`):

```csharp
            _tape       = new TapeSpeed(0.1);
            _accel      = new TapeAcceleration(0.1);   // mirror TapeSpeed's alpha/warmup (spec §14 open q)
            _controller = new ControllerStateMachine(InstrumentPresets.For("ES").Controller, _cfg.TickSize);
            _reactive   = new ReactiveController(InstrumentPresets.For("ES").Reactive, _cfg.TickSize);
```

> If `InstrumentPreset` has no `Reactive` yet, use `new ReactiveConfig()` in all three ctor sites (here + D-3.4/D-3.5) and flag the presets cluster.

Then subscribe to the dropdown event right after `_chartTrader = new RadarChartTrader();` (`:220`):

```csharp
            _chartTrader = new RadarChartTrader();
            _chartTrader.SetupChanged += OnSetupChanged;   // spec §9: swap the active controller on dropdown change
```

**Step D-3.3 — the swap handler.** Add a new method next to `OnSelectorChanged` (after `:305`). Swaps under `_engineLock` (same lock the instrument-thread handlers take), rebuilding only the setup switched TO so Break's in-flight state is never disturbed by selecting React, and dropping `_lastCtrl` (its identity-pin belonged to the outgoing setup):

```csharp
        // Dropdown (RadarChartTrader.SetupChanged) picked a different setup. Swap under _engineLock —
        // the same lock the depth/trade handlers hold around _controller/_reactive.Update — so no engine
        // run straddles the swap. Rebuild only the setup being switched TO (fresh per-run state); leave
        // the other frozen (selecting React must not reset Break's in-calibration state machine). Reset
        // _lastCtrl: its identity-pinned wall feed (ResolveWallFeed) belonged to the outgoing setup.
        private void OnSetupChanged(SetupKind kind)
        {
            lock (_engineLock)
            {
                _activeSetup = kind;
                var preset = InstrumentPresets.For(_instrument != null ? _instrument.MasterInstrument.Name : "ES");
                if (kind == SetupKind.Reactive) _reactive   = new ReactiveController(preset.Reactive, _cfg.TickSize);
                else                            _controller = new ControllerStateMachine(preset.Controller, _cfg.TickSize);
                _lastCtrl = default(ControllerOutput);
            }
        }
```

**Step D-3.4 — re-apply at the instrument-switch reset site.** In the `Instrument` setter's `lock (_engineLock)` block, the existing rebuild is at `:333-338`. Add `_accel` + `_reactive` alongside `_tape`/`_controller` (right after `:335`/`:336`):

```csharp
                    _book       = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
                    _tracker    = new WallTracker(_cfg);
                    _tape       = new TapeSpeed(0.1);
                    _accel      = new TapeAcceleration(0.1);                              // new instrument = fresh accel EWMA
                    _controller = new ControllerStateMachine(preset.Controller, _cfg.TickSize);
                    _reactive   = new ReactiveController(preset.Reactive, _cfg.TickSize); // rebuild both; _activeSetup (the selection) is deliberately untouched — that IS the re-apply
                    _pressure   = new PressureModel(preset.Pressure);
```

(`_activeSetup` is NOT reset here — preserving it across the rebuild is exactly the "re-apply at the reset site" the spec §4 requires.)

**Step D-3.5 — re-apply at the replay-reset site.** In `HandleReplayReset`, the existing `_tape`/`_controller` rebuild is at `:903`/`:909`. Add `_accel` after `_tape` (`:903`) and `_reactive` after `_controller` (`:909`):

```csharp
            _tape       = new TapeSpeed(0.1);
            _accel      = new TapeAcceleration(0.1);   // rewound history must not inherit pre-rewind accel EWMA
```
```csharp
            _controller = new ControllerStateMachine(resetPreset.Controller, _cfg.TickSize);
            _reactive   = new ReactiveController(resetPreset.Reactive, _cfg.TickSize);   // rebuild both; _activeSetup preserved (re-apply)
            _lastCtrl   = default(ControllerOutput);
```

**Step D-3.6 — build both inputs each frame + select the controller.** Three edits inside `MaybeRunEngine`:

(a) **Dominant-wall outcome capture** — extend the existing loop (`:557-567`). Add two `NodeState` locals with the px/sz declarations (`:555-556`) and capture `.State` in the same two assignments (`:565-566`). Replace `:555-567`:

```csharp
            double wallAbovePx = 0, wallBelowPx = 0;
            long   wallAboveSz = 0, wallBelowSz = 0;
            // Reactive setup (spec §7): the dominant wall's episode state per side, fed to ReactiveController.
            // ponytail: piggybacks the SAME InWindow-dominant node Break uses — smallest diff (spec §14 open q).
            // Ceiling: a wall CONSUMED (best-quote crossed) can blink out of InWindow the same tick its state
            // flips to Consumed; if BREAK never fires in playback (D-4), cluster B must read the outcome at
            // its own latched price (incl. Remembered nodes) rather than this live InWindow feed.
            NodeState wallAboveState = NodeState.Live, wallBelowState = NodeState.Live;
            for (int i = 0; i < snapNodes.Count; i++)
            {
                RadarNode wn = snapNodes[i];
                if (!wn.InWindow) continue;
                if (wn.Price > pMid && wn.LastKnownSize > wallAboveSz) { wallAboveSz = wn.LastKnownSize; wallAbovePx = wn.Price; wallAboveState = wn.State; }
                if (wn.Price < pMid && wn.LastKnownSize > wallBelowSz) { wallBelowSz = wn.LastKnownSize; wallBelowPx = wn.Price; wallBelowState = wn.State; }
            }
```

(b) **Accel sample** — right after the tape sample (`:584`). `win1s` (`:583`) already holds `BuyVol`/`SellVol`; `netRate = BuyVol − SellVol` over the same 1s window IS the per-second net rate (spec §5):

```csharp
            var win1s = _book.WindowSince(now.AddSeconds(-1));
            _tape.Sample(win1s.Prints, now);
            // spec §5: signed net-aggressor acceleration. netRate = BuyVol - SellVol / 1s window; the
            // TapeAcceleration EWMA takes the frame-to-frame derivative. Sign arms the wall on that side.
            _accel.Sample(win1s.BuyVol - win1s.SellVol, now);
```

(c) **Feed the two new `cin` fields + select the active controller** — extend the `cin` initializer (`:586-595`) and replace the single controller call (`:596`):

```csharp
            ControllerInputs cin = new ControllerInputs
            {
                WallAbovePrice = ctrlWallAbovePx, WallAboveCurrent = ctrlWallAboveSz,
                WallBelowPrice = ctrlWallBelowPx, WallBelowCurrent = ctrlWallBelowSz,
                AggressorDelta = aggDelta15,
                TapeZScore = _tape.ZScore,
                TapeAlternations = _book.RecentAlternations(8),
                Mid = pMid, Now = now, Book = _book,
                AdaptiveSignificance = _depthBase.P85,
                TapeAccel = _accel.Value,                                  // spec §5 (cluster A read; reconcile name if not .Value)
                WallAboveOutcome = wallAboveState, WallBelowOutcome = wallBelowState   // spec §7
            };
            // spec §4: exactly ONE active controller is Update()-ed per frame; both consume the identical
            // cin bundle. When React is active _controller is not stepped (Break state frozen); when Break
            // is active _reactive is not stepped. The fire latch below is Kind-agnostic — see D-3.7.
            ControllerOutput cout = _activeSetup == SetupKind.Reactive
                ? _reactive.Update(cin)
                : _controller.Update(cin);
```

**Step D-3.7 — routing the reactive fire (spec §4→§8, item 3): NO new code, verified transparent.** The fire latch (`:601-613`) stores `cout.Fire`; the paint tick delivers it via `_chartTrader.OnSetupFire(pf)` (`:276`); `OnSetupFire(FireEvent f)` (`:668`) already receives the **whole** struct, now including `Kind`. So a reactive fire flows through `_pendingFire`→`OnSetupFire` **carrying `SetupKind` with zero cluster-D change** — the ponytail win: the existing latch/route path is already Kind-transparent. Cluster C reads `f.Kind`/`f.ReactKind` inside `OnSetupFire`/`TryAutoFire` to pick MKT (fade) vs LMT (break) and to go auto-aggressive; the Sim/Playback guard 1b (`:709`) and `TryAutoFire` guards (`:701`) are unchanged and still hard-gate execution. **Verify this claim by inspection during D-3.8** (grep that no other code between `cout.Fired` and `OnSetupFire` inspects or strips fields of `FireEvent`).

**Step D-3.8 — validate the whole add-on compiles (the authoritative gate).**
```
bash build/stage-custom.sh && nt8c build --custom-dir build/.stage/Custom
```
Expected: `Compiled N files in ~1200 ms.` / `OK → …/NinjaTrader.Custom.dll (0 warnings)`, **0 errors 0 warnings**. (Requires `Engine/TapeAcceleration.cs` and `Engine/ReactiveController.cs` from clusters A/B to be present in the tree so the stage picks them up; if drafting D standalone, expect CS0246 on `TapeAcceleration`/`ReactiveController`/`preset.Reactive` until A/B land — that is the cross-cluster dependency, not a cluster-D defect.) Re-run `dotnet test Tests/TradingRadar.Tests.csproj` — still `47 passed` (RadarTab isn't in the test project; engine additive changes stay green).

**Step D-3.9 — commit.**
```
git commit -am "feat(nt): RadarTab wires TapeAccel + wall-outcome inputs, swappable Break/React controller

- build TapeAccel (netRate derivative) + dominant-wall NodeState into ControllerInputs each frame
- hold _reactive + _accel + _activeSetup; swap under _engineLock on SetupChanged (rebuild only the
  setup switched to, Break state untouched); re-apply selection at instrument-switch + replay-reset
- reactive fire routes through the existing _pendingFire -> OnSetupFire path, carrying SetupKind

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### D-4 — Manual playback verification (what a unit test cannot cover)

Runs after clusters A/B/C are integrated. Precise checklist:

1. **Load / recompile.** Deploy the staged `NinjaTrader.Custom.dll` (or compile in the NT8 editor). Open the Liquidity Radar tab; confirm the Output line `[Radar] preset: ES (calibrated, rounds 1-8)`.
2. **Account gate.** Select the **Playback** account (Provider.Playback) in the Chart Trader; do NOT check ARM LIVE. Confirm the account combo shows a Sim/Playback account (guard 1b at `RadarChartTrader.cs:709` is what blocks live).
3. **Start Market Replay 06/29 ES** (the studied wall-reaction session). Let the tape warm ~30 s so `_accel.Ready`/`_tape.Ready` are true (>= 20 samples).
4. **Default = Break.** With the dropdown on **Break**, confirm the cockpit banner + fires behave exactly as today (regression: React wiring must not perturb Break). Note a Break fire's banner text is unchanged.
5. **Switch dropdown to React.** Confirm: no exception in Output; `_lastCtrl` reset means no stale Break arm bleeds in. Break's own state is now frozen (not stepped).
6. **Watch the banner sequence on a real wall event** (wall above, price near, tape accelerating up): banner goes **WATCHING WALL · waiting for resolution** (React `Watching`) → then either **REJECT · FADE** (wall held/absorbed → SELL) or **BREAK · FOLLOW** (wall consumed → BUY), per §3/§10. Confirm the **TAPE ACCEL** readout on the cockpit moves with the burst (positive into an up-burst).
7. **Direction check.** On the studied wall-above case: REJECT must produce a **SELL** ticket pre-stage (Chart Trader shows Sell), BREAK a **BUY**. (This validates the `FireEvent.Side` = trade-side convention from §1.)
8. **Auto-aggressive + hard gate.** With the Playback account selected, confirm a reactive fire **auto-submits regardless of the AUTO checkbox** (spec §8) but ONLY on Sim/Playback: switch to a non-Sim account and confirm the fire logs `guard_skip — account no longer Sim at fire time` and does NOT submit (`RadarChartTrader.cs:709-713`).
9. **Instrument-switch / replay-reset re-apply.** While React is selected: (a) switch instrument ES→NQ→ES and confirm the dropdown/behavior stays **React** (not silently reverted to Break) and the Output shows the NQ preset; (b) rewind the replay > 2 s and confirm React survives the reset (banner returns to WAITING, then re-arms) — proves D-3.4/D-3.5 re-apply.
10. **BREAK-blink ceiling watch (D-3.6a).** Specifically confirm **BREAK · FOLLOW** actually fires on the 06/29 consume. If it never does (the consumed wall blinked out of InWindow before its `NodeState` was captured), that is the documented ceiling → file the follow-up for cluster B to read the outcome at React's latched price. This is the one behavior the unit layer can't prove.

**Honest scope note:** cluster D adds no engine edge and its RadarTab portion has no unit-testable pure logic beyond D-1 (the `Kind` default) — the pure signal (TapeAcceleration, cluster A) and the pure state machine (ReactiveController, cluster B) carry their own xUnit suites. D-1 guards the routing contract; D-3 is compile-gated by nt8c and behavior-gated by the D-4 playback checklist.

---

# Task Group E — `RadarChartTrader` dropdown + reactive fire routing

I have everything needed. Here is the Cluster E plan.

---

# Cluster E — RadarChartTrader: setup dropdown + reactive fire routing (spec §8, §9)

## 1. Shared conventions observed (for cross-cluster reconciliation)

- **(a) How tests are RUN** — xUnit via the .NET SDK, targeting the test project explicitly (there is no `.sln`; a bare `dotnet test` at repo root can't pick a project):
  ```
  dotnet test Tests/TradingRadar.Tests.csproj --nologo
  ```
  I ran it just now: `Passed! - Failed: 0, Passed: 117, Skipped: 0, Total: 117, Duration: 32 ms`. (README says 111; it's 117 today.) `Tests/TradingRadar.Tests.csproj` references **only** `..\Engine\TradingRadar.Engine.csproj` (`:12-14`) — so **anything under `NinjaTrader/` is NOT compiled or testable by `dotnet test`** (it needs the NinjaTrader DLLs). Every unit-testable helper must live in the **Engine** assembly (`netstandard2.0`).
- **(b) How the add-on is COMPILED/validated** — the engine alone is `dotnet build`; the **full add-on** (Engine + `NinjaTrader/*.cs`) is compile-checked outside the NinjaScript editor with a staged mirror + `nt8c`:
  ```
  bash build/stage-custom.sh
  nt8c build --custom-dir build/.stage/Custom      # expect 0 errors
  ```
  Per README: a per-file `nt8c check` reports false `CS0246`/`CS0234` for engine types — trust the project-wide `nt8c build` (or the editor's F5). A `PostToolUse` hook also runs `nt8c` on each `.cs` edit under `projects/Trading/`.
- **(c) Test assertion idiom** — top-level public class **with no namespace**, `using TradingRadar.Engine; using Xunit;`, methods marked `[Fact]`, assertions `Assert.Equal(expected, actual)` / `Assert.True` / `Assert.False` / `Assert.InRange` / `Assert.NotEqual`. Static local factory helpers (e.g. `static DateTime T(int ms) => ...` in `TapeSpeedTests.cs:7`, `static ControllerInputs In(...)` in `Phase0GuardTests.cs:64`) are the house style for building inputs.

**Cross-cluster dependency I rely on (please reconcile):** Cluster E consumes two additive members on `FireEvent` — `SetupKind Kind` and `ReactKind ReactKind` — plus the enums `SetupKind { Break, Reactive }` and `ReactKind { None, Reject, Break }`. Spec §13 assigns that additive change to the **Engine/ReactiveController cluster** (it *sets* those fields when the reactive machine fires; E only *reads* them). To keep this cluster runnable in isolation, Step 3 includes the additive enum + field hunk **marked SHARED** — land it in whichever cluster runs first; the other skips it. It is additive with zero-value defaults (`SetupKind.Break == 0`, `ReactKind.None == 0`), so `default(FireEvent)` and every existing Break fire route **exactly as today** — Break logic is untouched.

---

## 2. Files

**New (owned by Cluster E):**
- `Engine/ReactiveExecution.cs` — pure routing helper `ReactiveExecution.Route(FireEvent) → FireRouting` (the one unit-testable piece: side + MKT-vs-LMT + auto-aggressive). No NT deps.
- `Tests/ReactiveExecutionTests.cs` — 8 `[Fact]`s covering the §3 direction table + §8 entry mechanics + Break-unchanged regression.

**SHARED additive (reconcile with Engine cluster — add only if not already present):**
- `Engine/Primitives.cs` — add enums `SetupKind`, `ReactKind` (after `Outcome` at `:8`).
- `Engine/ControllerStateMachine.cs` — add `public SetupKind Kind; public ReactKind ReactKind;` to `FireEvent` (`:7-11`).

**Modified (owned by Cluster E) — `NinjaTrader/RadarChartTrader.cs`:**
- `:161-162` region — add `_setupCombo` field, `_setupSelectionHandler` field, `public event Action<SetupKind> SetupChanged;`.
- `:274` region (constructor) — style + populate `_setupCombo`, wire the stored handler.
- `:432` — drop `Grid.SetColumnSpan(autoGroup, 2)`; place `_setupCombo` at col1/row3.
- `:668-692` — `OnSetupFire`: route via `ReactiveExecution.Route`, branch LMT pre-stage vs marketable-reject.
- `:701-795` — `TryAutoFire`: signature `bool isBuy → FireRouting route`; guard 1a auto-aggressive bypass; guard 4 LMT-only; final submit MKT(reject)/LMT(break).

**NOT touched by E (other clusters own them):** `RadarTab.cs` (Cluster D subscribes `SetupChanged`, swaps `_activeController`, re-applies on instrument/replay reset), `ReactiveController.cs`, `TapeAcceleration.cs`, `CockpitVisual.cs`.

---

## 3. Interfaces (consumes / produces) — real signatures

**Consumes (current, verified):**
- `FireEvent` — `Engine/ControllerStateMachine.cs:7-11`:
  ```csharp
  public struct FireEvent
  {
      public Side Side; public double WallPrice; public double EntryHint;
      public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time;
  }
  ```
  (E adds `Kind`/`ReactKind` — SHARED, Step 3.)
- `Side` — `Engine/Primitives.cs:5`: `public enum Side { Bid, Ask }`.
- RadarChartTrader submit path E reuses unchanged (`NinjaTrader/RadarChartTrader.cs`):
  - `private void SubmitLimit(bool isBuy, bool isAuto = false)` — `:1543`
  - `private void SubmitRaw(OrderAction action, OrderType type, int qty, double limitPrice, string tag, bool isEntry = false, bool isAuto = false)` — `:1689` (isEntry attaches the picked ATM; isAuto seeds `_autoOrder` + exit-leg telemetry; aborts a naked auto entry when `atm==null` at `:1708`)
  - `private int GetQty()` — `:~1418`; `private double EffectiveTick()` — `:1505`; `private static double RoundToTick(double, double)` — `:1511`
  - AUTO hard gates in `TryAutoFire` that MUST remain: guard 1b Sim/Playback `:709`, guard 1c hours `:719`, `ValidateForSubmit()` `:731/:1490`, guard 2 busy/one-trade `:739`, guard 3 daily cap `:758`, guard 5 ATM-at-fire `:786`.
  - Existing account combo styling copied for parity: `:161`, `:267-274`.
  - `ApplyPendingSetupUi()` `:880-889`, `ClearPendingSetup()` `:898-903` (LMT pre-stage UI).

**Produces:**
- `Engine/ReactiveExecution.cs`:
  ```csharp
  public struct FireRouting { public bool IsBuy; public bool Marketable; public bool AutoAggressive; }
  public static FireRouting ReactiveExecution.Route(FireEvent f);
  ```
- `RadarChartTrader.SetupChanged` — `public event Action<SetupKind> SetupChanged;` (Cluster D subscribes; §9).

---

## 4. TDD steps

### Step E1.1 — write the failing test (real code)

**New file `Tests/ReactiveExecutionTests.cs`:**
```csharp
using TradingRadar.Engine;
using Xunit;

// Cluster E — reactive fire routing (spec 2026-07-04 §3 direction table + §8 entry mechanics).
// Pure, so it runs under `dotnet test` even though the RadarChartTrader WPF wiring that CONSUMES
// it cannot (the test project references only Engine).
public class ReactiveExecutionTests
{
    static FireEvent Fire(SetupKind kind, ReactKind react, Side side)
        => new FireEvent { Kind = kind, ReactKind = react, Side = side };

    // ---- Break setup is frozen: routing must be identical to the shipped behavior ----

    [Fact]
    public void Break_AskWall_BuysWithLmt_NotAutoAggressive()
    {
        var r = ReactiveExecution.Route(Fire(SetupKind.Break, ReactKind.None, Side.Ask));
        Assert.True(r.IsBuy);              // ask wall above consumed by buys -> long (unchanged)
        Assert.False(r.Marketable);        // wall-anchored LMT
        Assert.False(r.AutoAggressive);    // still honors the AUTO checkbox
    }

    [Fact]
    public void Break_BidWall_SellsWithLmt()
    {
        var r = ReactiveExecution.Route(Fire(SetupKind.Break, ReactKind.None, Side.Bid));
        Assert.False(r.IsBuy);
        Assert.False(r.Marketable);
        Assert.False(r.AutoAggressive);
    }

    [Fact]
    public void Default_FireEvent_RoutesAsBreak()
    {
        // Additive-safety: a zero-value FireEvent (Kind==Break, ReactKind==None) must route as the
        // frozen Break setup, so no pre-existing Break fire changes behavior.
        var r = ReactiveExecution.Route(default(FireEvent));
        Assert.False(r.Marketable);
        Assert.False(r.AutoAggressive);
    }

    // ---- Reactive REJECT (wall holds) => FADE, marketable, auto-aggressive (§3, §8) ----

    [Fact]
    public void Reactive_Reject_WallAbove_Sells_Marketable_AutoAggressive()
    {
        var r = ReactiveExecution.Route(Fire(SetupKind.Reactive, ReactKind.Reject, Side.Ask));
        Assert.False(r.IsBuy);             // wall above holds -> price bounced down -> SELL
        Assert.True(r.Marketable);         // fade chases the printed rejection -> MKT
        Assert.True(r.AutoAggressive);     // fires even with AUTO unchecked (still Sim-gated downstream)
    }

    [Fact]
    public void Reactive_Reject_WallBelow_Buys_Marketable()
    {
        var r = ReactiveExecution.Route(Fire(SetupKind.Reactive, ReactKind.Reject, Side.Bid));
        Assert.True(r.IsBuy);              // wall below holds -> price bounced up -> BUY
        Assert.True(r.Marketable);
        Assert.True(r.AutoAggressive);
    }

    // ---- Reactive BREAK (wall consumed) => FOLLOW, wall-anchored LMT, auto-aggressive (§3, §8) ----

    [Fact]
    public void Reactive_Break_WallAbove_Buys_Lmt_AutoAggressive()
    {
        var r = ReactiveExecution.Route(Fire(SetupKind.Reactive, ReactKind.Break, Side.Ask));
        Assert.True(r.IsBuy);              // wall above eaten -> BUY the break
        Assert.False(r.Marketable);        // follow rests the wall-anchored LMT, like the Break setup
        Assert.True(r.AutoAggressive);
    }

    [Fact]
    public void Reactive_Break_WallBelow_Sells_Lmt()
    {
        var r = ReactiveExecution.Route(Fire(SetupKind.Reactive, ReactKind.Break, Side.Bid));
        Assert.False(r.IsBuy);             // wall below eaten -> SELL the break
        Assert.False(r.Marketable);
        Assert.True(r.AutoAggressive);
    }
}
```

### Step E1.2 — run it, expect FAIL (build red)

```
dotnet test Tests/TradingRadar.Tests.csproj --nologo
```
Expected: the test project **fails to build** (the red), test run does not execute:
```
Build FAILED.
Tests/ReactiveExecutionTests.cs(...): error CS0246: The type or namespace name 'SetupKind' could not be found ...
Tests/ReactiveExecutionTests.cs(...): error CS0246: The type or namespace name 'ReactKind' could not be found ...
Tests/ReactiveExecutionTests.cs(...): error CS0103: The name 'ReactiveExecution' does not exist in the current context
Tests/ReactiveExecutionTests.cs(...): error CS1061: 'FireEvent' does not contain a definition for 'Kind' ...
```

### Step E1.3 — minimal implementation (real code)

**SHARED hunk A** — add to `Engine/Primitives.cs` immediately after `public enum Outcome { Absorbed, Pulled, Consumed }` (`:8`):
```csharp
    // Setup selector (spec 2026-07-04 §9). Break = 0 so default(FireEvent) routes as the frozen Break
    // setup — additive-safe, existing Break fires unchanged.
    public enum SetupKind { Break, Reactive }
    // Reactive dual-outcome branch (spec §6). None = 0 (a Break fire carries no reactive verdict).
    public enum ReactKind { None, Reject, Break }
```

**SHARED hunk B** — in `Engine/ControllerStateMachine.cs`, extend `FireEvent` (`:7-11`) with two additive fields (Break logic untouched — the frozen machine simply never sets them, so they stay at their zero defaults):
```csharp
    public struct FireEvent
    {
        public Side Side; public double WallPrice; public double EntryHint;
        public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time;
        // Reactive setup tags (spec §8). Additive; default (Break/None) keeps every existing Break fire
        // routing byte-identical. Set by ReactiveController (Engine cluster); read by ReactiveExecution.
        public SetupKind Kind; public ReactKind ReactKind;
    }
```

**Owned file — new `Engine/ReactiveExecution.cs`:**
```csharp
namespace TradingRadar.Engine
{
    // Pure routing decision for a fire event: which side, MKT-vs-LMT, and whether it is auto-aggressive
    // (may fire even with the AUTO checkbox unchecked). No NinjaTrader deps => unit-testable via
    // `dotnet test`. Spec 2026-07-04-reactive-wall-setup-design.md §3 (direction table) + §8 (entry).
    public struct FireRouting
    {
        public bool IsBuy;
        public bool Marketable;     // true => MKT entry (chase the printed move); false => wall-anchored LMT
        public bool AutoAggressive; // true => eligible to auto-submit even if the AUTO box is unchecked
    }

    public static class ReactiveExecution
    {
        public static FireRouting Route(FireEvent f)
        {
            FireRouting r;
            if (f.Kind == SetupKind.Reactive)
            {
                if (f.ReactKind == ReactKind.Reject)
                {
                    // Wall holds -> fade. Wall above (Ask) => SELL, wall below (Bid) => BUY. Marketable
                    // (the rejection already printed — chase it, don't rest a limit price has left). §8.
                    r.IsBuy = f.Side == Side.Bid;
                    r.Marketable = true;
                }
                else
                {
                    // Wall consumed -> follow. Wall above (Ask) => BUY, wall below (Bid) => SELL.
                    // Wall-anchored LMT, identical to the Break setup's pre-staged limit. §8.
                    r.IsBuy = f.Side == Side.Ask;
                    r.Marketable = false;
                }
                r.AutoAggressive = true;   // §8 auto-aggressive (all hard gates still apply in TryAutoFire)
            }
            else
            {
                // Break setup (frozen): unchanged routing — isBuy = Side==Ask, wall-anchored LMT, and it
                // honors the AUTO checkbox (not auto-aggressive).
                r.IsBuy = f.Side == Side.Ask;
                r.Marketable = false;
                r.AutoAggressive = false;
            }
            return r;
        }
    }
}
```

### Step E1.4 — run, expect PASS

```
dotnet test Tests/TradingRadar.Tests.csproj --nologo
```
Expected: `Passed! - Failed: 0, Passed: 125, Skipped: 0, Total: 125` (117 existing + 8 new).

### Step E1.5 — commit

```
git add Engine/Primitives.cs Engine/ControllerStateMachine.cs Engine/ReactiveExecution.cs Tests/ReactiveExecutionTests.cs
git commit -m "$(cat <<'EOF'
feat(engine): ReactiveExecution.Route — reactive fire MKT/LMT + auto-aggressive routing (spec §3/§8)

Pure, NT-free routing helper the Chart Trader consumes: reject->fade->MKT,
break->follow->LMT, both auto-aggressive; Break setup routing unchanged.
Adds additive SetupKind/ReactKind + FireEvent.Kind/ReactKind (zero-default = Break).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Step E2-E4 — RadarChartTrader WPF wiring (manual verification; not `dotnet test`-able)

> The dropdown + fire-routing wiring lives in the NT layer, which the test project does not compile. The **load-bearing decision** (side/MKT-LMT/auto-aggressive) is already unit-tested via `ReactiveExecution.Route` above; the code below is thin plumbing that calls it, verified by `nt8c` compile + a Market Replay behavioral pass.

**Edit E2a — fields.** In `NinjaTrader/RadarChartTrader.cs`, after the `_accountSelectionHandler` field (`:162`), add:
```csharp
        // Setup selector (spec §9) — "Break" (frozen) vs "React" (reactive dual-outcome). Styled like
        // _accountCombo. Stored handler for symmetry with the account combo's detach pattern.
        private readonly ComboBox _setupCombo = new ComboBox { Margin = new Thickness(0, 5, 10, 0) };
        private SelectionChangedEventHandler _setupSelectionHandler;
        // Raised on SelectionChanged; RadarTab subscribes and swaps _activeController under _engineLock (§9).
        public event Action<SetupKind> SetupChanged;
```

**Edit E2b — constructor wiring.** After `_accountCombo.SelectionChanged += _accountSelectionHandler;` (`:274`), add (styling mirrors `:267-270`):
```csharp
            _setupCombo.Background = Ink;
            _setupCombo.Foreground = TextCol;
            _setupCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
            _setupCombo.Items.Add("Break");
            _setupCombo.Items.Add("React");
            _setupCombo.SelectedIndex = 0;   // Break = default (the frozen, in-calibration setup)
            _setupSelectionHandler = (o, e) =>
            {
                SetupKind kind = _setupCombo.SelectedIndex == 1 ? SetupKind.Reactive : SetupKind.Break;
                Action<SetupKind> h = SetupChanged;
                if (h != null) h(kind);
            };
            _setupCombo.SelectionChanged += _setupSelectionHandler;
```

**Edit E2c — layout.** Replace line `:432`:
```csharp
                Grid.SetColumn(autoGroup, 0); Grid.SetRow(autoGroup, 3); Grid.SetColumnSpan(autoGroup, 2);
                grid.Children.Add(autoGroup);
```
with:
```csharp
                Grid.SetColumn(autoGroup, 0); Grid.SetRow(autoGroup, 3);   // col0 only now — _setupCombo fills col1/row3
                grid.Children.Add(autoGroup);
                // Setup selector fills the empty col1/row3 slot beside AUTO (spec §9), mirroring how
                // capGroup fills col1/row1.
                Grid.SetColumn(_setupCombo, 1); Grid.SetRow(_setupCombo, 3);
                grid.Children.Add(_setupCombo);
```

**Edit E3 — `OnSetupFire` routing.** Replace the body of `OnSetupFire` (`:668-692`, from `bool isBuy = ...` through `TryAutoFire(f, isBuy, tick);`) with:
```csharp
            FireRouting route = ReactiveExecution.Route(f);   // §3 side + §8 MKT-vs-LMT + auto-aggressive
            double tick = EffectiveTick();

            if (route.Marketable)
            {
                // Reactive REJECT (fade) — a marketable chase; no resting LMT to pre-stage. Drop any stale
                // LMT pre-stage so its glow/indicator don't misrepresent this fire; the AUTO path submits MKT.
                ClearPendingSetup();
                LogAuto("prestage", route.IsBuy ? "Buy" : "Sell", _lastPrice, _lastPrice,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "REACT reject — marketable fade, wall {0:0.00}.", f.WallPrice));
            }
            else
            {
                // Break setup OR reactive BREAK (follow) — wall-anchored LMT pre-stage, exactly as the
                // shipped Break path: join-near-inside the wall, never past it (would be marketable on the
                // near side). isBuy comes from Route (identical to the old f.Side==Side.Ask for Break).
                double tol = tick * SetupJoinToleranceTicks;
                double price;
                if (_lastPrice <= 0)
                    price = f.WallPrice;   // stale/no inside context yet — human re-checks the ticket before clicking anyway
                else if (route.IsBuy)
                    price = Math.Min(_lastPrice + tick + tol, f.WallPrice);    // never above the wall (would be marketable below it)
                else
                    price = Math.Max(_lastPrice - tick - tol, f.WallPrice);    // never below the wall
                _pendingSetup = new PendingSetup { IsBuy = route.IsBuy, Price = RoundToTick(price, tick) };
                ApplyPendingSetupUi();
                LogAuto("prestage", route.IsBuy ? "Buy" : "Sell", _pendingSetup.Price, _lastPrice,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "wall {0:0.00}, fraction {1:0.00}.", f.WallPrice, f.Fraction));
            }
            TryAutoFire(f, route, tick);   // AUTO path — guards 1-5, auto-aggressive honored inside
```

**Edit E4a — `TryAutoFire` signature.** Change `:701`:
```csharp
        private void TryAutoFire(FireEvent f, bool isBuy, double tick)
```
to:
```csharp
        private void TryAutoFire(FireEvent f, FireRouting route, double tick)
```
and the first line `:703`:
```csharp
            string side = route.IsBuy ? "Buy" : "Sell";
```

**Edit E4b — guard 1a auto-aggressive bypass.** Replace `:704-708`:
```csharp
            if (!_autoArmed)                                              // guard 1a — was a silent return; the #1 blind spot of round-3
            {
                DiagAuto("guard_skip", side, f.WallPrice, _lastPrice, "AUTO skip — not armed at fire time.");
                return;
            }
```
with:
```csharp
            // guard 1a: the AUTO checkbox must be armed — UNLESS this is an auto-aggressive reactive fire,
            // which fires regardless of the checkbox (spec §8). Every hard gate below (Sim 1b, hours 1c,
            // busy 2, daily cap 3, stale 4, ATM 5) still applies unchanged; auto-aggressive waives ONLY
            // the manual arm toggle.
            if (!_autoArmed && !route.AutoAggressive)                     // guard 1a
            {
                DiagAuto("guard_skip", side, f.WallPrice, _lastPrice, "AUTO skip — not armed at fire time.");
                return;
            }
```

**Edit E4c — guard 4 (LMT-only).** Replace `:771-780`:
```csharp
            bool marketableThrough = isBuy
                ? _pendingSetup.Price - _lastPrice > AutoStaleTicks * tick
                : _lastPrice - _pendingSetup.Price > AutoStaleTicks * tick;
            if (_lastPrice <= 0 || marketableThrough)
            {
                DiagAuto("guard_skip", side, _pendingSetup.Price, _lastPrice,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "AUTO skip — stale at fire (setup {0:0.00} vs mid {1:0.00}).", _pendingSetup.Price, _lastPrice));
                return;
            }
```
with:
```csharp
            // guard 4: degenerate/stale-quote guard. The no-context (_lastPrice<=0) case trips for BOTH
            // branches; the marketable-through check is LMT-only — a reactive REJECT is INTENTIONALLY
            // marketable (no resting pre-stage price to judge), so it must not be skipped for being so.
            if (_lastPrice <= 0)
            {
                DiagAuto("guard_skip", side, _lastPrice, _lastPrice, "AUTO skip — no price context at fire.");
                return;
            }
            if (!route.Marketable)
            {
                bool marketableThrough = route.IsBuy
                    ? _pendingSetup.Price - _lastPrice > AutoStaleTicks * tick
                    : _lastPrice - _pendingSetup.Price > AutoStaleTicks * tick;
                if (marketableThrough)
                {
                    DiagAuto("guard_skip", side, _pendingSetup.Price, _lastPrice,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "AUTO skip — stale at fire (setup {0:0.00} vs mid {1:0.00}).", _pendingSetup.Price, _lastPrice));
                    return;
                }
            }
```

**Edit E4d — final submit MKT/LMT.** Replace `:793-794`:
```csharp
            _autoFireCount++;                                            // all clear — fire
            SubmitLimit(isBuy, isAuto: true);   // the "submit" log row (order id/qty/price) is written from SubmitRaw once the order exists
```
with:
```csharp
            _autoFireCount++;                                            // all clear — fire
            if (route.Marketable)
                // Reactive REJECT — marketable chase. isEntry:true attaches the picked ATM bracket (same
                // as SubmitLimit); isAuto:true seeds _autoOrder so exit-leg telemetry + the one-trade
                // busy guard still see it. guard 5 + SubmitRaw's naked-auto abort keep it ATM-gated.
                SubmitRaw(route.IsBuy ? OrderAction.Buy : OrderAction.Sell, OrderType.Market, GetQty(), 0,
                    route.IsBuy ? "Buy" : "Sell", isEntry: true, isAuto: true);
            else
                SubmitLimit(route.IsBuy, isAuto: true);   // Break setup + reactive BREAK — wall-anchored LMT
```

### Step E2-E4 verify — compile check (the automated gate)

```
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```
Expected: `0 errors`. (The `nt8c` PostToolUse hook also runs on each edited `.cs`.)

### Step E2-E4 verify — Market Replay behavioral pass (manual, honest — WPF/NT8 has no automatable harness here)

1. Open the Liquidity Radar window on a **Market Replay (Playback)** account with an ATM template picked.
2. Confirm the **Break / React** dropdown renders in col1/row3 beside AUTO, styled like the account combo (dark `Ink` bg, `TextCol` fg, stretched).
3. Select **React**; confirm `SetupChanged(SetupKind.Reactive)` fires (RadarTab swaps `_activeController` — Cluster D).
4. With the **AUTO checkbox UNCHECKED**, play into a wall-reaction event:
   - a **REJECT** submits a **MKT** entry (chase) despite AUTO being unchecked;
   - a **BREAK** submits the **wall-anchored LMT**;
   - verify in `Documents/NinjaTrader 8/LiquidityRadar/lr-auto-*.csv` the `prestage` + `submit` rows, and that hours (guard 1c), busy/one-trade (guard 2), daily cap (guard 3), and ATM-required (guard 5) still gate.
5. Switch back to **Break**: behavior is byte-identical to today (LMT pre-stage, AUTO honors the checkbox, no auto-aggressive).
6. **Live-account guard:** select a non-Sim account → confirm NO reactive fire submits (`DiagAuto: "AUTO skip — account no longer Sim at fire time."`, guard 1b `:709`).

### Step E2-E4 — commit

```
git add NinjaTrader/RadarChartTrader.cs
git commit -m "$(cat <<'EOF'
feat(nt): setup Break/React dropdown + reactive fire routing in Chart Trader (spec §8/§9)

_setupCombo in col1/row3 exposes SetupChanged(SetupKind). OnSetupFire/TryAutoFire
route via ReactiveExecution.Route: reject->MKT fade, break->wall-anchored LMT, both
auto-aggressive (fire even with AUTO unchecked) — all hard gates (Sim/Playback,
hours, busy, daily cap, ATM) unchanged. Break setup path untouched.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

**Ponytail notes:** the reject/MKT branch reuses `SubmitRaw(..., isEntry:true, isAuto:true)` rather than a new market-order path — the ATM attach + `_autoOrder`/exit-leg bookkeeping already ride on it. Skipped a `ComboBoxItem`-with-tag model for `_setupCombo` (string items + one index→enum line — trivial, no test). Skipped touching `RadarTab.cs` (Cluster D owns the `SetupChanged` subscription + controller swap). The only thing genuinely worth a unit test — the side/MKT-LMT/auto-aggressive decision — is extracted into `ReactiveExecution.Route` and fully covered.

---

# Task Group F — `CockpitVisual` reactive banner + TAPE ACCEL readout

I have everything I need. Here is the cluster plan.

---

# Cluster F — `CockpitVisual.cs`: reactive banner + TAPE ACCEL readout (spec §10)

## (1) Shared conventions observed (for cross-cluster reconciliation)

**(a) How tests are RUN** — xUnit 2.9.2 on net8.0 via the CLI. Whole suite: `dotnet test Tests/TradingRadar.Tests.csproj` (README claims 111 green; 117 `[Fact]` attributes currently in `Tests/`). Single cluster: `dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~CockpitBannerTests"`. There is **no custom runner / Main / nt8c involvement for unit tests** — plain `dotnet test`.

**(b) How the add-on is COMPILED / validated** — the NT/WPF layer is **not in any csproj**; `Tests/TradingRadar.Tests.csproj` references **only** `Engine/TradingRadar.Engine.csproj`. So `CockpitVisual.cs` (WPF `DrawingContext`) **cannot be reached by `dotnet test`** — its only gate is the staged NinjaScript compile:
```bash
bash build/stage-custom.sh                          # copies Engine/*.cs + NinjaTrader/*.cs into build/.stage/Custom
nt8c build --custom-dir build/.stage/Custom         # expect 0 errors, 0 warnings
```
Per-file `nt8c check` emits **false** `CS0246`/`CS0234` on engine types (they resolve only when all files compile as one assembly) — trust the whole-project staged build (`build/deploy-notes.md:28`, `README.md:195`). Because `stage-custom.sh` copies `Engine/*.cs`, a **new `Engine/CockpitBanner.cs` is automatically included in the nt8c gate**. This is why the pure banner-text mapping is extracted into the engine: it is the only way to unit-test it (matches the repo's "WPF `OnRender` has NO unit-test cycle — `nt8c` compile + Market Replay only", `docs/plans/2026-06-29-anchored-ladder.md:3`).

**(c) Exact assertion idiom** — plain `public class XxxTests` with **no namespace**, `using TradingRadar.Engine; using Xunit;`. **Only `[Fact]` is used anywhere (117×); zero `[Theory]`/`[InlineData]`.** Assertions: `Assert.Equal(expected, actual)`, `Assert.True/False(cond, "message")`, `Assert.InRange`, `Assert.NotEqual`, inline `out var` (`Tests/BookMirrorTests.cs:31`). Static helpers like `T(int ms)` build fixtures. I follow this exactly (multi-assert `[Fact]` + a private static assert helper, no `[Theory]`).

## (2) Files

| File | New/Mod | What |
|---|---|---|
| `Engine/CockpitBanner.cs` | **NEW** | pure `enum BannerKind`, `enum ReactBanner`, `static class CockpitBanner { BreakLabel; ReactLabel }` — the `(kind,state)→(text,sub)` mapping |
| `Tests/CockpitBannerTests.cs` | **NEW** | two `[Fact]`s proving every Break + Reactive state → exact strings |
| `NinjaTrader/CockpitVisual.cs` | **MOD** | remove private `enum BannerKind` (`:78`), drop `Text` from `BannerState` (`:79`) + its 4 set-sites in `ComputeBanner` (`:123-140`), route `DrawBannerCard` (`:142-164`) by `SetupKind`, add `DrawTapeAccelCard`, `SetFrame` (`:54`) gains `tapeAccel`/`setup`/`react`, `OnRender` (`:60-75`) calls the new card, add `AccelMaxPerSec2` const (`:45`) |

## (3) Interfaces (consumes / produces)

**Consumed — real current signatures:**
- `NinjaTrader/CockpitVisual.cs:54` — `public void SetFrame(ControllerOutput ctrl, double buyPerSec, double sellPerSec, double tapeZ, double bookSkew)` (the signature I extend).
- `Engine/ControllerStateMachine.cs:26-61` — `struct ControllerOutput { bool Chop; SideState Long; double LongFraction; …; SideState Short; …; bool Fired; FireEvent Fire; … }` (unchanged; `ComputeBanner` still reads `.Chop/.Long/.Short/.Fire`).
- `Engine/ControllerStateMachine.cs:5` — `enum SideState { Waiting, Armed, Countdown, Fired, Cooldown }`.
- `Engine/ControllerStateMachine.cs:7-11` — `struct FireEvent { Side Side; double WallPrice; … }` — **note: no `Kind`/`ReactKind` today.**
- Caller: `NinjaTrader/RadarTab.cs:258` — `_cockpit.SetFrame(f.Ctrl, f.BuyPerSec, f.SellPerSec, f.TapeZ, f.BookSkew);` (**cluster E** must extend to `…, f.TapeAccel, activeSetup, reactBanner`).

**Consumed — DOES NOT EXIST YET (produced by sibling clusters; reconcile):**
- `SetupKind { Break, Reactive }` — added to the engine by the **FireEvent/ControllerStateMachine cluster** (spec §13: "`FireEvent` gains `SetupKind Kind` + `ReactKind`"). `CockpitVisual.DrawBannerCard`/`SetFrame` reference `SetupKind`. **The `nt8c` gate for the render wiring (Step 7) fails until that cluster lands `SetupKind`.** My pure-method test (Steps 1-5) does **not** touch `SetupKind` and is fully independent.
- `ReactKind { Reject, Break }` — same cluster. Cluster F does **not** consume it directly; **RadarTab (cluster E)** collapses the reactive controller's `(state, ReactKind)` → my `ReactBanner` and passes it into `SetFrame`.

**Produced (cluster F owns these):**
- `Engine/CockpitBanner.cs`: `enum BannerKind { SetupLong, SetupShort, Countdown, Armed, Chop, Waiting }` (promoted out of `CockpitVisual`'s private enum so it is testable), `enum ReactBanner { Waiting, Watching, FiredReject, FiredBreak }`, and `static class CockpitBanner` with `void BreakLabel(BannerKind, out string text, out string sub)` + `void ReactLabel(ReactBanner, out string text, out string sub)`.

> Reconciliation note for the orchestrator: `SetupKind` (FireEvent cluster) and my `BannerKind`/`ReactBanner` all end up in `TradingRadar.Engine`. No name collision (`SetupKind` ≠ `BannerKind` ≠ `ReactBanner`). If the FireEvent cluster also wants a banner enum, it must not redefine these two.

---

## (4) TDD steps

### Step 1 — write the failing test (`Tests/CockpitBannerTests.cs`, NEW file)

```csharp
using TradingRadar.Engine;
using Xunit;

// Cluster F: the cockpit banner text/sub mapping is pure (no WPF/DrawingContext), lifted into the
// engine so it is reachable from this test project (which references only the engine — CockpitVisual
// is never compiled by dotnet test). Proves every Break and Reactive banner state maps to the exact
// string the render layer draws (spec §10). No [Theory] — the suite uses [Fact] exclusively.
public class CockpitBannerTests
{
    [Fact]
    public void BreakLabel_maps_every_state_to_its_shipped_strings()
    {
        AssertBreak(BannerKind.SetupLong,  "SETUP LONG",  "● LATCHED · resets on break cross / fail");
        AssertBreak(BannerKind.SetupShort, "SETUP SHORT", "● LATCHED · resets on break cross / fail");
        AssertBreak(BannerKind.Countdown,  "COUNTDOWN",   "wall being eaten by trades");
        AssertBreak(BannerKind.Armed,      "ARMED",       "wall intact — waiting for erosion");
        AssertBreak(BannerKind.Chop,       "CHOP",        "slow, alternating tape");
        AssertBreak(BannerKind.Waiting,    "WAITING",     "no dominant wall armed");
    }

    [Fact]
    public void ReactLabel_maps_every_state_to_the_spec_10_strings()
    {
        AssertReact(ReactBanner.Watching,    "WATCHING WALL", "waiting for resolution");
        AssertReact(ReactBanner.FiredReject, "REJECT · FADE",  "● LATCHED · wall held — fading");
        AssertReact(ReactBanner.FiredBreak,  "BREAK · FOLLOW", "● LATCHED · wall consumed — following");
        AssertReact(ReactBanner.Waiting,     "WAITING",        "no wall watched");
    }

    static void AssertBreak(BannerKind kind, string text, string sub)
    {
        CockpitBanner.BreakLabel(kind, out var t, out var s);
        Assert.Equal(text, t);
        Assert.Equal(sub, s);
    }

    static void AssertReact(ReactBanner state, string text, string sub)
    {
        CockpitBanner.ReactLabel(state, out var t, out var s);
        Assert.Equal(text, t);
        Assert.Equal(sub, s);
    }
}
```

The Break strings are copied **verbatim** from the shipped cockpit (`CockpitVisual.cs` `ComputeBanner` text + `DrawBannerCard` sub switch, `:152-161`) so this test also pins that the extraction changed no user-visible Break string. The Reactive strings are spec §10's mandated labels.

### Step 2 — run it, expect FAIL (compile error: the types don't exist yet)

Command:
```bash
dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~CockpitBannerTests"
```
Expected output — **build failure**, no tests executed (RED for a not-yet-created type):
```
Tests/CockpitBannerTests.cs(...): error CS0103: The name 'CockpitBanner' does not exist in the current context
Tests/CockpitBannerTests.cs(...): error CS0246: The type or namespace name 'BannerKind' could not be found ...
Tests/CockpitBannerTests.cs(...): error CS0246: The type or namespace name 'ReactBanner' could not be found ...
Build FAILED.
```
(Non-zero exit. `BannerKind` currently exists only as a **private** enum inside `CockpitVisual` — invisible to the test project — so it does not resolve.)

### Step 3 — minimal implementation (`Engine/CockpitBanner.cs`, NEW file)

```csharp
using System;

namespace TradingRadar.Engine
{
    // Pure banner text/sub mapping for the CockpitVisual banner card, keyed by active setup + state.
    // Lives in the engine (not CockpitVisual) so it is unit-testable without a WPF/NT reference — the
    // test project references only the engine. CockpitVisual keeps the DrawingContext + color/glow; the
    // STRINGS live here, one source of truth, covered by CockpitBannerTests.
    // ponytail: kind selects the method, state is the arg (two thin mappers) instead of one int-tagged
    // method — type-safe, and no System.ValueTuple in the NT-facing layer (C# 7.3 / netstandard2.0).

    // Break setup (Consumption-Break controller) banner states. Promoted out of CockpitVisual (was a
    // private enum there) so the mapping is reachable from the test project.
    public enum BannerKind { SetupLong, SetupShort, Countdown, Armed, Chop, Waiting }

    // Reactive setup (React) banner projection: the ReactiveController's Waiting/Watching/Fired(+ReactKind)
    // collapsed by RadarTab to the 4 labels the banner shows (spec §10). Cooldown reads as Waiting.
    public enum ReactBanner { Waiting, Watching, FiredReject, FiredBreak }

    public static class CockpitBanner
    {
        // Break banner text/sub — the exact strings the shipped cockpit renders today, moved verbatim
        // out of ComputeBanner's inline text (:133-139) + DrawBannerCard's sub switch (:152-161).
        public static void BreakLabel(BannerKind kind, out string text, out string sub)
        {
            switch (kind)
            {
                case BannerKind.SetupLong:  text = "SETUP LONG";  sub = "● LATCHED · resets on break cross / fail"; break;
                case BannerKind.SetupShort: text = "SETUP SHORT"; sub = "● LATCHED · resets on break cross / fail"; break;
                case BannerKind.Countdown:  text = "COUNTDOWN";   sub = "wall being eaten by trades"; break;
                case BannerKind.Armed:      text = "ARMED";       sub = "wall intact — waiting for erosion"; break;
                case BannerKind.Chop:       text = "CHOP";        sub = "slow, alternating tape"; break;
                default:                    text = "WAITING";     sub = "no dominant wall armed"; break;   // BannerKind.Waiting
            }
        }

        // Reactive banner text/sub (spec §10). Waiting is the default (also covers the controller's Cooldown).
        public static void ReactLabel(ReactBanner state, out string text, out string sub)
        {
            switch (state)
            {
                case ReactBanner.Watching:    text = "WATCHING WALL"; sub = "waiting for resolution"; break;
                case ReactBanner.FiredReject: text = "REJECT · FADE";  sub = "● LATCHED · wall held — fading"; break;
                case ReactBanner.FiredBreak:  text = "BREAK · FOLLOW"; sub = "● LATCHED · wall consumed — following"; break;
                default:                      text = "WAITING";        sub = "no wall watched"; break;   // ReactBanner.Waiting
            }
        }
    }
}
```

### Step 4 — run, expect PASS

```bash
dotnet test Tests/TradingRadar.Tests.csproj --filter "FullyQualifiedName~CockpitBannerTests"
```
Expected:
```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2
```

### Step 5 — commit

```
git add Engine/CockpitBanner.cs Tests/CockpitBannerTests.cs
git commit -m "feat(cockpit): pure banner text/sub mapping (Break + Reactive) with tests

Extract the CockpitVisual banner (kind,state)->(text,sub) mapping into a pure
engine helper so it is unit-testable (the test project references only the
Engine, never the WPF layer). Adds Reactive banner strings per reactive-wall
spec §10; Break strings copied verbatim from the shipped cockpit.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Step 6 — wire the render layer (`NinjaTrader/CockpitVisual.cs`, MODIFY) — no unit test (WPF `OnRender`), nt8c-gated

**6a. Fields + `SetFrame` (replace `:48-58`):**
```csharp
        private ControllerOutput _ctrl;
        private double _buyPerSec, _sellPerSec, _tapeZ, _bookSkew, _tapeAccel;
        private SetupKind _setup;
        private ReactBanner _react;
        private bool _has;

        public CockpitVisual() { ClipToBounds = true; }

        // tapeAccel = signed net-aggressor acceleration (spec §5); setup = active dropdown selection
        // (spec §9); react = the ReactiveController's banner projection — RadarTab collapses
        // Watching / Fired+ReactKind / idle -> ReactBanner. Break-setup frames pass react = ReactBanner.Waiting.
        public void SetFrame(ControllerOutput ctrl, double buyPerSec, double sellPerSec, double tapeZ, double bookSkew,
                             double tapeAccel, SetupKind setup, ReactBanner react)
        {
            _ctrl = ctrl; _buyPerSec = buyPerSec; _sellPerSec = sellPerSec; _tapeZ = tapeZ; _bookSkew = bookSkew;
            _tapeAccel = tapeAccel; _setup = setup; _react = react;
            _has = true; InvalidateVisual();
        }
```

**6b. Add the accel display cap next to the other display constants (after `:45` `ZGaugeMax`):**
```csharp
        private const double AccelMaxPerSec2 = 20.0; // display-scale cap for the signed accel bar (net contracts/s²);
                                                     // deterministic per frame, retune from Rec like VelMaxPerSec.
```

**6c. `OnRender` — draw the accel card after TAPE Z-SCORE (replace `:71-73`):**
```csharp
            y = DrawVelocityCard(dc, x, y, cw, h, pad, dpi);
            y = DrawTapeZCard(dc, x, y, cw, h, pad, dpi);
            y = DrawTapeAccelCard(dc, x, y, cw, h, pad, dpi);
            y = DrawChopCard(dc, x, y, cw, h, pad, dpi);
```

**6d. Drop the private enum + `Text` field (replace `:78-79`):**
```csharp
        // BannerKind now lives in the engine (TradingRadar.Engine.CockpitBanner) so its text/sub mapping
        // is unit-testable; the struct keeps only the draw-side state (color + glow).
        private struct BannerState { public BannerKind Kind; public Color Color; public double Glow; }
```

**6e. `ComputeBanner` — stop setting `Text` inline (Break Kind/Color/Glow only; replace `:123-140`):**
```csharp
        private static BannerState ComputeBanner(ControllerOutput c)
        {
            ActiveSide a = ResolveActiveSide(c);
            if (!a.Any)
            {
                if (c.Chop) return new BannerState { Kind = BannerKind.Chop, Color = SlateC, Glow = 0.35 };
                return new BannerState { Kind = BannerKind.Waiting, Color = SlateC, Glow = 0.15 };
            }
            Color col = a.IsLong ? Bid : Ask;
            if (c.Long == SideState.Fired || c.Short == SideState.Fired)
                return new BannerState {
                    Kind = a.IsLong ? BannerKind.SetupLong : BannerKind.SetupShort, Color = col, Glow = 1.0 };
            SideState st = a.IsLong ? c.Long : c.Short;
            return new BannerState {
                Kind = st == SideState.Countdown ? BannerKind.Countdown : BannerKind.Armed, Color = col, Glow = 0.45 };
        }
```

**6f. `DrawBannerCard` — route by `SetupKind`, pull both strings from the pure method (replace `:142-164`):**
```csharp
        private double DrawBannerCard(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double bh = 72;
            if (y + bh > h - pad) return y;

            Color col; double glow; string text, sub;
            if (_setup == SetupKind.Reactive)
            {
                CockpitBanner.ReactLabel(_react, out text, out sub);
                // ponytail: draw-only color/glow (not spec-mandated). REJECT vs BREAK is distinguished by
                // TEXT, not color — the fade/follow *direction* is ambiguous from the banner alone (it
                // depends on which side the wall is on, spec §3). A colored anchor is a later Replay call.
                switch (_react)
                {
                    case ReactBanner.Watching:    col = AmberC; glow = 0.5; break;
                    case ReactBanner.FiredReject:
                    case ReactBanner.FiredBreak:  col = AmberC; glow = 1.0; break;
                    default:                      col = SlateC; glow = 0.15; break;   // ReactBanner.Waiting
                }
            }
            else
            {
                BannerState bs = ComputeBanner(_ctrl);
                col = bs.Color; glow = bs.Glow;
                CockpitBanner.BreakLabel(bs.Kind, out text, out sub);
            }

            Brush cardBg = new SolidColorBrush(Color.FromArgb((byte)(22 + 70 * glow), col.R, col.G, col.B));
            dc.DrawRoundedRectangle(cardBg, CardLn, new Rect(x, y, cw, bh), 8, 8);
            Left(dc, "CONTROLLER", x + 12, y + 10, 11, Muted, dpi);
            Brush txtBr = new SolidColorBrush(Color.FromArgb((byte)(150 + 105 * glow), col.R, col.G, col.B));
            dc.DrawText(FT(text, 26, txtBr, dpi), new Point(x + 14, y + 28));
            Left(dc, sub, x + 14, y + 56, 10, Muted2, dpi);
            return y + bh + Gap;
        }
```

**6g. `DrawTapeAccelCard` — new card, mirrors the small TAPE Z-SCORE card (`:212-225`) but signed/bipolar like the velocity bar (`:201-207`). Insert immediately after `DrawTapeZCard` ends (~`:225`):**
```csharp
        // ---- 4b. Tape acceleration — signed derivative of the net aggressor rate (spec §5). Renders
        //          every frame regardless of setup (spec §10). Positive = buyers accelerating (green,
        //          right of center), negative = sellers accelerating (coral, left). ----
        private double DrawTapeAccelCard(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double ch = 52;
            if (y + ch > h - pad) return y;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, ch), 8, 8);
            Left(dc, "TAPE ACCEL", x + 12, y + 10, 11, Muted, dpi);
            double a = _tapeAccel;
            bool up = a >= 0;
            string txt = (up ? "+" : "-") + Math.Abs(a).ToString("0.0", CultureInfo.InvariantCulture) + "/s\u00b2";
            RightM(dc, txt, x + cw - 12, y + 6, 15, up ? BidTxt : AskTxt, dpi);
            double bx = x + 12, by = y + 32, bw = cw - 24, bh2 = 10, cx = bx + bw / 2.0, half = bw / 2.0;
            dc.DrawRoundedRectangle(Track, CardLn, new Rect(bx, by, bw, bh2), 5, 5);
            double mag = half * Clamp01(Math.Abs(a) / AccelMaxPerSec2);
            if (mag > 1)
            {
                Color tint = up ? Bid : Ask;
                double segX = up ? cx : cx - mag;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, tint.R, tint.G, tint.B)), null, new Rect(segX, by, mag, bh2), 5, 5);
            }
            dc.DrawRectangle(Divider, null, new Rect(cx - 0.5, by - 2, 1, bh2 + 4));
            return y + ch + Gap;
        }
```
(`"/s\u00b2"` = `/s²`; escaped so the byte is unambiguous. Uses the existing `FTM`/`RightM` mono path like the other numeric readouts.)

### Step 7 — validate the render wiring (nt8c staged build — the only gate for WPF render code)

```bash
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```
Expected: **0 errors, 0 warnings** across the staged Custom set. Ignore any per-file `CS0246`/`CS0234`.

> **Cross-cluster gate — must be sequenced.** `CockpitVisual.cs` now references `SetupKind` (FireEvent cluster) and its `SetFrame` arity changed, so the caller `RadarTab.cs:258` (cluster E) must pass the three new args in the same staged build. Therefore this `nt8c build` goes green only **after**: (i) the FireEvent cluster has added `enum SetupKind { Break, Reactive }` to the engine, and (ii) cluster E has updated the `SetFrame` call + put `TapeAccel` on the frame and computed the `ReactBanner`. The **pure-method gate (Steps 2/4) is independent** and already green. If the orchestrator runs cluster F before E and the FireEvent cluster, expect this step to fail on `CS0103: SetupKind` / `CS1501: no overload for 'SetFrame' takes 5 arguments` — that is the expected joint-integration failure, not a cluster-F defect.

### Step 8 — commit (after the joint nt8c build is clean)

```
git add NinjaTrader/CockpitVisual.cs
git commit -m "feat(cockpit): route banner by SetupKind + TAPE ACCEL readout (spec §10)

DrawBannerCard renders the Reactive banner (WATCHING WALL / REJECT·FADE /
BREAK·FOLLOW / WAITING) when the active setup is Reactive, else the unchanged
Break banner; both pull strings from the pure CockpitBanner mapper. Adds a
signed TAPE ACCEL card mirroring TAPE SPEED/Z. SetFrame gains tapeAccel/setup/
react (additive; RadarTab wires them). Break logic untouched.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

**Skipped, by design:** Reactive REJECT/BREAK banner color is amber-for-both (differentiated by text) — add a side-derived color anchor when Market Replay shows it's wanted (spec §12 ceiling territory). `AccelMaxPerSec2 = 20.0` is a display-scale placeholder — retune from `Rec` like `VelMaxPerSec`, it is not an engine threshold.