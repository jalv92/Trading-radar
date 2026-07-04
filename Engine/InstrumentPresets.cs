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
                MinAbsSize = 20,
                Label      = "ES (calibrated, rounds 1-8)"
            };
        }

        // UNCALIBRATED structural priors (trading-quant-researcher 2026-07-04): scaled from ES by a
        // ~0.30 resting-depth ratio (size floors) and ~0.50 aggressor-flow ratio (delta knobs); all
        // dimensionless/tick knobs stay ES-identical (both tick 0.25). SignificanceBand biased LOW (12,
        // not the ~18 central estimate) on purpose: the arm gate is max(SignificanceBand, adaptiveSig)
        // so the self-adapting p85-of-depth floor lifts it on genuinely deep walls — too-low self-
        // corrects, ES's 60 would permanently over-gate NQ's thinner book. Re-derive from a real RTH NQ
        // capture (day-1 acceptance criteria in the commit/plan) before treating any of these as measured.
        static InstrumentPreset Nq()
        {
            return new InstrumentPreset
            {
                Controller = new ControllerConfig { SignificanceBand = 12, DeltaFloor = 15 },
                Pressure   = new PressureConfig   { DeltaScale = 7.0, AirThinSize = 3 },
                MinAbsSize = 6,
                Label      = "NQ (recon starting point - UNCALIBRATED)"
            };
        }
    }
}
