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
