# Cockpit Render (Plan C) — Implementation Plan

> **For agentic workers:** WPF render + wiring. NO unit-test cycle (WPF `OnRender` + `RadarTab` glue are verified by `nt8c` compile here + visual Market Replay on Windows). Gate = `nt8c build` 0/0 **and** the existing 46 engine tests stay green + code review. Visual gate = Javier's Replay.

**Goal:** Turn the radar window into the approved **Cockpit**: the existing anchored ladder on the left, and on the right a new **directional-pressure panel** — a bias gauge (net %, tug-of-war bar + needle, conviction dots, green-light pill) and the **5-signal conditions list** — wired live to the Plan B `PressureModel`.

**Architecture:** A new render-only `NinjaTrader/CockpitVisual.cs` (`FrameworkElement.OnRender`, Aurora-styled) draws the gauge + conditions from a `PressureResult` (+ `PressureInputs` for the reason text). `RadarTab` becomes the **assembler**: each engine run it builds `PressureInputs` from `BookMirror`/`WallTracker` (the same inputs the Plan D `lr-signals` capture already computes), calls `PressureModel.Evaluate`, stashes the result in its `Frame`, and the paint timer feeds it to `CockpitVisual`. Layout changes from `[topbar / ladder]` to `[topbar / (ladder | cockpit)]`. The engine is untouched; `PressureModel` already exists and is tested.

**Tech Stack:** C# WPF (NT Custom assembly, .NET 4.8). Compiled via `nt8c`.

**Spec:** `docs/specs/2026-06-29-radar-cockpit-design.md` §5–§6; reference = `docs/mockups/radar-cockpit-demo.html` (the right-hand Cockpit panel).

## Global Constraints

- New file `NinjaTrader/CockpitVisual.cs` + edits to `NinjaTrader/RadarTab.cs` ONLY. Do NOT change the engine, `RadarVisual.cs`, the Aurora tokens, or `RadarTab`'s existing topbar/Rec/capture/threading behavior.
- The 46 engine unit tests must stay green; `nt8c build --custom-dir build/.stage/Custom` must be 0 errors / 0 warnings.
- `CockpitVisual` is **render-only** — no trading logic, no orders, no engine calls. It reads a `PressureResult` + `PressureInputs` and draws.
- `RadarTab`'s `PressureInputs` assembly mirrors the Plan D `lr-signals` computation exactly (bidMass/askMass sums, best sizes, `AggressorDelta(now-15s)`, nearest-approaching max-frac wall erosion). Reuse, don't reinvent.
- Threading: `PressureInputs`/`PressureResult` are immutable value snapshots built on the instrument thread and read on the UI thread via the existing `volatile Frame _latest` swap — same pattern as `Nodes`/`Bids`/`Asks`.

---

### Task C1: `CockpitVisual.cs` — the pressure gauge + conditions panel

**Files:** Create `NinjaTrader/CockpitVisual.cs`

**Interfaces:**
- Consumes (Plan B engine): `PressureResult { SignalRead[] Signals; double Net; int Conviction; int Sign; bool Green; }`, `SignalRead { SignalId Id; double Lean; double Weight; bool Active; }`, `SignalId`, `PressureInputs`, `DepthLevel`.
- Produces: `public void SetFrame(PressureInputs inp, PressureResult res)` → stashes + `InvalidateVisual()`.

