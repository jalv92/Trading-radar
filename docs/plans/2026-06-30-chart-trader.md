# Plan E — Chart Trader (order-entry surface) — build record + risk gate

- **Date:** 2026-06-30
- **Status:** Built, reviewed, **CLEARED for Sim/Playback only**. `trading-risk-manager` **VETO stands for any real-money account** (spec §2/§8 gate).
- **Spec:** `docs/specs/2026-06-29-radar-cockpit-design.md` §8 (Chart Trader), §2 (execution-surface gate), §9 (over-trading risk).
- **Commit:** `eb9ff03`. **Files:** `NinjaTrader/RadarChartTrader.cs` (new), `NinjaTrader/RadarTab.cs` (wired). nt8c staged build 15 files 0/0. AbsorptionScalper + Engine untouched.

## What shipped (v1 — MKT + wall-anchored LMT)
Order ticket docked under the Cockpit in RadarTab's right column. **Layout (NT8-style, top→bottom):** BUY/SELL MKT → BUY/SELL LMT → move ▲/▼ → Rev/Close/Flat (3 equal cols) → account+qty (one row) → PnL bar.
- **MKT:** `CreateOrder(…, OrderType.Market, OrderEntry.Manual, TimeInForce.Day, …, Core.Globals.MaxDate, null)` → `Submit`; `Flatten`, `CancelAllOrders`; `Position.GetUnrealizedProfitLoss`.
- **Wall-anchored LMT** (`LimitAnchorPrice`): SELL LMT = biggest wall above mid + 1 tick; BUY LMT = biggest wall below mid − 1 tick (one-shot at submit, does NOT chase the wall). Fallback mid±1 tick when no wall on that side. Wall computed per engine run in `RadarTab.MaybeRunEngine`, pushed via `SetContext(mid, wallAbove, wallBelow, tick)`.
- **Move ▲/▼:** `Order.LimitPriceChanged` + `Account.Change` (preserves queue priority, guarded by `Order.IsTerminalState`). Clamped non-marketable against `_lastPrice` mid.
- **One working limit:** same-side re-click re-anchors via `Change`; opposite-side flip sequences cancel→submit on the `Cancelled` ack (`_pendingReplace`); Cancel-throw leaves state intact. `Cleanup`/instrument-switch/account-switch/`Close` cancel the resting limit first.
- **Ladder marker:** `RadarChartTrader.TryGetActiveOrder` → `RadarTab` paint tick → `RadarVisual.SetActiveOrder` draws a dashed side-colored line + gutter/edge tag at the order price; moves with the order, clears on fill/cancel.
- Order/Execution/Position updates instrument-filtered + marshaled to UI via `Dispatcher.InvokeAsync`; `Account.Positions`/`Account.All` reads `lock`-ed; `_activeLimit`/`_workingOrders` UI-thread confined.

## Safety gate (implemented)
- **Default Sim/Playback.** Real broker/prop account is **BLOCKED** unless the per-account **ARM LIVE** checkbox is set; arming is tracked per account (`_armedFor`, reference-equal) and reset on every account switch.
- **`IsSimAccount` fail-closed:** only `Provider.Simulator`/`Provider.Playback` classify Sim; any exception or other value ⇒ real (arming required). No name-based classification.
- **Single submit choke point** (`ValidateForSubmit`); **in-flight guard** (`_workingOrders`) blocks double-fire while an order is working.

