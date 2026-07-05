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
- **F14 — MAJOR (deferred):** LMT submit + move key off the possibly-stale `_lastPrice` mid. A frozen mid (feed stall) defeats the non-marketable clamp. Before real: gate on fresh-quote/`Connection.Status == Connected` and clamp/anchor against the real best bid/ask (needs L2 quotes piped into the ticket). **Widened (2026-07-01):** now also scopes the Task-12 pre-stage price (`OnSetupFire` derives it from `_lastPrice`/`f.WallPrice`, same mid proxy) and the missing click-time clamp called out in F18.

### Added by the ATM re-review (2026-06-30) — real-account only
- **F1 — re-scoped (still top blocker).** ATM *optionally* attaches a server-resting SL/TP bracket (partially implements F1) but is None-default + optional + degrades to plain on attach-failure. On real: mandate a bracket (ATM or code SL/TP), block the plain-entry path, and hard-block the degrade-to-plain fallback.
- **F15 (BLOCKER) — DONE:** own-order tracking. `_ownOrders` (seeded at `CreateOrder`) gates `OnOrderUpdate`, so ATM bracket legs / other account orders can never be captured as `_activeLimit` (the marker / ▲▼ / one-limit logic only ever act on orders this control submitted). Also closes F7 for the marker path.
- **F16 (MAJOR) — DONE:** ATM attaches only on explicit user selection (`DropDownClosed` gate, reset on account/instrument change) — never from the selector's async auto-populate.
- **F17 (MAJOR) — DONE:** ATM-attach-failure only falls back to `Submit` when the order is still `Initialized` — no duplicate entry.

### Added by the Consumption-Break pre-stage re-review (2026-07-01) — real-account only
(From `trading-risk-manager`, reviewing commit `43b9934` — Task 12: on a Controller fire, `OnSetupFire` pre-stages a break-direction LMT price+side for the NEXT manual BUY/SELL LMT click and lights a "SETUP LONG/SHORT listo" glow. **Posture verified against the code, not the summary:** `OnSetupFire` creates no `Order` and never calls any `Account.*` — it only sets `_pendingSetup` (two fields) + paints UI. Submit still routes through `SubmitLimit` → `ValidateForSubmit` → `CanTrade` (`IsSimAccount` fail-closed + ARM LIVE per-account) → `SubmitRaw`, all unchanged; the pre-stage only substitutes the `price` value. Sim/Playback default-select unchanged. **No auto-submit, no new path to a real account. Sim/Playback clearance STANDS; real-account VETO unchanged.**)
- **F18 (BLOCKER) — new:** the pre-stage price is computed once at fire time and **latched indefinitely** — it is not re-priced on `SetContext` mid updates, has no staleness timeout, and `SubmitLimit` submits `setup.Price` verbatim with **no click-time marketability re-check** (unlike `LimitAnchorPrice`/`MoveOrder`, which clamp non-marketable). Failure scenario: the Controller fires a LONG on a wall being eaten; the break fails and price reverses **below** the latched buy-limit (at/near the ask-wall — or the `_lastPrice<=0` fallback = `f.WallPrice`) before the human clicks. That buy limit is now **marketable** → fills instantly into the reversal, converting a conditional "join the break" limit into an unconditional marketable entry, and breaking the "1 tick beyond the wall" structural-stop sizing (spec §10) — the stop can land on the wrong side of the fill. This is worse than the naked MKT buttons because the tool **actively suggested** the price and the glow invites a fast click in exactly the moment reversal risk is highest. Repro: Playback an ES break that fails; let the Controller fire; wait for the reversal; click the still-lit LMT → observe the marketable fill against the stale price. Before real: at click time either (a) re-clamp `setup.Price` non-marketable against real best bid/ask (mid proxy until F14), or (b) **fail-safe — block the submit + drop the stale pre-stage with a Diag ("pre-stage stale — re-evaluate")** so a stale break signal never auto-becomes a marketable fill; **plus** auto-clear the pre-stage when the Controller's `Fired` latch resets (its lifetime is currently decoupled from the signal's own validity). *Sim/Playback: not blocking (no capital) — but Diag the marketable-at-click case so the calibration pass sees how often it happens.*
- **F19 (MAJOR) — new:** `OnSetupFire` lights the "SETUP LONG/SHORT listo" label + LMT-button glow **regardless of account/arm state and on UNMEASURED placeholder thresholds** (spec §9 calibration not yet done). On a non-armed **real** account the button is disabled but still glows a "ready" label — an active inducement to check ARM LIVE and take an uncalibrated signal, on the exact account where it must NOT be trusted. Before real: (1) the "listo" affordance must not render as actionable/"ready" on a non-armed real account (suppress it, or render it explicitly as "SIM ONLY / not armed"), and (2) carry an "UNCALIBRATED" qualifier until the Rec-CSV pass (spec §9) validates the thresholds vs baseline. *Sim/Playback: MINOR — add a "calibrating" tag so trust isn't built on placeholder fires.*
- **F1 (re-scoped) now also covers the pre-stage entry — no new item, note the mitigation:** a pre-staged LMT click routes through `SubmitRaw(isEntry:true)`, so on real it is subject to the same mandatory-bracket rule (ATM or code SL/TP) as every other entry. The **pre-stage + ATM interaction is intended and mitigating** — the ATM bracket is the F1 stop that would protect even a stale F18 fill (bracket offsets are ATM-template ticks from the actual fill, not the structural stop, but it is a server-resting stop). The F1 bracket mandate must not be considered satisfied by the pre-stage path alone without a stop attached.

