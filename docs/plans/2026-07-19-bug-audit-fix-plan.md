# 2026-07-19 — Full Bug Audit: Fix Plan (Break + React strategies, NT8 UI/order path)

**Origin:** multi-agent adversarial audit (run `wf_ddf0fa60-e9c`): 8 find lenses -> dedup -> 3 skeptics per finding (code-trace / NT8-runtime / history) -> synthesis. 70 agents, 0 errors. **26 raw -> 20 deduped -> 17 CONFIRMED, 3 refuted.** Every confirmed finding survived >=2 of 3 independent refutation attempts; all line numbers re-verified against HEAD by direct read.

**User-reported symptoms this audit set out to explain:**
- **S1** — the UI sometimes gets stuck/misbehaves after switching instrument (ES <-> NQ).
- **S2** — sometimes it bugs out right after placing a limit order.
- **S3** — the up/down (▲/▼) nudge buttons sometimes do nothing.

**Symptom coverage:** S3 is fully explained by fixes A3+A4 (silently-absorbed Change() while one is in flight; clamp block with Output-tab-only feedback). S2 by A1+A2 (unsynchronized `_ownOrders` race at submit time; uncaught throw in the OnOrderUpdate dispatcher body — either wedges `CanTrade()` and every button until a switch force-clears). S1 by B1-B6 (stale frame painting, Rec CSV cross-instrument blending, `_engineLock` held across blocking Flush() I/O, stale AUTO restore state, swallowed cancel on switch, missing volatile).

## Execution protocol

- Implementer: `trading-ninjascript-developer`; review: `trading-code-reviewer` per batch; `trading-risk-manager` re-check on Batch D and anything touching order submission (A) before the next Playback session.
- Engine invariants: Engine/ stays pure/deterministic (time only via `inp.Now`), C# 7.3 netstandard2.0, no NT8 types, AUTO hard gates stay out of `ControllerConfig` (Phase0GuardTests). No runtime config files (ML-calibration ADR).
- Validation per batch: `dotnet test` (engine, must stay green; C adds 1 new test) + `bash build/stage-custom.sh && nt8c build --custom-dir build/.stage/Custom` (expect 0 errors; per-file `nt8c check` gives false CS0246 on engine types) + deploy the changed `.cs` to `Documents/NinjaTrader 8/bin/Custom/AddOns/LiquidityRadar/`, F5, **close & reopen the radar window**.
- Each fix below carries its own manual Market Replay repro — run them as the batch acceptance checklist.

## Batch A — Order path — RadarChartTrader.cs (symptoms S2 + S3)

_One PR. The two criticals live in OnOrderUpdate and land together; fixes 5 touches the same body and lands right after; 3/4/6 are independent but share the file._

### A1. [CRITICAL] _ownOrders (HashSet<Order>) is read unsynchronized on the NT8 account-event thread while every mutation happens on the UI thread

**Where:** `NinjaTrader/RadarChartTrader.cs:1529`

**Root cause:** `_ownOrders`/`_workingOrders` (RadarChartTrader.cs:60,64) are plain `HashSet<Order>` with zero locking anywhere in the file. `OnOrderUpdate` (line 1522) reads `!_ownOrders.Contains(ord)` at line 1529 on NT8's account-event thread (confirmed by the file's own comment at 1521, "account-thread handlers: marshal any WPF mutation to the UI thread"), BEFORE the `Dispatcher.InvokeAsync` marshal at line 1534. Every mutation runs on the UI thread instead: `.Add` at 2242/2301/2097/1204, `.Remove` at 1575/1576/1608/2109/1222, `.Clear()` at 705-706/1414-1415/1463/2493-2494. A concurrent `Contains` vs `Add`/`Remove`/`Clear`-triggered bucket resize on a non-thread-safe `HashSet<T>` is undefined behavior (can throw or answer wrong), and `OnOrderUpdate` has no try/catch, so a throw here aborts before the Dispatcher continuation that empties `_workingOrders` and calls `RefreshArmUi()` (which gates every ticket button via `CanTrade()`).

**Fix:** Add `private readonly object _orderLock = new object();` near `_workingOrders`/`_ownOrders`. Wrap every access to those two sets in `lock (_orderLock) { ... }`: the `Contains` check at 1529 (snapshot the bool inside the lock before branching), every `.Add`/`.Remove`/`.Clear()` call listed above, and the `.Count` reads in `ValidateForSubmit` (2015) and `CanTrade()` (1362). Keep the lock scope to just the collection access, not the whole handler body.

**Validation:** Not xUnit-testable (WPF/NT8-bound class, no Engine/ counterpart). Validate compile with `nt8c check` on RadarChartTrader.cs (use `nt8c build --custom-dir <staged>` if this touches cross-namespace references). Manual Market Replay repro for Javier: load ES on Market Replay with a Sim/Playback account, click BUY LMT repeatedly (5-10 rapid clicks) while replay runs at 4x-24x speed so OrderUpdate events cluster tightly around each SubmitRaw's `_ownOrders.Add`; confirm no exception appears as a new Diag line in the Output tab, and confirm every ticket button (BUY/SELL/LMT/▲▼/Rev/Close/Flat) re-enables once `_workingOrders` empties — repeat across ~30 min of replay watching for a ticket that stays permanently disabled without an instrument/account switch.