## Risk VETO — hard preconditions before this may EVER touch a real account
(From `trading-risk-manager`, 2026-06-30. Deferred here; MKT-only v1 is Sim/Playback only.)
1. **F1** — server-resting protective stop attached at entry (bracket or ATM). No naked MKT on real. *(top blocker)*
2. **F2** — in-code per-order max qty + max-net-position clamp (don't trust broker-side limits).
3. **F3** — confirm-on-live dialog for real accounts (also narrows double-fire) and/or disable-during-submit. *(partially mitigated: in-flight guard added.)*
4. **F4** — `IsSimAccount` fail-closed. **DONE.**
5. **F5** — arm resets on every account change. **DONE.**
6. **F6** — Rev/Close cancel working orders for the instrument first (robust to partial fills).
7. **F7** — shared-account interference: dedicated account or scope ops to this ticket's own order ids (Flatten/Close/Rev currently net the whole account+instrument position — collides with AbsorptionScalper/ATM on the same account).
8. **F8** — gate submit on `Connection.Status == Connected`; flag PnL stale when the mid stops updating.
9. **F9** — if the real target is a **prop firm**: daily-loss limit, max-trades/day, loss-streak cooldown (button lockout).
10. **F10** — surface order rejections in the ticket UI, not only the Output tab.

### Added by the LMT / move re-review (2026-06-30) — real-account only
- **F1/F2/F6 widened:** F1 (server stop at entry) now also covers a **filled resting limit** (fills while unattended — more acute). F2 (qty clamp) must also bound LMT qty + the `Reverse` ×2. F6 (Close cancels working orders) is now load-bearing — **DONE for this control's own limit** (Close/Flat/teardown/switch cancel `_activeLimit`); a broader "cancel all my working orders" still owed for real.
- **F11 (BLOCKER) — DONE for this control:** orphaned working limit on teardown/context-switch — `Cleanup`/instrument-switch/account-switch now `Cancel` the resting limit. (A hard-warn/confirm on window-close still nice-to-have.)
- **F12 (BLOCKER) — DONE:** cancel/replace sequenced (same-side `Change`, opposite-side cancel→submit on ack; no double-rest, no orphan on Cancel-throw).
- **F13 — MAJOR (deferred):** no wall on the required side → LMT silently rests at mid±1 tick (near market). Before real: block or require explicit confirm (ties to F10). `ponytail:` note at `LimitAnchorPrice`.
- **F14 — MAJOR (deferred):** LMT submit + move key off the possibly-stale `_lastPrice` mid. A frozen mid (feed stall) defeats the non-marketable clamp. Before real: gate on fresh-quote/`Connection.Status == Connected` and clamp/anchor against the real best bid/ask (needs L2 quotes piped into the ticket).

### Added by the ATM re-review (2026-06-30) — real-account only
- **F1 — re-scoped (still top blocker).** ATM *optionally* attaches a server-resting SL/TP bracket (partially implements F1) but is None-default + optional + degrades to plain on attach-failure. On real: mandate a bracket (ATM or code SL/TP), block the plain-entry path, and hard-block the degrade-to-plain fallback.
- **F15 (BLOCKER) — DONE:** own-order tracking. `_ownOrders` (seeded at `CreateOrder`) gates `OnOrderUpdate`, so ATM bracket legs / other account orders can never be captured as `_activeLimit` (the marker / ▲▼ / one-limit logic only ever act on orders this control submitted). Also closes F7 for the marker path.
- **F16 (MAJOR) — DONE:** ATM attaches only on explicit user selection (`DropDownClosed` gate, reset on account/instrument change) — never from the selector's async auto-populate.
- **F17 (MAJOR) — DONE:** ATM-attach-failure only falls back to `Submit` when the order is still `Initialized` — no duplicate entry.

**Real-money VETO stands; preconditions are now F1(re-scoped)–F17** (F11/F12/F15/F16/F17 done; F1/F2/F6/F7 widened; F13/F14 + F1(bracket-mandate)/F2/F9/F10 deferred). Re-submit for VETO review once F1(re-scoped)–F8 and F13/F14 (plus F9 if prop) are implemented. Until then: **Sim/Playback testing only.**

### Sim test checklist for the ATM path (before trusting it)
1. Open the tab, pick a Sim/Playback account + instrument → confirm the **ATM box shows nothing pre-selected** (blank), i.e. no auto-picked template.
2. Select an ATM template, take a bracketed BUY/SELL MKT → let it fill → confirm the ATM's stop/target appear at the broker AND that the **ladder marker / ▲▼ do NOT latch onto the ATM's target** (F15 check — they should stay tied only to your own manual LMT, if any).
3. With an ATM position open, click **Close** → confirm the position flattens and the ATM bracket legs don't leave an orphaned stop.

## How to test (Sim/Playback)
F5 compile in NT8 → **close & reopen** the Liquidity Radar window (open Add-Ons don't refresh on recompile). Select **Sim101** (or a Playback connection) → the ticket is live; BUY/SELL/Rev/Close/Flat operate on the Sim position with live PnL. Selecting a real account disables the buttons and shows ARM LIVE (leave it OFF).