### Added by the AUTO-mode re-review (2026-07-01) – real-account only
(From `trading-risk-manager`, reviewing commit `de91b2b` – AUTO mode auto-submits the pre-staged break-direction
limit on a Controller fire. **Posture verified against the code, not the summary:** AUTO arms ONLY via `TryArmAuto`
(`IsSimAccount` fail-closed + an ATM template picked); every account/instrument switch and a manual **Flat**
force-disarm; the fire path routes through the UNCHANGED `TryAutoFire` – `ValidateForSubmit` – `CanTrade` – `SubmitLimit`
– `SubmitRaw` choke – a strict *superset* of the manual click's validations (`ValidateForSubmit` runs twice), no
skipped check, no new NT8 Account/Order API. **Containment holds:** `_autoArmed` is set only after the `IsSimAccount`
gate and cleared on every account switch, so AUTO can neither arm nor fire on a non-Sim account. **Sim/Playback
clearance STANDS.** The items below are the hard preconditions before AUTO may EVER arm on a real account – on top
of F1–F19, which ALL reopen in the unattended context.)
- **F20 (BLOCKER) – new:** the mandatory-ATM invariant is enforced at ARM time but NOT at FIRE time. `TryArmAuto`
  requires an ATM template, yet `SubmitRaw` re-reads `_atmSelector.SelectedAtmStrategy` at submit and **silently
  degrades to a naked plain `Account.Submit` when it is null** – there is no `isAuto` guard requiring a bracket (the
  exact degrade-to-plain fallback the F1 re-scope said must be hard-blocked). `_atmUserPicked`/`_autoArmed` only clear
  on DropDownClosed-None / account / instrument switch; a selector that self-clears its selection by ANY OTHER route
  (e.g. after a `StartAtmStrategy`'d ATM completes and the selector reverts, or an async repopulate that deselects
  without raising `DropDownClosed`) leaves AUTO armed with no template. Failure scenario: fire #1 attaches the ATM and
  fills; the ATM's target/stop closes the position; guard 2 (busy) clears the instant it goes flat; fire #2 auto-opens
  an **unattended entry with no server-resting stop** – and the 15s auto-cancel only kills an *unfilled* entry, never
  a *filled* naked position. On real this is the F1 catastrophe put on autopilot. Fix: `TryAutoFire` must assert
  `_atmSelector.SelectedAtmStrategy != null` (belt to the `DropDownClosed` suspenders – force-disarm + Diag if the
  template was lost), AND `SubmitRaw`'s degrade-to-plain branch must be hard-blocked for any `isAuto`/entry order
  (mandate a bracket or refuse the submit). *Sim/Playback: **fix now** – it is a ~2-line guard and a naked
  auto-position corrupts the very calibration data AUTO exists to gather.*
