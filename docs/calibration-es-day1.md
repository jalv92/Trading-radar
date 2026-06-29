# ES Calibration — Day 1 (2026-06-22 RTH replay)

Capture: `lr-capture-ES-20260628-203633.csv` — full RTH 09:30–16:00, 13,169 rows (1,361 Wall, 333 Absorbed, 1 Consumed, 0 Pulled, 11,473 price points). Config: Auto MinSize (1.8×median), K×=1.5, Persist=1000ms.

## Headline: the Absorbed signal is REAL
"Held" = price respected the absorbed level (support stayed above / resistance stayed below) after the event.

| Horizon | Absorbed hold-rate (n=333) |
|---|---|
| 15s | **77%** |
| 30s | **69%** |
| 60s | 65% |
| 120s | 61% |

vs ~50% random → a genuine, validated short-term edge. Magnitude is modest: median favorable move +0.38 pts (~1.5 ticks), mean +0.6–0.78. → good for tight-stop confluence/scalp, not a big directional call by itself.

## Key calibration finding: SIZE filters, confidence does NOT
Hold-rate @30s by wall peak size:
- <70: 65% · 70–100: 67% · **100–200: 76%** · 200+: 73%

Hold-rate @30s by confidence: flat ~68–74% across ALL buckets (0.4→1.0). **Confidence as currently modeled does not discriminate good from bad absorptions; wall SIZE does.**
→ **Actionable rule: prioritize absorption at walls ≥ ~100 contracts (≈3× median)** for ~75% hold. Confidence is not a useful filter yet.

Wall peak sizes: min 52, p25 71, median 83, p75 110, max 1063. Auto-threshold (~63) is well-placed.
Signal volume: ~209 walls/h, ~51 absorbed/h (only the few near your chart level matter).

## The gap: Pulled (0) and Consumed (1) barely fire
Across a full day, the classifier essentially never produced PULLED or CONSUMED. Either (a) ES inside walls genuinely get absorbed rather than pulled/spoofed or consumed-through (plausible on a deep liquid book), or (b) the EpisodeClassifier's Pulled/Consumed conditions are too strict. Cannot calibrate those two outcomes from this data → needs a classifier-logic review + a trending/breaking day (for Consumed) and a spoofy period (for Pulled).

## Verdict
- **Sufficient to validate + calibrate the main signal (Wall + Absorbed): YES.** Current auto-thresholds are good; add a size-priority filter (≥100).
- **For production confidence:** confirm the 65–77% across 2–3 more ES days (one day, one instrument is a strong start, not proof).
- **Pulled/Consumed:** investigate classifier + capture a trending and a spoofy session.

## W_assoc note
Trade attribution still sums over the whole episode (not windowed). Despite that, Absorbed already yields a real 65–77% edge — wiring W_assoc may sharpen it but is not blocking.

---

## Multi-day update (6/22 + 6/24 — two full RTH days, 918 absorbed events)

Cross-day consistency is the real test (one day could be luck):

| Day | walls | absorbed | hold@15s | hold@30s | hold@60s | big≥100 @30 | small<100 @30 |
|---|---|---|---|---|---|---|---|
| 6/22 | 1361 | 333 | 77% | 69% | 65% | 75% | 66% |
| 6/24 | 2560 | 585 | 67% | 61% | 56% | 71% | 59% |
| **Combined** | 3921 | **918** | — | **64%** | — | **~73%** | **~62%** |

**Verdict: the Absorbed edge is REPRODUCIBLE.** Both independent days clearly beat 50% (64% combined @30s). The **size filter holds on BOTH days**: walls ≥100 → ~71-75% vs small ~59-66%. → lock `prioritize walls ≥100`.

**Honest scope:** the edge is SHORT-TERM — strongest at 15s (67-77%), decays toward 56-65% by 60s. So Absorbed = "this level likely holds for the next ~15-30s" → a timing/scalp confluence signal, not a position-trade signal. 6/24 was a weaker regime than 6/22 (61 vs 69%); real but regime-dependent.

**Pulled/Consumed across 2 full days: 0 Pulled, 6 Consumed.** Structurally near-zero on ES inside walls → either genuine (deep books absorb, don't get pulled/consumed at the inside) or the classifier conditions are too strict. Cannot calibrate; requires a classifier-logic review and/or a deliberately trending (Consumed) / spoofy (Pulled) session.

**6/23 was empty NOT due to a bug:** that replay day had median book size = 0 all day → it only carried Level-1 (price), no L2 depth volumes. So "nothing shows" = that day's replay lacks recorded depth. Recognise it: if the book bars show no sizes / medBid=medAsk=0 in the diag, that session has no L2.

## Calibrated defaults (locked from 2 days)
- Auto MinSize (1.8×median), K×=1.5, Persist=1000 → **good, keep.**
- Quality filter for acting: **wall peak ≥ 100 (≈3× median)** raises hold ~10pts.
- Treat Absorbed as a ~15-30s confluence signal at a chart level, small size, tight stop.

---

## Trade economics — is there a tradeable edge? (sim on 918 absorbed events, 2 days)
Simulated a scalp at each Absorbed level (limit/marketable entry at the level, bracket exit, 60s horizon, mids @2s). ES tick=$12.50.

**Realistic cost (1.4 ticks = ~1 spread + commission):**
- Tight brackets (2/2, 2/3): NEGATIVE (-0.23 to -0.38 t/trade) despite 79-83% win — classic high-win-rate / negative-expectancy trap (wins too small vs cost + tail).
- Wider (4/4): +0.30 t/trade (+$3.75), 71% win. **Big walls ≥100, 4/4: +0.72 t/trade (+$9), 76% win** — the only clearly positive cell.

**Optimistic cost (0.8 t = passive LIMIT entry+exit, commission only):** all positive — ALL 3/4 +0.62t, BIG 3/4 +0.91t (82% win).

### Verdict (honest)
A **thin, real, but fragile edge.** The signal predicts (high win rate confirmed), but the favorable move is small (1.5-3 ticks), so profitability is dominated by EXECUTION, not the signal:
- Market-in + tight stop → negative. Passive limit-in at the wall + wider bracket + big-wall filter → positive on paper (+0.7-0.9 t/trade).
- **Killers not in this sim:** queue position (your limit may not fill ahead of a real wall), stop slippage in fast moves, 2s-mid granularity (misses spikes → understates stop hits), in-sample bracket choice, replay≠live latency. Real edge is likely BELOW the optimistic numbers.

**Conclusion:** worth pursuing as an automated scalper ONLY with: big-wall (≥100) filter, passive limit entry, wide-ish bracket (3-4t target), and rigorous out-of-sample + Market-Replay-sim + risk-manager gate before real money. It is NOT a proven money-maker; it is a real signal whose edge lives or dies on fills.
