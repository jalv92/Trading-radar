namespace TradingRadar.Engine
{
    // A per-instrument bundle of DETECTION thresholds. Compiled, not loaded (ML-calibration ADR
    // forbids a runtime config file — the human + compile gate is the only path to production).
    // Presets carry detection thresholds ONLY — never AUTO hard gates (those live in RadarChartTrader
    // and are compile-fenced out of ControllerConfig by Phase0GuardTests).
    public sealed class InstrumentPreset
    {
        public ControllerConfig Controller;
        public PressureConfig   Pressure;
        // NQ day-1 wiring (2026-07-19): the React setup previously took `new ReactiveConfig()` at every
        // construction site — ES-scale defaults on every instrument, which left React structurally dead
        // on NQ (a >=60-contract wall exists in 0.23% of NQ rows). Same compiled-switch rule as the rest.
        public ReactiveConfig   Reactive;
        public long             MinAbsSize;   // RadarConfig absolute wall floor (Auto-calib may override live)
        public string           Label;        // shown in the Output window on load
    }

    public static class InstrumentPresets
    {
        // ES is the only calibrated instrument (8 rounds); its preset == the compiled ControllerConfig/
        // PressureConfig defaults, so re-calibrating ES later just edits those classes. Unknown instruments
        // fall back to ES (the only calibrated set) — logged via the Label so it's visible.
        public static InstrumentPreset For(string masterName)
        {
            switch (masterName)
            {
                case "NQ": return Nq();
                default:   return Es();
            }
        }

        static InstrumentPreset Es()
        {
            return new InstrumentPreset
            {
                Controller = new ControllerConfig(),   // current defaults ARE the ES calibration
                Pressure   = new PressureConfig(),
                Reactive   = new ReactiveConfig(),     // compiled defaults verbatim — React's ES baseline
                MinAbsSize = 20,
                Label      = "ES (calibrated, rounds 1-8)"
            };
        }

        // Day-1 MEASURED priors (docs/calibration-nq-day1.md, full RTH NQ capture, replay 2026-07-06):
        // - SignificanceBand 12 KEPT: ~55 arm-edges/hr RTH — inside the ES 50-100/hr acceptance target.
        //   NQ walls churn so fast that arm-RATE equivalence, not percentile equivalence, is the right
        //   yardstick (12 ~= p97 of wall sizes; matching ES's p85 would flood 226-282 arms/hr).
        // - DeltaFloor 15 -> 30: 15 ~= p40 of RTH |delta15s| (near-coin-flip, the day-1-ES defect at 8);
        //   30 = p66 RTH, mirroring ES's own 30 ~= p66 placement. The 2026-07-04 x0.5 flow-ratio prior
        //   was wrong — NQ RTH aggressor flow ~= ES's, not half.
        // - Reactive: first per-instrument React preset. Band 8 = RTH p90 of dominant walls (~5-8 joint
        //   watches/hr vs 0.3/hr at the ES-scale 60 — structurally dead); DeltaFloor 30 mirrors Break.
        //   One knob at a time: AccelFloor/proximity judged only after this funnel breathes.
        // Fire-path gates (FireFrac/trade-backed/K-window/reload) remain ES values — zero NQ RTH
        // Countdown episodes yet; still placeholders until ~15-20 real fires exist.
        static InstrumentPreset Nq()
        {
            return new InstrumentPreset
            {
                Controller = new ControllerConfig { SignificanceBand = 12, DeltaFloor = 30 },
                Pressure   = new PressureConfig   { DeltaScale = 7.0, AirThinSize = 3 },
                Reactive   = new ReactiveConfig   { SignificanceBand = 8, DeltaFloor = 30 },
                MinAbsSize = 6,
                Label      = "NQ (day-1 priors 2026-07-19 - uncalibrated fire path)"
            };
        }
    }
}
