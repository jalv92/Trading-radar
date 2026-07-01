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
}
