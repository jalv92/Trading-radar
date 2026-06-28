using System;
using System.Collections.Generic;
using System.Linq;
using TradingRadar.Engine;
using Xunit;

public class WallTrackerTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);

    static BookMirror NewBook() => new BookMirror(0.25, TimeSpan.FromSeconds(30));

    static void AddBidWallBook(BookMirror b, long wall, DateTime t)
    {
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.00, Volume = 10, Time = t });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 1, Price = 20999.75, Volume = 12, Time = t });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 2, Price = 20999.50, Volume = wall, Time = t });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21000.25, Volume = 11, Time = t });
    }

    [Fact]
    public void Confirmed_wall_appears_as_a_node_after_persistence()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 500, T0);
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600));
        var s = wt.GetSnapshot(T0.AddMilliseconds(1600));
        Assert.Contains(s, n => Math.Abs(n.Price - 20999.50) < 0.01 &&
                                (n.State == NodeState.Wall || n.State == NodeState.Live));
    }

    [Fact]
    public void Small_levels_never_become_nodes()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 15, T0); // below threshold
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600));
        var s = wt.GetSnapshot(T0.AddMilliseconds(1600));
        Assert.Empty(s);
    }

    [Fact]
    public void Wall_persists_in_memory_after_scrolling_out_of_window()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 500, T0);
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600)); // confirmed & remembered

        // Price moves up; the wall level is removed from the visible book (blind).
        var book2 = NewBook();
        book2.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21000.50, Volume = 10, Time = T0.AddSeconds(2) });
        book2.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21000.75, Volume = 11, Time = T0.AddSeconds(2) });
        wt.Update(book2, T0.AddSeconds(2));
        var s = wt.GetSnapshot(T0.AddSeconds(2));
        var node = s.FirstOrDefault(n => Math.Abs(n.Price - 20999.50) < 0.01);
        Assert.False(node.InWindow);
        Assert.Equal(NodeState.Remembered, node.State);
    }

    [Fact]
    public void OnReset_marks_all_nodes_blind()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 500, T0);
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600));
        wt.OnReset(T0.AddMilliseconds(1700));
        var s = wt.GetSnapshot(T0.AddMilliseconds(1700));
        Assert.All(s, n => Assert.False(n.InWindow));
    }
}
