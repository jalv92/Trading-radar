using System;
using TradingRadar.Engine;
using Xunit;

public class WallDetectorTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);

    // Build a bid book: small levels + one candidate wall at index 2.
    static BookMirror BookWithBidWall(long wallSize)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(10));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.00, Volume = 10, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 1, Price = 20999.75, Volume = 12, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 2, Price = 20999.50, Volume = wallSize, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 3, Price = 20999.25, Volume = 11, Time = T0 });
        return b;
    }

    [Fact]
    public void Baseline_is_median_of_other_levels_not_inflated_by_wall()
    {
        var cfg = new RadarConfig();
        var d = new WallDetector(cfg);
        var book = BookWithBidWall(500);
        d.Update(book, T0);
        // median of {10,12,11} = 11
        Assert.Equal(11, d.Baseline(Side.Bid, 20999.50, book, T0));
    }

    [Fact]
    public void Not_confirmed_before_persistence_elapsed()
    {
        var cfg = new RadarConfig(); // K_mult 4, MinAbsSize 40, T_persist 1500ms
        var d = new WallDetector(cfg);
        var book = BookWithBidWall(500); // 500 >= 4*11 and >= 40
        d.Update(book, T0);
        Assert.False(d.IsConfirmed(Side.Bid, 20999.50, book, T0)); // 0ms elapsed
    }

    [Fact]
    public void Confirmed_after_persistence_when_all_criteria_hold()
    {
        var cfg = new RadarConfig();
        var d = new WallDetector(cfg);
        var book = BookWithBidWall(500);
        d.Update(book, T0);
        d.Update(book, T0.AddMilliseconds(1600)); // > T_persist
        Assert.True(d.IsConfirmed(Side.Bid, 20999.50, book, T0.AddMilliseconds(1600)));
    }

    [Fact]
    public void Fails_absolute_floor_even_if_relatively_large()
    {
        var cfg = new RadarConfig { MinAbsSize = 40 };
        var d = new WallDetector(cfg);
        // Tiny book where 30 is 4x the median(2,3,2)=2 but below the 40 floor.
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(10));
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21000.25, Volume = 2, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 1, Price = 21000.50, Volume = 30, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 2, Price = 21000.75, Volume = 3, Time = T0 });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 3, Price = 21001.00, Volume = 2, Time = T0 });
        d.Update(b, T0);
        d.Update(b, T0.AddMilliseconds(1600));
        Assert.False(d.IsConfirmed(Side.Ask, 21000.50, b, T0.AddMilliseconds(1600)));
    }

    [Fact]
    public void Persistence_resets_if_size_drops_below_threshold()
    {
        var cfg = new RadarConfig();
        var d = new WallDetector(cfg);
        var big = BookWithBidWall(500);
        d.Update(big, T0);
        // Wall shrinks below threshold -> persistence clock resets.
        var small = BookWithBidWall(20);
        d.Update(small, T0.AddMilliseconds(800));
        var bigAgain = BookWithBidWall(500);
        d.Update(bigAgain, T0.AddMilliseconds(900));
        // Only ~100ms since re-qualify, not confirmed yet.
        Assert.False(d.IsConfirmed(Side.Bid, 20999.50, bigAgain, T0.AddMilliseconds(900)));
    }

    [Fact]
    public void Flicker_above_threshold_rejects_the_wall()
    {
        var cfg = new RadarConfig { F_flicker = 6.0 };
        var d = new WallDetector(cfg);
        double px = 20999.50;
        // Oscillate present/absent many times within one second at the wall price.
        for (int i = 0; i < 10; i++)
        {
            var present = BookWithBidWall(500);
            d.Update(present, T0.AddMilliseconds(i * 100));
            var absent = new BookMirror(0.25, TimeSpan.FromSeconds(10));
            absent.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.00, Volume = 10, Time = T0 });
            d.Update(absent, T0.AddMilliseconds(i * 100 + 50));
        }
        var final = BookWithBidWall(500);
        d.Update(final, T0.AddMilliseconds(2000));
        Assert.False(d.IsConfirmed(Side.Bid, px, final, T0.AddMilliseconds(2000)));
    }
}
