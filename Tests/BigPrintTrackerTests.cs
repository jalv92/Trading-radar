using System;
using TradingRadar.Engine;
using Xunit;

public class BigPrintTrackerTests
{
    static DateTime T(int s) => new DateTime(2026, 1, 1, 9, 40, 0, DateTimeKind.Utc).AddSeconds(s);

    // Buyer lifts the ask in size => positive net; small prints never count.
    [Fact]
    public void Big_buy_at_ask_counts_small_prints_do_not()
    {
        var bp = new BigPrintTracker(10, TimeSpan.FromMinutes(5));
        bp.OnTrade(100.25, 12, 100.00, 100.25, T(0));   // 12 lots at ask
        bp.OnTrade(100.25, 5, 100.00, 100.25, T(1));    // 5 lots — below MinLots
        Assert.Equal(12, bp.Net(T(2)));
        Assert.Equal(1, bp.Count);
    }

    // Seller hits the bid in size => negative net; buys and sells offset.
    [Fact]
    public void Sells_at_bid_offset_buys_at_ask()
    {
        var bp = new BigPrintTracker(10, TimeSpan.FromMinutes(5));
        bp.OnTrade(100.25, 20, 100.00, 100.25, T(0));   // +20
        bp.OnTrade(100.00, 30, 100.00, 100.25, T(1));   // -30
        Assert.Equal(-10, bp.Net(T(2)));
    }

    // Prints inside the spread or with a crossed/absent quote are ignored.
    [Fact]
    public void Inside_spread_and_bad_quotes_are_ignored()
    {
        var bp = new BigPrintTracker(10, TimeSpan.FromMinutes(5));
        bp.OnTrade(100.10, 50, 100.00, 100.25, T(0));   // inside spread
        bp.OnTrade(100.25, 50, 0, 100.25, T(1));        // no bid
        bp.OnTrade(100.25, 50, 100.25, 100.25, T(2));   // ask == bid
        Assert.Equal(0, bp.Net(T(3)));
    }

    // Prints roll out of the window and the net follows.
    [Fact]
    public void Window_eviction_removes_stale_prints()
    {
        var bp = new BigPrintTracker(10, TimeSpan.FromMinutes(5));
        bp.OnTrade(100.25, 15, 100.00, 100.25, T(0));
        bp.OnTrade(100.00, 25, 100.00, 100.25, T(60));
        Assert.Equal(-10, bp.Net(T(61)));
        Assert.Equal(-25, bp.Net(T(301)));  // the +15 at t=0 expired
        Assert.Equal(0, bp.Net(T(400)));    // all expired
    }
}
