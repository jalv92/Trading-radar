# ADR — ML strategy for auto-calibrating the Consumption-Break Controller

- **Date:** 2026-07-03
- **Status:** Accepted
- **Context:** multi-agent brainstorm (2 grounding agents + 4 architecture proposals + 4 adversarial risk-manager kill-passes, ~1M tokens), anchored on the workspace's `rl-trading-failure-modes` audit and the real repo/captures. This ADR records the decision, the anti-collapse principles, what Phase 0 shipped, and the data gate everything else waits behind.

---

## 1. The question

Can the ~15 hand-calibrated `ControllerConfig` thresholds (8 manual review rounds so far) be learned automatically from each capture round — **without** the system ever being able to converge on "never trade" (the sit-and-hold degenerate optimum, the #1 documented failure of RL applied to intraday futures)?

## 2. The verdicts (adversarially vetted)

| Approach | Verdict | Score | One-line reason |
|---|---|---|---|
| **Percentile self-normalization** of the 2 raw-count knobs | viable | 6/10 | The only adaptive component worth running TODAY; spec §5 already mandated it |
| **Ghost-Grid** — offline counterfactual calibrator (replay captures through the pure C# engine under candidate configs, constrained search, human-approved config diff) | viable | 5/10 | The winning "real ML" architecture — but only once the data gate is met |
| **Meta-labeling** (loosened controller + GBDT scorer with quantile take-floor + sizing) | viable | 4/10 | Sound phase-2; blocked until real fills exist (mid-labels are systematically optimistic for a passive 1-tick-stop entry) |
| **Contextual bandit** over vetted configs | weak | 3/10 | REJECTED — per-fire reward protects the arm that goes quiet on hard days (zero fires = zero penalty): the collapse re-enters through the back door |
| **Deep RL / offline RL / preference RL / CMA-ES over Replay** | deferred | — | No data shape for it (0–5 fires/day, ~1 distinct captured day); deflation-dead on arrival |

**The binding constraint is data, not algorithms.** The current corpus is essentially ONE distinct ES day (2026-06-22, replayed across rounds 3–8) with ZERO completed trades end-to-end. No calibrator is trustworthy on that.

## 3. Anti-collapse principles (non-negotiable)

1. **Coverage is the search domain, never a reward term.** Any future optimizer may only search configs that (a) fire on ≥R% of *textbook episodes* (labeled by a **frozen, version-pinned, config-independent** labeler) and (b) keep fires within [f_min, 5]/session on a majority of distinct days. A never-trade config is **infeasible** — not penalized, not a candidate.
2. **Empty feasible set = "no proposal", said out loud.** If no config clears the recall floor without coin-flip precision, the honest output is "this setup has no measurable edge on this data" — delivered to the human, never worked around by loosening the labeler.
3. **The AUTO hard gates are non-learnable, forever:** 5 fires/day cap, 15 s auto-cancel, ATM-required, Sim/Playback-only, ARM LIVE. They live hardcoded in `RadarChartTrader.cs`, never in `ControllerConfig`, never in any loadable config, never in a search space. Enforced by `Phase0GuardTests.ControllerConfig_NeverContainsTheAutoHardGates` (build fails if violated).
4. **Adaptive components must be bounded estimators with no objective function.** `SignificanceBand = max(p85(recent depth), 60)`: a percentile is bounded by the max observed size (never-arm unreachable) and the compiled-floor clamp closes the symmetric overtrading face (dead-liquidity sessions can't arm spoof micro-walls).
5. **The human is the only write path to production.** Any calibrator emits a config **diff for review**; Javier edits, `nt8c build` validates, redeploys.

## 4. Phase 0 — shipped with this ADR (2026-07-03)

| Item | Where | Why |
|---|---|---|
| **Realized-fill telemetry**: `fill` row (AverageFillPrice, filled/qty, state, `[auto]` tag) on every own order's terminal state | `RadarChartTrader.OnOrderUpdate` → `lr-auto-*.csv` | The future label is realized outcome, not forward-mid; starts accumulating NOW |
| **Percentile SignificanceBand**: `DepthBaseline` (ring of recent level sizes, 1 s batches, cached p85, 300-sample warm-up) → `ControllerInputs.AdaptiveSignificance` → arm gate `max(60, p85)` | `Engine/DepthBaseline.cs`, `ControllerStateMachine`, `RadarTab` | Spec §5 mandate ("percentile of recent depth, never a fixed count"); the hardcoded 60 was a divergence. Logged as `adaptiveSig` column in `lr-signals-*.csv` |
| **Quiet-degradation alarm**: per-Rec-session summary row (`lr-sessions.csv`: time, instrument, arms, fires) + ALERT when fires=0 for 3 consecutive sessions that still armed | `RadarTab.WriteSessionSummary` | "The day it quietly degrades" must be visible; report-only |
| **Non-learnable-gates CI test** + DepthBaseline/clamp tests (8 new tests, 109/109) | `Tests/Phase0GuardTests.cs` | Principle 3 enforced by the build, not by intention |

Deliberately NOT shipped (per the kill-passes): adaptive DeltaFloor (trend-regime fire-suppression back door — revisit only with evidence), any config file loader (bypasses the compile gate; YAGNI), the optimizer itself (data gate below).

## 5. The data gate (what unlocks Phase 1 = Ghost-Grid)

Build the offline counterfactual calibrator only when **ALL** of:
- **≥ 10 distinct market days** captured with Rec (not replays of the same day), covering ≥ 2 regimes: trend, chop, and ideally one event day (FOMC/CPI/NFP);
- **≥ 15–20 real fires** with realized-fill outcomes logged;
- the fires show at least coin-flip-plus quality worth optimizing (otherwise: principle 2).

### Capture campaign protocol (the "10-day playback")
Per session: pick a **new** Market Replay date (download with depth), open the radar on ES, check **Rec**, let the Controller run (AUTO optional, Sim/Playback), uncheck Rec at session end → the summary row lands in `lr-sessions.csv`. ~20 min/session. Track distinct-day count in the sessions CSV; the pre-reset CSVs were archived and removed on 2026-07-03 so every file in `LiquidityRadar/` belongs to this campaign.

## 6. Phase 1+ (design of record, gated)

**Ghost-Grid**: a `net8.0` console project referencing the existing `Engine.csproj` (same compiled decision code as live → no serving skew), replaying captured input streams under candidate configs over a **reduced knob set** (self-normalizing knobs frozen; ~2–4 genuinely free), scoring with a **path-aware label** (does the 1-tick structural stop survive AND the move reach +N — not mid-at-15s), purged CV across distinct days, deflated statistics counting every trial, output = a config diff + a one-page report. Most runs will output "no change proposed" — that is correct behavior.
**Phase 2 (optional, later):** meta-labeling with realized-ATM-outcome labels, size fixed at 1 until ATM bracket coverage for size>1 is proven, shadow-mode first.
**Rejected/deferred:** contextual bandit (inverted anti-collapse), deep RL (see `rl-trading-failure-modes`).

## 7. Sources

- Brainstorm run `wf_b8b5d8c4-c15` (2026-07-03) — 4 designs + 4 adversarial verdicts.
- `.claude/skills/rl-trading-failure-modes/SKILL.md` — the failure-mode catalog this design is built against.
- `docs/specs/2026-07-01-consumption-break-setup-design.md` §5, §9, §10.
- `docs/calibration-es-day1.md`, `docs/calibration-consumption-break-es-day1.md` — the manual rounds this replaces (eventually).
