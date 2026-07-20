using TradingRadar.Engine;
using Xunit;

// ADR 2026-07-03 Phase 0 follow-up: per-instrument DETECTION-threshold presets (compiled switch,
// no runtime config loader — see Engine/InstrumentPresets.cs). ES is the calibrated baseline; NQ
// is an uncalibrated structural-prior starting point pending a real RTH NQ capture.
public class InstrumentPresetsTests
{
    [Fact]
    public void Es_MatchesCompiledDefaults()
    {
        var p = InstrumentPresets.For("ES");
        Assert.Equal(60, p.Controller.SignificanceBand);
        Assert.Equal(30, p.Controller.DeltaFloor);
    }

    // Day-1 measured priors (docs/calibration-nq-day1.md): SignificanceBand stays 12 (arm-RATE
    // equivalence with ES, ~55 edges/hr RTH); DeltaFloor moved 15 -> 30 (30 = p66 of RTH |delta15s|,
    // mirroring ES's own p66 placement — the old x0.5 flow-scaling prior measured wrong).
    [Fact]
    public void Nq_Day1ArmingThresholds()
    {
        var es = InstrumentPresets.For("ES");
        var nq = InstrumentPresets.For("NQ");
        Assert.Equal(12, nq.Controller.SignificanceBand);
        Assert.Equal(30, nq.Controller.DeltaFloor);
        Assert.True(nq.Controller.SignificanceBand < es.Controller.SignificanceBand);
    }

    // NQ day-1 wiring: React gets its first per-instrument preset — the ES-scale defaults left it
    // structurally dead on NQ (a >=60-contract wall exists in 0.23% of NQ rows -> 4 watches/day).
    [Fact]
    public void ReactPreset_EsKeepsDefaults_NqScaledToItsBook()
    {
        var es = InstrumentPresets.For("ES").Reactive;
        var defaults = new ReactiveConfig();
        Assert.Equal(defaults.SignificanceBand, es.SignificanceBand);
        Assert.Equal(defaults.DeltaFloor, es.DeltaFloor);

        var nq = InstrumentPresets.For("NQ").Reactive;
        Assert.Equal(8, nq.SignificanceBand);    // RTH p90 of dominant walls (~5-8 joint watches/hr)
        Assert.Equal(30, nq.DeltaFloor);         // mirrors Break's p66 placement
        Assert.Equal(defaults.AccelFloor, nq.AccelFloor);   // one knob at a time — untouched until the funnel breathes
    }

    [Fact]
    public void Nq_PressureKnobsScaledForThinnerBook()
    {
        var nq = InstrumentPresets.For("NQ");
        Assert.Equal(7.0, nq.Pressure.DeltaScale);
        Assert.Equal(3, nq.Pressure.AirThinSize);
    }

    [Fact]
    public void Nq_MinAbsSizeScaledForThinnerBook()
    {
        Assert.Equal(6, InstrumentPresets.For("NQ").MinAbsSize);
    }

    [Fact]
    public void Nq_KeepsDimensionlessKnobsIdenticalToEs()
    {
        var es = InstrumentPresets.For("ES").Controller;
        var nq = InstrumentPresets.For("NQ").Controller;
        Assert.Equal(es.FireFrac, nq.FireFrac);
        Assert.Equal(es.ZFloor, nq.ZFloor);
        Assert.Equal(es.MinTradeBackedRatio, nq.MinTradeBackedRatio);
        Assert.Equal(es.MinDropBandFrac, nq.MinDropBandFrac);
        Assert.Equal(es.JudgeTicks, nq.JudgeTicks);
        Assert.Equal(es.AwayTicks, nq.AwayTicks);
        Assert.Equal(es.K, nq.K);
        Assert.Equal(es.KWindow, nq.KWindow);
    }

    [Fact]
    public void UnknownInstrument_FallsBackToEs()
    {
        Assert.Equal(60, InstrumentPresets.For("unknown-symbol").Controller.SignificanceBand);
    }
}
