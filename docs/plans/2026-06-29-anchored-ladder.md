# Anchored Price Ladder (Plan A) — Implementation Plan

> **For agentic workers:** single-file WPF render change. There is NO unit-test cycle (WPF `OnRender` is verified by `nt8c` compile here + visual Market Replay on the Windows box). Gate = `nt8c build` clean + code review + Javier's Replay checklist (§Verification).

**Goal:** Replace `RadarVisual`'s mid-centered-per-frame vertical mapping (the whole profile jumps every time price moves — "es una locura") with an **anchored** ladder: the price scale holds still, bars grow/shrink in place, the inside-market marker glides through, and the price column re-anchors only at the edges (DOM-standard).

**Architecture:** Only `NinjaTrader/RadarVisual.cs` changes. The engine, `RadarTab`, and the Aurora visual tokens are untouched. The behavioral reference is the approved mockup `docs/mockups/radar-cockpit-demo.html` (its **Anclado** mode) — port that exact anchoring logic to WPF `OnRender`.

**Tech Stack:** C# WPF (`FrameworkElement.OnRender(DrawingContext)`), NinjaTrader 8 Custom assembly (.NET Framework 4.8). Compiled via `nt8c`.

## Global Constraints

- Touch **only** `NinjaTrader/RadarVisual.cs`. Do not change `RadarTab.cs`, the engine, or the Aurora palette/tokens (all the brushes, colors, fonts, badges, bar/glow drawing stay exactly as they are).
- Keep the existing render of: faint book ladder, wall/absorbed glow + bars, state badges, Pulled dashed bars, price/size labels, edge markers for off-screen nodes. **Only the vertical mapping (y of each row) and the band logic change.**
- WPF immediate-mode: no CSS-style transitions. Smoothness comes for free from the 33 ms paint timer re-rendering as `_mid` drifts; the marker moves a little each frame, the anchored bars stay put.
- C# for the NT Custom assembly (.NET 4.8) — normal C# is fine here (this is NOT the netstandard2.0/7.3 engine).
- Determinism of *rendering* is not required (it's a view), but do not introduce `DateTime.Now`-driven layout — anchor state is driven by `_mid` only.

## File Structure

- **Modify:** `NinjaTrader/RadarVisual.cs` — add anchor state fields + a re-anchor helper; rewrite the y-mapping inside `OnRender`; render the mid line/chip at the gliding `y(mid)` instead of the fixed center.

---

### Task A1: Anchor the ladder in `RadarVisual.OnRender`

**Files:**
- Modify: `NinjaTrader/RadarVisual.cs`

**Reference:** `docs/mockups/radar-cockpit-demo.html` — the JS `reanchor()`, `visiblePrices()`, and the `y = (anchorTop - price)/TICK * ROWH` mapping in `renderLadder()`, plus the gliding `#marker` at `top = (anchorTop - price)/TICK * ROWH`. Port that behavior.

**The change (precise):**

1. **Add fields** to `RadarVisual` (alongside `_mid`, `_tick`):
   ```csharp
   private double _anchorTop = double.NaN;   // price of the top visible row; persists across frames
   private const int ANCHOR_GUARD_TICKS = 4; // re-anchor when mid comes within this many ticks of an edge
   private const double TARGET_ROW_PX = 26.0;
   ```

2. **Compute a stable row grid** at the top of `OnRender` (after `if (_mid <= 0) return;`), replacing the current "Auto-fit band to live book only" block (the loop that derives `half` from the book and the `rows = 2*half+1` / `centerY = h/2` lines):
   ```csharp
   int rows = (int)Math.Max(9, Math.Floor(h / TARGET_ROW_PX));
   double rowH = h / rows;
   ```

3. **Anchor / re-anchor** (replaces mid-centering). Initialize on first frame or after a discontinuity, then slide only at the edges:
   ```csharp
   // (Re)initialise if uninitialised or mid jumped outside a sane window (instrument change / reset).
   if (double.IsNaN(_anchorTop)
       || _mid > _anchorTop + _tick
       || _mid < _anchorTop - (rows + 1) * _tick)
       _anchorTop = RoundToTick(_mid) + (rows / 2) * _tick;

   double guard = ANCHOR_GUARD_TICKS * _tick;
   double bottom = _anchorTop - (rows - 1) * _tick;
   // Edge re-anchor: slide the price column only when mid nears the top/bottom edge.
   int safety = 0;
   while (_mid > _anchorTop - guard && safety++ < rows) { _anchorTop += _tick; bottom += _tick; }
   safety = 0;
   while (_mid < bottom + guard && safety++ < rows) { _anchorTop -= _tick; bottom -= _tick; }
   ```
   Add a small private helper (NT exposes `Instrument.MasterInstrument.RoundToTickSize`, but `RadarVisual` has no instrument — round locally):
   ```csharp
   private double RoundToTick(double p) { return Math.Round(p / _tick) * _tick; }
   ```

4. **Anchored y-mapping** — replace **every** `centerY - ((<price>) - _mid) / _tick * rowH` expression in `OnRender` with the anchored form:
   ```csharp
   double Y(double price) { return ((_anchorTop - price) / _tick) * rowH; }
   ```
   Declare `Y` as a local function (C# 7 supports it) right after `rowH` is computed, and use `Y(_bids[i].Price)`, `Y(_asks[i].Price)`, `Y(n.Price)` in place of the current `y` computations. (The current code has three such sites: the bid book loop, the ask book loop, and the wall-node loop. Plus the inside-market line + chip — see step 5.)
   - Keep the existing `if (y < 0 || y > h) continue;` culling on each row (now using `Y(...)`).

5. **Glide the inside-market line + mid chip** to `Y(_mid)` instead of the fixed `centerY`:
   - The amber inside line: draw it as a short horizontal line at `Y(_mid)` (spanning the width), NOT across a fixed center. (Keep the same `AmberLine` pen.)
   - The mid chip: position it vertically centred on `Y(_mid)` (replace the `chipY = centerY - chipH/2.0` with `chipY = Y(_mid) - chipH/2.0`, and the `DrawText` at `centerY` with `Y(_mid)`).

6. **Edge markers** (the existing above/below off-screen node lists): keep them, but compute each node's `ny` with `Y(...)`; "above" = `ny < 0`, "below" = `ny > h` (same as today, just via the anchored `Y`). No other change to that block.

7. **maxSize / bar widths / colors / badges / glow / Pulled-dash / labels:** unchanged. Only the `y` source changed.

8. **`SetFrame` / `AdvanceAnimation`:** unchanged.

**Steps:**

- [ ] **Step 1: Read the current file** `NinjaTrader/RadarVisual.cs` in full so every `centerY`/`((price - _mid)/_tick)*rowH` site is found (bid loop, ask loop, node loop, mid line+chip, edge markers).
- [ ] **Step 2: Apply the change** exactly as specified above (fields, grid, re-anchor, `Y(...)` local function, gliding mid line+chip, edge markers via `Y`). Remove the obsolete "Auto-fit band" / `centerY` / `rows = 2*half+1` lines.
- [ ] **Step 3: Compile-gate with nt8c** (this is the real gate here):
  ```bash
  bash build/stage-custom.sh
  nt8c build --custom-dir build/.stage/Custom
  ```
  Expected: **0 errors, 0 warnings** for the whole staged Custom set (engine + NT layer + strategy). If you see CS0246/CS0234 on a *per-file* basis from the editor hook, ignore those — the authoritative gate is `nt8c build --custom-dir` compiling all files together. A real error here (wrong type, missing brace, bad `Y` usage) MUST be fixed.
- [ ] **Step 4: Self-review** — confirm: (a) no `centerY` references remain; (b) every row y uses `Y(...)`; (c) the re-anchor `while` loops have the `safety` bound (no infinite loop if `_tick` is bad); (d) the band re-initialises on instrument change (the `_mid` jump guard); (e) the Aurora visuals (brushes, badges, glow) are byte-for-byte unchanged; (f) only `RadarVisual.cs` changed.
- [ ] **Step 5: Commit**
  ```bash
  git add NinjaTrader/RadarVisual.cs
  git commit -m "feat(nt): anchored price ladder — fixed scale, gliding mid marker, edge re-anchor (RadarVisual)"
  ```

---

## Verification (Javier, in Market Replay — the visual gate)

`nt8c` proves it compiles; only Replay proves it looks right. On the Windows box, after deploying the rebuilt Custom:
1. Open Liquidity Radar on an ES Replay session with L2 (verify `medBid/medAsk > 0`).
2. As price moves: the **price column stays fixed**, the bars grow/shrink **in place**, and the **amber mid line + chip glide** up/down through the ladder. The whole profile must NOT jump every tick.
3. When price reaches ~4 ticks of the top/bottom edge, the column **scrolls** by a tick or two (smooth, occasional) to keep price in view.
4. On instrument change / feed reset, the band **re-centres** cleanly (no frozen/blank ladder).
5. Walls, absorbed glow, pulled dashes, remembered dim rows, edge markers, size/price labels — all still render exactly as before, just anchored.

If anything jumps or freezes, capture a short clip / note the instrument+time and we tune `ANCHOR_GUARD_TICKS` / `TARGET_ROW_PX`.

## Self-Review (plan vs spec §4)

- Spec §4 "map screen rows to fixed prices via `anchorTop`, not the live mid" → Steps 1-4.
- "inside-market marker glides to `y(price)`" → Step 5.
- "re-anchor only at the edges, hysteresis buffer (4 ticks)" → Step 3 (`ANCHOR_GUARD_TICKS = 4`).
- "side derived from price vs live price" → already how `RadarTab`/engine label bid/ask; the renderer draws each at its anchored price, so a level crossing the mid naturally flips position. No change needed.
- Reference behavior = mockup Anclado mode (Step reference). No placeholders; the one task is a self-contained, compile-gated, reviewable file change whose final visual gate is Javier's Replay (explicitly noted, not hidden).
