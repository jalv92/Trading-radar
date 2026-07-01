using System;
using TradingRadar.Engine;
using Xunit;

public class TapeSpeedTests
{
    static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    [Fact]
    public void ZScore_is_positive_when_rate_spikes_above_baseline()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 60; i++) ts.Sample(10.0, T(i * 50)); // steady baseline ~10/s
        ts.Sample(40.0, T(3100));                                // spike
        Assert.True(ts.Ready);
        Assert.True(ts.ZScore > 2.0);
    }

    [Fact]
    public void ZScore_near_zero_on_steady_rate()
    {
        var ts = new TapeSpeed(0.1);
        for (int i = 0; i < 60; i++) ts.Sample(10.0, T(i * 50));
        Assert.True(Math.Abs(ts.ZScore) < 0.5);
    }
}
