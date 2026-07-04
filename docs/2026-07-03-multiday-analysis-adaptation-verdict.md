# Multi-Day CSV Analysis & Self-Adaptation Verdict — 2026-07-03

Multi-agent analysis (4 measurement readers + 3 adaptive-architecture designs + adversarial risk review per design + completeness critic) over the full Rec corpus: ~496k signal rows, 23 qualifying files, 11 replayed content-days (2026-06-01 → 06-26), 75 fire transitions, 60 AUTO fills. Primary comparison set = the 2026-07-03 evening sequential batch (same 43-col build, one session per content-day: 06-15/16/17/18/22/24/25/26). Full agent output: session task `wvajj2vws`.

## Headline verdict

**Self-adapting optimization is premature. The corpus supports instrumentation, not adaptation.** Three independent designs were produced and adversarially reviewed; two were killed outright (outcome-feedback loop, regime meta-loop) and the third (adaptive percentiles) survives only as shadow-mode work plus one bug fix. The binding constraint is not threshold values — it is that **no fire can currently be graded by realized quality** (no exit leg is logged), so any adaptive loop would optimize a proxy and re-enter the Red Queen decay pattern (validation-good, live-bad).

## What the data actually says

### The funnel (primary set)
- 690–1210 arms/day (both sides) → only **0.8–3.8% reach Countdown** (8–35/day) → 13–47% of Countdowns fire (2–14 fires/day). Blended arm→fire conversion ≈ 0.72%.
- 97–99% of arms die in Armed. The funnel's mass loss is at the ARM layer; its *quality* decisions happen at the FIRE gates.