**Dependencies:** None; land before or alongside the OnOrderUpdate try/catch fix since both edit the same function body.

### A2. [CRITICAL] OnOrderUpdate's entire Dispatcher.InvokeAsync body has no try/catch — a throw mid-body permanently wedges every ticket button

**Where:** `NinjaTrader/RadarChartTrader.cs:1534`

**Root cause:** OnOrderUpdate marshals its ~80-line bookkeeping body to `Dispatcher.InvokeAsync((Action)(() => {...}))` (1534-1613) with no enclosing try/catch. This project already proved (commit 39b326b, RadarAddOn window-open path) that an exception thrown inside a `Dispatcher.InvokeAsync` delegate on this NT8 host is silently swallowed — it lands in the discarded `DispatcherOperation.Task`, never the trace. Any throw anywhere in this lambda (e.g. `NinjaTrader.Code.Output.Process` inside `Diag`/`LogAuto` at 1600/1602/1901/1930, or the ATM lookup inside the deferred `SubmitRaw` call at 1598) can abort before the tail `RefreshPositionUi()`/`RefreshArmUi()` (1611-1612) runs — and `RefreshArmUi` is not called from the 33ms paint loop, only from discrete events, so nothing self-heals a stale button state.

**Fix:** Wrap the lambda body in `try { ... } catch (Exception ex) { Diag("OnOrderUpdate: " + ex.Message); } finally { RefreshPositionUi(); RefreshArmUi(); }` — moving the two tail calls into `finally` guarantees the ticket resyncs even if an earlier statement throws. Apply the identical wrap to `OnExecutionUpdate`'s `Dispatcher.InvokeAsync` body (1626-1630).

**Validation:** Not xUnit-testable. `nt8c check` on RadarChartTrader.cs. Manual repro: in Market Replay submit a BUY LMT, then immediately submit a manual opposite-side flip (SELL LMT) before the first order's cancel confirms while deliberately destabilizing the ATM selector (deselect/reselect the template, or trigger this during an account reconnect blip) to try to force an exception inside the deferred SubmitRaw call; confirm the ticket buttons still re-enable afterward and the Output tab shows the new "OnOrderUpdate: ..." Diag line rather than a silent freeze.

**Dependencies:** Land together with/after the _ownOrders locking fix (same function body).

### A3. [HIGH] No in-flight guard around Account.Change() — nudge (▲/▼) and same-side LMT re-anchor can issue overlapping Change() requests, silently absorbing clicks

**Where:** `NinjaTrader/RadarChartTrader.cs:2122`

**Root cause:** `ChangeActiveLimitPrice` (2122-2140), the single choke point used by both `MoveOrder` (▲/▼, 2144-2157) and `SubmitLimit`'s same-side re-anchor branch (2088-2093), only checks `Order.IsTerminalState(ord.OrderState)` before calling `_account.Change(new[]{ord})` — it never tracks that a Change is already in flight. NT8's non-terminal `ChangePending`/`ChangeSubmitted` states are not covered by `IsTerminalState`, and `MoveOrder` computes `newPrice` from `ord.LimitPrice`, which NT8 doesn't update until the pending Change confirms via `OnOrderUpdate`. Two rapid clicks before that confirmation read the same stale price and can issue overlapping/indeterminate Change() requests, matching "the up/down nudge buttons sometimes do nothing."

**Fix:** Add `private bool _changePending;` near `_activeLimit`. In `ChangeActiveLimitPrice`, after the terminal-state guard, add `if (_changePending) { Diag(context + ": change already in flight — ignoring."); return; }`, then set `_changePending = true;` right before the try block; in its `catch`, set `_changePending = false;`. Clear `_changePending = false;` in `OnOrderUpdate`'s terminal branch (near 1577-1578) and its `Working && OrderType.Limit` branch (near 1608-1609) — both already run whenever the pending order settles. This one guard in the shared function protects both `MoveOrder` and `SubmitLimit`'s re-anchor without touching either caller.

**Validation:** Not xUnit-testable. `nt8c check` on RadarChartTrader.cs. Manual repro: rest a working BUY LMT in Market Replay, click ▲ twice as fast as possible (or ▲ then a same-side BUY LMT re-anchor click); confirm the Output tab shows the new "change already in flight — ignoring" Diag for the second click, and the order settles at the FIRST click's intended price rather than an indeterminate one.

**Dependencies:** None.

### A4. [HIGH] MoveOrder's cross-market clamp silently blocks a nudge with zero on-ticket feedback (Diag-only)

**Where:** `NinjaTrader/RadarChartTrader.cs:2154`

**Root cause:** `MoveOrder` (2144-2157) clamps a nudge against `_lastPrice` (book mid) and on a block only calls `Diag(...)` (2154-2155), which writes exclusively to the NT8 Output tab. `RefreshArmUi`'s `canMove` (1813) is `canTrade && _activeLimit != null`, with no dependency on whether the last click was clamped, so a routine "resting limit within one tick of mid" block is visually indistinguishable from a broken/unresponsive button — the ▲/▼ buttons stay enabled and nothing on the ticket changes.

