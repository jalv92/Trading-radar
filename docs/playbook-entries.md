# Liquidity Radar — Entry Playbook (honest edition)

- **Date:** 2026-06-28
- **Author:** Quant Researcher (strategy/edge owner) — read-only on the `.cs`; this is a spec/usage doc, not code.
- **Scope:** Primary instrument **ES** (deep book). NQ called out explicitly where it differs (mostly: it doesn't translate).
- **Status:** The radar is a *confirmation/timing* tool. This playbook is honest about that. Read §1 before §4.
- **Grounded in:** the actual engine (`Engine/WallDetector.cs`, `EpisodeClassifier.cs`, `LiquidityMemory.cs`, `WallTracker.cs`), the design spec (`docs/specs/2026-06-28-liquidity-radar-design.md` §6), and the order-flow literature cited at the end.

---

## 1. Honest edge assessment (read this first)

**Verdict: the radar is a confirmation/timing layer on top of a chart thesis, not a standalone signal generator.** Used that way on ES it has a real, defensible role. Used as a buy/sell trigger by itself, it is mostly noise and confirmation bias. Here is why, claim by claim.

**What genuinely has an edge (and is in the academic record):**
- **Order-book / order-flow imbalance predicts the *next few* mid-price ticks.** Cont–Kukanov–Stoikov show order-flow imbalance has a near-linear relationship to short-horizon price change, with sensitivity **inversely proportional to depth**. Queue-imbalance work (Gould/Bonart, Lipton et al.) finds imbalance predicts the next ~1–2 mid-price changes and then decays to ~0. The "microprice" (Stoikov) beats the mid as a short-horizon predictor. This is real — but the horizon is **seconds and a couple of ticks**, and **the radar does not currently compute it** (see §6, gap #1). Today you read it by eye off the ladder bars.
- **Absorption / icebergs are observable on a deep book.** When repeated prints hit a level and it refuses to fall (refills), there genuinely was more size than displayed. This is the radar's strongest signal and the one piece that a plain price chart cannot show you. It is an *after-the-fact* read ("more was there than shown") — never a prediction.

**What is mostly noise / confirmation, and you must treat as such:**
- **A single resting wall is weak evidence of intent.** The depth feed never proves *why* size is there or why it left. Stacked size that looks like a floor can vanish the instant price arrives — there was never intent. Large orders are not always informed money (Archegos was forced liquidation, not a view).
- **Spoofing is real and concentrates exactly where you'd read walls.** Documented CFTC/CME cases (Sarao on the ES e-mini) layered large non-bona-fide orders to fake depth. Spoofing is most prevalent in *thinner* liquidity because faking depth there is cheap. So the very "wall" you want to lean on is the thing a spoofer most wants you to see. The radar's PULLED classifier is a *probabilistic* down-weight, **not** proof of a spoof (the code says so: MBP can't prove intent).
- **Latency reality.** True DOM scalping (capturing a few-hundred-ms inefficiency) is an HFT game; retail edge vanishes above ~200 ms latency, and HFT front-runs visible flow. **The radar is not a latency play and must not be used as one.** Its timers are slow on purpose: wall persistence is 1.0–1.5 s, episodes resolve over ~3 s. It trades the *persistent structure* (where defended liquidity sits), which lives on a 1 s–minutes timescale — the one timescale a retail seat can actually act on. Do not use it to win the next tick.

**Realistic expectation.** On ES, the radar turns "price is at my level" into "price is at my level **and** the level is defended / fake / gone." That is a meaningful improvement to entry timing and trade selection — worth maybe tighter stops and better skip-decisions, not a new alpha source. Expect it to make a *chart strategy* sharper, not to print money on its own. Plan on 6–12 months of journaling absorption quality before you trust your own read; the literature and every honest order-flow practitioner say the same.

**NQ, bluntly:** classic wall-reading does not work on NQ and the engine is effectively **inert** there by its own defaults. The visible MBP book is ~2–3 contracts median, ~13 max even at the open — but `MinAbsSize` (NQ default) is **40**. A wall needs ≥40 resting contracts that also clear K×median *and* persist 1.5 s; 40 > the entire visible book, so walls essentially never confirm. That is not a bug to "fix" by dropping the floor — NQ liquidity is just-in-time / hidden, so a low floor would only manufacture fake walls out of noise. **On NQ, use the radar (if at all) as a raw imbalance/absorption eyeball at the inside, not for walls.** The rest of this playbook is ES.

---

## 2. What the engine actually emits (signal inventory)

Every setup below ties to a real field on `RadarNode` (the engine→view contract). Know exactly what you're reading:

| Signal | Where it comes from | What it means | Honesty caveat |
|---|---|---|---|
| **WALL** (`State=Wall`) | `WallDetector.IsConfirmed` | Level ≥ K×median baseline **and** ≥ `MinAbsSize`, held ≥ `T_persist`, not flickering. | Persistence + size only. Says nothing about intent. A spoofer who rests >1.5 s defeats it. |
| **ABSORBED** (`State=Absorbed`) | `EpisodeClassifier` → Outcome.Absorbed | Trades hit the level ≥ its full open size **and** it refilled (`traded ≥ RefillRatio×net-drop`), price never crossed. Iceberg/defense. | **Strongest signal.** But trade attribution currently sums over the *whole episode*, not within `W_assoc` of each decrease (`EpisodeClassifier.cs:85-87` ponytail TODO). Until calibrated in Market Replay, treat ABSORBED as "likely" not "proven." |
| **PULLED** (`State=Pulled`, `Phantom`) | → Outcome.Pulled | Size vanished with the quote still ≥ `D_pull` ticks away and **not** explained by trades. Probable spoof. | Probabilistic only. Node dies after `P_max`(=2) pulls. Use as "this level is fake → ignore it," never as a reversal trigger. |
| **CONSUMED** (`State=Consumed`) | → Outcome.Consumed | Inside quote crossed the level coincident with trades. Real break. | Demoted to a **flipped S/R reference** at half confidence — usable as a retest level. |
| **Remembered** (`State=Remembered`, `Confidence`, `AgeSeconds`) | `LiquidityMemory` decay | A wall that scrolled beyond the live 10 levels: last-known size + age + decaying confidence (half-life `H`=30 s). | **Never live.** It is "there was X here Y seconds ago, confidence Z." Past ~2–3 half-lives it's unknown. |
| **Confidence** 0..1 | promotion `C0` + revisit deltas | Engine's running trust in a level. | A composite, not a probability of holding. Use ordinally (higher = lean more), not as a number. |
| **size bars / peakSize** | `BookMirror` | Live resting size per level, brightness ∝ size. | This is your only *imbalance* read today — by eye. No aggregate metric exists (gap #1). |

**Not in the engine (do not pretend it is):** cumulative/running delta, total bid-mass vs ask-mass imbalance number, delta-at-price, volume-at-price history, a persistent absorption log, alerts. The trader supplies all directional bias from the chart + eyeballed bar sizes. See §6.

---

## 3. Reading bias: imbalance + the wall lifecycle

Two inputs combine into a directional lean. Neither is a trigger alone.

### 3.1 Imbalance (eyeballed, until gap #1 ships)
- **Resting mass skew:** sum the bid bars vs the ask bars across the visible band. Heavier, brighter bid side = supportive lean; heavier ask = resistive. On ES this is meaningful because the book is deep and persistent (median ~33, levels 30–70). On NQ it's noise.
- **Sensitivity scales inversely with depth** (Cont-Kukanov-Stoikov): the *same* imbalance moves price more when the book is thin. So a lopsided book at a thin moment (e.g. just before a number) is a stronger short-horizon push than the same ratio in a fat midday book.
- **Horizon is short** (≈ next 1–2 mid changes). Imbalance times an entry; it does not hold a position. Don't fade a wall *because* imbalance flipped for two ticks.
- **The shape matters more than the sum:** a single huge bar (one wall) vs a smoothly stacked side are different. One wall = one removable obstacle. A stacked side = genuine layered interest (or layered spoof — the lifecycle tells you which).

### 3.2 Wall lifecycle as bias
The state transition IS the information. Read it as a story:

```
WALL appears ──► price approaches ──► one of:
   ├─ ABSORBED  → level held, refilled        → DEFENDED. Bias = with the wall (fade into it).
   ├─ PULLED    → vanished, no trades         → FAKE. Bias = the obstacle was never there → lean the OTHER way / continuation through.
   └─ CONSUMED  → traded through, quote crossed → BROKEN. Bias = momentum through; the level flips (old support→resistance).
```

- **WALL still standing (no episode resolved):** neutral-to-supportive *at* the level. Tradeable only on a *hold* (price tests, doesn't break) — that's setup #2.
- **ABSORBED:** the highest-information event. Someone with size defended a price and the tape proves it. Strongest mean-reversion lean *at that level*.
- **PULLED:** removes a level from your map. Counter-intuitively *bullish for the side it was blocking* — a pulled ask wall means overhead supply was fake. But never enter *on* the pull; it just clears a path.
- **CONSUMED:** the level is now a flipped reference. Trade the *retest* of the consumed price, not the break bar itself.

---

## 4. Entry setups (5)

All are ES, RTH (and the liquid overnight European/US session), front month. Every setup = **chart gives the location, radar confirms the liquidity, you execute on your own DOM.** Stops are structural (price), never radar-state. The radar can *invalidate* a thesis fast; it cannot *hold* a stop.

> Tick/point note for stops & targets: ES = 0.25/tick, $12.50/tick. "Structural buffer" below means ≥ a few ticks beyond the actual wall price so a 1-tick probe doesn't stop you.

### Setup 1 — Absorption Reversal (the flagship)
- **Context:** price extended into a swing high/low or a prior session level / VWAP band on the chart. You want a fade, but only if liquidity defends.
- **Radar trigger:** a confirmed **WALL** on the far side at/just beyond the extreme that resolves to **ABSORBED** (`State=Absorbed`) as price tests it — trades hit it, it refilled, the inside never crossed it.
- **Entry:** with the wall, on the first tick of price *rejecting away* from it after the ABSORBED print (e.g. long 1 tick above a bid wall that just absorbed selling). Do **not** enter on the wall appearing — wait for the *absorbed* resolution.
- **Stop:** structural, a few ticks beyond the wall price (if the absorbing wall gets eaten, the thesis is dead — that's exactly setup 3 against you).
- **Target:** opposite edge of the range / next remembered wall on the way back / mid. Scale at the first opposing WALL the radar shows.
- **Invalidation:** the same node transitions to **CONSUMED** (it didn't hold — it broke). Exit immediately; don't wait for the price stop.
- **Why it's the flagship:** ABSORBED is the one signal a chart can't give you and the one with a real microstructure basis. **But** it inherits the uncalibrated-attribution caveat (§2) — size this smallest until the classifier is validated in Replay (§7).

### Setup 2 — Wall-Hold Continuation (pullback in trend)
- **Context:** established trend on the chart; price pulls back toward a level on the *trend side* (e.g. uptrend, pulling back to a bid wall).
- **Radar trigger:** a confirmed **WALL** on the trend side that price approaches and **stays Wall** — no episode resolves as Consumed; ideally it grows (`Confidence` ticks up on a GREW revisit) or briefly ABSORBS.
- **Entry:** in trend direction on the hold — price touches the wall band and turns, wall still standing.
- **Stop:** a few ticks past the wall price (a clean break of the defended level kills the pullback thesis).
- **Target:** prior swing in trend direction / measured move. Trail behind subsequent walls.
- **Invalidation:** wall **PULLED** before price gets there (the support was fake → no trade, stand aside) or **CONSUMED** on the test (trend pullback became a reversal).

### Setup 3 — Break-and-Retest of a Consumed Wall
- **Context:** a level that *was* a wall breaks. Classic continuation/breakout on the chart.
- **Radar trigger:** a tracked wall resolves **CONSUMED** (traded through, quote crossed) and the engine keeps it as a demoted **flipped reference** (half confidence). Price comes back to retest that exact price.
- **Entry:** in the break direction on the retest holding (short the retest of a consumed bid wall from below, i.e. old support now resistance).
- **Stop:** a few ticks back inside the old range (if price reclaims the consumed level, the break failed).
- **Target:** next remembered wall / range extension. The radar's memory band is useful here — it shows the *next* liquidity shelf you're targeting.
- **Invalidation:** price reclaims and *holds* above/below the consumed level (especially if a fresh WALL forms there in the new direction).

### Setup 4 — Spoof-Clearance Continuation (trade the PULL, not at it)
- **Context:** price is grinding toward a chart objective but a big **WALL** sits in the way (e.g. a large ask wall capping an up-move).
- **Radar trigger:** that blocking wall resolves **PULLED** (`Phantom`) — it vanished with the quote still away and no trades to explain it. The obstacle was fake.
- **Entry:** continuation *through where the wall was*, on the first confirmation (e.g. the inside quote lifting after the ask wall pulled). The path is now clear.
- **Stop:** structural, behind the last chart swing — **not** at the phantom price (it's gone; it's not support).
- **Target:** the next *real* (remembered/confirmed) wall in the direction of travel.
- **Invalidation:** a *new* genuine WALL re-forms and persists at/near the same price (someone real stepped in) — the clearance was temporary.
- **Honesty flag:** PULLED is the radar's least certain classification. This is the lowest-conviction setup; use it to *remove a reason not to take a chart trade*, rarely as the whole thesis.

### Setup 5 — Memory-Revisit Confluence
- **Context:** intraday, price returns to a price where the radar earlier tracked a strong wall — now a **Remembered** node still carrying meaningful **Confidence** after decay — and that price *also* lines up with a chart level (prior POC, session high, VWAP).
- **Radar trigger:** as price re-enters the band, the remembered node comes back into the live window and **re-confirms** (flips Remembered→Wall, `TimesConfirmed` increments, confidence steps up). Confluence = chart level + re-confirmed liquidity.
- **Entry:** at the confluence, in the direction the re-confirmed wall supports, ideally waiting for an ABSORBED tick (then it's really Setup 1 with extra confluence).
- **Stop:** structural beyond the level.
- **Target:** opposite confluence / next shelf.
- **Invalidation:** the revisit shows the level **gone/pulled** (memory was stale — confidence was decaying for a reason) → no trade. *Never trade a remembered node as if it were live size* — wait for the live re-confirm.

---

## 5. What to AVOID

- **Trading a single fresh wall as a level.** A wall that just appeared and hasn't been tested is the *least* proven object on the screen and the spoofer's favorite. Wait for an episode to resolve (ABSORB/PULL/CONSUME) before you believe it.
- **Spoof traps.** If a wall keeps appearing/vanishing without trades, it's layering — the flicker guard (`F_flicker`) and PULLED classifier exist precisely so you *don't* lean on it. Honor `Phantom`.
- **NQ walls, full stop.** With the thin MBP book and the 40-contract floor, walls don't confirm; if you lower the floor you'll trade noise. Use NQ only for raw inside-the-spread imbalance/absorption eyeballing, if at all.
- **Chasing the fast tape.** The engine timers are seconds-scale; if the tape is ripping, classifications lag and the `VolGovernor` is already telling you memory is suspect. Stand aside in violent moves; don't try to out-click HFT.
- **Over-trusting one wall / one ABSORB.** Absorption can be one big player who then *also* gives up. Size the absorption fade small; let it prove itself (Setup 1's invalidation is the same node going CONSUMED).
- **RTH-open and number chaos.** The first minutes and macro releases reset the book repeatedly (`IsReset`); the radar correctly refuses to read resets as real pulls, but that also means the wall/memory picture is unreliable until the book settles. Let it form.
- **Treating ABSORBED as proven before Replay calibration.** The attribution window (`W_assoc`) isn't wired yet (§2). Until it's validated, ABSORBED is "probably" — trade it, but at reduced size.
- **Using it for the next tick.** Wrong tool, wrong latency seat. It's a structure/timing tool, not an HFT edge.

---

## 6. Feature gaps — prioritized (to make entries sharper)

| # | Feature | Edge it adds | Effort | Notes |
|---|---|---|---|---|
| **1** | **Cumulative-delta / running bid-vs-ask imbalance readout** (a number + small history strip alongside the ladder) | Adds the **one order-flow signal with real documented predictive power** (OFI ↔ short-horizon price, Cont-Kukanov-Stoikov) that the radar currently makes you eyeball. Directly powers the bias in §3.1 and times every setup. | **Medium** | `BookMirror` already infers aggressor side and tracks `Traded@P`; aggregate it into running delta + a bid-mass/ask-mass ratio. Mostly engine-side; one new readout in `RadarVisual`. |
| **2** | **Persistent absorption/episode log** (scrolling, timestamped list of ABSORBED/PULLED/CONSUMED with price, size, time) | Today episodes feed memory then vanish. A log lets you see *"this price absorbed 3× today"* = exactly the multi-touch confluence that distinguishes a real defended level from a one-off. Massive for trade selection. | **Low–Med** | Episodes already exist (`EpisodeResult`); just persist a ring buffer + render a panel. No new microstructure logic. |
| **3** | **Wire & calibrate `W_assoc` trade attribution** (the `EpisodeClassifier.cs:85-87` TODO) | Makes ABSORBED vs PULLED *correct* per-decrease instead of episode-summed. Sharpens the flagship signal you're actually trading. | **Medium** | Correctness fix, not a new feature — but it's the prerequisite to trusting Setup 1/4 at full size. Needs a Market Replay data-capture path to calibrate. |
| **4** | **Delta-at-price / volume-at-price strip** per ladder row | Shows *where* the day's trading concentrated next to where liquidity rests — turns "a wall is here" into "a wall is here and 4k traded into it." Confluence with absorption. | **Med–High** | New per-price accumulation in `BookMirror`; new render column. |
| **5** | **Confluence alerts** (sound/popup when ABSORBED or wall-re-confirm fires at a user-marked price) | Lets you *not stare* — the tool watches levels you care about and pings on the exact resolution you trade. | **Low** | Deferred non-goal in the spec, but cheap and high-leverage for a confirmation tool. |
| 6 | Cross-restart memory persistence | Keeps remembered levels across NT restarts (useful for overnight→RTH continuity). | Low | Spec-deferred; low impact on intraday entries. |

**Top 2 by value: #1 (imbalance/cumulative-delta readout) and #2 (persistent absorption log).** #1 supplies the missing directional signal with literature behind it; #2 makes the radar's best signal *accumulate into confluence* instead of evaporating.

---

## 7. Final verdict

**(b) — a confirmation / timing tool layered on a chart strategy. On ES. Not a standalone signal generator; not worth trading on NQ for walls.**

- It does not generate trades. It *qualifies* trades a chart thesis already proposes: is the level **defended** (ABSORBED → take the fade), **fake** (PULLED → clear the path or skip), or **gone** (CONSUMED → trade the retest). That qualification is real, ES-specific, and not available from price alone.
- Its weakest links are honestly disclosed by the code itself: walls prove persistence not intent, PULLED can't prove a spoof, and the absorb attribution window isn't calibrated yet.
- The biggest *missing* edge is the one the literature most supports — order-flow imbalance — and it's currently eyeballed, not computed.

**Single highest-value next step:** run a **dedicated ES Market Replay calibration session** to (a) validate that ABSORBED/PULLED/CONSUMED fire when they should on a deep book, (b) tune the ES thresholds (`MinAbsSize`, `K_mult`, `RefillRatio`, `A_absorb`) against real episodes, and (c) wire `W_assoc` (gap #3). **Reason:** every setup in §4 rests on those classifications being trustworthy, and the engine currently flags them as uncalibrated. Calibrate first — *then* build the imbalance/cumulative-delta readout (gap #1) as the first new feature. Trading the ABSORBED signal at size before this calibration is the single biggest unforced risk in the whole project.

---

## Sources

- Cont, Kukanov, Stoikov — order-flow imbalance ↔ short-horizon price (near-linear, sensitivity ∝ 1/depth): https://www.emergentmind.com/topics/order-book-imbalance-obi , https://arxiv.org/pdf/1707.01167
- Queue imbalance as a one-tick-ahead predictor (decays after ~2 mid-changes): https://arxiv.org/pdf/1512.03492
- Microprice (Stoikov) beats mid for short horizon: https://arxiv.org/html/2411.13594v1
- Iceberg / absorption reading (refill = hidden size): https://bookmap.com/blog/how-to-read-and-trade-iceberg-orders-hidden-liquidity-in-plain-sight , https://justintrading.com/iceberg-orders-spoofing-futures/
- Spoofing/layering prevalence, concentrates in thin liquidity, Sarao/ES e-mini CFTC case: https://academic.oup.com/cmlj/article/20/3/kmaf012/8257809 , https://www.morganlewis.com/-/media/files/publication/outside-publication/article/financier-worldwide-spoofing-the-order-book-nov2015.pdf
- Latency reality / HFT front-running of visible flow; retail edge vanishes >~200 ms: https://onlinelibrary.wiley.com/doi/10.1111/fire.12103 , https://bookmap.com/blog/can-real-time-order-flow-give-you-an-edge-in-scalp-trading
- Large orders ≠ informed (Archegos forced liquidation), confluence > single signal, journaling 100+ trades: https://orderflowlabs.com/blogs/theblog/trading-with-the-dom-depth-of-market , https://journalplus.co/strategies/order-flow-trading/