### Which gate kills Countdowns
- `tapeZ` is **confounded in the logs**: red at 87% of deaths but ALSO red at 77% of successful fires (the 0.35s z-latch state isn't logged, so the CSV column can't reproduce the controller's actual z decision). Excluded from ranking; needs a `zLatchActive`/`peakZ` column before it can ever be tuned from data.
- Among the 5 clean gates (all 100% green at every fire): **consumeFrac (FireFrac=0.6) is the #1 killer on 7 of 8 days** (~54% of deaths); delta (DeltaFloor) is #2 (~30%) and takes the top spot only on 06-17. The killer hierarchy is mostly stable day-to-day, with frac/delta as a joint dominant pair.
- **20.6% of Countdown deaths have ALL logged gates green** — killed by ReloadFrac / AwayTicks / wall-vanish, none of which is logged as an explicit veto reason. The kill accounting is 20% blind.

### Cross-day drift ("every day is different" — measured)
- TRUE for inputs: wall≥60 pass-rate 24.1% (06-25) vs 47.1% (06-18) = **1.96×**; participation (prints/vol per sec) 2.1–2.25×; top-of-book depth 2.1×; chopShare 1.83×.
- FALSE for tape z: tapeZ p95 CV 0.034 (most stable stat in the corpus) — it is already a rolling z-score. **ZFloor needs no adaptation; leave it alone.**
- DeltaFloor=30 barely filters anything: |delta15s| p50 sits above 30 on every day (gate passes 60–70% of all rows unconditionally). Its stability is an artifact of rarely binding.
- **NOT demonstrated for edge**: per-day fire quality (100% → 0% → 17% favorable) does NOT reject a single fixed 56.8% rate + binomial noise at n=2–9/day (χ²=6.95, df≈7, crit 14.07). Input drift ≠ edge drift, on current evidence.

### Fire outcomes (mid-path proxy — optimistic, no real exits)
- 44 primary fills: median MFE/MAE @60s = **9.5t / 3.8t (~2.5:1)**; 56.8% reach +3t before −3t. The static config is not obviously broken.
- **guard_skip = 29, of which 15 (52%) are "not armed at fire time"** — real signals lost to a session/UX gap, not to any threshold. Largest measured loss in the whole system.
- All 9 auto_cancels = the fixed 15s unfilled-limit timeout.

### Bug found (highest-leverage single fix)
**The shipped AdaptiveSignificance is inert.** `RadarTab` feeds `DepthBaseline` every individual book level (p85 = 29–45), which sits structurally below the wall population (wall p50 35–56, p85 70–96). Result: `adaptiveSig < 60` on ~100% of rows on 6/7 days — the compiled floor of 60 does all the gating and the adaptive arm gate has never influenced behavior. Fix = rebase the sample feed to wall-candidate sizes. **Caveat from the risk review:** with the wall-candidate feed, p85 lands at 70–96 every day, i.e. the bar would RISE above 60 — simulate the exact feed offline against the CSVs (≈20-line script) before wiring, and keep a bounded relax below 60 on thin days only if the simulation supports it.

## Design verdicts (adversarial review)

| Design | Verdict | What survives |
|---|---|---|
| Adaptive percentiles (extend p85 pattern) | **Survives, narrowed** | DepthBaseline feed fix (post-simulation); refusal list (never percentile-ize FireFrac/MinTradeBackedRatio/ReloadFrac — selection-biased; DeltaFloor static — trend-suppression back door; ZFloor already self-normalized); shadow-first + per-knob kill switches |
| Outcome-feedback ladder (labels → grading → scorer → bandit) | **Killed for today** (data starvation: ~8 deterministic replay days, 0 real exit legs) | Stage 0 is gold: the episode ledger + logging fixes. Everything above Stage 0 is gated on ≥10 distinct days with realized-R |
| Regime profiles + VolGovernor + nightly meta-loop | **Killed** (VolGovernor has zero quality evidence — all captures at VG=1.0 — and its direction is plausibly backwards: shrinking T_persist on fast days admits spoofier walls; regime axis may be trend-vs-chop, not quiet-vs-fast) | The **nightly report job** (automating this session's funnel/gate-kill/drift analyses); shadow regime classifier logging only |

## Next step — one instrumentation week, zero adaptive knobs

Priority order (each item was independently demanded by ≥2 agents):

1. **Exit-leg logging (decisive item).** Log the ATM bracket (SL/TP) at `atm_attach` + an explicit exit/flatten event linked to the entry order id. Without realized R, no design can ever grade quality — every capture week without this produces more ungradeable data.
2. **Episode ledger.** One row per Countdown episode (fired AND near-miss), with gate values at resolution, explicit veto reason (closes the 20.6% mystery), `zLatchActive`/`peakZ`, and a forward MFE/MAE label appended by the add-on ~120s later.
3. **Deterministic joins.** `sessionId` + `schemaVersion`/`buildId` stamped on every row of all CSV types; fire rows carry a signals snapshot. (Today's fire→outcome join is a wall-clock heuristic.)
4. **DepthBaseline feed fix** — after the 20-line offline simulation of the wall-candidate feed (see bug above).
5. **Realized-R proxy today, for free:** run the AbsorptionScalper bracket (6t SL / +8t TP / BE+3 / time-stop) offline over the existing 44 fills + the 15 "not armed" guard-skips → ~59 bracket-graded outcomes from data already on disk.
6. **Always-armed session mode** (or persistent ATM/account selection): recovers the 52%-of-skips loss. Threshold-free, biggest measured behavioral win.
7. **Nightly report job** (report-only): formalize this analysis (funnel, gate kills, drift, outcome grading) into a repeatable script over each day's CSVs. This — not live self-tuning — is what replaces the manual calibration rounds.

## Adaptation ladder (pre-registered gates, for later)

- **Gate to Stage 1** (nightly grading that proposes human-approved config diffs): ≥10 distinct market days with realized-R labels.
- **Gate to Stage 2** (small supervised scorer, shadow only): ≥150–200 labeled episodes.
- **Gate to Stage 3** (contextual bandit confirm/skip, forced exploration floor): Stage 2 shadow beats static config on realized-R over ≥4 weeks.
- Hard rules throughout: no deep RL; no live self-modification (offline nightly + champion/challenger only); every adaptive knob keeps floor/ceiling + max daily change + kill switch to compiled MEASURED values; quality metric = realized R, never fire count.
