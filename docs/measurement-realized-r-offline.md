# Offline Realized-R Bracket Grading — AbsorptionScalper exit, 2026-07-03

- **Date:** 2026-07-03
- **Goal:** answer the multiday analysis's item 5 ("Realized-R proxy today, for free") — grade the **60 existing AUTO fills** + the **15 "not armed at fire time" guard-skips** with the AbsorptionScalper bracket (`docs/strategy-absorption-scalper.md` §2.3), replayed over the ~20Hz `mid` path already on disk, and run the doc's own **decisive test**: fires vs a random-entry baseline.
- **Tool:** `tools/measure/realized_r_bracket.py` (argparse, stdlib + numpy/pandas; `python3 realized_r_bracket.py --selftest` runs an assert-based self-check of the bracket engine before trusting any number below).
- **Data:** `lr-signals-ES-*.csv` (~20Hz mid snapshots) + `lr-auto-ES-*.csv` (order events) across 23 files / 8 content-days (06-15,16,17,18,22,24,25,26), the same corpus as the 2026-07-03 multiday analysis. Fires and their matched signals file were **reused** from that analysis's `fires_final.json` (60 rows, `matchedSignalsFile` per row) rather than re-derived — the matching method (candidate files whose time range contains the fill's content-time, tie-broken by closest file-wallclock to the auto session's own wallclock) is mirrored in the script for the guard-skip extraction, which *is* newly derived here.

**Headline: this is a PROXY on mid-path fills, optimistic vs real queue position.** Every inflation source is listed in §6 before any number here should move a go/no-go decision.

---

## 1. What got graded

