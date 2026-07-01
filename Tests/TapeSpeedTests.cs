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
}
