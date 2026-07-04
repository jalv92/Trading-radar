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

    [Fact]
    public void Nq_HasLowerArmingThresholdsThanEs()
    {
        var es = InstrumentPresets.For("ES");
        var nq = InstrumentPresets.For("NQ");
        Assert.Equal(12, nq.Controller.SignificanceBand);
        Assert.Equal(15, nq.Controller.DeltaFloor);
        Assert.True(nq.Controller.SignificanceBand < es.Controller.SignificanceBand);
        Assert.True(nq.Controller.DeltaFloor < es.Controller.DeltaFloor);
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