**Fix:** In `MoveOrder`'s two clamp branches (2154-2155), after the existing `Diag(...)` call and before `return;`, add `_warnText.Text = "MOVE BLOCKED — would cross market"; _warnText.Foreground = Coral;` (reusing the existing `_warnText`/`Coral` brush already used by `RefreshArmUi`).

**Validation:** Not xUnit-testable. `nt8c check` on RadarChartTrader.cs. Manual repro: rest a BUY LMT one tick below mid in Market Replay, click ▲ so the target price would equal/cross mid, and confirm `_warnText` now shows the blocked-move message on the ticket itself (persists until the next `RefreshArmUi()`-triggering event) instead of only the Output-tab Diag line.

**Dependencies:** None; can land alongside the Account.Change in-flight guard fix (same function area).

### A5. [HIGH] Partial-fill-during-cancel stacking fix (da1d82d) is scoped to isAuto only — a manual opposite-side LMT flip still stacks a partial position plus a fresh full-qty order

**Where:** `NinjaTrader/RadarChartTrader.cs:1594`

**Root cause:** Commit da1d82d added `bool autoPartialFill = p.IsAuto && ord.Filled > 0;` (1593) gating the deferred resubmit at line 1594 to guard against NT8's documented Cancelled-with-Filled>0 race. `SubmitLimit`'s manual opposite-side flip (2096-2098) builds `_pendingReplace` with the default `IsAuto = false` (field declared at 78-80), so `autoPartialFill` is always false for a manual flip regardless of `ord.Filled` — the exact race the fix targets still lets a full-qty replacement stack on top of a partial fill for the manual path, which is the ONLY currently live-account-capable path in this control (AUTO is Sim/Playback-only).

**Fix:** In `OnOrderUpdate`, change line 1593 from `bool autoPartialFill = p.IsAuto && ord.Filled > 0;` to `bool partialFill = ord.Filled > 0;` and use `!partialFill` at line 1594 — dropping the `p.IsAuto` term entirely so the guard applies to both manual and AUTO replacements. Update the Diag messages at 1599-1601 to drop the auto-specific wording.

**Validation:** Not xUnit-testable. `nt8c check` on RadarChartTrader.cs. Manual repro: in Market Replay rest a 2-lot BUY LMT, click SELL LMT to flip sides at a moment engineered to produce a partial fill during the cancel (thin book, high replay speed near the resting price); confirm the Output tab now shows "pending replace dropped — old order partially filled" for a MANUAL flip (previously only logged for AUTO), and confirm no stacked full-qty SELL LMT appears alongside the partial long position.

**Dependencies:** Land after the two critical OnOrderUpdate fixes (same function body) to avoid re-touching it twice.

### A6. [HIGH] BUY MKT / SELL MKT and Rev never cancel a resting _activeLimit before submitting — only ClosePosition(false) does

**Where:** `NinjaTrader/RadarChartTrader.cs:2020`

**Root cause:** `SubmitMarket` (2020-2024) and `Reverse` (2165-2176) call only `ValidateForSubmit()` before submitting. `ValidateForSubmit` never checks `_activeLimit`, and a resting limit moves OUT of `_workingOrders` once it reaches `OrderState.Working` (line 1608, "resting limit no longer counts as 'in flight'"), so neither function's guard catches it. `ClosePosition(false)` (2183-2202) already calls `CancelActiveLimitIfWorking("close")` (2196) for exactly this hazard ("a resting limit... could otherwise fill later and silently re-open the position"), but that fix was never propagated to its two siblings — confirmed unchanged since the introducing commit (f197eb7).

**Fix:** Add `CancelActiveLimitIfWorking("market entry");` as the first statement of `SubmitMarket` (right after `if (!ValidateForSubmit()) return;`, line 2022) and `CancelActiveLimitIfWorking("reverse");` as the first statement of `Reverse` (right after its own `ValidateForSubmit()` check, line 2167) — mirroring `ClosePosition(false)`'s exact call.

**Validation:** Not xUnit-testable. `nt8c check` on RadarChartTrader.cs. Manual repro: rest a BUY LMT in Market Replay, then click SELL MKT (or Rev, with an open position); confirm the Output tab shows a cancel for the old resting limit at/before the market order fills, and confirm the resting limit never fills independently later on top of the new position.

**Dependencies:** None.

## Batch B — Instrument switch — RadarTab.cs + RadarChartTrader.cs (symptom S1)

_One PR. Fixes 7+8 edit the same Instrument setter; 9 depends lightly on 8; 10/14/15 are ChartTrader-side switch hygiene._

### B1. [HIGH] RadarVisual + CockpitVisual keep painting the OLD instrument's last frame after a switch (never cleared, only overwritten by the next engine run)

**Where:** `NinjaTrader/RadarTab.cs:420`

**Root cause:** The `Instrument` setter (RadarTab.cs:386-434) nulls `_latest` at line 420 and rebuilds the engine, but never calls into `_visual`/`_cockpit`. The paint tick (274-327) only calls `_visual.SetFrame`/`_cockpit.SetFrame` when `_latest != null` (line 283/f-guard). `RadarVisual` caches `_nodes/_bids/_asks/_mid` (RadarVisual.cs:51-54), only overwritten in `SetFrame`; `ResetLadderMemory()` (89-94) only clears ghost-row memory (`_ladderMem`/`_memEvict`/`_anchorTop`), not those live fields, and it's wired only to `_replayResetPending` (276-281), never the `Instrument` setter. `CockpitVisual`'s `_has` flag (55) is only ever set true, never reset. So until the new instrument's book seeds and a full `MaybeRunEngine` run completes (unbounded during a quiet feed or paused replay), both visuals keep rendering the OLD instrument's last prices/walls/banner with no staleness indicator.

