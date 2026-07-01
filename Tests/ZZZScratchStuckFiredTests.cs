using System;
using TradingRadar.Engine;
using Xunit;

public class ZZZScratchStuckFiredTests
{
    static DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);
    static BookMirror EmptyBook() => new BookMirror(0.25, TimeSpan.FromSeconds(30));
    static ControllerInputs In(double wallAbovePrice, long wallAboveCur, double wallBelowPrice, long wallBelowCur,
                               long delta, double z, int alt, double mid, int sec, BookMirror book)
        => new ControllerInputs {
            WallAbovePrice = wallAbovePrice, WallAboveCurrent = wallAboveCur,
            WallBelowPrice = wallBelowPrice, WallBelowCurrent = wallBelowCur,
            AggressorDelta = delta, TapeZScore = z, TapeAlternations = alt, Mid = mid, Now = T(sec), Book = book };
    static BookMirror BookWithBuys(double wallPrice, long vol, int sec)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice - 0.5, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) });
        return b;
    }

    [Fact]
    public void Fired_gets_stuck_forever_on_a_false_break_reversal()
    {
        var m = new ControllerStateMachine(new ControllerConfig(), 0.25);
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook())); // arm, peak 120
        ControllerOutput o = default(ControllerOutput);
        for (int s = 2; s <= 8; s++) { var b = BookWithBuys(100.25, 90, s); o = m.Update(In(100.25, 30, 0, 0, 20, 2.0, 0, 100.00, s, b)); }
        Assert.Equal(SideState.Fired, o.Long); // fired as expected

        // Price pokes through only 2 ticks (0.50) then reverses hard back below the old wall,
        // and a NEW dominant wall now sits above the (lower) mid -- exactly what RadarTab's fresh
        // "biggest wall above mid" recompute would report every run.
        o = m.Update(In(101.00, 80, 0, 0, 0, 0, 0, 100.50, 9, EmptyBook()));  // brief poke through, new dominant wall @101.00
        Assert.Equal(SideState.Fired, o.Long);

        o = m.Update(In(100.75, 70, 0, 0, 0, 0, 0, 99.50, 10, EmptyBook())); // reversed back BELOW the old wall
        Assert.Equal(SideState.Fired, o.Long); // <-- still Fired, never reset

        // Even much later, with price sitting well below the old wall and a fresh, unrelated large
        // wall now dominant above the new (lower) mid -- it NEVER resets, because cur>0 always and
        // Mid never again exceeds WallPrice+AwayTicks*tick once price reversed away in the wrong direction.
        for (int s = 11; s <= 100; s++)
            o = m.Update(In(100.75, 70, 0, 0, 0, 0, 0, 99.50, s, EmptyBook()));
        Assert.Equal(SideState.Fired, o.Long); // stuck forever -- Long candidate permanently disabled
    }
}