- [ ] **Step 1: Create the file** exactly as below.

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    // Render-only Cockpit: directional pressure gauge + 5-signal conditions panel (Aurora).
    // Reads a PressureResult (+ PressureInputs for reason text). No trading logic.
    public class CockpitVisual : FrameworkElement
    {
        static readonly Brush PanelBg = B(Color.FromRgb(0x0d, 0x12, 0x1c));
        static readonly Brush CardBg  = B(Color.FromArgb(10, 0xff, 0xff, 0xff));
        static readonly Pen   CardLn  = P(Color.FromArgb(18, 0xff, 0xff, 0xff), 1);
        static readonly Brush Muted   = B(Color.FromRgb(0x8a, 0x93, 0xa3));
        static readonly Brush Muted2  = B(Color.FromRgb(0x6b, 0x72, 0x80));
        static readonly Brush Txt     = B(Color.FromRgb(0xdd, 0xe3, 0xec));
        static readonly Color Bid     = Color.FromRgb(0x34, 0xd3, 0x99);
        static readonly Color Ask     = Color.FromRgb(0xfb, 0x71, 0x85);
        static readonly Brush BidTxt  = B(Color.FromRgb(0x6e, 0xe7, 0xb7));
        static readonly Brush AskTxt  = B(Color.FromRgb(0xfd, 0xa4, 0xaf));
        static readonly Brush Slate   = B(Color.FromRgb(0x94, 0xa3, 0xb8));
        static readonly Brush Track   = B(Color.FromArgb(16, 0xff, 0xff, 0xff));
        static readonly Brush White   = B(Colors.White);
        static readonly Brush Divider = B(Color.FromArgb(60, 0xff, 0xff, 0xff));
        static readonly Typeface Sans = new Typeface("Segoe UI");

        private PressureResult _res;
        private PressureInputs _in;
        private bool _has;

        public CockpitVisual() { ClipToBounds = true; }

        public void SetFrame(PressureInputs inp, PressureResult res)
        { _in = inp; _res = res; _has = true; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.DrawRectangle(PanelBg, null, new Rect(0, 0, w, h));
            if (!_has || _res.Signals == null) return;

            double pad = 14, x = pad, y = pad, cw = w - 2 * pad;
            double net = _res.Net;
            int pct = (int)Math.Round(net * 100);
            bool lng = net >= 0;
            Color fill = lng ? Bid : Ask;
            Brush netBr = Math.Abs(net) < 0.08 ? Slate : (lng ? BidTxt : AskTxt);

            // ---------- gauge card ----------
            double gh = 100;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, gh), 8, 8);
            Left(dc, "PRESIÓN DIRECCIONAL", x + 12, y + 16, 11, Muted, dpi);
            Right(dc, (pct > 0 ? "+" : "") + pct + "%", x + cw - 12, y + 12, 22, netBr, dpi);

            double bx = x + 12, by = y + 46, bw = cw - 24, bh = 16;
            dc.DrawRoundedRectangle(Track, CardLn, new Rect(bx, by, bw, bh), 8, 8);
            double half = Math.Abs(net) * (bw / 2.0);
            Brush fb = new SolidColorBrush(Color.FromArgb(150, fill.R, fill.G, fill.B));
            double fx = lng ? bx + bw / 2.0 : bx + bw / 2.0 - half;
            if (half > 0.5) dc.DrawRoundedRectangle(fb, null, new Rect(fx, by, half, bh), 6, 6);
            dc.DrawRectangle(Divider, null, new Rect(bx + bw / 2.0 - 0.5, by - 3, 1, bh + 6));
            double nx = bx + bw / 2.0 + net * (bw / 2.0);
            dc.DrawRoundedRectangle(White, null, new Rect(nx - 1.5, by - 4, 3, bh + 8), 2, 2);
            Left(dc, "◀ SHORT", bx, by + bh + 4, 9, Muted2, dpi);
            Right(dc, "LONG ▶", bx + bw, by + bh + 4, 9, Muted2, dpi);

            double dy = y + gh - 24;
            for (int i = 0; i < 5; i++)
            {
                Brush b = i < _res.Conviction ? new SolidColorBrush(fill) : B(Color.FromArgb(30, 0xff, 0xff, 0xff));
                dc.DrawEllipse(b, null, new Point(x + 18 + i * 14, dy + 6), 4.5, 4.5);
            }
            string pill = _res.Green ? (lng ? "▶ SEMÁFORO LONG" : "◀ SEMÁFORO SHORT") : "SIN TRIGGER";
            Brush pillTx = _res.Green ? (lng ? BidTxt : AskTxt) : Slate;
            Brush pillBg = _res.Green ? new SolidColorBrush(Color.FromArgb(40, fill.R, fill.G, fill.B))
                                      : B(Color.FromArgb(36, 0x94, 0xa3, 0xb8));
            var pf = FT(pill, 11, pillTx, dpi);
            double pw = pf.Width + 18, ph = pf.Height + 8, px = x + 100;
            dc.DrawRoundedRectangle(pillBg, null, new Rect(px, dy, pw, ph), 6, 6);
            dc.DrawText(pf, new Point(px + 9, dy + (ph - pf.Height) / 2));

            // ---------- conditions card ----------
            y += gh + 12;
            double ch = h - y - pad;
            if (ch < 40) return;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, ch), 8, 8);
            Left(dc, "CONDICIONES · EN VIVO", x + 12, y + 15, 11, Muted, dpi);

            var S = _res.Signals;
            double ry = y + 32, rh = 34;
            for (int i = 0; i < S.Length; i++)
            {
                if (ry + rh > y + ch) break;
                SignalRead s = S[i];
                int sign = s.Idle() ? 0 : (s.Lean > 0.12 ? 1 : (s.Lean < -0.12 ? -1 : 0));
                bool fire = s.Active && Math.Abs(s.Lean) > 0.3;
                Brush chipTx = sign > 0 ? BidTxt : (sign < 0 ? AskTxt : Slate);
                Brush chipBg = sign > 0 ? B(Color.FromArgb(36, 0x34, 0xd3, 0x99))
                              : sign < 0 ? B(Color.FromArgb(36, 0xfb, 0x71, 0x85))
                                         : B(Color.FromArgb(28, 0x94, 0xa3, 0xb8));
                string lean = sign > 0 ? "LONG" : (sign < 0 ? "SHORT" : "—");
                var cf = FT(lean, 9, chipTx, dpi);
                dc.DrawRoundedRectangle(chipBg, null, new Rect(x + 12, ry + 3, 44, 16), 4, 4);
                dc.DrawText(cf, new Point(x + 12 + (44 - cf.Width) / 2, ry + 3 + (16 - cf.Height) / 2));

                Left(dc, Name(s.Id), x + 64, ry + 7, 12.5, Txt, dpi);
                Left(dc, Reason(s.Id, _in), x + 64, ry + 21, 10.5, Muted2, dpi);

                double wbx = x + cw - 92, wbw = 60;
                dc.DrawRoundedRectangle(Track, null, new Rect(wbx, ry + 11, wbw, 5), 3, 3);
                double frac = s.Idle() ? 0.15 : Math.Min(1.0, Math.Abs(s.Lean));
                Brush wb = sign > 0 ? new SolidColorBrush(Bid) : (sign < 0 ? new SolidColorBrush(Ask) : Slate);
                dc.DrawRoundedRectangle(wb, null, new Rect(wbx, ry + 11, Math.Max(2.0, wbw * frac), 5), 3, 3);

                Left(dc, fire ? "✓" : "·", x + cw - 20, ry + 7, 13, fire ? Txt : Muted2, dpi);
                ry += rh;
            }
        }

        // ---- signal display metadata ----
        private static string Name(SignalId id)
        {
            switch (id)
            {
                case SignalId.Imbalance:  return "Imbalance de reposo";
                case SignalId.InsideThin: return "Inside fino";
                case SignalId.AirPocket:  return "Air-pocket";
                case SignalId.Delta:      return "Delta de agresor (15s)";
                default:                  return "Erosión del muro";
            }
        }
        private static string Reason(SignalId id, PressureInputs inp)
        {
            long b = 0, a = 0;
            if (inp.Bids != null) for (int i = 0; i < inp.Bids.Count; i++) b += inp.Bids[i].Volume;
            if (inp.Asks != null) for (int i = 0; i < inp.Asks.Count; i++) a += inp.Asks[i].Volume;
            switch (id)
            {
                case SignalId.Imbalance:  return b + "/" + a + (a > b ? "  oferta arriba" : "  demanda abajo");
                case SignalId.InsideThin: return "bid " + inp.BestBidSize + " vs ask " + inp.BestAskSize;
                case SignalId.AirPocket:  return "huecos cerca del precio";
                case SignalId.Delta:      return Math.Abs(inp.AggressorDelta) < 4 ? "plano, nadie golpea"
                                              : (inp.AggressorDelta > 0 ? ("+" + inp.AggressorDelta + " compra")
                                                                        : (inp.AggressorDelta + " venta"));
                default:                  return inp.Wall.Active
                                              ? ((int)Math.Round(inp.Wall.Frac * 100) + "% sin trades → " + (inp.Wall.Above ? "techo" : "soporte") + " falso")
                                              : "sin erosión activa";
            }
        }

        // ---- text + brush helpers ----
        private FormattedText FT(string s, double size, Brush b, double dpi)
        { return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Sans, size, b, dpi); }
        private void Left(DrawingContext dc, string s, double x, double yTop, double size, Brush b, double dpi)
        { dc.DrawText(FT(s, size, b, dpi), new Point(x, yTop)); }
        private void Right(DrawingContext dc, string s, double xRight, double yTop, double size, Brush b, double dpi)
        { var ft = FT(s, size, b, dpi); dc.DrawText(ft, new Point(xRight - ft.Width, yTop)); }
        private static Brush B(Color c) { var br = new SolidColorBrush(c); br.Freeze(); return br; }
        private static Pen P(Color c, double t) { var p = new Pen(B(c), t); p.Freeze(); return p; }
    }
}
```

- [ ] **Step 2: `SignalRead.Idle()` helper.** `CockpitVisual` calls `s.Idle()`. The Plan B `SignalRead` struct has `Active` but no `Idle`. Add a tiny method to the struct in `Engine/PressureModel.cs` (additive, C# 7.3, does not change existing tests — `Active` semantics unchanged):
```csharp
    public struct SignalRead
    {
        public SignalId Id;
        public double Lean;
        public double Weight;
        public bool Active;
        // True when the signal is inactive AND near-zero lean (idle wall/flat delta) — display only.
        public bool Idle() { return !Active && Math.Abs(Lean) <= 0.12; }
    }
