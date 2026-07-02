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

    // (a) locks I1: a continuously-live confirmed wall must not ratchet confidence to 1.0 every tick.
    [Fact]
    public void Continuously_live_confirmed_wall_confidence_does_not_saturate()
    {
        var wt = new WallTracker(new RadarConfig());
        var book = NewBook();
        AddBidWallBook(book, 500, T0);
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600)); // confirm
        double cAfterConfirm = wt.GetSnapshot(T0.AddMilliseconds(1600))
            .First(n => Math.Abs(n.Price - 20999.50) < 0.01).Confidence;

        // 10 more updates; same book, same confirmed wall, never blinded.
        for (int i = 1; i <= 10; i++)
            wt.Update(book, T0.AddMilliseconds(1600 + i * 100));

        var node = wt.GetSnapshot(T0.AddMilliseconds(2600))
            .First(n => Math.Abs(n.Price - 20999.50) < 0.01);
        Assert.True(node.Confidence < 0.95,
            $"Confidence {node.Confidence:F3} saturated; expected < 0.95");
        Assert.True(node.Confidence <= cAfterConfirm + 0.01,
            $"Confidence climbed from {cAfterConfirm:F3} to {node.Confidence:F3} while continuously live");
    }

    // (b) locks I2: idle episode timeout must not mint an Absorbed outcome or raise confidence.
    [Fact]
    public void Idle_episode_timeout_does_not_absorb_or_raise_confidence()
    {
        var wt = new WallTracker(new RadarConfig());
        // Wall at 20999.50 (best bid); two small levels below keep the baseline small
        // so the wall qualifies (500 >= 4*12). Ask at 20999.75 is 1 tick above the wall:
        // NearInside(Bid, 20999.50): (20999.75 - 20999.50) = 0.25 <= D_approach*tick + tick/2 = 0.375 -> episode opens.
        var book = new BookMirror(0.25, TimeSpan.FromSeconds(30));
        book.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 20999.50, Volume = 500, Time = T0 });
        book.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 1, Price = 20999.25, Volume = 10, Time = T0 });
        book.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 2, Price = 20999.00, Volume = 12, Time = T0 });
        book.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 20999.75, Volume = 15, Time = T0 });
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600)); // confirm + open episode
        double cBeforeApproach = wt.GetSnapshot(T0.AddMilliseconds(1600))
            .First(n => Math.Abs(n.Price - 20999.50) < 0.01).Confidence;

        // Advance past T_episode (3 s) with no trades; episode times out.
        wt.Update(book, T0.AddMilliseconds(4700));

        var node = wt.GetSnapshot(T0.AddMilliseconds(4700))
            .First(n => Math.Abs(n.Price - 20999.50) < 0.01);
        Assert.True(node.Confidence <= cBeforeApproach + 0.01,
            $"Confidence rose from {cBeforeApproach:F3} to {node.Confidence:F3} after idle timeout");
        Assert.NotEqual(NodeState.Absorbed, node.State);
    }

    // (c) locks I3: a price gap larger than MemoryBandTicks in one tick must blind the old node.
    [Fact]
    public void Price_gap_beyond_band_blinds_tracked_node()
    {
        // Use a narrow band (5 ticks = 1.25 pts) so a 14-tick gap is unambiguously outside it.
        var cfg = new RadarConfig { MemoryBandTicks = 5, TickSize = 0.25 };
        var wt = new WallTracker(cfg);
        var book = NewBook();
        AddBidWallBook(book, 500, T0);
        wt.Update(book, T0);
        wt.Update(book, T0.AddMilliseconds(1600)); // confirm wall at 20999.50

        // Gap price 14 ticks (3.50 pts) above the wall — old band-scan (±5 ticks from new mid ~21003.125)
        // would never reach 20999.50; new TrackedLevels iteration always blinds it.
        var gapBook = new BookMirror(cfg.TickSize, cfg.BaselineWindow);
        DateTime tGap = T0.AddSeconds(2);
        gapBook.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21003.00, Volume = 10, Time = tGap });
        gapBook.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = 21003.25, Volume = 15, Time = tGap });
        wt.Update(gapBook, tGap);

        // Query with the OLD mid so the band includes 20999.50 (0.625 pts < band 1.25 pts).
        var node = wt.GetSnapshot(21000.00, 21000.25, tGap)
            .FirstOrDefault(n => Math.Abs(n.Price - 20999.50) < 0.01);
        Assert.False(node.InWindow, "Node must be blinded after a gap > MemoryBandTicks");
        Assert.Equal(NodeState.Remembered, node.State);
    }

    // Round-6: WallTracker.TrustedSize backs the armed-wall identity feed's bounded blind-trust.
    [Fact]
    public void TrustedSize_returns_last_known_size_when_in_window_regardless_of_age()
    {
        Assert.Equal(500, WallTracker.TrustedSize(true, 999.0, 500, 1.0));
    }

    [Fact]
    public void TrustedSize_trusts_last_known_size_while_blind_within_the_grace_window()
    {
        Assert.Equal(500, WallTracker.TrustedSize(false, 0.5, 500, 1.0));
    }

    [Fact]
    public void TrustedSize_returns_zero_once_blind_past_the_grace_window()
    {
        Assert.Equal(0, WallTracker.TrustedSize(false, 1.5, 500, 1.0));
    }

    // Pins the strict `<` at the exact boundary — an accidental `<=` would extend trust one tick.
    [Fact]
    public void TrustedSize_boundary_age_equal_to_window_is_not_trusted()
    {
        Assert.Equal(0, WallTracker.TrustedSize(false, 1.0, 500, 1.0));
    }
}