- **F21 (BLOCKER) – new:** no connection-loss / session handling for a WORKING auto order. A disconnect or session
  close with a resting AUTO limit (or an AUTO entry mid-attach) has no handler; worse, `MaybeAutoCancel` ages against
  the market-data clock (`_now`), which STOPS advancing on a data disconnect – so the 15s auto-cancel never fires and
  an unattended auto order can rest/fill across the gap. Before real (ties F8): gate the auto-fire on
  `Connection.Status == Connected`, cancel working auto orders on disconnect, and reconcile working orders/position on
  reconnect before AUTO re-enables.
- **F22 (BLOCKER) – new:** AUTO must not ARM on real until (a) the Rec-CSV pass (spec §9) validates every threshold it
  fires on – the Controller's AND `AutoFireCapPerDay`/`AutoStaleTicks`/`AutoCancelSeconds`, all placeholders today –
  against baseline; (b) N documented profitable Sim/Playback sessions exist; and (c) a **per-session realized-loss cap
  force-disarms AUTO** (automation needs its own cumulative-loss kill-switch, distinct from the per-trade F9). If the
  real target is a **prop firm**, F9 (daily-loss / max-trades / loss-streak cooldown) must gate the AUTO *arm*, not
  just the manual buttons.
- **F23 (MAJOR) – new:** cap key + persistence. The daily cap is keyed to the REPLAY date and held in an **in-memory**
  counter. On real: key it to the session/trading-day in the account's exchange timezone, and make the count survive
  an add-on reload / platform restart mid-session (persist it, or re-derive from the account's execution history) – an
  in-memory reset silently re-opens the full 5-fire cap on every reload.
- **F24 (MAJOR, gating) – new:** explicit `trading-risk-manager` re-review of AUTO-on-real is itself a precondition.
  AUTO overturns the standing "human always clicks the final submit" pillar; no code path may arm AUTO on a real
  account until F20–F23 + the reopened F1–F19 are implemented and re-verified against calibrated thresholds in this seat.

**Sim-scope notes (not blockers – recorded for the calibration pass):**
- **Guard 4 (anti-stale) is effectively inert at fire time.** The pre-stage price is computed from `_lastPrice` and
  checked against the *same* `_lastPrice` synchronously inside `TryAutoFire`, so `marketableThrough` is false by
  construction (≤ tolerance always); guard 4 only ever trips on `_lastPrice <= 0`. HARMLESS for AUTO (the immediate
  submit is non-marketable by construction – unlike F18's *delayed* manual click), but the "F18 essence" comment
  overclaims: do NOT treat F18's click-time re-clamp as satisfied by guard 4 when porting to real.
- **Multi-day replay dormancy.** Hitting the cap force-disarms; on crossing into a new replay day guard 1
  (`!_autoArmed`) returns BEFORE the day-reset runs, so AUTO stays dormant until manually re-armed. The counter does
  reset, but it is moot until re-arm – risk-reducing, flagged only so it isn't mistaken for a bug.
- **Defense-in-depth:** `TryAutoFire` trusts `_autoArmed` as the Sim proxy and does not itself re-assert
  `IsSimAccount(_account)` at fire time (it relies on force-disarm-on-switch). Containment holds today, but a direct
  `IsSimAccount` re-check in guard 1 would fail-closed at fire time, matching the ARM LIVE philosophy (which re-checks
  `IsSimAccount` on every submit via `CanTrade`). Cheap hardening.

**Real-money VETO stands; preconditions are now F1(re-scoped)–F24** (F11/F12/F15/F16/F17 done; F1/F2/F6/F7/F14 widened; F13/F14 + F1(bracket-mandate)/F2/F9/F10 + **F18/F19** + **F20–F24 (AUTO)** deferred to the real gate). **The Consumption-Break pre-stage (`43b9934`) and AUTO mode (`de91b2b`) are CLEARED for Sim/Playback only** – verified no auto-submit-to-real path (AUTO arms only on `IsSimAccount`, disarms on every switch/Flat), ARM LIVE + Sim-default intact. **AUTO-on-real stays VETOED** until F20–F24 (+ the reopened F1–F19, plus F9 if prop) are implemented and re-reviewed in this seat. Until then: **Sim/Playback testing only** – and **fix F20 now** so the calibration pass never logs a naked auto-position.

## AUTO mode (2026-07-01, Sim/Playback only)

Built on top of the Consumption-Break pre-stage above, in `RadarChartTrader.cs` only (`RadarTab.cs`
gained one plumbing field — see Time source below). When armed, a Controller fire auto-submits the
already-pre-staged limit through the SAME `SubmitLimit` → `SubmitRaw` path a manual LMT click uses — no
parallel order logic, no new NT8 Account/Order API.

**Toggle:** an "AUTO" checkbox + status label, docked in Row 1 next to the "SETUP listo" indicator (the
340px dock height in `RadarTab` is unchanged). Amber accent when armed, Muted otherwise; the status label
shows why the system disarmed it (e.g. "AUTO: cap 5/5", "AUTO: cuenta no-Sim"). Can only ARM
(`TryArmAuto`) when `IsSimAccount` (the same fail-closed helper the ARM LIVE gate trusts) is true AND an
ATM template is selected (`_atmUserPicked` + `SelectedAtmStrategy != null`).

**Force-disarm** (`ForceDisarmAuto`, no-op if already disarmed) fires on: account switch, instrument
switch, the ATM selector closing on None, the daily cap being reached, and a manual **Flat** click
(kill-switch semantics). It does NOT disarm across `OnReplayReset` — a Playback rewind is the
calibration workflow this mode exists for, not a reason to drop the arm.

**The 6 guards** (evaluated in `TryAutoFire`, called from `OnSetupFire` right after the pre-stage is
stored; every skip Diag's and leaves the pre-stage lit for manual use):
1. AUTO not armed → no-op (today's behavior, unchanged).
2. Busy — `_activeLimit` still working OR the position isn't flat (`IsFlat()`, reuses `CurrentPosition()`).
3. Daily cap — `AutoFireCapPerDay = 5` (placeholder, "matches the setup's expected 0-5 fires/day"),
   counted per REPLAY date (`FireEvent.Time.Date`, not wall clock) so a new replay day resets the count.
   Hitting the cap force-disarms.
4. Anti-stale (F18's essence, enforced pre-emptively at fire time) — skip if `_lastPrice <= 0` or the
   pre-stage is already marketable-through by more than `AutoStaleTicks = 2` (placeholder) ticks, so a
   reversed/late break can never auto-become an unconditional marketable fill.
5. All clear → `SubmitLimit(isBuy, isAuto: true)` — identical validation/ATM-attach/ownership path a
   manual click takes.
6. Auto-cancel of the unfilled AUTO limit — a still-working AUTO-submitted order (tracked via
   `_autoOrder`/`_autoSubmittedAt`, a manual order never sets these) is cancelled through the existing
   `CancelActiveLimitIfWorking` path once `AutoCancelSeconds = 15` (placeholder) of REPLAY time have
   elapsed unfilled. Checked every `SetContext` tick (~30Hz UI feed), never on manual limits.

**Time source (the one RadarTab.cs change):** the auto-cancel clock uses the REPLAY-aware market-data
clock, not wall clock. `RadarTab`'s internal `Frame` gained a `Now` field carrying the `now`/`e.Time`
value `MaybeRunEngine` already threads through every engine run, and `SetContext` gained a `DateTime now`
parameter to receive it. Wall clock (`DateTime.Now`/`UtcNow`) would desync from Playback's speed
(paused, sped up, rewound) — exactly during the calibration runs AUTO mode exists to support — so a
15-wall-second timeout could fire early or late relative to how much market time the order actually
aged. This is the only edit to `RadarTab.cs`; no engine file changed.

**Constants — all placeholders pending the Rec-CSV calibration pass (spec §9), like every other
Controller threshold in this file:** `AutoFireCapPerDay = 5`, `AutoStaleTicks = 2`,
`AutoCancelSeconds = 15`.

**Scope: Sim/Playback only.** AUTO adds no new path to a real account — `TryArmAuto`'s `IsSimAccount`
check fails closed exactly like ARM LIVE, so AUTO can never arm on a real account regardless of ARM LIVE
state. **Real-account AUTO is out of scope until the risk-manager re-reviews it post-calibration**, under
the same F1–F19 preconditions above (F18's stale-price fail-safe is now also pre-emptively enforced by
guard 4, but the click-time re-check F18 calls for is still unimplemented for the manual path).

**Follow-ups (deferred, not blocking):**
- `_autoOrder`/`_autoSubmittedAt` are tracked alongside `_activeLimit` rather than folded into it, so
  the two are mirrored/nulled at 4 call sites instead of 1 — deliberate for this pass (the lifecycle was
  just reviewed fresh); revisit if a 5th mirror site appears or the two drift. Not pure duplication
  though: an auto order rejected before ever reaching `Working` never touches `_activeLimit` at all —
  only `_autoOrder` lets `OnOrderUpdate`'s terminal-state branch clean up that case.
- The checkbox+status-label WrapPanel pattern (ARM LIVE, now AUTO) is inlined twice — extract a shared
  helper if a 3rd toggle needs it.

### Hardening (2026-07-01, same-day review round) — F20 fixed for Sim scope, spurious-disarm fixed

Applied after `trading-code-reviewer` (no blockers) and `trading-risk-manager` (F20–F24 above; Sim/Playback
clearance stands) reviewed `de91b2b`.

- **F20 — DONE for Sim scope.** `TryAutoFire` gained guard 5: asserts `_atmSelector.SelectedAtmStrategy
  != null` at the actual fire moment (not just at arm time) — force-disarms (`"ATM perdido"`) + Diag's if
  the template was lost since arming. `SubmitRaw` gained a second, lower-level check: `isAuto &&
  atm == null` now aborts the submit entirely BEFORE `CreateOrder` — no order, no degrade-to-plain — so an
  auto entry can never go out naked even if guard 5 were somehow bypassed. Manual clicks are unaffected
  (still degrade to plain on ATM-attach failure per F17 — a human is watching those). F21–F24 (connection
  loss, real-arm preconditions, cap persistence, and the mandatory risk-manager re-review) remain open and
  are explicitly out of scope for Sim/Playback.
- **Guard 1 hardened (defense in depth):** now also re-asserts `IsSimAccount(_account)` at fire time
  (`ForceDisarmAuto("cuenta no-Sim")` if it fails), mirroring how `CanTrade` re-checks `IsSimAccount` on
  every manual submit rather than trusting the arm-time check alone.
- **Spurious AUTO disarm on connection blips — fixed.** `PopulateAccounts` reassigns
  `_accountCombo.ItemsSource` on every `AccountStatusUpdate`; WPF transiently resets `SelectedItem` to
  `null` as part of that reassignment, which used to run `OnAccountSelected`'s full account-switch
  teardown (including `ForceDisarmAuto("cambio de cuenta")`) before the very next line restored the SAME
  account — silently disarming AUTO on a mere connection blip, not a real switch. Fixed by detaching the
  (now field-stored, not inline-lambda) `SelectionChanged` handler around the `ItemsSource`/`SelectedItem`
  reassignment and re-asserting once after: a no-op if the settled selection matches `_account` (the blip
  case), or the real teardown+setup if it's genuinely different (e.g. the account actually disappeared). A
  user-driven account switch in the dropdown is untouched — that path never goes through `PopulateAccounts`.
- Comment corrections: guard 4 no longer claims to implement F18's click-time re-clamp (it's a
  degenerate-quote guard only — F18's manual-path fix is still open); the daily-cap comment now states the
  counter burns on every ATTEMPT, including one whose submit later throws (conservative, deliberate).

Gates: `dotnet test` 88/88, `nt8c` staged build 0/0.

### Round-3 observability hardening — 5-day AUTO run, 3 fires, 0 positions, decision trail lost

Full writeup: `docs/calibration-consumption-break-es-day1.md` § Round 3. Guard 1a
(`if (!_autoArmed) return;`) was a silent return — the #1 blind spot when trying to reconstruct why
3 real fires produced zero orders, since `Diag()` only reaches NT8's Output window (nobody was
watching it, nothing persists it). Fixed alongside the near-wall fire-time gate
(`Engine/ControllerStateMachine.cs`, `StepCountdown` — see the calibration doc for the mechanism).

Added, all in `RadarChartTrader.cs` unless noted:
- Guard 1a now Diags before returning (`"AUTO skip — not armed at fire time."`).
- `SetAutoArmed` Diags the arm transition too (previously disarm-only).
- Every AUTO-path Diag routes through a new `DiagAuto`/`LogAuto` helper pair that ALSO appends a row
  to a new persistent, append-only CSV — `lr-auto-<instrument>-<yyyyMMdd-HHmmss>.csv`, same
  `MyDocuments/NinjaTrader 8/LiquidityRadar` folder as `lr-signals-*`. Schema:
  `time,event,side,price,mid,detail`, events `{arm, disarm, prestage, guard_skip, submit,
  atm_attach, order_update, auto_cancel}`. Independent of the Rec toggle — writes whenever an event
  occurs. `submit`/`atm_attach` carry the order id (only available once `SubmitRaw` actually creates
  the `Order`, so those two events log from there, not from `TryAutoFire`'s pre-submit intent
  message, which was folded into the `submit` row to avoid a duplicate lower-detail entry).
  `order_update` logs only for the auto-tracked order (`_autoOrder`), every state transition, CSV-only
  (no Output spam).
- `RadarChartTrader.IsAutoArmed` (new public property) exposes `_autoArmed`, now `volatile` — it's
  written only on the UI thread but read from `RadarTab.MaybeRunEngine` on the **instrument** thread
  for the new `lr-signals-*` `autoArmed` column. `volatile`'s eventual-visibility guarantee is enough
  here (a diagnostic read, not a decision gate) and matches the pattern `RadarTab` already uses for
  `_instrument`/`_latest`/`_replayResetPending` — no lock/marshal added.
- `_autoLogWriter` lazily opens on first AUTO log event, flushes per write (events are rare),
  disposed in `Cleanup()` alongside the existing writers.

Gates: `dotnet test` 89/89 (new engine regression test for the near-wall gate), `nt8c` staged build
0/0. `ControllerConfig` thresholds unchanged — see the calibration doc for why (grid re-confirms
round-2, n=3 too thin to move anything).

### ATM governs entry size (2026-07-04) — bug fix + new risk items

Bug (found in a Sim capture, `lr-auto-ES-20260704-100841.csv`): the entry qty came from the manual
Qty spinbox regardless of the attached ATM. With `ES_2C` (a 2-contract template) but the box at 1,
order #1407 submitted qty 1 → NT8 armed only ONE of the two brackets = a de-facto 1-contract ATM.
Fix (`RadarChartTrader.cs`): when an ATM is attached, the entry qty = `AtmTotalQty(atm)` (sum of
`Bracket.Quantity`) at the single `SubmitRaw` chokepoint — matches NT8's Chart Trader/SuperDOM, where
the ATM template governs position size and the Qty box is manual-only. UI: `ApplyAtmQtyLock()` reflects
the ATM total into the box + disables it (and the ▲▼ steppers) whenever an ATM is picked, re-enables on
None — called from all 4 sites that flip `_atmUserPicked` (DropDownClosed, Instrument setter,
OnAccountSelected, MaybeResolveAtmRestore) so the box can't strand in a stale enable/value state.
Reviewed: code-reviewer + `trading-risk-manager` — **APPROVED for Sim/Playback, VETO for real-money
reliance** until the items below. `dotnet test` 111/111, `nt8c` staged 0/0.

New real-money preconditions (risk-manager, 2026-07-04):
- **F2 is now more load-bearing** (was "max qty clamp"): an ATM's bracket sum can now silently exceed
  whatever the operator typed. Clamp final qty to a configurable per-fire max AFTER the ATM override; if
  the ATM total exceeds it, **refuse** the submit (never silently downsize below the bracket count — that
  re-creates the 1-leg-fill bug). Bounds the per-day envelope together with the fire cap. Required before
  real-money AUTO (the fire cap counts fires, not contracts — N-contract templates multiply turnover NxN).
- **F25 (size-signalling invariant) — DONE for Sim:** every path that sets `_atmUserPicked=true` must
  lock+reflect the qty box; every reset must unlock it. Enforced via the single `ApplyAtmQtyLock()` helper.
- **F26 (BLOCKER, real) — ATM-attach-failure degrade must not plain-submit for `isAuto`.** The F17 catch
  block (`_account.Submit` when still `Initialized`) runs for auto too — an AUTO entry whose
  `StartAtmStrategy` throws pre-dispatch is submitted **naked at the ATM total qty**, defeating F20's
  "never a naked auto entry". The F20 comment claiming the degrade "only affects a MANUAL entry" is
  factually wrong about this branch. Pre-existing; this change enlarges its size. Sim-only for AUTO today.
  Before real-money AUTO: for `isAuto`, the degrade must abort/cancel, never plain-submit, and never at the
  ATM total.

### Break-limit lifetime + re-mount (2026-07-05) — bug fix + new real-account preconditions

`AutoCancelSeconds` raised 15 → 300 (an unfilled auto-limit now lives ~5 min of replay/market time — at
15s, a 4x–24x Playback speed cancelled break-setup limits before they could fill, in a fraction of a
wall-clock second). Added re-mount: a genuinely new setup fire while our own unfilled auto-limit is still
resting cancels it and resubmits at the new break level/side (cancel-first replace, reusing the existing
opposite-side-flip sequencing) instead of being skipped by guard 2's busy check; the window restarts; it
does not burn a `Cap/day` slot (only real submissions-that-can-fill toward a position count, as before). An
actual open position still hits the busy guard unchanged. Reviewed: `trading-code-reviewer` (found one real
Sim-reproducible bug, fixed below) + `trading-risk-manager` (**APPROVED for Sim/Playback**; four new
real-account preconditions below). `dotnet test` 146/146, `nt8c` staged build 0 errors/0 warnings.

**Bug fixed (code review) — partial-fill-during-cancel stacked a position + a fresh auto-limit.** NT8
terminates a limit that partially fills and then cancels as `Cancelled` **with** `Filled > 0` — the two are
not mutually exclusive. The `_pendingReplace` resolution in `OnOrderUpdate` opens `_openAutoTrade` whenever
`ord.Filled > 0`, but was dropping the deferred replacement only on `state != Cancelled` — so a
partial-then-cancel hit BOTH branches: the partial position opened (bracketed, correct) AND the
replacement fired (the deferred `SubmitRaw` re-runs neither `ValidateForSubmit` nor guard 2, so nothing
downstream caught it) → a stacked partial position plus a new resting auto-limit on top, exactly what
guard 2 exists to prevent. Fixed by gating the resubmit on `state == OrderState.Cancelled &&
!(p.IsAuto && ord.Filled > 0)`, scoped to `p.IsAuto` only (a manual opposite-side flip still replaces on
any `Cancelled`, partial fill included — a human is watching that path). Net: a partial-then-cancel is now
one honest bracketed partial position, no stacked limit; `_pendingReplace` is still cleared either way, and
the now-open partial position is caught by the existing busy guard on the next fire.

New real-account preconditions (risk-manager, 2026-07-05 — these gate the still-VETOed real-account port,
not Sim/Playback):
- **F27 (NEW, BLOCKER for real)** — bounded auto-limit lifetime + a max re-anchors (or max total
  pending-intent lifetime) per intent. Nothing today stops a single break intent from re-mounting
  indefinitely and chasing the level unattended for a whole session. `AutoCancelSeconds` must be both
  calibrated (spec §9 Rec-CSV pass) AND capped before real — either a hard ceiling on re-mount count per
  intent, or a hard ceiling on the intent's total pending lifetime across all its re-mounts.
- **F18 — annotated.** The stale-fill window this pre-stage price can sit unfilled-but-latched against is
  now 20× larger (15s → 300s), so the click-time/rest-time non-marketable re-clamp against REAL best
  bid/ask that F18 calls for is significantly more load-bearing for AUTO than when F18 was first opened —
  a 300s-stale reversal has much more room to make the latched price marketable than a 15s one did.
- **F21 — annotated (sharp one).** `MaybeAutoCancel` ages the limit against `_now`, the REPLAY-aware clock
  — which STOPS ADVANCING on a data disconnect. On real, a 300s timer that never fires because the feed
  stalled leaves a live resting limit for the ENTIRE disconnect, unattended. Before real, `AutoCancelSeconds`
  needs a wall-clock or `Connection.Status`-driven failsafe layered on top of the `_now` check, not `_now`
  alone — F21's existing "gate the auto-fire on `Connection.Status == Connected`" fix must also cover the
  cancel-timeout path, not just the fire path.
- **F23/F9 — annotated.** Re-mount makes the in-memory `_autoFireCount` an even looser proxy for actual
  filled-trade count than it already was (F23) — one "fire" can now spawn a chain of cancel+resubmit
  re-anchors that never touch the account beyond resting/cancelling limits, while a prop `max-trades/day`
  rule (F9) must count actual EXECUTIONS, not fire/submit attempts. The real fix for both — re-deriving the
  count from the account's execution history rather than this in-memory counter — handles re-mount
  correctly for free (a re-anchor that never fills contributes zero executions); a submit-attempt counter
  does not.

### Sim test checklist for the ATM path (before trusting it)
1. Open the tab, pick a Sim/Playback account + instrument → confirm the **ATM box shows nothing pre-selected** (blank), i.e. no auto-picked template.
2. Select an ATM template, take a bracketed BUY/SELL MKT → let it fill → confirm the ATM's stop/target appear at the broker AND that the **ladder marker / ▲▼ do NOT latch onto the ATM's target** (F15 check — they should stay tied only to your own manual LMT, if any).
3. With an ATM position open, click **Close** → confirm the position flattens and the ATM bracket legs don't leave an orphaned stop.

## How to test (Sim/Playback)
F5 compile in NT8 → **close & reopen** the Liquidity Radar window (open Add-Ons don't refresh on recompile). Select **Sim101** (or a Playback connection) → the ticket is live; BUY/SELL/Rev/Close/Flat operate on the Sim position with live PnL. Selecting a real account disables the buttons and shows ARM LIVE (leave it OFF).
