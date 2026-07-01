using System;
using TradingRadar.Engine;
using Xunit;

public class ConsumptionTrackerTests
{
    static DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    static BookMirror BookWithInside()
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(30));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 99.75, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 5, Time = T(0) });
        return b;
    }

    // Ask wall at 100.25 eaten from 100 -> 30 with 70 of buy volume printed there = fully trade-backed.
    [Fact]
    public void Consumption_is_trade_backed_when_prints_explain_the_drop()
    {
        var b = BookWithInside();
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 70, Time = T(1) }); // buy aggressor (>= best ask)
        var r = ConsumptionTracker.Read(Side.Ask, 100.25, peak: 100, current: 30, armTime: T(0), book: b);
        Assert.Equal(70, r.Drop);
        Assert.True(r.Fraction > 0.69 && r.Fraction < 0.71);
        Assert.True(r.TradeBackedFraction >= 0.99); // 70 traded / 70 drop
    }

    // Same drop, but NO prints at the wall = a pull/spoof: trade-backed fraction ~0.
    [Fact]
    public void Consumption_is_not_trade_backed_when_no_prints()
    {
        var b = BookWithInside();
        var r = ConsumptionTracker.Read(Side.Ask, 100.25, peak: 100, current: 30, armTime: T(0), book: b);
        Assert.Equal(70, r.Drop);
        Assert.True(r.TradeBackedFraction < 0.01);
    }
}