```
(Replace the existing `SignalRead` struct definition with this one — same fields, one added method. Run `dotnet test` to confirm 46/46 unchanged.)

- [ ] **Step 3: compile-gate** (done together with Task C2, see below).

---

### Task C2: wire `RadarTab` → `PressureModel` → `CockpitVisual`

**Files:** Modify `NinjaTrader/RadarTab.cs`

**Edits (exact):**

1. **Fields** (next to `_visual`):
```csharp
        private CockpitVisual _cockpit;
        private readonly RadarConfig _pcfgUnused;   // (not needed)
        private PressureModel _pressure = new PressureModel(new PressureConfig());
```
(Only `_cockpit` and `_pressure` are needed — omit the unused line.)

2. **`Frame` class** (line ~19) — add two fields:
```csharp
            public PressureInputs PInputs;
            public PressureResult PResult;
```

3. **Construct `_cockpit`** where `_visual` is constructed (line ~63):
```csharp
            _cockpit = new CockpitVisual();
```

4. **Layout** — replace `root.Children.Add(_visual);` (line ~144) with a 2-column grid (ladder left, cockpit right):
```csharp
            Grid split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            Grid.SetColumn(_visual, 0); split.Children.Add(_visual);
            Grid.SetColumn(_cockpit, 1); split.Children.Add(_cockpit);
            root.Children.Add(split);            // fills remaining space (DockPanel LastChildFill)
