# Liquidity Radar — NT8 UI Layer Implementation Plan (Plan 2)

> **For agentic workers:** REQUIRED SUB-SKILL: this layer is NinjaTrader-specific. Delegate to the `trading-*` agents (`trading-ninjascript-developer` → `trading-code-reviewer`), and consult the `nt8-addon` / `nt8-common` skills before writing. Unlike the engine, this layer is **NOT unit-testable** — validation is `nt8c` compile + **Market Replay** visual checks. Steps use checkbox (`- [ ]`) tracking.

**Goal:** Wrap the finished pure-C# engine (`TradingRadar.Engine`, repo HEAD `f28c6ee`) in a standalone, floating NinjaTrader 8 Add-On window that reads Level-2 depth + trades, drives the engine, and renders the Aurora sonar-ladder — no chart, persisted in the workspace.

**Architecture:** Four NT classes around the engine: `RadarAddOn` (AddOnBase — registers the Control-Center menu item, opens the window), `RadarWindow` (NTWindow + IWorkspacePersistence — the floating shell) + `RadarTabFactory` (INTTabFactory), `RadarTab` (NTTabPage — the **threading boundary**: subscribes L2+trades on the instrument dispatcher, maps NT event args → engine DTOs, drives `WallTracker`, marshals immutable snapshots to the UI thread), and `RadarVisual` (FrameworkElement — Aurora WPF render of `RadarNode[]`). The engine is consumed as **source files compiled into the same NinjaScript Custom assembly** (not an external DLL), so everything builds as one unit.

**Tech Stack:** C# (NinjaScript / NinjaTrader 8 on .NET Framework 4.8, or .NET 8 for NT 2025+), WPF `DrawingContext` rendering, `nt8c` for compile validation, NinjaTrader Market Replay (NQ session with recorded depth) for behavioral/visual validation. The engine sources stay C# 7.3 / `netstandard2.0`-compatible (already are).

## Global Constraints

