using System;
using TradingRadar.Engine;
using Xunit;

public class PrimitivesTests
{
    [Fact]
    public void RadarConfig_defaults_to_NQ()
    {
        var c = new RadarConfig();
        Assert.Equal(0.25, c.TickSize);
        Assert.Equal(4.0, c.K_mult);
        Assert.Equal(40, c.MinAbsSize);
        Assert.Equal(25, c.MemoryBandTicks);
        Assert.Equal(TimeSpan.FromSeconds(30), c.BaselineWindow);
        Assert.Equal(TimeSpan.FromSeconds(30), c.H);
    }

    [Fact]
    public void DepthEvent_carries_its_own_timestamp()
    {
        var t = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);
        var e = new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.25, Volume = 50, Time = t };
        Assert.Equal(t, e.Time);
        Assert.False(e.IsReset);
    }
}
