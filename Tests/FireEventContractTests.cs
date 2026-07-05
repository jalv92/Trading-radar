using System;
using TradingRadar.Engine;
using Xunit;

// Cluster B: additive discriminators on FireEvent + reactive fields on ControllerInputs.
// Proves the defaults leave every existing Break-path construction compiling AND behaving unchanged.
public class FireEventContractTests
{
    static DateTime T(double s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    // Break-style FireEvent built exactly as StepCountdown does today (the original 7 fields only) must
    // report Kind=Break, React=None — so the frozen fire path is tagged correctly with zero logic change.
    [Fact]
    public void FireEvent_discriminators_default_to_break_and_none()
    {
        var f = new FireEvent {
            Side = Side.Ask, WallPrice = 100.25, EntryHint = 100.25,
            Fraction = 0.75, DeltaAtFire = 40, ZAtFire = 2.0, Time = T(1) };
        Assert.Equal(SetupKind.Break, f.Kind);
        Assert.Equal(ReactKind.None, f.React);
        Assert.Equal(SetupKind.Break, default(FireEvent).Kind);
        Assert.Equal(ReactKind.None, default(FireEvent).React);
    }

    // A Break-style ControllerInputs object-initializer sets none of the reactive fields — they must
    // default to 0 / invalid so no consumer mistakes default(Outcome)==Absorbed for a real resolution.
    [Fact]
    public void ControllerInputs_reactive_fields_default_sane()
    {
        var inp = new ControllerInputs {
            WallAbovePrice = 100.25, WallAboveCurrent = 120,
            Mid = 100.00, Now = T(1), Book = new BookMirror(0.25, TimeSpan.FromSeconds(30)) };
        Assert.Equal(0.0, inp.TapeAccel);
        Assert.False(inp.WallAboveOutcomeValid);                    // guard: Outcome meaningless until true
        Assert.False(inp.WallBelowOutcomeValid);
        Assert.Equal(default(ControllerInputs).TapeAccel, inp.TapeAccel);
        Assert.False(default(ControllerInputs).WallAboveOutcomeValid);
        Assert.False(default(ControllerInputs).WallBelowOutcomeValid);
    }

    // The real, FROZEN Break state machine still fires on full confluence (mirrors the shipped
    // Fires_long_once_on_full_confluence_then_latches scenario) and the emitted FireEvent now carries
    // the additive tags Break/None with its original Side semantics intact — proof Break logic is untouched.
    [Fact]
    public void Break_setup_fire_is_tagged_break_none_without_touching_break_logic()
    {
        var m = new ControllerStateMachine(new ControllerConfig(), 0.25);
        m.Update(In(100.25, 120, 0, 0.0, 100.00, 1, new BookMirror(0.25, TimeSpan.FromSeconds(30)))); // arm, peak 120
        m.Update(In(100.25, 60, 20, 2.0, 100.00, 2, BookWithBuys(100.25, 60, 2)));                    // -> Countdown
        ControllerOutput o = default(ControllerOutput);
        for (int s = 3; s <= 8; s++)                                                                  // full confluence -> fire+latch
            o = m.Update(In(100.25, 30, 40, 2.0, 100.00, s, BookWithBuys(100.25, 90, s)));
        Assert.Equal(SideState.Fired, o.Long);        // Break machine latched a fire (frozen logic)
        Assert.Equal(Side.Ask, o.Fire.Side);          // ask wall above => long break; original semantics
        Assert.Equal(SetupKind.Break, o.Fire.Kind);   // now additively tagged
        Assert.Equal(ReactKind.None, o.Fire.React);
    }

    static ControllerInputs In(double wallAbovePrice, long wallAboveCur, long delta, double z,
                               double mid, double sec, BookMirror book)
        => new ControllerInputs {
            WallAbovePrice = wallAbovePrice, WallAboveCurrent = wallAboveCur,
            AggressorDelta = delta, TapeZScore = z, Mid = mid, Now = T(sec), Book = book };

    static BookMirror BookWithBuys(double wallPrice, long vol, int sec)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice - 0.5, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) }); // buy aggressor at the wall
        return b;
    }
}
