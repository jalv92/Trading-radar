# Liquidity Radar — Deploy & Run Procedure

## Deploy to NinjaTrader 8

1. Copy `Engine/*.cs` and `NinjaTrader/*.cs` into:
   ```
   %USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\AddOns\LiquidityRadar\
   ```
   Also copy `docs/images/channel-logo.jpg` into the same folder — the branding lockup in the
   cockpit loads it from there (missing file = no branding, everything else unaffected).

2. Open NinjaTrader 8 → NinjaScript Editor → Compile (F5).
   Fix any compile error in the NT classes ONLY (never the engine).

3. Restart NinjaTrader (external menu registration requires it the first time).

4. Control Center → New → "Liquidity Radar" → the floating window opens.

5. Pick NQ (front month) in the InstrumentSelector.

## Compile validation (local, before deploying)

```bash
bash build/stage-custom.sh
nt8c build --custom-dir build/.stage/Custom
```

Expected: 0 errors. The per-file hook reports cross-file CS0246/CS0234 false positives
(engine types only resolve when all files are compiled together) — trust `nt8c build`.

## Market Replay validation

After deploying, load a recorded NQ Market Replay session with depth
(Tools → Historical Data → depth; Replay Connection). Verify:

- Window opens; sonar ladder renders; inside-market amber line tracks.
- A genuinely large resting level shows WALL badge only after ~1.5s persistence.
- ABSORB (emerald) when trades hit the wall and it holds/refills.
- PULL (desaturated, dashed, slate) when size vanishes without trades.
- BROKE when price trades through.
- Memory band: walls beyond the live window dim to opacity .34 with "· Ns" age tag.
- No phantom absorption on approach-and-bounce without trades.
- Reset: stop/restart Replay; ladder rebuilds cleanly (no frozen ghost walls).