| Set | n | Source |
|---|---|---|
| Real AUTO fills | 60 | `fires_final.json` (44 from the `primary_2026-07-03_evening` build, 16 from a `superseded_older_calibration_build` re-run of the same days) |
| "Not armed at fire time" guard-skips | 15 | Extracted directly from `lr-auto-*.csv`; entry = the `prestage` row's limit price logged immediately before the skip |
| ...of which would have filled (proxy) | 14 | Mid traded to/through the limit within 15s (mirrors AUTO's own 15s unfilled-limit age-out) |
| ...unfilled | 1 | 06-18 13:16:57 Buy 7577.00 — mid never traded down to it within 15s |
| **Combined graded set** | **74** | 60 real + 14 backfilled |

The bracket: initial stop **−6t**, target **+8t**, BE trigger at **+3t** MFE, time-stop **until BE** (30s / 45s variants), tick = 0.25. The doc's BE wording ("entry − 1 t") is ambiguous, so **both readings** are graded as separate variants:

| Variant | BE stop (long) | Reading |
|---|---|---|
| `be_entry_plus1t` | entry + 1t | locks 1 tick of profit |
| `be_entry_minus1t` | entry − 1t | small giveback (literal doc wording) |

Conservative tie-break used throughout: if a single snapshot-to-snapshot gap spans both the stop and the target (or both the target and the BE-stop), it is counted as the **stop**.

---

## 2. The decisive test (doc §3a): P(reach +3t before −6t), fires vs random baseline

For each of the 8 content-days, **N=250 random RTH entry times × both sides = 500 baseline trades/day → 4000 total**, run through the identical bracket on the identical `mid` path (canonical per-day file taken from the prior analysis' `primary_files.json`).

| | k / n | rate | 95% Wilson CI |
|---|---|---|---|
| **Fires (60 real + 14 backfilled)** | 50 / 74 | **67.6%** | [56.3%, 77.1%] |
| **Random baseline** | 2595 / 4000 | **64.9%** | [63.4%, 66.3%] |

Two-proportion z = 0.48, **p ≈ 0.63** — **not significant.** The 95% CIs overlap heavily.

### 2a. A structural catch that complicates this metric: the baseline is not "no edge = 50%"

The baseline rate is **not** ~50% and shouldn't be read as a coin-flip reference. With a driftless (or near-driftless) price process and two barriers at +3t and −6t, the classic gambler's-ruin / reflection-principle result is P(hit +a before −b) ≈ b/(a+b) = 6/(3+6) = **66.7%** — purely from the barriers being asymmetric (target 3t away, stop 6t away), independent of any real direction call. The measured baseline is **64–66% on every single one of the 8 days** (06-15: 65%, 06-16: 65%, 06-17: 64%, 06-18: 64%, 06-22: 66%, 06-24: 65%, 06-25: 64%, 06-26: 65%) — a near-perfect match to the 66.7% prediction and essentially flat across days of very different character (per the multiday doc's own drift measurements). **This means "P(+3t before −6t)" is dominated by the bracket's own geometry, not by day-to-day market conditions or by whether the entry follows a real signal.** The fires' 67.6% sits barely above that structural floor — the honest read is that the signal adds very little on this metric, not that it's flat-out zero.

This is a different (and complementary) test to the one the strategy doc already ran and passed: doc §"BASELINE TEST RESULT" compared the signal's direction against the **opposite-side entry at the same instant** (which controls out the barrier geometry and any instant-specific drift, isolating direction) and found a real +40 to +61-point edge. **That test still stands** — it is not repeated here. What this exercise adds is the complementary question the doc's §3a literally asks for: *is firing on an ABSORBED signal, with random side undetermined, a better time+direction to enter than a uniformly random time+side?* On the current n=74, the answer is **not demonstrated** — the gap is small and not statistically distinguishable from noise.

---

## 3. Expectancy (all 4 bracket-reading variants), gross and net of cost

Cost scenarios: **gross** (0t), **net_1.5t**, **net_2.5t** round-trip.

### Fires + backfilled (n=74)

| Variant | win% | exp(gross) | exp(net 1.5t) | exp(net 2.5t) |
|---|---|---|---|---|
| be_entry_plus1t, ts30s | 66.2% | **+1.54t** | +0.04t | −0.96t |
| be_entry_plus1t, ts45s | 64.9% | +1.59t | +0.09t | −0.91t |
| be_entry_minus1t, ts30s | 52.7% | +1.84t | +0.34t | −0.66t |
| be_entry_minus1t, ts45s | 51.4% | +1.88t | +0.38t | −0.62t |

### Random baseline (n=4000)

| Variant | win% | exp(gross) | exp(net 1.5t) | exp(net 2.5t) |
|---|---|---|---|---|
| be_entry_plus1t, ts30s | 58.5% | +0.30t | −1.20t | −2.20t |
| be_entry_plus1t, ts45s | 61.6% | +0.34t | −1.16t | −2.16t |
| be_entry_minus1t, ts30s | 31.6% | +0.16t | −1.34t | −2.34t |
| be_entry_minus1t, ts45s | 32.3% | +0.18t | −1.32t | −2.32t |

The baseline's gross expectancy is not exactly 0 either (this asymmetric bracket has positive convexity from cutting losers at BE and letting one side run — a well-known property of trail/BE stop mechanics, present with or without a real signal). That is itself informative: it further deflates the apparent "edge" of the raw fires number, because some of the fires' positive expectancy is *also* just bracket convexity, not signal.

### Expectancy gap (fires+backfilled − baseline), gross, `be_entry_plus1t__ts30s`, bootstrap CI

**mean diff = +1.24t, 95% bootstrap CI [−0.11t, +2.53t]** (5000 resamples). **Crosses zero.** Consistent with the race-rate test above: at n=74, the gap is directionally positive but not statistically distinguishable from noise.

---

## 4. Profit factor (gross vs net of cost), combined 74

| Variant | PF gross | PF net 1.5t | PF net 2.5t |
|---|---|---|---|
| be_entry_plus1t, ts30s | 1.82 | **1.02** | 0.69 |
| be_entry_plus1t, ts45s | 1.84 | 1.03 | 0.71 |
| be_entry_minus1t, ts30s | 1.91 | 1.12 | 0.80 |
| be_entry_minus1t, ts45s | 1.93 | 1.14 | 0.81 |

Gross PF clears the doc's advance gate (§3d, PF ≥ 1.3) comfortably. **Net of a realistic 1.5–2.5 tick round-trip cost, PF collapses to ~1.0–1.1 (breakeven) or below.** This is the same conclusion the strategy doc's own §1 already reached by reasoning from first principles ("no execution that cleanly captures a 1.5-tick edge net of ES round-trip cost + adverse selection") — this offline grading is a **quantitative confirmation**, not a new finding.

---

## 5. Sensitivity cuts

### By build group (the 60 real fills split by which build produced them)

| Cut | n | race3 rate | exp(gross) | PF gross | PF net1.5t | PF net2.5t |
|---|---|---|---|---|---|---|
| `primary_2026-07-03_evening` | 44 | 72.7% | **+2.17t** | 2.37 | **1.31** | 0.88 |
| `superseded_older_calibration_build` | 16 | 62.5% | **−0.16t** | — | — | — |
| All 60 real fills | 60 | 70.0% | +1.55t | — | — | — |
| 14 backfilled not-armed (proxy) | 14 | 57.1% | +1.50t | — | — | — |

The `primary_2026-07-03_evening` build (the same 43-col build the multiday analysis treated as canonical) is where essentially all of the apparent edge lives — the older-build re-runs of the same 8 days are **net negative gross**, before any cost. This is a large swing (2.17t vs −0.16t) for "same content-day, different logging session," and matches the multiday doc's own caution about mixing build generations. **On the primary-build-only cut (n=44), net expectancy at 1.5t cost is +0.67t and PF is 1.31** — the only cut in this whole exercise that clears the doc's own advance thresholds (PF ≥ 1.3), and it does so right at the edge, on n=44.

### MFE/MAE @60s sanity-check against the prior analysis

Primary-build-only median MFE/MAE @60s = **9.5t / 3.8t** — this exactly reproduces the number already reported in `docs/2026-07-03-multiday-analysis-adaptation-verdict.md` ("44 primary fills: median MFE/MAE @60s = 9.5t / 3.8t"), which cross-validates the matching + mid-path logic in this script against the independently-built prior analysis.

### Outcome mix (combined 74, default variant)

`target`=29, `stop_initial`=22, `stop_be`=16, `time_stop`=7. No `unresolved_end_of_data` in the graded 74 (the 300s horizon cap was always enough); baseline had 4/4000 unresolved (near session-end draws), negligible.

---

## 6. Every inflation source (checklist, per the strategy doc's own §caveats + this exercise's additions)

1. **Mid-path fills, not real fills.** Every entry/exit here is "mid traded to/through this level," not a real limit fill against a resting queue or a real stop-order slip. The strategy doc's own §1.3 flags passive-limit-at-the-wall as an **adverse-selection trap** — this proxy cannot see that at all; it assumes the entry price and stop/target levels are always achievable exactly as specified.
2. **~20Hz snapshot discretization.** Between snapshots (~50ms) price can move; the gap-spanning tie-break (conservative: count the stop) partially compensates but the general effect is still likely to **overstate BE/TP-reached and understate stop-outs**, the same distortion the strategy doc already flagged for the coarser 2s captures.
3. **The "not armed" backfill is a double proxy.** Its entry price comes from the `prestage` limit and its fill condition is "mid touches it within 15s" — this stacks the mid-path-fill assumption on top of an already-hypothetical fill (these 14 trades never actually executed; we are asking "what if AUTO had been armed").
4. **Small, correlated sample.** n=74 (44 of which come from one build) across only 8 content-days that were also used to build/tune the very config that produced these fires — not a held-out sample. The doc's own OOS protocol (§3c) is not satisfied here.
5. **Build-group mixing.** 16 of the 60 real fills come from a superseded config; §5 shows this materially changes every number. Treat the primary-build-only cut (n=44) as the more decision-relevant one, and even that is small.
6. **The race-to-+3t metric is dominated by barrier geometry (§2a), not direction.** ~66.7% of the "signal beats baseline" story is arithmetic, not edge; only the residual few points above that floor are informative, and they are not statistically significant here.
7. **Random baseline itself is not a zero-edge process.** Its own gross expectancy is mildly positive (+0.16 to +0.34t), reflecting either the bracket's BE convexity, mild ES drift/serial correlation over the sampled days, or discretization — none of which is "no edge," so subtracting it (rather than comparing to a literal zero) is the more honest read, and is exactly what §2/§3 do.
8. **Costs are estimates, not measured slippage.** 1.5t/2.5t round-trip scenarios are the doc's own stated range (§1: "~0.5–1 t" spread+lag for a marketable/stop-confirmation entry, doubled for round-trip commentary elsewhere) — they are not derived from this data.
9. **BE wording ambiguity is unresolved by data, only bracketed.** Both readings are shown; they move net expectancy at 1.5t cost from +0.04t to +0.34t (combined) — the "right" reading matters and should be a deliberate strategy decision, not an artifact of this script's defaults.
10. **Horizon cap (300s) and time-stop-at-market assumptions.** A time-stop or unresolved-at-cap exit is booked at "market" using the prevailing mid — real slippage on a market order during a fast move is not modeled beyond the flat cost scenarios above.

---

## 7. Verdict against the strategy doc's own advance gates (§3d)

| Gate | Threshold | Result (primary-build n=44, most decision-relevant cut) | Pass? |
|---|---|---|---|
| Net expectancy after costs | ≥ +1.0t/trade | +0.67t (at 1.5t cost); −0.33t (at 2.5t) | **No** |
| Profit factor | ≥ 1.3 | 1.31 (net 1.5t); 0.88 (net 2.5t) | **Marginal / knife-edge** |
| Signal expectancy − baseline expectancy | ≥ +0.75t/trade | +1.24t gross (all-fires cut), but 95% CI **[−0.11t, +2.53t]** — not significant | **Not demonstrated statistically** |

**Headline: this offline grading does not clear the doc's own go/no-go bar, and it does not overturn the doc's own §1 honest interim verdict** ("a real signal that is probably too thin to stand alone as an automated 8-tick scalper"). It adds two new, specific, quantitative facts to that verdict:
- The gross edge is real-looking (+1.5 to +1.9t/trade, PF 1.8–1.9) but **evaporates under a realistic 1.5–2.5 tick round-trip cost** — exactly the mechanism the doc predicted from first principles.
- The signal-vs-random-baseline comparison, run properly for the first time here, is **directionally positive but not statistically significant at current n** — a materially different (and more sobering) result than the doc's already-passed signal-vs-opposite-side test, because the two tests control for different things (see §2a).

**Recommendation:** do not promote to live on this evidence. This is exactly the kind of result that calls for **more graded episodes, not adaptive tuning** — consistent with the multiday analysis's own verdict that instrumentation (exit-leg logging, episode ledger) is the correct next investment, not self-adaptation. The primary-build-only PF=1.31 at 1.5t cost is the one genuinely encouraging number in this report; it is also the one built on the smallest, most build-specific slice (n=44) and should not be over-read.

---

## Appendix: reproduction

```bash
python3 tools/measure/realized_r_bracket.py --selftest   # assert-based self-check, run first

python3 tools/measure/realized_r_bracket.py \
  --data-dir <dir with lr-signals-*.csv and lr-auto-*.csv> \
  --fires-json <prior-analysis fires_final.json> \
  --primary-files-json <prior-analysis primary_files.json> \
  --n-random 250 --seed 20260703 \
  --out-json <path to dump full JSON results>
```

If `--fires-json` is omitted the script re-derives the 60 fills from the `lr-auto-*.csv` `fill` events (+ old-schema `order_update→Filled` synthesis) and matches each to a signals file itself, so it does not hard-depend on the prior analysis's scratch artifacts for future re-runs. `--primary-files-json` is similarly optional; without it, the canonical file per content-day is auto-picked as the file with the most in-range RTH rows for that day.
