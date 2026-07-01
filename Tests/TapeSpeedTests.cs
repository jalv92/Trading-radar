using System;
using TradingRadar.Engine;
using Xunit;

public class TapeSpeedTests
{
    static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    [Fact]
    public void ZScore_exceeds_gate_on_a_real_spike()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 80; i++) ts.Sample(10.0 + (i % 2 == 0 ? 1.0 : -1.0), T(i * 50)); // baseline ~10 ±1
        ts.Sample(60.0, T(4100));   // strong spike
        Assert.True(ts.Ready);
        Assert.True(ts.ZScore > 2.0);
    }

    [Fact]
    public void ZScore_stays_below_gate_on_an_in_baseline_sample()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 80; i++) ts.Sample(10.0 + (i % 2 == 0 ? 1.0 : -1.0), T(i * 50));
        ts.Sample(11.0, T(4100));   // within the noise band
        Assert.True(ts.ZScore < 2.0);
    }

    // Regression guard: post-update scoring caps z at sqrt(0.9/0.1)=3.0 regardless of spike size.
    // Pre-update scoring must scale — a ~10x-baseline spike reads well above that ceiling.
    [Fact]
    public void ZScore_does_not_saturate_and_scales_with_spike_size()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 80; i++) ts.Sample(10.0 + (i % 2 == 0 ? 1.0 : -1.0), T(i * 50));
        ts.Sample(100.0, T(4100));  // ~10x baseline
        Assert.True(ts.ZScore > 5.0);
    }

    // Boundary: on the exact call where Ready first flips true, ZScore must be computable,
    // not force-zeroed by a one-sample gate desync.
    [Fact]
    public void ZScore_is_computable_on_the_call_that_first_reports_ready()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 19; i++) ts.Sample(10.0 + (i % 2 == 0 ? 1.0 : -1.0), T(i * 50));
        Assert.False(ts.Ready);              // 19 samples: not yet ready
        ts.Sample(40.0, T(2000));            // 20th sample -> Ready flips true THIS call
        Assert.True(ts.Ready);
        Assert.NotEqual(0.0, ts.ZScore);     // must be computable on the same call
    }

    // Fix 5 regression: a NaN/Infinity sample must be ignored, not absorbed into _mean/_var
    // (which would poison every future ZScore for the rest of the session).
    [Fact]
    public void NaN_or_infinite_sample_does_not_poison_the_baseline()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 80; i++) ts.Sample(10.0 + (i % 2 == 0 ? 1.0 : -1.0), T(i * 50));
        ts.Sample(double.NaN, T(4000));
        ts.Sample(double.PositiveInfinity, T(4050));
        Assert.False(double.IsNaN(ts.ZScore) || double.IsInfinity(ts.ZScore));
        ts.Sample(11.0, T(4100));            // within the noise band — must still read sane, not poisoned
        Assert.True(ts.ZScore < 2.0);
    }
}