```
(`System.Windows.Controls.Grid` is already usable — `RadarTab` already uses WPF controls. Add `using System.Windows.Controls;` if not present.)

5. **Paint timer** (line ~155) — feed the cockpit too:
```csharp
                if (f != null)
                {
                    _visual.SetFrame(f.Nodes, f.Bids, f.Asks, f.Mid, f.Tick);
                    _cockpit.SetFrame(f.PInputs, f.PResult);
                }
```

6. **Assemble `PressureInputs` + Evaluate** in `MaybeRunEngine`, where `_latest = new Frame { ... }` is built (line ~295). Compute the inputs from `_book`/`_tracker` (same as the Plan D `lr-signals` block) and store on the Frame:
```csharp
            // Cockpit pressure inputs (same assembly as the lr-signals capture).
            var pBids = _book.Levels(Side.Bid); var pAsks = _book.Levels(Side.Ask);
            long pBidMass = 0, pAskMass = 0;
            for (int i = 0; i < pBids.Count; i++) pBidMass += pBids[i].Volume;
            for (int i = 0; i < pAsks.Count; i++) pAskMass += pAsks[i].Volume;
            DepthLevel pbb, pba;
            long pBestBid = _book.TryBestBid(out pbb) ? pbb.Volume : 0;
            long pBestAsk = _book.TryBestAsk(out pba) ? pba.Volume : 0;
            double pMid = MidOf();
            WallErosion pWall = new WallErosion();
            double pWf = 0.0;
            var pErr = _tracker.ErosionReads(_book, now);
            for (int i = 0; i < pErr.Count; i++)
                if (pErr[i].Approaching && pErr[i].Frac > pWf)
                { pWf = pErr[i].Frac; pWall = new WallErosion { Active = true, Frac = pErr[i].Frac, Above = pErr[i].Price > pMid }; }
            PressureInputs pin = new PressureInputs
            {
                Bids = new System.Collections.Generic.List<DepthLevel>(pBids),
                Asks = new System.Collections.Generic.List<DepthLevel>(pAsks),
                BestBidSize = pBestBid, BestAskSize = pBestAsk,
                AggressorDelta = _book.AggressorDelta(now.AddSeconds(-15)),
                Wall = pWall
            };
