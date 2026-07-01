using System;
using TradingRadar.Engine;
using Xunit;

public class ControllerStateMachineTests
{
    static DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    static BookMirror EmptyBook() => new BookMirror(0.25, TimeSpan.FromSeconds(30));

    static ControllerInputs In(double wallAbovePrice, long wallAboveCur, double wallBelowPrice, long wallBelowCur,
                               long delta, double z, int alt, double mid, int sec, BookMirror book)
        => new ControllerInputs {
            WallAbovePrice = wallAbovePrice, WallAboveCurrent = wallAboveCur,
            WallBelowPrice = wallBelowPrice, WallBelowCurrent = wallBelowCur,
            AggressorDelta = delta, TapeZScore = z, TapeAlternations = alt, Mid = mid, Now = T(sec), Book = book };

    static ControllerStateMachine Machine() => new ControllerStateMachine(new ControllerConfig(), 0.25);

    // A big ask wall above price arms the LONG candidate.
    [Fact]
    public void Arms_long_when_dominant_ask_wall_above_meets_significance()
    {
        var m = Machine();
        var o = m.Update(In(100.25, 120, 0, 0, delta: 0, z: 0, alt: 0, mid: 100.00, sec: 1, book: EmptyBook()));
        Assert.Equal(SideState.Armed, o.Long);
    }

    // Intact wall + heavy book skew must NOT advance past Armed (no countdown, no fire) — §5b.
    [Fact]
    public void Intact_wall_does_not_advance_on_book_skew_alone()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        var o = m.Update(In(100.25, 120, 0, 0, delta: 999, z: 5, alt: 0, mid: 100.00, sec: 2, book: EmptyBook())); // size unchanged
        Assert.Equal(SideState.Armed, o.Long);
        Assert.False(o.Fired);
    }

    // A wall below significance does not arm.
    [Fact]
    public void Does_not_arm_when_wall_below_significance()
    {
        var m = Machine();
        var o = m.Update(In(100.25, 5, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        Assert.Equal(SideState.Waiting, o.Long);
    }

    static BookMirror BookWithBuys(double wallPrice, long vol, int sec)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice - 0.5, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) }); // buy aggressor at the wall
        return b;
    }

    [Fact]
    public void Enters_countdown_when_drop_is_trade_backed()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));         // arm at peak 120
        var book = BookWithBuys(100.25, 60, 2);                                    // 60 bought at the wall
        var o = m.Update(In(100.25, 60, 0, 0, 0, 0, 0, 100.00, 2, book));          // size 120->60 (drop 60, all traded)
        Assert.Equal(SideState.Countdown, o.Long);
        Assert.True(o.LongFraction > 0.49 && o.LongFraction < 0.51);
    }

    [Fact]
    public void Pull_without_trades_vetoes_to_cooldown()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));          // arm
        var o = m.Update(In(100.25, 60, 0, 0, 0, 0, 0, 100.00, 2, EmptyBook()));   // size dropped, NO prints => pull
        Assert.Equal(SideState.Cooldown, o.Long);
    }

    // Drives an ask wall from 120 down to ~30 (75% eaten) with buys, delta+ and z high, for K snapshots.
    [Fact]
    public void Fires_long_once_on_full_confluence_then_latches()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook())); // arm, peak 120
        var b2 = BookWithBuys(100.25, 60, 2);
        m.Update(In(100.25, 60, 0, 0, 20, 2.0, 0, 100.00, 2, b2));        // countdown
        var b3 = BookWithBuys(100.25, 90, 3);                             // more buys at the wall (cumulative 90+)
        ControllerOutput o = default(ControllerOutput);
        int fires = 0;
        for (int s = 3; s <= 8; s++)                                      // hold several snapshots (K=3)
        {
            var b = BookWithBuys(100.25, 90, s);
            o = m.Update(In(100.25, 30, 0, 0, delta: 20, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b));
            if (o.Fired) fires++;
        }
        Assert.Equal(1, fires);                       // one-shot
        Assert.Equal(Side.Ask, o.Fire.Side);          // ask wall above => long break
        Assert.Equal(SideState.Fired, o.Long);        // latched
    }

    // Opposing delta blocks the fire even at high consumption.
    [Fact]
    public void Does_not_fire_long_when_delta_opposes()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        for (int s = 2; s <= 8; s++)
        {
            var b = BookWithBuys(100.25, 90, s);
            var o = m.Update(In(100.25, 30, 0, 0, delta: -30, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b)); // sellers pressing
            Assert.False(o.Fired);
        }
    }

    [Fact]
    public void Chop_suppresses_fire_even_at_high_consumption()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        for (int s = 2; s <= 8; s++)
        {
            var b = BookWithBuys(100.25, 90, s);
            // z below ChopSlowZ and alternations above ChopAltCount => CHOP, must not fire despite 75% eaten + buys.
            var o = m.Update(In(100.25, 30, 0, 0, delta: 20, z: -0.5, alt: 4, mid: 100.00, sec: s, book: b));
            Assert.True(o.Chop);
            Assert.False(o.Fired);
        }
    }

    // THE anti-flip gate: an input stream whose book skew flips every tick must produce NO fire and
    // never leave a stable non-firing state. Walls are intact (no consumption), delta/skew oscillate.
    [Fact]
    public void Does_not_oscillate_or_fire_on_flipping_book_skew()
    {
        var m = Machine();
        int fires = 0;
        for (int s = 1; s <= 40; s++)
        {
            long delta = (s % 2 == 0) ? 50 : -50;      // skew flips every snapshot
            double z = (s % 2 == 0) ? 2.0 : -0.5;      // and so does speed
            int alt = (s % 3 == 0) ? 4 : 0;
            // Walls present but INTACT (current == peak), so no consumption can arm the countdown.
            var o = m.Update(In(100.25, 120, 99.75, 120, delta, z, alt, 100.00, s, EmptyBook()));
            if (o.Fired) fires++;
            Assert.NotEqual(SideState.Countdown, o.Long);   // intact wall never enters countdown
            Assert.NotEqual(SideState.Countdown, o.Short);
        }
        Assert.Equal(0, fires);
    }
}
