using System;
using TradingRadar.Engine;
using Xunit;

public class ZZZScratchStaleFractionTests
{
    static DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);
    static BookMirror EmptyBook() => new BookMirror(0.25, TimeSpan.FromSeconds(30));
    static ControllerInputs In(double wallAbovePrice, long wallAboveCur, double wallBelowPrice, long wallBelowCur,
                               long delta, double z, int alt, double mid, int sec, BookMirror book)
        => new ControllerInputs {
            WallAbovePrice = wallAbovePrice, WallAboveCurrent = wallAboveCur,
            WallBelowPrice = wallBelowPrice, WallBelowCurrent = wallBelowCur,
            AggressorDelta = delta, TapeZScore = z, TapeAlternations = alt, Mid = mid, Now = T(sec), Book = book };

    [Fact]
    public void Waiting_after_wall_hop_from_armed_leaks_stale_nonzero_fraction()
    {
        var m = new ControllerStateMachine(new ControllerConfig(), 0.25);
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook())); // arm, peak 120
        // Sub-band jitter (drop=1 < MinDropBand=3) -- stays Armed but Fraction is set nonzero (1/120).
        var o2 = m.Update(In(100.25, 119, 0, 0, 0, 0, 0, 100.00, 2, EmptyBook()));
        Assert.Equal(SideState.Armed, o2.Long);
        Assert.True(o2.LongFraction > 0);

        // Wall hop -> identity guard fires -> State goes Waiting, but Fraction is never cleared.
        var o3 = m.Update(In(100.50, 120, 0, 0, 0, 0, 0, 100.00, 3, EmptyBook()));
        Assert.Equal(SideState.Waiting, o3.Long);
        Assert.True(o3.LongFraction > 0, "BUG CONFIRMED: Waiting state leaks stale fraction=" + o3.LongFraction);
    }
}
