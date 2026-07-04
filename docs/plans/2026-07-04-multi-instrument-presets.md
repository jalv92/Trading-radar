# Per-instrument presets + concurrent ES/NQ capture — 2026-07-04

Goal: run ES and NQ in two radar windows at once, each recording, to collect per-instrument
calibration data in parallel. Everything was tuned for ES; NQ now loads its own (uncalibrated)
starting preset instead of silently inheriting the ES floors.

## What shipped (mechanism)

- **`Engine/InstrumentPresets.cs`** — compiled `InstrumentPresets.For(masterName)` → `InstrumentPreset`
  (ControllerConfig + PressureConfig + MinAbsSize + Label). `"NQ"` → NQ preset; anything else → ES
  (the only calibrated set). **No runtime config file** — the ML-calibration ADR forbids a loader (it
  bypasses the compile gate); presets are compiled and redeployed via `nt8c`. Presets carry **detection
  thresholds only** — the AUTO hard gates stay in `RadarChartTrader` and are fenced out of
  `ControllerConfig` by `Phase0GuardTests`.
- **`RadarTab`** applies the preset at the three engine-rebuild sites — the Instrument setter (also
  rebuilds `_pressure`, which previously never rebuilt on switch), `HandleReplayReset` (same instrument),
  and the ctor (ES default). On instrument select it prints `[Radar] preset: <Label>` to the Output
  window so you can confirm which config is live.
- **`lr-sessions-<instrument>.csv`** — the per-session summary is now per-instrument, so the ADR
  distinct-day count and the fires=0 alarm don't cross-contaminate between ES and NQ.
- ES behavior is byte-identical (ES preset == the compiled defaults). `dotnet test` 117/117, `nt8c` 20
  files 0/0.

## NQ starting preset (UNCALIBRATED structural priors — trading-quant-researcher)

Scaled from ES by a ~0.30 resting-depth ratio (size floors) and ~0.50 aggressor-flow ratio (delta
knobs). Everything dimensionless / tick-based stays ES-identical (both tick 0.25).

| Knob | ES | NQ start | Why |
|------|----|----|-----|
| `ControllerConfig.SignificanceBand` | 60 | **12** | biased LOW on purpose — arm gate is `max(SignificanceBand, adaptiveSig)`, so the self-adapting p85-of-depth floor lifts it on deep walls; too-low self-corrects, ES's 60 would permanently over-gate NQ's thinner book. |
| `ControllerConfig.DeltaFloor` | 30 | **15** | same `AggressorDelta` feed as DeltaScale; ×0.50, keeps the ES 2.14× coupling. |
| `PressureConfig.DeltaScale` | 14 | **7** | ×0.50; feeds the reference-only cockpit (never fires), lowest risk. |
| `PressureConfig.AirThinSize` | 9 | **3** | ×0.30; keeps ES's "hole ≈ 0.21× per-level depth" ratio. |
| `RadarConfig.MinAbsSize` | 20 | **6** | ×0.30 secondary wall floor (K_mult=4×baseline dominates when the book is full). |

Held ES-constant (correct because both tick 0.25 and these are ratios/tick-distances/z-scores, not
contract counts): FireFrac 0.6, ZFloor 1.5, MinTradeBackedRatio 0.6, MinDropBandFrac 0.12, JudgeTicks 2,
AwayTicks 6, K 3, KWindow 5, ChopSlowZ −0.3, ChopAltCount 3, ZTrustSeconds 0.35, ReloadFrac 0.25,
Cooldown 10s, and all PressureConfig weights/gains.

## Day-1 dual run — procedure

Prereqs (platform, not code):
1. Download Market Replay data (depth + trades) for **the same date** for BOTH ES and NQ — a normal
   mixed trend+chop RTH day, **not** FOMC/CPI/NFP (save event days for the campaign). NT8 downloads one
   instrument at a time.
2. Recompile the add-on in NT8 (**close the radar window → F5 → reopen**) so the preset build is live.

Run:
3. Open **two** radar windows. Set one to **ES**, one to **NQ**. On each, confirm the Output window shows
   `[Radar] preset: ES (calibrated…)` and `[Radar] preset: NQ (recon…UNCALIBRATED)` — that's the proof
   the right config loaded.
4. On **both**: check **Rec**, leave **AUTO OFF** (this is an observation pass — we're measuring arm/
   detection behavior, not trading). Same Playback connection drives both (one shared clock/speed).
5. Run the full RTH session at high replay speed. At the end, uncheck Rec on both windows (a moment
   apart) and stop Playback.
6. Ping me — I read `lr-signals-ES-*` and `lr-signals-NQ-*` and grade both against the criteria below,
   then tell you whether we're clear to start the 10-distinct-day campaign or need one NQ knob nudged.

## Day-1 acceptance criteria (per the quant-researcher)

Reference = the ES "healthy funnel": thousands Armed/side → tens reach Countdown → tens Fire. NQ should
reproduce the funnel **shape**, not the magnitudes.

**PASS (NQ floors in the right ballpark):**
- Armed count per side same order of magnitude as ES, roughly **[500, 5000]/side** for the day.
- `adaptiveSig` stabilizes at a sane NQ level (expect ~**8–20** vs ES 39–45) and its active-RTH value
  sits **at or above SignificanceBand=12** — i.e. the adaptive band, not the static floor, is gating.
- **Countdown reachable on both sides**, ≥ ~5 total.
- **Fires** in a sane band, roughly **[1, ~150]/day** (a few to some tens). Zero fires is
  acceptable-but-marginal **only if Countdown was reached** (the last-mile confluence is dimensionless,
  shared with ES — a miss there isn't an NQ-floor problem).
- `ctrlWallSz` p50 lands meaningfully above 12 with a real right tail (armed walls were genuinely
  significant, not floor-pinned).

**Falsifiable "adjust X" rules (one-knob, one re-run):**
- Armed ≈ 0 (both sides < ~100/day) → over-gated → **SignificanceBand 12→8** (and MinAbsSize 6→4).
- Armed ≈ every row / tens of thousands → under-gated → **SignificanceBand 12→18**.
- Armed healthy but **Countdown = 0** → **DeltaFloor 15→10** (only contract-denominated confirm gate).
- Fires in the thousands → floors too low → raise SignificanceBand + DeltaFloor.
- Delta pressure lean pinned at ±1 → DeltaScale 7→11; never above ActiveFloor → DeltaScale 7→4.
- Radar draws ~no walls → MinAbsSize 6→4; spams 1–2-lot levels → 6→10.

**Single most important read: Armed count per side.** A starved Armed funnel is the only outcome that
wastes the whole replay day (no Countdown/Fire data to grade); everything else is a cheaper single-knob
fix. So I check that first.

## After day-1

- If NQ passes → start the **concurrent 10-distinct-day campaign** (ADR §5 protocol: a new date per
  session, Rec on, varied regimes — now capturing ES-day-N and NQ-day-N synchronized off one replay
  clock, roughly halving calendar time to the gate).
- Calibration stays **independent per instrument**: the NQ preset keeps its "uncalibrated" label until NQ
  has its OWN gate-clearing corpus (≥10 distinct days, ≥15–20 realized fills), exactly as the ADR
  mandates for ES. AUTO on NQ beyond Sim observation is out of bounds until then.
- Don't read the per-window PnL bar as account risk if both windows share one Sim account — NT8 nets
  PnL/margin across ES+NQ (cosmetic in Sim). A dedicated Playback account per window gives clean PnL.
