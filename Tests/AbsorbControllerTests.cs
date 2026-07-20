using System;
using TradingRadar.Engine;
using Xunit;

public class AbsorbControllerTests
{
    // Test clock: a weekday morning, exchange wall time.
    static DateTime T(int h, int m, int s = 0) => new DateTime(2026, 1, 5, h, m, s);

    // Small fallback gate (10 ticks * 0.25 = 2.5 pts) so tests control it directly.
    static AbsorbConfig Cfg() => new AbsorbConfig { MinDayRangeTicks = 10, MinWallSize = 50 };

    // Feed one trade per minute from 09:10 to 09:34 (builds >15 completed
    // 1-min bars). Declining path opens the fallback event gate (range 5 pts)
    // and leaves the last prices at the session low.
    static AbsorbController Warmed(bool violent, out double lowPx)
    {
        var c = new AbsorbController(Cfg(), 0.25);
        double px = 100.0;
        for (int m = 10; m < 35; m++)
        {
            if (violent) px -= 0.2;                       // total ~5 pts range
            c.OnTrade(px, 1, px - 0.25, px, T(9, m));     // small print at ask
        }
        lowPx = px;
        return c;
    }

    static ControllerInputs Inp(DateTime now, double wallBelow, long size)
    {
        return new ControllerInputs
        {
            WallBelowPrice = wallBelow,
            WallBelowCurrent = size,
            WallBelowOutcome = Outcome.Absorbed,
            WallBelowOutcomeValid = true,
            Now = now
        };
    }

    [Fact]
    public void Fires_long_on_absorbed_wall_at_low_on_event_day()
    {
        double low;
        var c = Warmed(true, out low);
        c.OnTrade(low, 20, low - 0.25, low, T(9, 40));    // big BUY at ask
        var o = c.Update(Inp(T(9, 40, 5), low, 100));
        Assert.True(o.Fired);
        Assert.Equal(SetupKind.Absorb, o.Fire.Kind);
        Assert.Equal(Side.Ask, o.Fire.Side);              // TRADE side = buy
        Assert.Equal(Side.Bid, o.Fire.WallSide);
    }

    [Fact]
    public void No_fire_on_quiet_day_gate_closed()
    {
        double low;
        var c = Warmed(false, out low);                   // flat session, tiny range
        c.OnTrade(low, 20, low - 0.25, low, T(9, 40));
        var o = c.Update(Inp(T(9, 40, 5), low, 100));
        Assert.False(o.Fired);
        Assert.False(c.EventGateOpen);
    }

    [Fact]
    public void No_fire_outside_morning_window()
    {
        double low;
        var c = Warmed(true, out low);
        c.OnTrade(low, 20, low - 0.25, low, T(13, 0));
        var o = c.Update(Inp(T(13, 0, 5), low, 100));
        Assert.False(o.Fired);
    }

    [Fact]
    public void No_fire_when_big_prints_disagree()
    {
        double low;
        var c = Warmed(true, out low);
        c.OnTrade(low - 0.25, 20, low - 0.25, low, T(9, 40));  // big SELL at bid
        var o = c.Update(Inp(T(9, 40, 5), low, 100));
        Assert.False(o.Fired);
    }
}
