using System;
using TradingRadar.Engine;
using Xunit;

// React setup state machine (spec §6 + §3 direction table). Self-contained per the test convention:
// own clock helper + local book/inputs builders, no shared cross-class helpers.
public class ReactiveControllerTests
{
    static DateTime T(double s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    static BookMirror EmptyBook() => new BookMirror(0.25, TimeSpan.FromSeconds(60));

    static ReactiveController Machine() => new ReactiveController(new ReactiveConfig(), 0.25);

    // Optional params keep the arm-frame calls short; named args at the call sites stay readable.
    static ControllerInputs In(
        double wallAbovePrice = 0, long wallAboveCur = 0,
        double wallBelowPrice = 0, long wallBelowCur = 0,
        long delta = 0, double mid = 100.00, double sec = 1, double accel = 0, long adaptiveSig = 0,
        Outcome aboveOut = Outcome.Absorbed, bool aboveValid = false,
        Outcome belowOut = Outcome.Absorbed, bool belowValid = false,
        BookMirror book = null)
        => new ControllerInputs {
            WallAbovePrice = wallAbovePrice, WallAboveCurrent = wallAboveCur,
            WallBelowPrice = wallBelowPrice, WallBelowCurrent = wallBelowCur,
            AggressorDelta = delta, TapeZScore = 0, TapeAlternations = 0,
            Mid = mid, Now = T(sec), Book = book ?? EmptyBook(), AdaptiveSignificance = adaptiveSig,
            TapeAccel = accel,
            WallAboveOutcome = aboveOut, WallAboveOutcomeValid = aboveValid,
            WallBelowOutcome = belowOut, WallBelowOutcomeValid = belowValid };

    // Buy aggressor prints AT an ask wall above (matches ConsumptionTracker's ask-wall-consumed-by-buys).
    static BookMirror BookWithBuys(double wallPrice, long vol, int sec)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice - 0.5, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) });
        return b;
    }

    // Sell aggressor prints AT a bid wall below (bid-wall-consumed-by-sells).
    static BookMirror BookWithSells(double wallPrice, long vol, int sec)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice + 0.5, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) });
        return b;
    }

    // ---- ARM (both signs -> correct latch) ----

    // Buyers accelerating up into a dominant ask wall above, price near it, delta agreeing -> Watching.
    [Fact]
    public void Arms_watching_when_buyers_accelerate_into_wall_above()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0));
        Assert.Equal(ReactState.Watching, m.State);
        Assert.Equal(Side.Ask, m.WallSide);
    }

    // Sellers accelerating down into a dominant bid wall below -> Watching (negative accel/delta).
    [Fact]
    public void Arms_watching_when_sellers_accelerate_into_wall_below()
    {
        var m = Machine();
        m.Update(In(wallBelowPrice: 99.75, wallBelowCur: 120, delta: -50, mid: 100.00, sec: 1, accel: -2.0));
        Assert.Equal(ReactState.Watching, m.State);
        Assert.Equal(Side.Bid, m.WallSide);
    }

    // ---- DOES NOT ARM (significance / accel floor / accel sign / delta / proximity) ----

    [Fact]
    public void Does_not_arm_when_wall_below_significance()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 5, delta: 50, mid: 100.00, sec: 1, accel: 2.0));
        Assert.Equal(ReactState.Waiting, m.State);
    }

    [Fact]
    public void Does_not_arm_when_accel_below_floor()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 0.5));
        Assert.Equal(ReactState.Waiting, m.State);
    }

    // Wall above but tape is accelerating DOWN (sign points away from the wall) -> no arm (delta still agrees, so only sign fails).
    [Fact]
    public void Does_not_arm_when_accel_sign_points_away_from_wall()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: -2.0));
        Assert.Equal(ReactState.Waiting, m.State);
    }

    // Wall above, accel agrees, but aggressor delta opposes (sellers dominant) -> no arm.
    [Fact]
    public void Does_not_arm_when_aggressor_delta_disagrees()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: -50, mid: 100.00, sec: 1, accel: 2.0));
        Assert.Equal(ReactState.Waiting, m.State);
    }

    // Wall present + confluence, but price is 5 ticks away (> WatchProximityTicks=3) -> no arm.
    [Fact]
    public void Does_not_arm_when_wall_beyond_watch_proximity()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 99.00, sec: 1, accel: 2.0));
        Assert.Equal(ReactState.Waiting, m.State);
    }

    // ---- REJECT (wall holds/absorbs -> fade) ----

    // Wall ABOVE absorbed -> SELL (fade). Direction table: FireEvent.Side = Bid (Bid => SELL).
    [Fact]
    public void Reject_fires_sell_when_wall_above_absorbed()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0)); // arm
        var o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 2, accel: 2.0,
                            aboveOut: Outcome.Absorbed, aboveValid: true));
        Assert.True(o.Fired);
        Assert.Equal(SetupKind.Reactive, o.Fire.Kind);
        Assert.Equal(ReactKind.Reject, o.Fire.React);
        Assert.Equal(Side.Bid, o.Fire.Side);          // SELL
        Assert.Equal(ReactState.Fired, m.State);
    }

    // Wall BELOW absorbed -> BUY (fade). FireEvent.Side = Ask (Ask => BUY).
    [Fact]
    public void Reject_fires_buy_when_wall_below_absorbed()
    {
        var m = Machine();
        m.Update(In(wallBelowPrice: 99.75, wallBelowCur: 120, delta: -50, mid: 100.00, sec: 1, accel: -2.0)); // arm
        var o = m.Update(In(wallBelowPrice: 99.75, wallBelowCur: 120, delta: -50, mid: 100.00, sec: 2, accel: -2.0,
                            belowOut: Outcome.Absorbed, belowValid: true));
        Assert.True(o.Fired);
        Assert.Equal(SetupKind.Reactive, o.Fire.Kind);
        Assert.Equal(ReactKind.Reject, o.Fire.React);
        Assert.Equal(Side.Ask, o.Fire.Side);          // BUY
        Assert.Equal(ReactState.Fired, m.State);
    }

    // ---- BREAK (wall consumed + trade-backed -> follow) ----

    // Wall ABOVE consumed, drop trade-backed -> BUY (follow). FireEvent.Side = Ask.
    [Fact]
    public void Break_fires_buy_when_wall_above_consumed_and_trade_backed()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0)); // arm, peak 120
        var book = BookWithBuys(100.25, 90, 2);                                                              // 90 bought at the wall
        var o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 30, delta: 50, mid: 100.00, sec: 2, accel: 2.0,
                            aboveOut: Outcome.Consumed, aboveValid: true, book: book));                      // 120->30, fully trade-backed
        Assert.True(o.Fired);
        Assert.Equal(SetupKind.Reactive, o.Fire.Kind);
        Assert.Equal(ReactKind.Break, o.Fire.React);
        Assert.Equal(Side.Ask, o.Fire.Side);          // BUY
        Assert.Equal(ReactState.Fired, m.State);
    }

    // Wall BELOW consumed, drop trade-backed -> SELL (follow). FireEvent.Side = Bid.
    [Fact]
    public void Break_fires_sell_when_wall_below_consumed_and_trade_backed()
    {
        var m = Machine();
        m.Update(In(wallBelowPrice: 99.75, wallBelowCur: 120, delta: -50, mid: 100.00, sec: 1, accel: -2.0)); // arm, peak 120
        var book = BookWithSells(99.75, 90, 2);                                                               // 90 sold at the wall
        var o = m.Update(In(wallBelowPrice: 99.75, wallBelowCur: 30, delta: -50, mid: 100.00, sec: 2, accel: -2.0,
                            belowOut: Outcome.Consumed, belowValid: true, book: book));
        Assert.True(o.Fired);
        Assert.Equal(SetupKind.Reactive, o.Fire.Kind);
        Assert.Equal(ReactKind.Break, o.Fire.React);
        Assert.Equal(Side.Bid, o.Fire.Side);          // SELL
        Assert.Equal(ReactState.Fired, m.State);
    }

    // Consumed but NO trades at the wall (TradeBackedFraction 0 < MinTradeBackedRatio) -> no follow, keep watching.
    [Fact]
    public void Consumed_wall_without_trade_backing_does_not_fire()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0)); // arm, peak 120
        var o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 40, delta: 50, mid: 100.00, sec: 2, accel: 2.0,
                            aboveOut: Outcome.Consumed, aboveValid: true, book: EmptyBook()));                // big drop, ZERO prints
        Assert.False(o.Fired);
        Assert.Equal(ReactState.Watching, m.State);   // not fired, not abandoned
    }

    // Fix-pass regression: a valid Consumed outcome that never clears MinTradeBackedRatio (real case:
    // sweep prints land a tick off the wall, or age out of the trade ring) must NOT stick in Watching
    // forever — the non-firing Consumed branch has to fall through to the abandon checks or
    // MaxWatchSeconds never runs. Held across many frames past the timeout -> abandons to Cooldown,
    // never fires.
    [Fact]
    public void Consumed_without_trade_backing_still_times_out_and_does_not_stick()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0)); // arm, peak 120, watchStart T(1)
        Assert.Equal(ReactState.Watching, m.State);

        ControllerOutput o = default(ControllerOutput);
        for (int s = 2; s <= 15; s++)
        {
            o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 40, delta: 50, mid: 100.00, sec: s, accel: 2.0,
                            aboveOut: Outcome.Consumed, aboveValid: true, book: EmptyBook())); // Fraction 0.667 passes, TradeBackedFraction 0 fails
            Assert.False(o.Fired);
            Assert.Equal(ReactState.Watching, m.State);   // must NOT stick — still ticking toward the timeout, not stuck forever
        }

        o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 40, delta: 50, mid: 100.00, sec: 16, accel: 2.0,
                        aboveOut: Outcome.Consumed, aboveValid: true, book: EmptyBook())); // elapsed 15s >= MaxWatchSeconds(15) -> abandon
        Assert.False(o.Fired);
        Assert.Equal(ReactState.Cooldown, m.State);
    }

    // ---- ABANDON (timeout / vanish / hop / away-drift / pulled) ----

    // Wait-and-see timeout: no resolution within MaxWatchSeconds -> Cooldown, then re-arms after it elapses.
    [Fact]
    public void Abandons_to_cooldown_on_watch_timeout_then_can_rearm()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0));   // arm at T(1)
        var o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 17, accel: 2.0)); // 16s > 15s
        Assert.False(o.Fired);
        Assert.Equal(ReactState.Cooldown, m.State);
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 28, accel: 2.0));  // cooldown (until T27) elapses -> Waiting
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 29, accel: 2.0));  // fresh confluence -> re-arm
        Assert.Equal(ReactState.Watching, m.State);
    }

    // Latched wall vanishes (current size <= 0) -> abandon to Cooldown, no fire.
    [Fact]
    public void Abandons_to_cooldown_when_wall_vanishes()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0));
        var o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 0, delta: 50, mid: 100.00, sec: 2, accel: 2.0));
        Assert.False(o.Fired);
        Assert.Equal(ReactState.Cooldown, m.State);
    }

    // Dominant-wall identity hops >= 1 tick from the latched price -> abandon to Cooldown.
    [Fact]
    public void Abandons_to_cooldown_when_wall_identity_hops()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0)); // latch 100.25
        var o = m.Update(In(wallAbovePrice: 100.50, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 2, accel: 2.0)); // now 100.50
        Assert.False(o.Fired);
        Assert.Equal(ReactState.Cooldown, m.State);
    }

    // Price drifts >= AwayTicks from the wall without resolving (acceleration fizzled) -> abandon.
    [Fact]
    public void Abandons_to_cooldown_on_away_drift()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0));
        var o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 98.50, sec: 2, accel: 2.0)); // 7 ticks away
        Assert.False(o.Fired);
        Assert.Equal(ReactState.Cooldown, m.State);
    }

    // Pulled (spoof: cancelled, quote didn't cross) -> abstain, no trade, abandon to Cooldown.
    [Fact]
    public void Pulled_wall_abstains_no_fire()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0));
        var o = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 2, accel: 2.0,
                            aboveOut: Outcome.Pulled, aboveValid: true));
        Assert.False(o.Fired);
        Assert.Equal(ReactState.Cooldown, m.State);
    }

    // ---- COOLDOWN (blocks re-fire, then re-arms) ----

    // A fire latches Fired for ReactCooldownSeconds; full confluence during that window must NOT re-fire
    // or re-arm; once the cooldown elapses the same event can arm again.
    [Fact]
    public void Cooldown_blocks_refire_then_rearms_after_it_elapses()
    {
        var m = Machine();
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 1, accel: 2.0));   // arm at T(1)
        var oFire = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 2, accel: 2.0,
                                aboveOut: Outcome.Absorbed, aboveValid: true));                                // reject fire at T(2)
        Assert.True(oFire.Fired);
        Assert.Equal(ReactState.Fired, m.State);

        // Inside the cooldown (until T12): a fresh absorbed resolution must NOT fire again.
        var oBlocked = m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 5, accel: 2.0,
                                   aboveOut: Outcome.Absorbed, aboveValid: true));
        Assert.False(oBlocked.Fired);
        Assert.NotEqual(ReactState.Watching, m.State);   // still cooling, not re-armed

        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 13, accel: 2.0));  // cooldown elapses -> Waiting
        m.Update(In(wallAbovePrice: 100.25, wallAboveCur: 120, delta: 50, mid: 100.00, sec: 14, accel: 2.0));  // re-arm
        Assert.Equal(ReactState.Watching, m.State);
    }
}