**Fix:** Add `public void Clear()` to `RadarVisual` that nulls `_nodes/_bids/_asks`, sets `_mid = 0`, calls the existing `ResetLadderMemory()`, and calls `InvalidateVisual()`. Add `public void Clear()` to `CockpitVisual` that sets `_has = false` and calls `InvalidateVisual()`. Call both from RadarTab's `Instrument` setter, right after `_latest = null;` (line 420).

**Validation:** Not xUnit-testable (WPF FrameworkElement rendering). `nt8c check` on RadarTab.cs/RadarVisual.cs/CockpitVisual.cs. Manual repro: in Market Replay let ES paint a full ladder + cockpit banner, pause replay, switch the instrument selector to NQ, and confirm the ladder/cockpit blank out immediately (no ES prices/banner lingering) instead of staying frozen until NQ's first depth/trade event arrives.

**Dependencies:** None; touches the same Instrument setter as the Rec-writer-roll fix — land together to avoid re-editing the setter twice.

### B2. [HIGH] Rec-toggle CSV writers (_capWriter/_sigWriter) are never rolled or guarded on instrument switch — mid-session switch blends two instruments' rows into one file

**Where:** `NinjaTrader/RadarTab.cs:396`

**Root cause:** `recChk.Checked` (203-235) opens `_capWriter`/`_sigWriter` once, baking the CURRENT instrument's name into the filename. The `Instrument` setter (386-434) never references `_capture`/`_capWriter`/`_sigWriter`/`_prevStates`/`_recArms`/`_recFires`, and the write block in `MaybeRunEngine` (816-924) gates only on `_capture` — no instrument check — so a mid-session switch keeps writing the new instrument's rows into the old instrument's CSV files, and `WriteSessionSummary` (Rec-uncheck) files the accumulated counters under whichever instrument happens to be current.

**Fix:** Promote the local `recChk` to a field (`_recChk`, assigned right after its construction near line 198). In the `Instrument` setter, add `if (_recChk.IsChecked == true) _recChk.IsChecked = false;` right after `_latest = null;` — this reuses the EXISTING `Unchecked` handler (236-242), which already does the correct flush+dispose+`WriteSessionSummary()` sequence, so no new writer-open/close logic is needed.

**Validation:** Not xUnit-testable. `nt8c check` on RadarTab.cs. Manual repro: check Rec on ES, let a few rows accumulate, switch the instrument to NQ mid-session; confirm (a) the Rec checkbox visually unchecks itself, (b) the ES-named CSV files stop receiving rows at the switch timestamp with no NQ-scaled prices mixed in, and (c) a session-summary row is written at the switch.

**Dependencies:** None; batch with the stale-visual-frame fix (same Instrument setter).

### B3. [HIGH] _engineLock held across blocking Rec CSV Flush() I/O — contended by every UI-thread action needing the same lock (instrument switch, Break/React dropdown, Rec toggle)

**Where:** `NinjaTrader/RadarTab.cs:918`

**Root cause:** `OnMarketDepth`/`OnMarketData` (557-563, 570-576) call `MaybeRunEngine` entirely inside `lock (_engineLock)`. Inside it, with Rec on, `_capWriter.Flush()` (855) and `_sigWriter.Flush()` (918) run on every heartbeat/state-change trigger (ctrlStateChanged/holdChanged/reactStateChanged fire far more often than the nominal 2s heartbeat), still holding `_engineLock` on the instrument thread. `StreamWriter.Flush()` is a blocking OS write into a `Documents\NinjaTrader 8\LiquidityRadar` path that can be OneDrive-redirected/AV-scanned. Every UI-thread caller of the same lock (`Instrument` setter, `OnSetupChanged`, the Rec checkbox handler, `WriteSessionSummary`) blocks for however long that flush takes.

