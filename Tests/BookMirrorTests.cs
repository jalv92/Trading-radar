using System;
using System.Collections.Generic;
using TradingRadar.Engine;
using Xunit;

public class BookMirrorTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);
    static System.DateTime T(int s) => new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc).AddSeconds(s);
    static BookMirror NewBook() => new BookMirror(0.25, TimeSpan.FromSeconds(10));

    static DepthEvent Dep(Side s, DepthOp op, int pos, double px, long vol, double secs) =>
        new DepthEvent { Side = s, Op = op, Position = pos, Price = px, Volume = vol, Time = T0.AddSeconds(secs) };

    // ---- folded-in primitives check ----
    [Fact]
    public void RadarConfig_defaults_to_NQ()
    {
        var c = new RadarConfig();
        Assert.Equal(0.25, c.TickSize);
        Assert.Equal(4.0, c.K_mult);
        Assert.Equal(25, c.MemoryBandTicks);
    }

    [Fact]
    public void Add_then_best_bid_is_position_zero()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 1, 20999.75, 30, 0));
        Assert.True(b.TryBestBid(out var best));
        Assert.Equal(21000.00, best.Price);
        Assert.Equal(50, best.Volume);
    }

    [Fact]
    public void Update_overwrites_volume_at_position()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Add, 0, 21000.25, 20, 0));
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Update, 0, 21000.25, 200, 1));
        Assert.True(b.TryBestAsk(out var best));
        Assert.Equal(200, best.Volume);
    }

    [Fact]
    public void Remove_deletes_the_level()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 1, 20999.75, 30, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Remove, 0, 21000.00, 0, 1));
        Assert.True(b.TryBestBid(out var best));
        Assert.Equal(20999.75, best.Price);
        Assert.Single(b.Levels(Side.Bid));
    }

    [Fact]
    public void Reset_event_clears_both_sides()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Add, 0, 21000.25, 50, 0));
        b.ApplyDepth(new DepthEvent { IsReset = true, Time = T0.AddSeconds(1) });
        Assert.Empty(b.Levels(Side.Bid));
        Assert.Empty(b.Levels(Side.Ask));
    }

    [Fact]
    public void Median_is_robust_to_one_wall()
    {
        var b = NewBook();
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 10, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 1, 20999.75, 12, 0));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 2, 20999.50, 500, 0)); // wall
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 3, 20999.25, 11, 0));
        Assert.Equal(11, b.MedianSize(Side.Bid));                 // median(10,12,500,11)=11.5->11
        Assert.Equal(11, b.MedianSizeExcluding(Side.Bid, 20999.50)); // median(10,12,11)=11
    }

    [Fact]
    public void TradedAt_sums_only_matching_price_and_window_and_aggressor()
    {
        var b = NewBook();
        // Establish quote so aggressor can be inferred: best bid 21000.00, best ask 21000.25
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Add, 0, 21000.25, 50, 0));
        // Buy aggressor at the ask
        b.ApplyTrade(new TradeEvent { Price = 21000.25, Volume = 7, Time = T0.AddSeconds(1) });
        b.ApplyTrade(new TradeEvent { Price = 21000.25, Volume = 3, Time = T0.AddSeconds(2) });
        // A print at a different price must not count
        b.ApplyTrade(new TradeEvent { Price = 21000.00, Volume = 99, Time = T0.AddSeconds(2) });

        Assert.Equal(10, b.TradedAt(21000.25, T0, null));
        Assert.Equal(10, b.TradedAt(21000.25, T0, Side.Ask));   // buy aggressor hits ask
        Assert.Equal(0, b.TradedAt(21000.25, T0, Side.Bid));
        Assert.Equal(3, b.TradedAt(21000.25, T0.AddSeconds(1.5), null)); // window excludes the first
    }

    [Fact]
    public void TradeRing_prunes_entries_older_than_retention()
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(5));
        b.ApplyDepth(Dep(Side.Bid, DepthOp.Add, 0, 21000.00, 50, 0));
        b.ApplyDepth(Dep(Side.Ask, DepthOp.Add, 0, 21000.25, 50, 0));
        b.ApplyTrade(new TradeEvent { Price = 21000.25, Volume = 5, Time = T0.AddSeconds(0) });
        b.ApplyTrade(new TradeEvent { Price = 21000.25, Volume = 5, Time = T0.AddSeconds(20) }); // prunes the first
        Assert.Equal(5, b.TradedAt(21000.25, T0, null));
    }

    [Fact]
    public void AggressorDelta_is_buy_volume_minus_sell_volume_since_cutoff()
    {
        var t0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(30));
        // Establish a book so InferAggressor has best bid/ask.
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 100.00, Volume = 50, Time = t0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 50, Time = t0 });
        // Buy aggressors (print at/above ask): +70 total.
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 40, Time = t0.AddMilliseconds(100) });
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 30, Time = t0.AddMilliseconds(200) });
        // Sell aggressors (print at/below bid): -25 total.
        b.ApplyTrade(new TradeEvent { Price = 100.00, Volume = 25, Time = t0.AddMilliseconds(300) });
        Assert.Equal(45L, b.AggressorDelta(t0));               // 70 buy - 25 sell
        Assert.Equal(-25L, b.AggressorDelta(t0.AddMilliseconds(250))); // only the sell after cutoff
    }

    [Fact]
    public void WindowSince_counts_prints_and_splits_buy_sell_volume()
    {
        var b = new BookMirror(0.25, System.TimeSpan.FromSeconds(10));
        // Establish an inside so aggressor inference is well-defined.
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 99.75, Volume = 50, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 50, Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 3, Time = T(1) }); // buy (lifted ask)
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 2, Time = T(2) }); // buy
        b.ApplyTrade(new TradeEvent { Price = 99.75,  Volume = 4, Time = T(3) }); // sell (hit bid)
        var w = b.WindowSince(T(0));
        Assert.Equal(3, w.Prints);
        Assert.Equal(5, w.BuyVol);
        Assert.Equal(4, w.SellVol);
    }

    [Fact]
    public void RecentAlternations_counts_aggressor_sign_changes()
    {
        var b = new BookMirror(0.25, System.TimeSpan.FromSeconds(10));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 99.75, Volume = 50, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 50, Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(1) }); // buy
        b.ApplyTrade(new TradeEvent { Price = 99.75,  Volume = 1, Time = T(2) }); // sell  -> alt 1
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(3) }); // buy   -> alt 2
        Assert.Equal(2, b.RecentAlternations(3));
    }

    // Fix 6 regression: lookback=0 must mean "none", not "all" — contract says "last lookback retained trades".
    [Fact]
    public void RecentAlternations_zero_lookback_returns_zero()
    {
        var b = new BookMirror(0.25, System.TimeSpan.FromSeconds(10));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 99.75, Volume = 50, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 50, Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(1) }); // buy
        b.ApplyTrade(new TradeEvent { Price = 99.75,  Volume = 1, Time = T(2) }); // sell -> alt
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(3) }); // buy  -> alt
        Assert.Equal(0, b.RecentAlternations(0));
    }

    // Fix 6 regression: lookback must truncate to the last N trades, not silently fall back to full history.
    [Fact]
    public void RecentAlternations_lookback_truncates_to_the_last_n_trades()
    {
        var b = new BookMirror(0.25, System.TimeSpan.FromSeconds(30));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 99.75, Volume = 50, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 100.25, Volume = 50, Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(1) }); // buy
        b.ApplyTrade(new TradeEvent { Price = 99.75,  Volume = 1, Time = T(2) }); // sell -> alt
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(3) }); // buy  -> alt
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(4) }); // buy
        b.ApplyTrade(new TradeEvent { Price = 100.25, Volume = 1, Time = T(5) }); // buy
        Assert.Equal(2, b.RecentAlternations(100));  // full history: buy,sell,buy,buy,buy -> 2 alternations
        Assert.Equal(0, b.RecentAlternations(3));    // last 3 trades: buy,buy,buy -> 0 alternations
    }
}
