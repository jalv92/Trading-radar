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

    // Captured ES state (asks heavy, thin bid, no flow, wall idle) => net SHORT, NOT a green-light.
    [Fact]
    public void Evaluate_captured_state_leans_short_without_trigger()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(7589.00, 9), L(7588.75, 28), L(7588.50, 27) },
            Asks = new List<DepthLevel> { L(7589.25, 29), L(7589.75, 47), L(7590.00, 65) },
            BestBidSize = 9, BestAskSize = 29, AggressorDelta = 0, Wall = new WallErosion()
        };
        var r = Model().Evaluate(inp);
        Assert.True(r.Net < 0);
        Assert.False(r.Green);
    }

    // Ask wall erodes without trades + book lightens above + bid firms => green-light LONG.
    [Fact]
    public void Evaluate_eroding_ceiling_greenlights_long()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(7589.00, 30), L(7588.75, 32), L(7588.50, 33) },
            Asks = new List<DepthLevel> { L(7589.25, 6), L(7589.75, 10), L(7590.00, 16) },
            BestBidSize = 30, BestAskSize = 6, AggressorDelta = 0,
            Wall = new WallErosion { Active = true, Frac = 0.75, Above = true }
        };
        var r = Model().Evaluate(inp);
        Assert.True(r.Green);
        Assert.Equal(1, r.Sign);
        Assert.True(r.Conviction >= 4);
    }

    // Veto path in isolation: Sign=1, Conviction=4, |Net|≈0.64 all clear — only the opposing Delta veto blocks Green.
    // Book: heavy bids+thin asks → Imbalance/InsideThin/AirPocket all LONG ≈1.0; Wall LONG 0.75 (4 signals agree).
    // AggressorDelta=-9 → Delta lean ≈ −0.643 > OpposingVeto(0.55) but too small to drag |Net| below GreenNet.
    [Fact]
    public void Evaluate_opposing_veto_is_the_sole_blocker()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 80), L(99.50, 60), L(99.25, 50) },
            Asks = new List<DepthLevel> { L(100.25, 4), L(100.50, 6), L(100.75, 8) },
            BestBidSize = 80, BestAskSize = 4,
            AggressorDelta = -9,  // lean ≈ −0.643 > OpposingVeto(0.55); book remains LONG net
            Wall = new WallErosion { Active = true, Frac = 0.75, Above = true }
        };
        var r = Model().Evaluate(inp);
        Assert.False(r.Green);
        Assert.Equal(1, r.Sign);
        Assert.True(r.Conviction >= 4);
        Assert.True(System.Math.Abs(r.Net) >= 0.55);
    }

    // A strong opposing active signal blocks the green-light even if net clears the magnitude bar.
    [Fact]
    public void Evaluate_strong_opposing_signal_vetoes_green()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(7589.00, 30), L(7588.75, 32), L(7588.50, 33) },
            Asks = new List<DepthLevel> { L(7589.25, 6), L(7589.75, 10), L(7590.00, 16) },
            BestBidSize = 30, BestAskSize = 6,
            AggressorDelta = -20,  // heavy selling: strong SHORT lean opposing the LONG book
            Wall = new WallErosion { Active = true, Frac = 0.75, Above = true }
        };
        Assert.False(Model().Evaluate(inp).Green);
    }

    // Vote-less book skew: asks outweigh bids => negative context.
    [Fact]
    public void BookSkewContext_is_negative_when_asks_outweigh_bids()
    {
        var inp = new PressureInputs {
            Bids = new List<DepthLevel> { L(99.75, 10), L(99.50, 10) },
            Asks = new List<DepthLevel> { L(100.25, 40), L(100.50, 40) },
            BestBidSize = 10, BestAskSize = 40, AggressorDelta = 0, Wall = new WallErosion()
        };
        Assert.True(Model().BookSkewContext(inp) < 0);
    }
}
