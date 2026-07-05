using System;
using System.Linq;
using TradingRadar.Engine;
using Xunit;

public class LiquidityMemoryTests
{
    static readonly DateTime T0 = new DateTime(2026, 6, 28, 9, 30, 0, DateTimeKind.Utc);

    static RadarNode NodeAt(System.Collections.Generic.IReadOnlyList<RadarNode> s, double price)
        => s.First(n => Math.Abs(n.Price - price) < 0.01);

    [Fact]
    public void Promote_sets_C0_in_band_and_wall_state()
    {
        var m = new LiquidityMemory(new RadarConfig());
        m.Promote(Side.Bid, 21000.00, size: 500, baseline: 11, now: T0); // size/B=45.5, K=4 -> 0.4+0.1*41.5 clamps to 0.8
        var s = m.Snapshot(21000.00, 21000.25, T0);
        var n = NodeAt(s, 21000.00);
        Assert.Equal(NodeState.Wall, n.State);
        Assert.Equal(0.8, n.Confidence, 3); // size/B=45.5 → 0.4+0.1*41.5 clamps to ceiling
        Assert.True(n.InWindow);
        Assert.Equal(500, n.LastKnownSize);
    }

    [Fact]
    public void Confidence_decays_by_half_after_one_half_life_while_blind()
    {
        var cfg = new RadarConfig { H = TimeSpan.FromSeconds(30) };
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Bid, 21000.00, 500, 11, T0); // C0 = 0.8
        m.MarkBlind(Side.Bid, 21000.00);
        var s = m.Snapshot(21000.00, 21000.25, T0.AddSeconds(30)); // one half-life
        var n = NodeAt(s, 21000.00);
        Assert.False(n.InWindow);
        Assert.Equal(NodeState.Remembered, n.State);
        Assert.InRange(n.Confidence, 0.38, 0.42); // ~0.4
        Assert.InRange(n.AgeSeconds, 29.9, 30.1);
    }

    [Fact]
    public void Live_node_does_not_decay()
    {
        var m = new LiquidityMemory(new RadarConfig());
        m.Promote(Side.Bid, 21000.00, 500, 11, T0); // C0 ~0.8, InWindow=true
        double c0 = NodeAt(m.Snapshot(21000.00, 21000.25, T0), 21000.00).Confidence;

        // 300s later, still InWindow, NO ObserveLive/MarkBlind in between.
        double cLive = NodeAt(m.Snapshot(21000.00, 21000.25, T0.AddSeconds(300)), 21000.00).Confidence;
        Assert.Equal(c0, cLive); // live confidence never decays regardless of elapsed time

        // Contrast: once blind, the SAME elapsed time DOES decay it (proves the decay path is live).
        m.MarkBlind(Side.Bid, 21000.00);
        double cBlind = NodeAt(m.Snapshot(21000.00, 21000.25, T0.AddSeconds(300)), 21000.00).Confidence;
        Assert.True(cBlind < c0);
    }

    [Fact]
    public void Absorbed_outcome_raises_confidence()
    {
        var cfg = new RadarConfig();
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Ask, 21000.50, 200, 20, T0); // C0 ~ 0.4+0.1*(10-4)=clamp .8
        double before = NodeAt(m.Snapshot(21000.25, 21000.50, T0), 21000.50).Confidence;
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Absorbed, Traded = 600, Cancelled = 0, ResolvedAt = T0.AddSeconds(1) }, T0.AddSeconds(1));
        var n = NodeAt(m.Snapshot(21000.25, 21000.50, T0.AddSeconds(1)), 21000.50);
        Assert.Equal(NodeState.Absorbed, n.State);
        Assert.True(n.Confidence > before); // dC_absorb is strictly positive
    }

    [Fact]
    public void Pulled_outcome_collapses_confidence()
    {
        var cfg = new RadarConfig { PullPenalty = 0.2 };
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Ask, 21000.50, 200, 20, T0);
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Pulled, Traded = 0, Cancelled = 200, ResolvedAt = T0.AddSeconds(1) }, T0.AddSeconds(1));
        var n = NodeAt(m.Snapshot(21000.25, 21000.50, T0.AddSeconds(1)), 21000.50);
        Assert.Equal(NodeState.Pulled, n.State);
        Assert.True(n.Confidence < 0.4);
    }

    [Fact]
    public void Node_dies_after_P_max_pulls()
    {
        var cfg = new RadarConfig { P_max = 2 };
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Ask, 21000.50, 200, 20, T0);
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Pulled, ResolvedAt = T0 }, T0);
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Pulled, ResolvedAt = T0.AddSeconds(1) }, T0.AddSeconds(1));
        m.Evict(T0.AddSeconds(1));
        Assert.False(m.Contains(Side.Ask, 21000.50));
    }

    [Fact]
    public void RawState_carries_true_state_while_State_masks_blind_node_to_Remembered()
    {
        // The React latched-feed fix: a blind node's masked State goes to Remembered (Break/cockpit path,
        // unchanged), but RawState must still carry the wall's TRUE resolution so React — reading by
        // identity through the blink — can see it. Reproduces the day-17 bug where the latched wall's real
        // Absorbed/Consumed resolution was hidden behind the InWindow mask (wbValid never True all day).
        var m = new LiquidityMemory(new RadarConfig());
        m.Promote(Side.Ask, 21000.50, 200, 20, T0);                                     // State=Wall, InWindow=true
        m.ApplyOutcome(new EpisodeResult { Side = Side.Ask, Price = 21000.50, Outcome = Outcome.Absorbed, ResolvedAt = T0 }, T0); // true State -> Absorbed
        m.MarkBlind(Side.Ask, 21000.50);                                                // InWindow=false
        var n = NodeAt(m.Snapshot(21000.25, 21000.50, T0.AddSeconds(1)), 21000.50);
        Assert.False(n.InWindow);
        Assert.Equal(NodeState.Remembered, n.State);   // masked (unchanged): Break/cockpit see Remembered when blind
        Assert.Equal(NodeState.Absorbed, n.RawState);  // additive: React sees the latched wall's real resolution through the blink
    }

    [Fact]
    public void Snapshot_excludes_nodes_outside_the_memory_band()
    {
        var cfg = new RadarConfig { MemoryBandTicks = 25, TickSize = 0.25 }; // band = 6.25 price units
        var m = new LiquidityMemory(cfg);
        m.Promote(Side.Bid, 21000.00, 500, 11, T0);   // inside band
        m.MarkBlind(Side.Bid, 21000.00);
        m.Promote(Side.Bid, 20990.00, 500, 11, T0);   // 40 ticks away -> outside
        m.MarkBlind(Side.Bid, 20990.00);
        var s = m.Snapshot(21000.00, 21000.25, T0);
        Assert.Contains(s, n => Math.Abs(n.Price - 21000.00) < 0.01);
        Assert.DoesNotContain(s, n => Math.Abs(n.Price - 20990.00) < 0.01);
    }
}