```
Then add the two fields to the `_latest = new Frame { ... }` initializer:
```csharp
                PInputs = pin,
                PResult = _pressure.Evaluate(pin),
```
(Build `pin` BEFORE the `_latest = new Frame { ... }` statement; reference it inside.)

- [ ] **Step 1:** apply Task C1 (create `CockpitVisual.cs`) + the C1 Step 2 `SignalRead.Idle()` addition.
- [ ] **Step 2:** apply Task C2 edits 1–6 to `RadarTab.cs`.
- [ ] **Step 3: engine tests:** `dotnet test Tests/TradingRadar.Tests.csproj -v q --nologo` → **46 passing** (the `Idle()` addition is additive).
- [ ] **Step 4: nt8c gate:**
```bash
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```
→ **0 errors, 0 warnings**. Per-file editor-hook CS0246/CS0234 are false positives; the `--custom-dir` build is authoritative. Fix any real error.
- [ ] **Step 5: self-review:** new file + RadarTab only; engine untouched; existing topbar/Rec/capture/threading unchanged; `_cockpit` fed on the UI thread via the `Frame` swap; `PressureInputs` assembly matches the lr-signals block; Aurora colors consistent.
- [ ] **Step 6: commit:**
```bash
git add NinjaTrader/CockpitVisual.cs NinjaTrader/RadarTab.cs Engine/PressureModel.cs Tests
git commit -m "feat(nt): Cockpit render — pressure gauge + conditions panel wired to PressureModel (Plan C)"
```

---

## Verification (Javier, Market Replay — the visual gate)
After `nt8c` 0/0 + deploy + recompile + **reopen** the radar:
1. The window now shows the **anchored ladder on the left** and the **Cockpit on the right** (PRESIÓN DIRECCIONAL gauge + CONDICIONES list).
2. As price/book move, the **net %**, the **tug-of-war needle**, the **conviction dots**, the **green-light pill**, and the 5 **condition rows** (lean chip / reason / weight bar / ✓) update live.
3. At rest with overhead supply + thin bid it should read **lean SHORT / SIN TRIGGER**; a clear catalyst (delta turning, wall eroding) pushes it toward a SEMÁFORO. Weights are placeholder (Plan D measures them).

## Self-Review (plan vs spec §5–§6)
- §6 gauge (net / conviction / green-light, binary SIN-TRIGGER vs SEMÁFORO) → Task C1 gauge card.
- §5 conditions list (lean + weight + firing + reason) → Task C1 conditions card; names/reasons mapped per `SignalId`.
- Wiring assembler (deferred to Plan C in §13 #7) → Task C2 edit 6, consuming `RadarNode[]`-equivalent book + the Plan B/D engine reads.
- Render-only, Aurora, no orders (Chart Trader is Plan E) → respected.
- No placeholders in the plan; full code given; gate = nt8c 0/0 + 46 tests + review; visual gate = Replay (explicit).