**Fix:** Delete the two explicit `.Flush()` calls at lines 855 and 918 (StreamWriter's `AutoFlush` is already `false` by default here, so `WriteLine` just appends to an in-process buffer — cheap, non-blocking). Rely on the existing `.Dispose()` calls (Rec-uncheck 239-240, `Cleanup()` 1207-1209, and the roll-on-switch added by the Rec-writer fix) to flush on close. Mark with `// ponytail: trades a small crash-durability window (unflushed rows since last dispose) for removing lock-held blocking I/O from the hot path — acceptable for an opt-in diagnostic feature.`

**Validation:** Not xUnit-testable. `nt8c check` on RadarTab.cs. Manual repro: check Rec during an active Market Replay session, then rapidly toggle the Break/React dropdown and the instrument selector several times in a row; confirm the UI never visibly stalls during those toggles.

**Dependencies:** Do the Rec-writer-roll fix first for cleanliness (not a hard dependency).

### B4. [HIGH] Instrument switch never clears pending workspace-restore state — stale AUTO fire-count/ATM/account intent leaks onto the new instrument

**Where:** `NinjaTrader/RadarChartTrader.cs:689`

**Root cause:** `RestoreAutoState` (2394+) sets `_pendingAtmRestoreName`/`_pendingRestoreAccountName`/`_pendingRestoreFireDay`/`_pendingRestoreFireCount` (fields declared 203-210). `OnReplayReset` (1443-1482) explicitly clears `_pendingRestoreFireDay` (1455, "a persisted count is for the pre-rewind pass — never reseed it after a restart"), but the `Instrument` setter (689-721) and `OnAccountSelected` (1401-1427) — the two OTHER engine-state-swap sites — never touch any of the four pending-restore fields. `TryAutoFire`'s day-change branch (1085-1092) can therefore seed the NEW instrument/account's `_autoFireCount` from a stale persisted count, and `MaybeResolveAtmRestore` can auto-select a stale ATM template, after a switch.

**Fix:** In the `Instrument` setter, add `_pendingAtmRestoreName = null; _pendingRestoreAccountName = null; _pendingRestoreFireDay = DateTime.MinValue; _pendingRestoreFireCount = 0;` right after the existing `_pendingFireCtx = null;` line (703). Add the identical four-line reset to `OnAccountSelected` right after its own `_pendingFireCtx = null;` (1413) — mirroring `OnReplayReset`'s existing pattern (1455) exactly at both sites.

**Validation:** Not xUnit-testable. `nt8c check` on RadarChartTrader.cs. Manual repro: reopen the radar workspace mid-day after some AUTO fires (persisted count > 0), immediately switch instrument (or account) before AUTO fires again, then arm AUTO and let a fresh setup fire on the new instrument; confirm the DiagAuto "submit" line shows a 0/<cap>-based count for the new instrument, not the carried-over count, and confirm no stale ATM auto-selects on the new instrument/account.

**Dependencies:** None.

### B5. [MEDIUM] RadarChartTrader._instrument field is not volatile despite being read cross-thread in OnOrderUpdate/OnExecutionUpdate — RadarTab's identical field already got this fix

**Where:** `NinjaTrader/RadarChartTrader.cs:52`

**Root cause:** `private Instrument _instrument;` (line 52) has no `volatile`, despite being written only on the UI thread (`Instrument` setter, 696) and read cross-thread in `OnOrderUpdate` (1524) and `OnExecutionUpdate` (1619) before their `Dispatcher.InvokeAsync` marshals — the identical pattern already fixed with `volatile` for RadarTab.cs's own `_instrument` field (commit a60117a) and for this same file's own `_autoArmed` field (line 178), but never applied here.

**Fix:** Change line 52 to `private volatile Instrument _instrument;`.

**Validation:** Not xUnit-testable (compiler-level memory-model fix, no observable behavior). `nt8c check` on RadarChartTrader.cs is the whole verification — confirm it still compiles with the `volatile` modifier on a reference-type field. No manual Market Replay repro is meaningful for this one; it's a correctness-by-convention fix matching the file's own established pattern, not a reproducible symptom.

**Dependencies:** None; trivial one-word fix, land any time.

### B6. [MEDIUM] CancelActiveLimitIfWorking swallows a Cancel() failure with no rollback — instrument/account switch can orphan a still-live resting order

**Where:** `NinjaTrader/RadarChartTrader.cs:1496`

**Root cause:** `CancelActiveLimitIfWorking` (1496-1503) calls `_account.Cancel(new[]{ord})` in a try/catch that only `Diag`'s the exception, returning nothing. The `Instrument` setter (695) and `OnAccountSelected` (1405) both call it and then unconditionally null `_activeLimit`/`_autoOrder`/clear `_workingOrders`/`_ownOrders` regardless of whether the cancel reached the broker — unlike `SubmitLimit`'s opposite-side flip (2099-2112), which correctly rolls back on the identical `Cancel()` throw. If the cancel fails during a switch, the still-resting order becomes untracked: its `Instrument`/`Account` no longer match the reassigned `_instrument`/`_account`, so `OnOrderUpdate`'s ownership gate silently drops all future events for it.

**Fix:** Change `CancelActiveLimitIfWorking` to `return bool` (true on success or nothing-to-cancel, false if `Cancel()` threw). At the two call sites (`Instrument` setter:695, `OnAccountSelected`:1405), when it returns false, set `_warnText.Text = "CANCEL FAILED on switch — check orders manually"; _warnText.Foreground = Coral;` before proceeding with the rest of the switch (blocking the switch itself is not safe for a live trading UI; the fix is prominent visibility, not rollback).

**Validation:** Not xUnit-testable. `nt8c check` on RadarChartTrader.cs. Manual repro: forcing `Account.Cancel` to throw synchronously in Sim/Playback is rare (connectivity-blip path) — as a smoke check, temporarily inject a throw in a local debug build of `CancelActiveLimitIfWorking` and switch instrument/account, confirming `_warnText` shows the new warning instead of the switch silently succeeding with only an Output-tab trace.

**Dependencies:** None.

## Batch C — Strategy correctness — Engine + strategy swap (Break/React)

_One PR. Fix 11 is the only Engine/ change and carries the only new xUnit test; 12 and 16 are the RadarTab swap-hygiene pair._

### C1. [HIGH] Fired state has no timeout — a wall retested near the break point latches that side forever

**Where:** `Engine/ControllerStateMachine.cs:249`

**Root cause:** `StepLong`'s `case SideState.Fired:` (249-256, mirrored in `StepShort` 408-415) exits only on `cur <= 0` (near-impossible in an active book) or `Math.Abs(inp.Mid - c.WallPrice) >= _cfg.AwayTicks * _tick` (price moves 6 ticks / 1.5pt away, `AwayTicks` defaults to 6, tick 0.25). There is no elapsed-time exit, so a post-break consolidation/chop within 1.5pt of the break level (a common NQ/ES pattern) latches that side `Fired` indefinitely — blocking a fresh Waiting→Armed→Countdown cycle and leaving the cockpit's "SETUP LONG/SHORT" banner stuck stale. Independently verified with a scratch xUnit probe: 1 hour of ticks pinned 0.40pt from the wall price with nonzero fed size left `o.Long` still `Fired`.

**Fix:** Add `public double MaxFiredSeconds = 90;` to `ControllerConfig` (near `AwayTicks`/`Cooldown`, same "MEASURED later" placeholder convention as the file's other thresholds — the name matches none of Phase0GuardTests' forbidden tokens: AutoFire/AutoCancel/Cancel/Atm/Account/Sim/Playback/ArmLive/Provider, so it stays a legitimate calibratable engine knob, not an AUTO hard gate). In both `StepLong`'s and `StepShort`'s `case SideState.Fired:`, extend the exit condition to `|| (inp.Now - c.LastFire.Time).TotalSeconds >= _cfg.MaxFiredSeconds` — reusing `c.LastFire.Time` (already latched at the fire tick, line 335), so no new `Candidate` field is needed. Time flows only through `inp.Now`, preserving the engine's pure/deterministic invariant.

**Validation:** New xUnit fact in Tests/ControllerStateMachineTests.cs, modeled on the existing `Fired_long_resets_on_false_break_reversal_then_rearms_after_cooldown` (~line 397): using the file's own `Machine()`/`In(...)`/`T(s)` helpers, drive the Long candidate through Armed→Countdown→Fired, then call `m.Update(...)` again with `Mid` held within the AwayTicks band (e.g. 0.40pt from WallPrice) and `WallAboveCurrent` nonzero at `sec` just under `MaxFiredSeconds` (assert still `SideState.Fired`) and again past it (e.g. `sec: 200`, assert `SideState.Waiting`). Run `dotnet test` in Tests/.

**Dependencies:** None.

### C2. [HIGH] Strategy dropdown swap does not clear the pending-fire latch — stale auto-aggressive React fire executes after switching to Break

**Where:** `NinjaTrader/RadarTab.cs:341`

**Root cause:** `OnSetupChanged` (341-352) swaps `_activeSetup` and rebuilds only the controller being switched TO under `_engineLock`, resetting `_lastCtrl`/`_lastReactState`/`_lastReactAbandon` — but never clears `_pendingFire`/`_pendingFireSet`, unlike the `Instrument` setter (423) and `HandleReplayReset` (1091), both of which explicitly clear it ("a pre-switch fire must not fire into the new instrument/rewind"). A Reactive fire latched just before the user flips the dropdown to Break is still delivered to `_chartTrader.OnSetupFire` on the next paint tick; `ReactiveExecution.Route` sets `route.AutoAggressive = true` for any Reactive-kind fire, which waives `TryAutoFire`'s manual ARM checkbox (guard 1a) — an unexpected order can submit into the just-selected Break session.

**Fix:** Add `_pendingFireSet = false;` inside `OnSetupChanged`'s existing `lock (_engineLock) { ... }` block (341-352), matching the identical one-line pattern already at RadarTab.cs:423 and :1091.

**Validation:** Not xUnit-testable (RadarTab is NT-bound UI glue, excluded from the Tests/ project). `nt8c check` on RadarTab.cs. Manual repro: in Market Replay select React, let it reach Watching on a wall, let (or engineer) a Consumed/trade-backed resolution so a fire latches, then within the ~33ms paint-tick window switch the Strategy dropdown to Break; confirm no auto order submits on the Break session (no unexpected DiagAuto "submit" line right after the switch).

**Dependencies:** None; can land alongside the React-telemetry-leak fix (same RadarTab.cs area).

### C3. [MEDIUM] React telemetry columns (reactWallPx/reactWallSide/reactAbandon) leak stale values into Break-active CSV rows

**Where:** `NinjaTrader/RadarTab.cs:916`

**Root cause:** The sig CSV row (890-917) writes `_reactive.LatchedWallPrice`/`_reactive.LatchedSide`/`_reactive.LastAbandon` unconditionally at line 916, while the adjacent `reactState` column (915) IS correctly gated on `_activeSetup == SetupKind.Reactive`. `OnSetupChanged` (341-352) never steps or resets `_reactive` when switching TO Break (`_reactive.Update` only runs while Reactive is active, per lines 738-740), so these three fields freeze at whatever `_reactive` held at the switch instant and leak into every subsequent Break-active row — contradicting the file's own comment (898-900) claiming they "read 0/default naturally."

**Fix:** Gate the three fields at line 916 the same way `reactState` already is at 915: `_activeSetup == SetupKind.Reactive ? _reactive.LatchedWallPrice : 0.0`, `_activeSetup == SetupKind.Reactive ? _reactive.LatchedSide.ToString() : ""`, `_activeSetup == SetupKind.Reactive ? _reactive.LastAbandon.ToString() : ""`. Correct the now-inaccurate comment at 898-900 to state these are explicitly gated, not naturally-defaulting.

**Validation:** Not xUnit-testable (CSV-writer glue in NT-bound RadarTab.cs). `nt8c check` on RadarTab.cs. Manual repro: check Rec, run React until it Watches/Fires/Abandons a wall (non-zero LatchedWallPrice / non-None LastAbandon), switch the Strategy dropdown to Break, and confirm the next several lr-signals-*.csv rows show reactWallPx/reactWallSide/reactAbandon as 0/empty/empty (matching the already-correct reactState column) instead of the frozen React values.

**Dependencies:** None; can land alongside the pending-fire-latch fix (same RadarTab.cs/OnSetupChanged area).

## Batch D — Risk guardrail — HOURS flatten retry

_One PR. Mirrors CheckDailyPnlLimit's existing retry-until-confirmed-flat pattern._

### D1. [HIGH] 16:00 HOURS forced-flatten has no confirmed-flat retry — a single blocked Account.Flatten() call burns the once-per-day latch and the position/order is never retried

**Where:** `NinjaTrader/RadarChartTrader.cs:819`

**Root cause:** `MaybeHoursFlatten` (816-829) sets `_hoursFlattenDay = _now.Date;` (820) BEFORE attempting anything, then calls `ClosePosition(cancelOrdersFirst: true)` exactly once for the day. `ClosePosition` opens with `if (!ValidateForSubmit()) return;` (2185), which fails silently (Diag-only) whenever `_workingOrders.Count > 0` or the account isn't tradable — so an order in flight (or ARM LIVE unchecked on a real account) at the exact tick 16:00 crosses burns the once-per-day latch with no flatten ever sent, in contrast to `CheckDailyPnlLimit` (883-924), which deliberately doesn't latch success until `IsFlat && _activeLimit==null && _workingOrders.Count==0` is confirmed, retrying every `DailyKillRetrySeconds`.

**Fix:** Add `private DateTime _hoursFlattenRetryAt = DateTime.MinValue;` near `_hoursFlattenDay` (302). Restructure `MaybeHoursFlatten` to mirror `CheckDailyPnlLimit`: return early if `_now.Date == _hoursFlattenDay`; if already flat (`IsFlat(CurrentPosition()) && _activeLimit == null && _workingOrders.Count == 0`), set `_hoursFlattenDay = _now.Date;` and return (confirmed done, no action); otherwise throttle with `if (_hoursFlattenRetryAt != DateTime.MinValue && (_now - _hoursFlattenRetryAt).TotalSeconds < DailyKillRetrySeconds) return; _hoursFlattenRetryAt = _now;` before calling `ClosePosition`/`ForceDisarmAuto` — do NOT set `_hoursFlattenDay` on this retry branch, only once confirmed flat on a later tick. Also add `_hoursFlattenRetryAt = DateTime.MinValue;` to `OnReplayReset` (near line 1448, alongside the existing `_hoursFlattenDay` reset).

**Validation:** Not xUnit-testable. `nt8c check` on RadarChartTrader.cs. Manual repro: in Market Replay, arm AUTO with an open position/working limit at 15:59:58, and either leave a manual opposite-side flip's cancel in flight exactly as the clock crosses 16:00, or uncheck ARM LIVE on a non-Sim account beforehand with an open position; confirm the `hours_flatten` DiagAuto line repeats every `DailyKillRetrySeconds` until confirmed flat, rather than firing once and going silent for the rest of the day.

**Dependencies:** None.

## Deferred (explicitly NOT fixed now)

### [LOW] AdaptiveSignificance sampled from raw book levels, not wall candidates (confirmed, already-documented open item)

**Where:** `NinjaTrader/RadarTab.cs:623`

**Root cause:** `MaybeRunEngine` (620-626) samples every raw resting book level's `Volume` into `_depthBase` (a P85 percentile estimator) instead of wall-candidate sizes, exactly as measured and documented in docs/2026-07-03-multiday-analysis-adaptation-verdict.md (raw-book P85=29-45 vs wall-population p85=70-96). Because `Math.Max(SignificanceBand, AdaptiveSignificance)` (ControllerStateMachine.cs:232/391, ReactiveController.cs:110) means the inert adaptive term can only raise, never lower, the compiled floor (60), this is structurally a "missed selectivity" gap, not a correctness or risk bug — the compiled floor does the real gating today.

**Why deferred:** No code change now. The source doc explicitly calls for an offline simulation against the multi-day Rec CSV corpus (rebasing the sample feed to wall-candidate sizes and confirming the resulting P85 lands in a useful range) BEFORE wiring any fix, to avoid patching blind. Re-surface as item 4 of that doc's own next-steps list when calibration work resumes.

**Gate to act:** Not a code fix — the "test" is the offline simulation the doc already specifies: re-run the depth-baseline sampling fed from wall-candidate sizes (not raw book levels) against the existing multi-day Rec CSV corpus and confirm the resulting P85 lands in the previously-measured 70-96 range before touching RadarTab.cs:623-624.

> NOTE (NQ campaign): the NQ preset sets `SignificanceBand = 12` counting on the adaptive term to lift the arm bar on deep walls. With the current raw-book feed the adaptive term is near-inert, so the effective NQ arm gate is ~12 — watch the `adaptiveSig` column and the arms/hr rate on the day-1 NQ capture before concluding anything about NQ thresholds.

## Refuted findings (for the record — do NOT re-report)

- **Manual opposite-side LMT flip and AUTO's re-mount both write the single _pendingReplace slot with no mutual guard — race can silently submit the engine's re-anchor instead of the user's manual flip** (`NinjaTrader/RadarChartTrader.cs:1205`) — trace: Traced both write sites of _pendingReplace (SubmitLimit's flip branch, line 2098, and RemountAutoLimit, line 1205). Both are only reachable after their caller's ValidateForSubmit() check (SubmitLimit line 2066, TryAutoFire line 1042), which returns false whenever _workingOrders.Count > 0. Both write sites synchronously add the order-to-be-cancelled to _workingOrders (lines 2097 and 1204 respectively) in the same call that stashes _pendingR
- **ReloadFrac veto re-anchors Peak to the tick that trips it, so a 1-contract quote-noise uptick instantly reload-vetoes a live Countdown** (`Engine/ControllerStateMachine.cs:270`) — trace: Traced the exact path in Engine/ControllerStateMachine.cs. The mechanics as literally described are accurate: line 270 (`if (cur > c.Peak) c.Peak = cur;`) runs before the reload-veto check at line 276 (`if (cur - c.Min >= _cfg.ReloadFrac * c.Peak)`), so when `cur` sets a fresh peak, `c.Peak` already equals `cur` at check time. But the finding's causal diagnosis and its own worked example don't survive the algebra. Let P = pre-tick peak, m 
- **OnSetupFire's FireEvent.Time dedupe latch (_lastFireTime) survives an instrument switch — can silently swallow the first post-switch setup fire** (`NinjaTrader/RadarChartTrader.cs:961`) — runtime: Verified the code as described: `_lastFireTime` (RadarChartTrader.cs:116) is only reset in `OnReplayReset` (line 1462, with the exact comment quoted), and is NOT reset in the `Instrument` setter (689-721) or `OnAccountSelected` (1401-1427). So the literal code-gap claim is accurate. However, the failure scenario requires platform behavior that contradicts how this engine actually works, and the analogy to OnReplayReset is broken: 1. OnRe

