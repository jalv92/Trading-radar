using System;
using System.Reflection;
using TradingRadar.Engine;
using Xunit;

// ADR 2026-07-03 Phase 0 guards: the depth-percentile baseline, the adaptive arm clamp, and the
// non-learnable-gates invariant (the AUTO hard gates must never become ControllerConfig knobs —
// nothing a future calibrator sweeps may ever reach them).
public class Phase0GuardTests
{
    // ---- DepthBaseline ----

    [Fact]
    public void Baseline_AbstainsUntilWarm()
    {
        var b = new DepthBaseline(1000);
        for (int i = 0; i < DepthBaseline.MinSamples - 1; i++) b.Add(50);
        b.EndBatch();
        Assert.Equal(0, b.P85);   // below MinSamples the compiled floor must rule alone
    }

    [Fact]
    public void Baseline_ComputesP85()
    {
        var b = new DepthBaseline(1000);
        for (int i = 1; i <= 1000; i++) b.Add(i);   // uniform 1..1000
        b.EndBatch();
        Assert.InRange(b.P85, 840, 860);            // p85 of 1..1000 ≈ 850
    }

    [Fact]
    public void Baseline_RingKeepsOnlyRecentSamples()
    {
        var b = new DepthBaseline(400);
        for (int i = 0; i < 600; i++) b.Add(10);    // old regime: tiny sizes
        for (int i = 0; i < 400; i++) b.Add(1000);  // new regime fills the whole ring
        b.EndBatch();
        Assert.Equal(1000, b.P85);                  // old regime fully evicted
    }

    [Fact]
    public void Baseline_ResetForgetsEverything()
    {
        var b = new DepthBaseline(1000);
        for (int i = 0; i < 500; i++) b.Add(100);
        b.EndBatch();
        b.Reset();
        Assert.Equal(0, b.P85);
        Assert.Equal(0, b.SampleCount);
    }

    [Fact]
    public void Baseline_IgnoresEmptyLevels()
    {
        var b = new DepthBaseline(1000);
        for (int i = 0; i < 400; i++) { b.Add(100); b.Add(0); b.Add(-5); }
        b.EndBatch();
        Assert.Equal(400, b.SampleCount);
        Assert.Equal(100, b.P85);
    }

    // ---- Adaptive arm clamp (max(floor, p85) — can only RAISE the bar) ----

    private static ControllerInputs In(long wallAbove, long adaptive)
    {
        return new ControllerInputs
        {
            WallAbovePrice = 101.0, WallAboveCurrent = wallAbove,
            WallBelowPrice = 0, WallBelowCurrent = 0,
            AggressorDelta = 0, TapeZScore = 0, TapeAlternations = 0,
            Mid = 100.0, Now = new DateTime(2026, 7, 3, 10, 0, 0),
            Book = new BookMirror(0.25, TimeSpan.FromSeconds(30)),
            AdaptiveSignificance = adaptive,
        };
    }

    [Fact]
    public void AdaptiveBand_RaisesTheArmBar()
    {
        var m = new ControllerStateMachine(new ControllerConfig(), 0.25);   // floor = 60
        // wall 70 clears the compiled floor but NOT the adaptive p85 of 100 -> must stay Waiting
        Assert.Equal(SideState.Waiting, m.Update(In(70, 100)).Long);
        // same wall with the baseline still warming up (0) -> floor rules -> arms
        Assert.Equal(SideState.Armed, m.Update(In(70, 0)).Long);
    }

    [Fact]
    public void AdaptiveBand_NeverLowersTheFloor()
    {
        var m = new ControllerStateMachine(new ControllerConfig(), 0.25);   // floor = 60
        // adaptive p85 of a dead-liquidity book (10) must NOT let a 20-lot micro-wall arm
        Assert.Equal(SideState.Waiting, m.Update(In(20, 10)).Long);
    }

    [Fact]
    public void AdaptiveBand_AppliesToTheShortSideToo()
    {
        var inp = new ControllerInputs
        {
            WallAbovePrice = 0, WallAboveCurrent = 0,
            WallBelowPrice = 99.0, WallBelowCurrent = 70,   // bid wall below = short candidate
            AggressorDelta = 0, TapeZScore = 0, TapeAlternations = 0,
            Mid = 100.0, Now = new DateTime(2026, 7, 3, 10, 0, 0),
            Book = new BookMirror(0.25, TimeSpan.FromSeconds(30)),
            AdaptiveSignificance = 100,
        };
        var m = new ControllerStateMachine(new ControllerConfig(), 0.25);
        Assert.Equal(SideState.Waiting, m.Update(inp).Short);   // 70 < adaptive 100 -> no arm
        inp.AdaptiveSignificance = 0;
        Assert.Equal(SideState.Armed, m.Update(inp).Short);     // warm-up -> floor 60 rules -> arms
    }

    // ---- Non-learnable gates invariant ----

    [Fact]
    public void ControllerConfig_NeverContainsTheAutoHardGates()
    {
        // The AUTO gates (daily fire cap, auto-cancel timeout, ATM-required, account/Sim gate,
        // ARM LIVE) live hardcoded in RadarChartTrader and are non-learnable by design (ADR
        // 2026-07-03). If any of them ever migrates into ControllerConfig — the surface a future
        // calibrator sweeps — this test fails the build.
        // "Fire" alone is deliberately absent — FireFrac is a legitimate calibration knob; the cap
        // is covered by "AutoFire". Static members included so a `const` gate can't slip through.
        string[] forbidden = { "AutoFire", "AutoCancel", "Cancel", "Atm", "Account", "Sim", "Playback", "ArmLive", "Provider" };
        var members = typeof(ControllerConfig).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        foreach (MemberInfo m in members)
            foreach (string f in forbidden)
                Assert.False(m.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0,
                    "ControllerConfig." + m.Name + " matches forbidden gate token '" + f + "' — hard gates must never be calibrator-reachable.");
    }
}
