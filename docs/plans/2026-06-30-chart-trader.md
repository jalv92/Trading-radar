# Plan E — Chart Trader (order-entry surface) — build record + risk gate

- **Date:** 2026-06-30
- **Status:** Built, reviewed, **CLEARED for Sim/Playback only**. `trading-risk-manager` **VETO stands for any real-money account** (spec §2/§8 gate).
- **Spec:** `docs/specs/2026-06-29-radar-cockpit-design.md` §8 (Chart Trader), §2 (execution-surface gate), §9 (over-trading risk).
- **Commit:** `eb9ff03`. **Files:** `NinjaTrader/RadarChartTrader.cs` (new), `NinjaTrader/RadarTab.cs` (wired). nt8c staged build 15 files 0/0. AbsorptionScalper + Engine untouched.

## What shipped (MKT-only v1)
Order ticket docked under the Cockpit in RadarTab's right column:
- **BUY / SELL MKT** (neon emerald/coral Aurora), **Rev / Close / Flat**, **qty** stepper, **account** selector (ComboBox on `Account.All`), live **position + PnL** ($ and ticks).
- NT8 Account API: `CreateOrder(…, OrderType.Market, OrderEntry.Manual, TimeInForce.Day, …, Core.Globals.MaxDate, null)` → `Submit`; `Flatten(new[]{inst})`, `CancelAllOrders`; `Position.GetUnrealizedProfitLoss`. Order/Execution/Position updates instrument-filtered + marshaled to UI via `Dispatcher.InvokeAsync`; `Account.Positions`/`Account.All` reads `lock`-ed.

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

**Re-submit for VETO review once F1–F8 (plus F9 if prop) are implemented.** Until then: Sim/Playback testing only.

## How to test (Sim/Playback)
F5 compile in NT8 → **close & reopen** the Liquidity Radar window (open Add-Ons don't refresh on recompile). Select **Sim101** (or a Playback connection) → the ticket is live; BUY/SELL/Rev/Close/Flat operate on the Sim position with live PnL. Selecting a real account disables the buttons and shows ARM LIVE (leave it OFF).