## Suggested landing order

A (criticals first, S2/S3) -> B (S1) -> C (strategy correctness; C2 is the one live-risk item: a stale auto-aggressive React fire executing after a swap to Break) -> D (HOURS retry). A and B are independent and can be built in parallel by two `trading-ninjascript-developer` runs IF each batch stays inside its own PR; C/D after, they're small.

## Execution log (2026-07-19)

**All four batches IMPLEMENTED same-day.** Validation: `dotnet test` **149/149** (148 prior + the
new C1 regression fact `Fired_releases_via_elapsed_timeout_when_price_consolidates_inside_away_band`),
staged `nt8c build --custom-dir build/.stage/Custom` **24 files, 0 errors, 0 warnings**.

Two deliberate deviations from the synthesis text (both root-cause-preserving):

1. **B2 placement.** The Rec uncheck runs at the TOP of the Instrument setter (before `Unsubscribe()`,
   outside `_engineLock`), not after `_latest = null;` inside the lock as drafted — `WriteSessionSummary`
   files the session ledger under `_instrument` and takes `_engineLock` itself, so the drafted placement
   would have credited the session to the NEW instrument (the exact cross-contamination B2 fixes) and run
   flush I/O inside the lock (the exact contention B3 removes).
2. **A3 hardening.** `_changePending` is additionally cleared on instrument switch, account switch and
   replay reset — the synthesis only cleared it in OnOrderUpdate's settle branches, but those are gated on
   `_ownOrders`, which every switch clears; without the extra resets a change in flight across a switch
   would have left the nudge buttons permanently "in flight" (a new wedge).

**Deferred item resolved by simulation (not by wiring).** `tools/measure/adaptive_sig_feed_sim.py` ran the
multiday-verdict-mandated offline simulation over the full Rec corpus (13 ES + 4 NQ + 1 X sessions, 224k
dominant-wall samples):

- **ES:** wall-candidate P85 = **67–96** per session (aggregate 72) — matches the verdict's predicted
  70–96 — vs the current raw-book adaptiveSig P85 of 41–56 (**>99.9% of rows below the 60 floor**: the
  shipped adaptive gate is confirmed inert). Wiring the wall feed would RAISE the ES arm bar from 60 to
  ~72–96 — a calibration change, not a bug fix → **not wired**, per the audit's own fix-17 verdict and
  the ML-calibration ADR (instrument first, adapt later).
- **NQ (4 real captures, 2026-07-04):** dominant-wall P85 = **6–7 contracts — BELOW the NQ preset floor
  of 12**. The wall feed would be inert on NQ too, and it flags that `SignificanceBand = 12` may be HIGH
  for NQ rather than low. Feed this into the NQ day-1 calibration (re-derive the preset from the fresh
  RTH captures) instead of wiring the feed blind.
