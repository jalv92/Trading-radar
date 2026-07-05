using TradingRadar.Engine;
using Xunit;

// Cluster E — reactive fire routing (spec §3 direction table + §8 entry mechanics). Pure, so it runs
// under `dotnet test` even though the RadarChartTrader WPF wiring that CONSUMES it cannot (the test
// project references only Engine). This is the load-bearing MKT-vs-LMT + auto-eligible decision.
// One multi-assert [Fact] (house style — no [Theory]) covering the whole table.
public class ReactiveExecutionTests
{
    static FireEvent Fire(SetupKind kind, ReactKind react, Side side)
        => new FireEvent { Kind = kind, React = react, Side = side };

    [Fact]
    public void Route_maps_every_setup_and_reactkind_to_side_mkt_lmt_and_autoaggressive()
    {
        // ---- Break setup is frozen: routing identical to the shipped behavior (honors AUTO checkbox) ----
        Assert(Fire(SetupKind.Break, ReactKind.None, Side.Ask), isBuy: true,  marketable: false, autoAggr: false); // ask wall -> long, LMT
        Assert(Fire(SetupKind.Break, ReactKind.None, Side.Bid), isBuy: false, marketable: false, autoAggr: false); // bid wall -> short, LMT

        // Additive-safety: a zero-value FireEvent (Kind==Break, React==None) routes as frozen Break.
        var d = ReactiveExecution.Route(default(FireEvent));
        Xunit.Assert.False(d.Marketable);
        Xunit.Assert.False(d.AutoAggressive);

        // ---- Reactive REJECT (wall holds) => FADE, marketable, auto-aggressive. ReactiveController emits
        //      the TRADE side, so Side.Bid = SELL (wall above held), Side.Ask = BUY (wall below held). ----
        Assert(Fire(SetupKind.Reactive, ReactKind.Reject, Side.Bid), isBuy: false, marketable: true, autoAggr: true);
        Assert(Fire(SetupKind.Reactive, ReactKind.Reject, Side.Ask), isBuy: true,  marketable: true, autoAggr: true);

        // ---- Reactive BREAK (wall consumed) => FOLLOW, wall-anchored LMT, auto-aggressive ----
        Assert(Fire(SetupKind.Reactive, ReactKind.Break, Side.Ask), isBuy: true,  marketable: false, autoAggr: true); // wall above eaten -> BUY
        Assert(Fire(SetupKind.Reactive, ReactKind.Break, Side.Bid), isBuy: false, marketable: false, autoAggr: true); // wall below eaten -> SELL
    }

    static void Assert(FireEvent f, bool isBuy, bool marketable, bool autoAggr)
    {
        var r = ReactiveExecution.Route(f);
        Xunit.Assert.Equal(isBuy, r.IsBuy);
        Xunit.Assert.Equal(marketable, r.Marketable);
        Xunit.Assert.Equal(autoAggr, r.AutoAggressive);
    }
}
