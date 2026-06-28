using System;
using TradingRadar.Engine;
using Xunit;

public class EpisodeClassifierTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);

    // Ask wall at 21000.50; bids below. Buy aggressors (prints at/above ask) consume an ask wall.
    static BookMirror BookAskWall(long askWallSize, double bestAsk = 21000.50, double bestBid = 21000.25)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(30));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = bestBid, Volume = 20, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = bestAsk, Volume = askWallSize, Time = T0 });
        return b;
    }

    [Fact]
    public void Absorbed_when_trades_explain_drop_price_holds_and_level_refills()
    {
        var cfg = new RadarConfig();
        var c = new EpisodeClassifier(cfg);
        var book = BookAskWall(200);
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        // Heavy buying lifts the offer but it refills: displayed stays ~200, big Traded@P.
        for (int i = 1; i <= 5; i++)
            book.ApplyTrade(new TradeEvent { Price = 21000.50, Volume = 120, Time = T0.AddMilliseconds(i * 100) });
        // Still showing 200 (iceberg refill), price held at the ask.
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Update, Position = 0, Price = 21000.50, Volume = 200, Time = T0.AddMilliseconds(600) });
        c.Update(book, T0.AddMilliseconds(700));
        c.Update(book, T0.AddSeconds(4)); // timeout resolves
        Assert.True(c.TryTakeResolved(out var r));
        Assert.Equal(Outcome.Absorbed, r.Outcome);
    }

    [Fact]
    public void Pulled_when_size_vanishes_without_trades_and_quote_away()
    {
        var cfg = new RadarConfig();
        var c = new EpisodeClassifier(cfg);
        var book = BookAskWall(200); // quote at 21000.50, approach within 1 tick from bid 21000.25
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        // No trades at the wall. Size disappears while bid is still a tick away.
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Remove, Position = 0, Price = 21000.50, Volume = 0, Time = T0.AddMilliseconds(300) });
        c.Update(book, T0.AddMilliseconds(400));
        Assert.True(c.TryTakeResolved(out var r));
        Assert.Equal(Outcome.Pulled, r.Outcome);
        Assert.True(r.Cancelled > r.Traded);
    }

    [Fact]
    public void Consumed_when_price_breaks_through_with_trades()
    {
        var cfg = new RadarConfig();
        var c = new EpisodeClassifier(cfg);
        var book = BookAskWall(200);
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        // Trades eat the wall...
        for (int i = 1; i <= 2; i++)
            book.ApplyTrade(new TradeEvent { Price = 21000.50, Volume = 100, Time = T0.AddMilliseconds(i * 100) });
        // ...wall removed AND price breaks above: new best ask is higher.
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Remove, Position = 0, Price = 21000.50, Volume = 0, Time = T0.AddMilliseconds(250) });
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21000.75, Volume = 15, Time = T0.AddMilliseconds(250) });
        c.Update(book, T0.AddMilliseconds(300));
        Assert.True(c.TryTakeResolved(out var r));
        Assert.Equal(Outcome.Consumed, r.Outcome);
    }

    [Fact]
    public void OnApproach_is_idempotent_while_open()
    {
        var cfg = new RadarConfig();
        var c = new EpisodeClassifier(cfg);
        c.OnApproach(Side.Ask, 21000.50, 200, T0);
        Assert.True(c.HasOpenEpisode(Side.Ask, 21000.50));
        c.OnApproach(Side.Ask, 21000.50, 999, T0.AddMilliseconds(10)); // ignored
        Assert.True(c.HasOpenEpisode(Side.Ask, 21000.50));
    }
}