- **Repo:** `projects/Trading/Trading-radar/` (own git repo). NT layer lives in `NinjaTrader/`. All paths below are relative to the repo root.
- **Engine is frozen and reviewer-approved.** Do NOT modify any `Engine/*.cs` file to make the NT layer compile — the contract is fixed (`BookMirror.ApplyDepth/ApplyTrade/ResetFromSnapshot`, `WallTracker.Update(book, now)` / `OnReset(now)` / `GetSnapshot(now)`, `RadarNode`). If something seems to require an engine change, STOP and escalate.
- **Determinism preserved at the boundary:** pass the NT event's own timestamp (`e.Time`) as the engine `now`. Never read a wall clock to drive the engine.
- **Threading is law (spec §3.6):** subscribe/unsubscribe MarketDepth + MarketData ONLY on `instrument.Dispatcher` (guard with `HasShutdownStarted`). All engine calls (`BookMirror`, `WallTracker`) happen on the instrument thread. The ONLY cross-thread handoff is the immutable `RadarNode[]` snapshot, marshaled to the WPF UI thread by an `Interlocked` reference swap (RadarNode[] is immutable once built — no lock needed). Touch visuals only on the UI thread.
- **Rendering = WPF, never SharpDX** (SharpDX RenderTarget is chart-bound and unavailable here). Custom `FrameworkElement.OnRender(DrawingContext)`, animated by a `DispatcherTimer(DispatcherPriority.Render, 33ms)` ≈ 30 fps. Freeze every `Brush`/`Pen`/`Geometry` reused across frames.
- **Aurora design tokens are locked** (spec §7.2) — copy the exact hex values; do not invent colors. Numbers = Cascadia Mono/Consolas tabular; labels = Segoe UI.
- **NTWindow / IWorkspacePersistence classes:** top-level, non-nested, with a default constructor (NT requirement).
- **No unit tests** for this layer (NT/WPF glue isn't unit-testable). Each task's gate is: `nt8c` compiles the staged Custom mirror with **0 errors**; the final task adds Market Replay visual validation. The `nt8c` cross-file-namespace false-positives apply — build the whole staged `Custom/` dir, not single files (see `[[nt8c-cross-file-namespace-trap]]`).
- **Verbatim NT facts** (verified against `nt8-addon` docs): menu via `AddOnBase.OnWindowCreated(Window)` → cast to `ControlCenter` → `FindFirst("ControlCenterMenuItemNew")` → add `NTMenuItem` (Style `"MainMenuItem"`) → Click opens window via `Core.Globals.RandomDispatcher.InvokeAsync(() => new RadarWindow().Show())`. L2 via `instrument.MarketDepth.Update += handler(object, MarketDepthEventArgs)`. Trades via `instrument.MarketData.Update += handler(object, MarketDataEventArgs)` filtered to `MarketDataType.Last`.

---

## File Structure

```
projects/Trading/Trading-radar/
  NinjaTrader/                         # the NT8 Add-On layer (this plan)
    RadarAddOn.cs                      # AddOnBase: menu registration + window open + lifecycle
    RadarWindow.cs                     # NTWindow + IWorkspacePersistence (+ RadarTabFactory : INTTabFactory)
    RadarTab.cs                        # NTTabPage: threading boundary, NT→DTO mapping, drives engine
    RadarVisual.cs                     # FrameworkElement: Aurora sonar-ladder render
  Engine/                              # FROZEN — consumed as source by the staged build
    Primitives.cs RadarConfig.cs BookMirror.cs WallDetector.cs
    EpisodeClassifier.cs LiquidityMemory.cs WallTracker.cs
  build/
    stage-custom.sh                    # mirrors Engine/*.cs + NinjaTrader/*.cs into a Custom/ tree for nt8c
    deploy-notes.md                    # how to copy into NinjaTrader 8/bin/Custom/AddOns + restart
  docs/plans/2026-06-28-liquidity-radar-nt-ui.md   # this file
```

**Build/consume model (decided — resolves spec §11 open question):** the engine is compiled **as source** into NinjaTrader's `Custom` assembly, NOT referenced as an external DLL. Reason: external-DLL add-ons need a manual copy to `bin/Custom/` + an NT restart per build and reference wiring; dropping the engine `.cs` alongside the add-on `.cs` lets the NinjaScript editor (and `nt8c`) compile them together as one assembly, with hot-reload during development. Namespace stays `TradingRadar.Engine`; the NT classes live in `TradingRadar.NT` and `using TradingRadar.Engine;`.

**`nt8c` validation model:** `build/stage-custom.sh` assembles a directory that mirrors `Documents/NinjaTrader 8/bin/Custom/` containing both `Engine/*.cs` and `NinjaTrader/*.cs`, then `nt8c build --custom-dir <staged>` compiles the whole set (mirror build avoids the per-file cross-namespace false positives). Each task below ends with this compile gate.

**Deploy model:** copy `Engine/*.cs` + `NinjaTrader/*.cs` into `Documents/NinjaTrader 8/bin/Custom/AddOns/LiquidityRadar/`, then in NT8: compile (F5 in the NinjaScript editor) → restart NT → the "Liquidity Radar" item appears under Control Center → New.

---

### Task 1: RadarAddOn — menu registration + window lifecycle

**Files:**
- Create: `NinjaTrader/RadarAddOn.cs`
- Create: `build/stage-custom.sh` (needed to compile-gate this and every later task)

**Interfaces:**
- Consumes: nothing from the engine yet (just opens the window).
- Produces: a `RadarAddOn : AddOnBase` that adds a "Liquidity Radar" item under Control Center → New and opens a `RadarWindow` (a stub for now; real window in Task 2). Removes the menu item in `OnWindowDestroyed` (recompile-safe).

- [ ] **Step 1: Write `build/stage-custom.sh`** (the compile gate used by all tasks)

```bash
#!/usr/bin/env bash
# Stage a Custom/ mirror (Engine + NinjaTrader sources) for nt8c compile validation.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAGE="${1:-$ROOT/build/.stage/Custom}"
rm -rf "$STAGE"; mkdir -p "$STAGE/AddOns/LiquidityRadar"
cp "$ROOT"/Engine/*.cs       "$STAGE/AddOns/LiquidityRadar/"
cp "$ROOT"/NinjaTrader/*.cs   "$STAGE/AddOns/LiquidityRadar/"
echo "staged -> $STAGE"
```

- [ ] **Step 2: Write `NinjaTrader/RadarAddOn.cs`**

```csharp
using System.Windows;
using NinjaTrader.Core;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;        // NTMenuItem, ControlCenter
using NinjaTrader.NinjaScript.AddOns;

namespace TradingRadar.NT
{
    public class RadarAddOn : AddOnBase
    {
        private NTMenuItem radarMenuItem;
        private NTMenuItem existingMenuItem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "LiquidityRadar";
                Description = "Floating Level-2 liquidity radar (sonar ladder + tracked walls).";
            }
        }

        // Called in the thread of each new NTWindow (incl. on recompile).
        protected override void OnWindowCreated(Window window)
        {
            ControlCenter cc = window as ControlCenter;
            if (cc == null)
                return;

            existingMenuItem = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (existingMenuItem == null)
                return;

            radarMenuItem = new NTMenuItem
            {
                Header = "Liquidity Radar",
                Style  = Application.Current.TryFindResource("MainMenuItem") as Style
            };
            existingMenuItem.Items.Add(radarMenuItem);
            radarMenuItem.Click += OnMenuItemClick;
        }

        // Recompile-safe cleanup: pull our item back out.
        protected override void OnWindowDestroyed(Window window)
        {
            if (radarMenuItem != null && window is ControlCenter)
            {
                if (existingMenuItem != null && existingMenuItem.Items.Contains(radarMenuItem))
                    existingMenuItem.Items.Remove(radarMenuItem);
                radarMenuItem.Click -= OnMenuItemClick;
                radarMenuItem = null;
            }
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            Globals.RandomDispatcher.InvokeAsync(new System.Action(() => new RadarWindow().Show()));
        }
    }
}
```

- [ ] **Step 3: Add a minimal `RadarWindow` stub so Task 1 compiles standalone**

Create `NinjaTrader/RadarWindow.cs` (temporary stub — fully built in Task 2):

```csharp
using NinjaTrader.Gui;

namespace TradingRadar.NT
{
    public class RadarWindow : NTWindow
    {
        public RadarWindow()
        {
            Caption = "Liquidity Radar";
            Width = 460; Height = 820;
        }
    }
}
```

- [ ] **Step 4: Compile-gate with nt8c**

```bash
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```
Expected: 0 errors. (Engine sources compile alongside; the menu-registration add-on resolves `AddOnBase`, `ControlCenter`, `NTMenuItem`.)

- [ ] **Step 5: Commit**

```bash
git add NinjaTrader/RadarAddOn.cs NinjaTrader/RadarWindow.cs build/stage-custom.sh
git commit -m "feat(nt): RadarAddOn — Control Center menu registration + window open"
```

---

### Task 2: RadarWindow + RadarTabFactory — the floating shell with workspace persistence

**Files:**
- Modify: `NinjaTrader/RadarWindow.cs` (replace the Task 1 stub)
- Test: none (compile gate only)

**Interfaces:**
- Consumes: `RadarTab` (Task 3 — reference a stub until then).
- Produces: `RadarWindow : NTWindow, IWorkspacePersistence` (default ctor, Caption, sized, GUID-suffixed WorkspaceOptions, Save/Restore via MainTabControl) and `RadarTabFactory : INTTabFactory` (CreateParentWindow / CreateTabPage).

- [ ] **Step 1: Replace `NinjaTrader/RadarWindow.cs`**

```csharp
using System;
using System.Xml.Linq;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;

namespace TradingRadar.NT
{
    public class RadarWindow : NTWindow, IWorkspacePersistence
    {
        public RadarWindow()
        {
            Caption = "Liquidity Radar";
            Width   = 460;
            Height  = 820;

            TabControl tc = new TabControl();
            TabControlManager.SetIsMovable(tc, true);
            TabControlManager.SetCanAddTabs(tc, true);
            TabControlManager.SetCanRemoveTabs(tc, true);
            TabControlManager.SetFactory(tc, new RadarTabFactory());
            Content = tc;

            tc.AddNTTabPage(new RadarTab());

            Loaded += (o, e) =>
            {
                if (WorkspaceOptions == null)
                    WorkspaceOptions = new WorkspaceOptions("LiquidityRadar-" + Guid.NewGuid().ToString("N"), this);
            };
        }

        public void Restore(XDocument document, XElement element)
        {
            if (MainTabControl != null)
                MainTabControl.RestoreFromXElement(element);
        }

        public void Save(XDocument document, XElement element)
        {
            if (MainTabControl != null)
                MainTabControl.SaveToXElement(element);
        }

        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    public class RadarTabFactory : INTTabFactory
    {
        public NTWindow CreateParentWindow() { return new RadarWindow(); }
        public NTTabPage CreateTabPage(string typeName, bool isNewWindow = false) { return new RadarTab(); }
    }
}
```

- [ ] **Step 2: Add a minimal `RadarTab` stub so Task 2 compiles** (fully built in Task 3)

Create `NinjaTrader/RadarTab.cs` (temporary stub):

```csharp
using System.Xml.Linq;
using NinjaTrader.Gui.Tools;

namespace TradingRadar.NT
{
    public class RadarTab : NTTabPage
    {
        public RadarTab() { }
        public override void Cleanup() { }
        protected override string GetHeaderPart(string variable) { return "Liquidity Radar"; }
        protected override void Restore(XElement element) { }
        protected override void Save(XElement element) { }
    }
}
```

- [ ] **Step 3: Compile-gate**

```bash
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```
Expected: 0 errors. Resolves `NTWindow`, `IWorkspacePersistence`, `TabControl`, `TabControlManager`, `INTTabFactory`, `WorkspaceOptions`.

- [ ] **Step 4: Commit**

```bash
git add NinjaTrader/RadarWindow.cs NinjaTrader/RadarTab.cs
git commit -m "feat(nt): RadarWindow (NTWindow+IWorkspacePersistence) + RadarTabFactory"
```

---

### Task 3: RadarTab — the threading boundary (drives the engine)

**Files:**
- Modify: `NinjaTrader/RadarTab.cs` (replace the Task 2 stub)
- Test: none (compile gate; behavior validated in Task 5 Market Replay)

**Interfaces:**
- Consumes (engine, frozen): `BookMirror(double tickSize, TimeSpan tradeRetention)`, `BookMirror.ApplyDepth(DepthEvent)`, `ApplyTrade(TradeEvent)`, `ResetFromSnapshot(IList<DepthLevel>, IList<DepthLevel>)`; `WallTracker(RadarConfig)`, `WallTracker.Update(BookMirror, DateTime)`, `OnReset(DateTime)`, `GetSnapshot(DateTime) → IReadOnlyList<RadarNode>`; structs `DepthEvent/TradeEvent/DepthLevel`, enums `Side/DepthOp`, `RadarConfig`. Consumes `RadarVisual.SetNodes(IReadOnlyList<RadarNode>)` (Task 4 — stub it until then).
- Produces: a fully wired `RadarTab : NTTabPage, IInstrumentProvider` that subscribes L2+trades, maps NT args → engine DTOs on the instrument thread, swaps an immutable snapshot, and a UI `DispatcherTimer` paints `RadarVisual` at ~30 fps.

**NT→engine mapping (verified field names):**
- `MarketDepthEventArgs e`: `e.MarketDataType` (`MarketDataType.Bid`/`.Ask`), `e.Operation` (`Operation.Add`/`.Update`/`.Remove`), `e.Position` (int), `e.Price` (double), `e.Volume` (long), `e.Time` (DateTime). → `DepthEvent { Side = e.MarketDataType==MarketDataType.Ask ? Side.Ask : Side.Bid; Op = map(e.Operation); Position=e.Position; Price=e.Price; Volume=e.Volume; Time=e.Time; IsReset=false }`.
- `MarketDataEventArgs e` filtered to `e.MarketDataType==MarketDataType.Last`: → `TradeEvent { Price=e.Price; Volume=e.Volume; Time=e.Time }`.

- [ ] **Step 1: Replace `NinjaTrader/RadarTab.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using NinjaTrader.Cbi;                 // Instrument, MarketDepthEventArgs, MarketDataEventArgs, Operation, MarketDataType, Connection
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;           // NTTabPage, IInstrumentProvider, InstrumentSelector
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    public class RadarTab : NTTabPage, IInstrumentProvider
    {
        private readonly RadarConfig _cfg = new RadarConfig();   // NQ defaults; later: per-instrument presets
        private BookMirror _book;
        private WallTracker _tracker;
        private RadarVisual _visual;
        private InstrumentSelector _selector;

        private Instrument _instrument;
        private volatile IReadOnlyList<RadarNode> _latest;       // immutable snapshot, swapped from instrument thread
        private DispatcherTimer _paintTimer;
        private bool _subscribed;

        public RadarTab()
        {
            _book    = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
            _tracker = new WallTracker(_cfg);
            _visual  = new RadarVisual();

            _selector = new InstrumentSelector();
            _selector.InstrumentChanged += (o, e) => { Instrument = _selector.Instrument; };

            DockPanel root = new DockPanel();
            DockPanel.SetDock(_selector, Dock.Top);
            root.Children.Add(_selector);
            root.Children.Add(_visual);          // fills remaining space
            Content = root;

            // UI-thread paint/animation clock — independent of data arrival.
            _paintTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _paintTimer.Tick += (o, e) =>
            {
                IReadOnlyList<RadarNode> snap = _latest;          // volatile read of the latest immutable snapshot
                if (snap != null) _visual.SetNodes(snap);
                _visual.AdvanceAnimation();                       // sweep/fade clock (Task 4)
            };
            _paintTimer.Start();
        }

        // IInstrumentProvider — re-subscribe on instrument change (link-aware).
        public Instrument Instrument
        {
            get { return _instrument; }
            set
            {
                if (_instrument == value) return;
                Unsubscribe();
                _instrument = value;
                _cfg.TickSize = _instrument != null ? _instrument.MasterInstrument.TickSize : _cfg.TickSize;
                // Rebuild engine for the new instrument's tick size.
                _book    = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
                _tracker = new WallTracker(_cfg);
                _latest  = null;
                Subscribe();
                RefreshHeader();
            }
        }

        private void Subscribe()
        {
            if (_instrument == null || _subscribed) return;
            Instrument inst = _instrument;
            if (!inst.Dispatcher.HasShutdownStarted)
            {
                inst.Dispatcher.InvokeAsync(() =>
                {
                    inst.MarketDepth.Update += OnMarketDepth;
                    inst.MarketData.Update  += OnMarketData;
                    SeedFromSnapshot(inst);                       // prime the book with the current ladder
                });
                _subscribed = true;
            }
        }

        private void Unsubscribe()
        {
            if (_instrument == null || !_subscribed) return;
            Instrument inst = _instrument;
            if (!inst.Dispatcher.HasShutdownStarted)
                inst.Dispatcher.InvokeAsync(() =>
                {
                    inst.MarketDepth.Update -= OnMarketDepth;
                    inst.MarketData.Update  -= OnMarketData;
                });
            _subscribed = false;
        }

        // Build the initial ladder from the snapshot (instrument thread). Also the reset path.
        private void SeedFromSnapshot(Instrument inst)
        {
            List<DepthLevel> bids = new List<DepthLevel>();
            List<DepthLevel> asks = new List<DepthLevel>();
            for (int i = 0; i < inst.MarketDepth.Bids.Count; i++)
                bids.Add(new DepthLevel { Price = inst.MarketDepth.Bids[i].Price, Volume = inst.MarketDepth.Bids[i].Volume });
            for (int i = 0; i < inst.MarketDepth.Asks.Count; i++)
                asks.Add(new DepthLevel { Price = inst.MarketDepth.Asks[i].Price, Volume = inst.MarketDepth.Asks[i].Volume });
            _book.ResetFromSnapshot(bids, asks);
        }

        // ---- instrument-thread handlers: map -> engine -> swap snapshot ----
        private void OnMarketDepth(object sender, MarketDepthEventArgs e)
        {
            DepthOp op = e.Operation == Operation.Add ? DepthOp.Add
                       : e.Operation == Operation.Update ? DepthOp.Update : DepthOp.Remove;
            DepthEvent de = new DepthEvent
            {
                Side     = e.MarketDataType == MarketDataType.Ask ? Side.Ask : Side.Bid,
                Op       = op,
                Position = e.Position,
                Price    = e.Price,
                Volume   = e.Volume,
                Time     = e.Time,
                IsReset  = false
            };
            _book.ApplyDepth(de);
            _tracker.Update(_book, e.Time);
            _latest = _tracker.GetSnapshot(e.Time);
        }

        private void OnMarketData(object sender, MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;
            TradeEvent te = new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time };
            _book.ApplyTrade(te);
            _tracker.Update(_book, e.Time);
            _latest = _tracker.GetSnapshot(e.Time);
        }

        // ---- NTTabPage members ----
        public override void Cleanup()
        {
            if (_paintTimer != null) { _paintTimer.Stop(); _paintTimer = null; }
            Unsubscribe();
        }

        protected override string GetHeaderPart(string variable)
        {
            return _instrument != null ? _instrument.MasterInstrument.Name : "Liquidity Radar";
        }

        protected override void Restore(XElement element)
        {
            if (element == null) return;
            XElement instEl = element.Element("RadarInstrument");
            if (instEl != null && !string.IsNullOrEmpty(instEl.Value))
            {
                Instrument inst = Instrument.GetInstrument(instEl.Value);
                if (inst != null) { _selector.Instrument = inst; Instrument = inst; }
            }
        }

        protected override void Save(XElement element)
        {
            if (element == null) return;
            if (_instrument != null)
                element.Add(new XElement("RadarInstrument", _instrument.FullName));
        }
    }
}
```

- [ ] **Step 2: Ensure a `RadarVisual` stub with `SetNodes` + `AdvanceAnimation` exists** (Task 4 builds the real one)

Create `NinjaTrader/RadarVisual.cs` (temporary stub):

```csharp
using System.Collections.Generic;
using System.Windows;
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    public class RadarVisual : FrameworkElement
    {
        public void SetNodes(IReadOnlyList<RadarNode> nodes) { InvalidateVisual(); }
        public void AdvanceAnimation() { }
    }
}
```

- [ ] **Step 3: Compile-gate**

```bash
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```
Expected: 0 errors. Confirms the NT→DTO mapping resolves against the real engine types and NT `MarketDepthEventArgs`/`MarketDataEventArgs`/`InstrumentSelector` APIs. **If `MarketDepthEventArgs`/`MarketDataEventArgs` field names differ from those used here (e.g. `Volume` type, `MarketDataType` enum members), the compiler will flag it — fix the mapping, do NOT change the engine.**

- [ ] **Step 4: Commit**

```bash
git add NinjaTrader/RadarTab.cs NinjaTrader/RadarVisual.cs
git commit -m "feat(nt): RadarTab threading boundary — L2/trade subscription, NT->engine mapping, snapshot marshaling"
```

**Notes / known v1 limitations (validate/tune in Task 5):**
- **Episode timeouts are event-driven.** `WallTracker.Update` is called only on depth/trade events, so an episode's `T_episode` timeout resolves on the *next* event, not in dead air. NQ is liquid enough that this is fine for v1; if needed, add a low-rate tick posted to `inst.Dispatcher` calling `Update` with the last event time.
- **Reset fidelity.** `SeedFromSnapshot` primes the book on (re)subscribe. A mid-session feed reset is not yet wired to `WallTracker.OnReset` — add a `Connection.ConnectionStatusUpdate` handler that calls `OnReset(lastTime)` + `SeedFromSnapshot` on reconnect. Rare in Market Replay; verify on live.
- **Snapshot every event.** `GetSnapshot` runs per event on the instrument thread (cheap: ≤ band-size nodes). If profiling shows pressure, compute it on the paint timer instead via a dirty flag posted to the instrument dispatcher.

---

### Task 4: RadarVisual — the Aurora sonar-ladder render

**Files:**
- Modify: `NinjaTrader/RadarVisual.cs` (replace the Task 3 stub)
- Modify: `NinjaTrader/RadarTab.cs` (bundle mid + tick with the snapshot — small, see Step 2)
- Test: none (compile gate; **visual** validation in Task 5)

**Interfaces:**
- Consumes: `RadarNode` (frozen), and a market `mid` + `tickSize` (RadarNode carries neither — the layout needs the center price, so RadarTab passes them).
- Produces: `RadarVisual : FrameworkElement` with `SetNodes(IReadOnlyList<RadarNode> nodes, double mid, double tickSize)` and `AdvanceAnimation()`, rendering the Aurora skin in `OnRender(DrawingContext)`.

**Aurora tokens (spec §7.2 — copy verbatim):** bg radial `#121826→#0a0e16→#080b11`; bid `#34d399` (bar from `rgba(52,211,153,.22)`, glow `rgba(52,211,153,.50)`, size text `#6ee7b7`); ask `#fb7185` (bar from `rgba(251,113,133,.25)`, glow `rgba(251,113,133,.55)`, size text `#fda4af`); inside line amber `rgba(255,206,92,.5)`, chip text `#ffe08a`; price text `#cfd6e2`; bar track `rgba(255,255,255,.035)`. Numbers = Consolas tabular; labels = Segoe UI. State treatments (§7.3): WALL brighter+pill badge; ABSORB emerald `#a7f3d0` pulse; PULL desaturated+dashed+slate `#94a3b8`; Remembered opacity .34, no glow, "· Ns" age tag.

- [ ] **Step 1: Replace `NinjaTrader/RadarVisual.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    public class RadarVisual : FrameworkElement
    {
        // ---- Aurora palette (frozen) ----
        static readonly Brush BgTop    = Frozen(Color.FromRgb(0x12, 0x18, 0x26));
        static readonly Brush BgMid    = Frozen(Color.FromRgb(0x0a, 0x0e, 0x16));
        static readonly Brush Track    = Frozen(Color.FromArgb(9,  0xff, 0xff, 0xff));   // ~.035
        static readonly Color BidColor = Color.FromRgb(0x34, 0xd3, 0x99);
        static readonly Color AskColor = Color.FromRgb(0xfb, 0x71, 0x85);
        static readonly Brush BidText  = Frozen(Color.FromRgb(0x6e, 0xe7, 0xb7));
        static readonly Brush AskText  = Frozen(Color.FromRgb(0xfd, 0xa4, 0xaf));
        static readonly Brush PriceTxt = Frozen(Color.FromRgb(0xcf, 0xd6, 0xe2));
        static readonly Brush Amber    = Frozen(Color.FromArgb(128, 0xff, 0xce, 0x5c)); // .5
        static readonly Brush AmberTxt = Frozen(Color.FromRgb(0xff, 0xe0, 0x8a));
        static readonly Brush AbsorbBg = Frozen(Color.FromRgb(0xa7, 0xf3, 0xd0));
        static readonly Brush SlateBg  = Frozen(Color.FromRgb(0x94, 0xa3, 0xb8));
        static readonly Brush GridPen0 = Frozen(Color.FromArgb(16, 0xff, 0xff, 0xff));
        static readonly Pen   Grid     = FrozenPen(Color.FromArgb(16, 0xff, 0xff, 0xff), 1);
        static readonly Pen   PullDash = FrozenDash(Color.FromRgb(0x94, 0xa3, 0xb8), 1);
        static readonly Typeface Mono  = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface Sans  = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private IReadOnlyList<RadarNode> _nodes;
        private double _mid;
        private double _tick = 0.25;
        private int _bandTicks = 25;
        private double _sweep;          // 0..1 animation phase

        public void SetNodes(IReadOnlyList<RadarNode> nodes, double mid, double tickSize)
        {
            _nodes = nodes; _mid = mid; if (tickSize > 0) _tick = tickSize;
            InvalidateVisual();
        }

        public void AdvanceAnimation()
        {
            _sweep += 0.012; if (_sweep > 1.0) _sweep -= 1.0;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Background (vertical approximation of the radial token).
            dc.DrawRectangle(new LinearGradientBrush(((SolidColorBrush)BgTop).Color, ((SolidColorBrush)BgMid).Color, 90),
                             null, new Rect(0, 0, w, h));

            if (_nodes == null || _mid <= 0) return;

            int rows = 2 * _bandTicks + 1;
            double rowH = h / rows;
            double centerY = h / 2.0;
            double barX = 64, barMaxW = w - barX - 96;

            Func<double, double> yOf = price => centerY - ((price - _mid) / _tick) * rowH;

            // Iso-distance gridlines every 5/10/25 ticks.
            foreach (int g in new[] { 5, 10, 25 })
            {
                double yUp = centerY - g * rowH, yDn = centerY + g * rowH;
                if (yUp > 0) dc.DrawLine(Grid, new Point(0, yUp), new Point(w, yUp));
                if (yDn < h) dc.DrawLine(Grid, new Point(0, yDn), new Point(w, yDn));
            }

            long maxSize = 1;
            for (int i = 0; i < _nodes.Count; i++) if (_nodes[i].LastKnownSize > maxSize) maxSize = _nodes[i].LastKnownSize;

            for (int i = 0; i < _nodes.Count; i++)
            {
                RadarNode n = _nodes[i];
                double y = yOf(n.Price);
                if (y < -rowH || y > h + rowH) continue;
                bool blind = !n.InWindow || n.State == NodeState.Remembered;
                bool isBid = n.Side == Side.Bid;
                Color baseCol = isBid ? BidColor : AskColor;

                double frac = Math.Min(1.0, n.LastKnownSize / (double)maxSize);
                double barW = Math.Max(2.0, frac * barMaxW);
                double rowTop = y - rowH * 0.40, rowHt = rowH * 0.80;
                var barRect = new Rect(barX, rowTop, barW, rowHt);

                double op = blind ? 0.34 : 1.0;
                // Per-bar glow (manual: a larger, softer rect behind) — only when live (§7.5).
                if (!blind && (n.State == NodeState.Wall || n.State == NodeState.Absorbed))
                {
                    var glow = new SolidColorBrush(Color.FromArgb((byte)(isBid ? 80 : 90), baseCol.R, baseCol.G, baseCol.B));
                    dc.DrawRoundedRectangle(glow, null, new Rect(barX - 4, rowTop - 3, barW + 8, rowHt + 6), 5, 5);
                }
                dc.DrawRoundedRectangle(Track, null, new Rect(barX, rowTop, barMaxW, rowHt), 3, 3);

                Brush barBrush;
                Pen barPen = null;
                if (n.State == NodeState.Pulled) { barBrush = new SolidColorBrush(Color.FromArgb((byte)(0.7 * 255), 0x6b, 0x72, 0x80)); barPen = PullDash; }
                else barBrush = new SolidColorBrush(Color.FromArgb((byte)(op * 255), baseCol.R, baseCol.G, baseCol.B));
                dc.DrawRoundedRectangle(barBrush, barPen, barRect, 3, 3);

                // Price (left, tabular).
                DrawText(dc, n.Price.ToString("0.00", CultureInfo.InvariantCulture), 4, y, 11, Mono, PriceTxt, dpi, op);
                // Size (right of bar).
                DrawText(dc, n.LastKnownSize.ToString(), barX + barW + 6, y, 11, Mono, isBid ? BidText : AskText, dpi, op);
                // State badge / age.
                string badge = BadgeFor(n.State);
                if (blind) badge = "· " + Math.Round(n.AgeSeconds) + "s";
                if (!string.IsNullOrEmpty(badge))
                    DrawText(dc, badge, w - 70, y, 10, Sans, BadgeBrush(n.State, blind), dpi, op);
            }

            // Inside-market line + mid chip (amber).
            dc.DrawLine(new Pen(Amber, 1.0), new Point(0, centerY), new Point(w, centerY));
            DrawText(dc, _mid.ToString("0.00", CultureInfo.InvariantCulture), 4, centerY - 1, 12, Mono, AmberTxt, dpi, 1.0);

            // Refresh-pulse sweep: a faint moving band (honest "refresh", not a scan claim).
            double sy = _sweep * h;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(10, 0xff, 0xff, 0xff)), null, new Rect(0, sy, w, 2));
        }

        private static string BadgeFor(NodeState s)
        {
            switch (s)
            {
                case NodeState.Wall:     return "WALL";
                case NodeState.Absorbed: return "ABSORB";
                case NodeState.Pulled:   return "PULL";
                case NodeState.Consumed: return "BROKE";
                default:                 return "";
            }
        }

        private static Brush BadgeBrush(NodeState s, bool blind)
        {
            if (blind) return PriceTxt;
            if (s == NodeState.Absorbed) return AbsorbBg;
            if (s == NodeState.Pulled)   return SlateBg;
            return PriceTxt;
        }

        private static void DrawText(DrawingContext dc, string txt, double x, double yCenter, double size, Typeface tf, Brush b, double dpi, double op)
        {
            var ft = new FormattedText(txt, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, b, dpi);
            var pushed = op < 1.0;
            if (pushed) dc.PushOpacity(op);
            dc.DrawText(ft, new Point(x, yCenter - ft.Height / 2.0));
            if (pushed) dc.Pop();
        }

        // ---- frozen-resource helpers ----
        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static Pen FrozenPen(Color c, double t) { var p = new Pen(Frozen(c), t); p.Freeze(); return p; }
        private static Pen FrozenDash(Color c, double t) { var p = new Pen(Frozen(c), t) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) }; p.Freeze(); return p; }
    }
}
```

- [ ] **Step 2: Update `RadarTab` to pass mid + tick with the snapshot**

In `NinjaTrader/RadarTab.cs`, the snapshot must carry the market mid (RadarVisual needs the center price). Change the two handler swap sites and the paint timer:

Replace `private volatile IReadOnlyList<RadarNode> _latest;` with a bundled frame:
```csharp
private sealed class Frame { public IReadOnlyList<RadarNode> Nodes; public double Mid; public double Tick; }
private volatile Frame _latest;
```
Add a mid helper:
```csharp
private double MidOf()
{
    DepthLevel bb, ba;
    bool hb = _book.TryBestBid(out bb), ha = _book.TryBestAsk(out ba);
    if (hb && ha) return (bb.Price + ba.Price) / 2.0;
    if (hb) return bb.Price; if (ha) return ba.Price; return 0;
}
```
In both `OnMarketDepth` and `OnMarketData`, replace `_latest = _tracker.GetSnapshot(e.Time);` with:
```csharp
_latest = new Frame { Nodes = _tracker.GetSnapshot(e.Time), Mid = MidOf(), Tick = _cfg.TickSize };
```
In the paint timer Tick, replace the snapshot read with:
```csharp
Frame f = _latest;
if (f != null) _visual.SetNodes(f.Nodes, f.Mid, f.Tick);
_visual.AdvanceAnimation();
```

- [ ] **Step 3: Compile-gate**

```bash
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```
Expected: 0 errors. Confirms the WPF render + RadarTab frame bundling compile against `RadarNode`/`NodeState`/`Side`.

- [ ] **Step 4: Commit**

```bash
git add NinjaTrader/RadarVisual.cs NinjaTrader/RadarTab.cs
git commit -m "feat(nt): RadarVisual — Aurora sonar-ladder render (per-bar glow, state badges, decay opacity)"
```

---

### Task 5: Build, deploy, and Market Replay validation

**Files:**
- Create: `build/deploy-notes.md`
- Test: **Market Replay** (the real gate for this layer)

**Interfaces:**
- Consumes: the whole add-on + engine.
- Produces: a deployed, running radar validated on a recorded NQ depth session.

- [ ] **Step 1: Write `build/deploy-notes.md`**

Document the deploy + run procedure (no code):
```
1. Copy Engine/*.cs and NinjaTrader/*.cs into:
   %USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\AddOns\LiquidityRadar\
2. Open NinjaTrader 8 → NinjaScript Editor → Compile (F5). Fix any compile error in the NT classes ONLY (never the engine).
3. Restart NinjaTrader (external menu registration requires it the first time).
4. Control Center → New → "Liquidity Radar" → the floating window opens.
5. Pick NQ (front month) in the InstrumentSelector.
```

- [ ] **Step 2: Final full compile via nt8c**

```bash
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```
Expected: 0 errors, 0 warnings across the whole staged Custom mirror (engine + NT layer).

- [ ] **Step 3: Deploy + smoke test (live or sim-realtime)**

Deploy per `deploy-notes.md`. With the market open (or NT connected to sim-realtime), open the radar on NQ. Confirm: window opens, the sonar ladder renders, the inside-market amber line tracks, bars appear with size-proportional width, numbers are legible (opaque, tabular), no console errors in the NT Log tab.

- [ ] **Step 4: Market Replay behavioral validation (the real gate)**

Load a recorded **NQ Market Replay** session with depth (Tools → Historical Data has depth; Replay Connection). Play it and visually verify, against the design intent:
- **Wall detection:** a genuinely large resting level shows a brighter bar + `WALL` badge only after it has persisted (~1.5s), not instantly.
- **Three outcomes** when price reaches a tracked level: `ABSORB` (sustained, emerald) when trades hit it and it holds/refills; `PULL` (desaturated, dashed, slate) when size vanishes without trades; `BROKE` when price trades through.
- **Memory band:** a wall that scrolls beyond the live 10 levels dims to `opacity .34` with a "· Ns" age tag, and its confidence visibly decays; if price returns, it re-confirms.
- **No phantom absorption:** price merely approaching and bouncing off a wall **without trades** does NOT flash `ABSORB` (locks the I2 fix end-to-end in the live pipeline).
- **Reset:** if you stop/restart the Replay, the ladder rebuilds cleanly (no frozen ghost walls). If ghosts persist, wire the `Connection.ConnectionStatusUpdate` → `OnReset` hook noted in Task 3.

- [ ] **Step 5: Build the debug data-capture path (Javier's W_assoc decision)**

Per the engine decision: rather than tune blind, add an optional capture that, during Replay, logs per-episode raw inputs (price, S0, traded@P over time, displayed drop, cancelled, resolved outcome, confidence) to a CSV. Use it to calibrate `W_assoc`, `RefillRatioTrigger`, `K_mult`, `MinAbsSize`, `T_persist` and the rest of the NQ params against real data. Implementation sketch: a `RadarConfig.DebugCapturePath`; when set, `RadarTab` writes a row on each resolved episode / promotion (read from a lightweight diagnostics hook — add a non-invasive `WallTracker` event or expose last-resolved episodes; design this as a small follow-up that does NOT alter engine semantics). Tune, then bake the calibrated defaults into `RadarConfig`.

- [ ] **Step 6: Commit**

```bash
git add build/deploy-notes.md
git commit -m "docs(nt): deploy + Market Replay validation procedure; debug-capture plan for param calibration"
```

---

## Self-Review

**Spec coverage (design spec §-by-§ → task):**
- §2 v1 goals (floating window from Control Center → New, persisted; live L2 for one selectable instrument; sonar render; wall detection; 3 outcomes; memory band; tunable params) → Tasks 1–4 (UI/glue) over the engine (Plan 1). ✅
- §3 verified facts (4-class add-on; L2 without chart via `instrument.MarketDepth.Update`; 10-level MBP; WPF not SharpDX; no historical backtest → Replay; threading on instrument dispatcher) → Tasks 1–3 + Global Constraints + Task 5. ✅
- §4 the 4 NT units (RadarAddOn/RadarWindow/RadarTab/RadarVisual) → Tasks 1/2/3/4. ✅ RadarNode contract consumed read-only. ✅
- §5 data flow & threading (events on instrument dispatcher → BookMirror → WallTracker → snapshot → BeginInvoke/marshal → RadarVisual; reset wipe+rebuild; animation on independent timer) → Task 3 (mapping+marshal via volatile frame swap + paint timer) + Task 4 (AdvanceAnimation). ✅ (Reset partially deferred — seed-on-subscribe done, connection-driven OnReset flagged.)
- §7 Aurora visual (layout, tokens, state visuals, animation, WPF DrawingContext + per-bar glow) → Task 4. ✅
- §8 validation (no L2 backtest → unit tests for engine [Plan 1] + nt8c compile + Market Replay) → compile gates each task + Task 5. ✅
- §11 open questions: build mode → **decided: engine-as-source in the Custom assembly** (Global Constraints / File Structure); instrument via selector → done (also IInstrumentProvider for link); MemoryBandTicks/row height → tune in Task 5. ✅

**Placeholder scan:** the stubs in Tasks 1–3 are explicitly temporary and replaced by the next task (each labeled). No `TBD`/"add later" in shipped code. The debug-capture (Task 5 Step 5) is intentionally a sketch — it's the calibration tool Javier asked to build *during* testing, not v1 engine code; flagged as a non-semantic follow-up.

**Type consistency:** NT→DTO mapping uses the exact engine struct/enum members (`DepthEvent.Side/Op/Position/Price/Volume/Time/IsReset`, `Side.Bid/Ask`, `DepthOp.Add/Update/Remove`, `RadarNode.Price/Side/LastKnownSize/State/Confidence/InWindow/AgeSeconds`, `NodeState.*`). `RadarVisual.SetNodes(nodes, mid, tick)` matches the RadarTab call site after the Step-2 frame change. The frozen engine entry points (`Update`, `OnReset`, `GetSnapshot`, `ApplyDepth`, `ApplyTrade`, `ResetFromSnapshot`, `TryBestBid/Ask`) match Plan 1. ✅

**Honest risks carried into implementation:** NT `MarketDepthEventArgs`/`MarketDataEventArgs` exact member names/types must be confirmed at first compile (Task 3 gate) — the mapping is written from the documented API but NT version drift is possible; fix the mapping, never the engine. Episode-timeout-on-next-event and connection-driven reset are the two known v1 simplifications, both flagged with concrete upgrade paths and validated in Replay.

---

## Execution Handoff

This plan is saved to `projects/Trading/Trading-radar/docs/plans/2026-06-28-liquidity-radar-nt-ui.md` (inside the project's own repo). Implementation requires a **Windows machine with NinjaTrader 8 + `nt8c`** (the compile gates and Market Replay can't run in this WSL/Linux session). Two options when on that machine:

**1. Subagent-Driven (recommended)** — dispatch `trading-ninjascript-developer` per task with `trading-code-reviewer` between tasks, exactly as the engine was built. Each task's gate is `nt8c` compile (not `dotnet test`); Task 5 adds the Market Replay visual pass.

**2. Inline** — execute task-by-task in a session on the NT8 machine.

Either way: consult `nt8-addon` / `nt8-common` before each NT class, and never edit `Engine/*.cs` to satisfy the compiler.

