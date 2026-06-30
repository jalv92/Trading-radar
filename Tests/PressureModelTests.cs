using System.Collections.Generic;
using TradingRadar.Engine;
using Xunit;

public class PressureModelTests
{
    static PressureModel Model() => new PressureModel(new PressureConfig());
    static DepthLevel L(double p, long v) => new DepthLevel { Price = p, Volume = v };
    static SignalRead Find(SignalRead[] s, SignalId id)
    {
        for (int i = 0; i < s.Length; i++) if (s[i].Id == id) return s[i];
        return default(SignalRead);
    }

    // Heavier asks (supply overhead) => imbalance leans SHORT (negative).
    [Fact]
    public void Imbalance_leans_short_when_asks_outweigh_bids()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 10), L(99.50, 10) },
            Asks = new List<DepthLevel> { L(100.25, 40), L(100.50, 40) },
            BestBidSize = 10, BestAskSize = 40, AggressorDelta = 0,
            Wall = new WallErosion()
        };
        Assert.True(Find(Model().Signals(inp), SignalId.Imbalance).Lean < 0);
    }

    // Thin best bid vs fat best ask => inside-thin leans SHORT.
    [Fact]
    public void InsideThin_leans_short_when_best_bid_is_thin()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 9) },
            Asks = new List<DepthLevel> { L(100.25, 29) },
            BestBidSize = 9, BestAskSize = 29, AggressorDelta = 0, Wall = new WallErosion()
        };
        Assert.True(Find(Model().Signals(inp), SignalId.InsideThin).Lean < 0);
    }

    // Positive aggressor delta (buyers lifting) => delta leans LONG.
    [Fact]
    public void Delta_leans_long_when_buyers_aggress()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 20) }, Asks = new List<DepthLevel> { L(100.25, 20) },
            BestBidSize = 20, BestAskSize = 20, AggressorDelta = 12, Wall = new WallErosion()
        };
        var d = Find(Model().Signals(inp), SignalId.Delta);
        Assert.True(d.Lean > 0 && d.Active);
    }

    // An ask wall (above price) eroding without trades => wall signal leans LONG (fake ceiling).
    [Fact]
    public void WallErosion_above_leans_long_and_is_active()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 20) }, Asks = new List<DepthLevel> { L(100.25, 20) },
            BestBidSize = 20, BestAskSize = 20, AggressorDelta = 0,
            Wall = new WallErosion { Active = true, Frac = 0.5, Above = true }
        };
        var w = Find(Model().Signals(inp), SignalId.WallErosion);
        Assert.True(w.Lean > 0 && w.Active);
    }

    // No wall erosion => wall signal inactive (lean ~0).
    [Fact]
    public void WallErosion_inactive_when_not_eroding()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 20) }, Asks = new List<DepthLevel> { L(100.25, 20) },
            BestBidSize = 20, BestAskSize = 20, AggressorDelta = 0, Wall = new WallErosion { Active = false }
        };
        Assert.False(Find(Model().Signals(inp), SignalId.WallErosion).Active);
    }
}
