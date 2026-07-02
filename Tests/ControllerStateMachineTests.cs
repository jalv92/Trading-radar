using System;
using TradingRadar.Engine;
using Xunit;

public class ControllerStateMachineTests
{
    static DateTime T(int s) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);

    static BookMirror EmptyBook() => new BookMirror(0.25, TimeSpan.FromSeconds(30));

    static ControllerInputs In(double wallAbovePrice, long wallAboveCur, double wallBelowPrice, long wallBelowCur,
                               long delta, double z, int alt, double mid, int sec, BookMirror book)
        => new ControllerInputs {
            WallAbovePrice = wallAbovePrice, WallAboveCurrent = wallAboveCur,
            WallBelowPrice = wallBelowPrice, WallBelowCurrent = wallBelowCur,
            AggressorDelta = delta, TapeZScore = z, TapeAlternations = alt, Mid = mid, Now = T(sec), Book = book };

    static ControllerStateMachine Machine() => new ControllerStateMachine(new ControllerConfig(), 0.25);

    // A big ask wall above price arms the LONG candidate.
    [Fact]
    public void Arms_long_when_dominant_ask_wall_above_meets_significance()
    {
        var m = Machine();
        var o = m.Update(In(100.25, 120, 0, 0, delta: 0, z: 0, alt: 0, mid: 100.00, sec: 1, book: EmptyBook()));
        Assert.Equal(SideState.Armed, o.Long);
    }

    // Intact wall + heavy book skew must NOT advance past Armed (no countdown, no fire) — §5b.
    [Fact]
    public void Intact_wall_does_not_advance_on_book_skew_alone()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        var o = m.Update(In(100.25, 120, 0, 0, delta: 999, z: 5, alt: 0, mid: 100.00, sec: 2, book: EmptyBook())); // size unchanged
        Assert.Equal(SideState.Armed, o.Long);
        Assert.False(o.Fired);
    }

    // Fix 1 (root cause): a far-away drop (mid outside AwayTicks) must NOT pull-veto — TradedAt only
    // matches trades printed AT the wall, so judging trade-backing at a distance is tautologically 0.
    // Stay Armed instead of falsely vetoing to Cooldown (day-1: 94.2%/91.2% of all vetoes were this).
    [Fact]
    public void Far_away_drop_stays_armed_no_trade_backed_judgement()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 98.50, 1, EmptyBook()));         // arm, mid 7 ticks from wall (>= AwayTicks)
        var o = m.Update(In(100.25, 30, 0, 0, 0, 0, 0, 98.50, 2, EmptyBook())); // big drop, no trades, still far -> stays Armed
        Assert.Equal(SideState.Armed, o.Long);
    }

    // A wall below significance does not arm.
    [Fact]
    public void Does_not_arm_when_wall_below_significance()
    {
        var m = Machine();
        var o = m.Update(In(100.25, 5, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        Assert.Equal(SideState.Waiting, o.Long);
    }

    static BookMirror BookWithBuys(double wallPrice, long vol, int sec)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice - 0.5, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) }); // buy aggressor at the wall
        return b;
    }

    [Fact]
    public void Enters_countdown_when_drop_is_trade_backed()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));         // arm at peak 120
        var book = BookWithBuys(100.25, 60, 2);                                    // 60 bought at the wall
        var o = m.Update(In(100.25, 60, 0, 0, 0, 0, 0, 100.00, 2, book));          // size 120->60 (drop 60, all traded)
        Assert.Equal(SideState.Countdown, o.Long);
        Assert.True(o.LongFraction > 0.49 && o.LongFraction < 0.51);
    }

    [Fact]
    public void Pull_without_trades_vetoes_to_cooldown()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));          // arm
        var o = m.Update(In(100.25, 60, 0, 0, 0, 0, 0, 100.00, 2, EmptyBook()));   // size dropped, NO prints => pull
        Assert.Equal(SideState.Cooldown, o.Long);
    }

    // Drives an ask wall from 120 down to ~30 (75% eaten) with buys, delta+ and z high, for K snapshots.
    [Fact]
    public void Fires_long_once_on_full_confluence_then_latches()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook())); // arm, peak 120
        var b2 = BookWithBuys(100.25, 60, 2);
        m.Update(In(100.25, 60, 0, 0, 20, 2.0, 0, 100.00, 2, b2));        // countdown
        var b3 = BookWithBuys(100.25, 90, 3);                             // more buys at the wall (cumulative 90+)
        ControllerOutput o = default(ControllerOutput);
        int fires = 0;
        for (int s = 3; s <= 8; s++)                                      // hold several snapshots (K=3)
        {
            var b = BookWithBuys(100.25, 90, s);
            o = m.Update(In(100.25, 30, 0, 0, delta: 40, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b));
            if (o.Fired) fires++;
        }
        Assert.Equal(1, fires);                       // one-shot
        Assert.Equal(Side.Ask, o.Fire.Side);          // ask wall above => long break
        Assert.Equal(SideState.Fired, o.Long);        // latched
    }

    // Opposing delta blocks the fire even at high consumption.
    [Fact]
    public void Does_not_fire_long_when_delta_opposes()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        for (int s = 2; s <= 8; s++)
        {
            var b = BookWithBuys(100.25, 90, s);
            var o = m.Update(In(100.25, 30, 0, 0, delta: -30, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b)); // sellers pressing
            Assert.False(o.Fired);
        }
    }

    [Fact]
    public void Chop_suppresses_fire_even_at_high_consumption()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        for (int s = 2; s <= 8; s++)
        {
            var b = BookWithBuys(100.25, 90, s);
            // z below ChopSlowZ and alternations above ChopAltCount => CHOP, must not fire despite 75% eaten + buys.
            var o = m.Update(In(100.25, 30, 0, 0, delta: 40, z: -0.5, alt: 4, mid: 100.00, sec: s, book: b));
            Assert.True(o.Chop);
            Assert.False(o.Fired);
        }
    }

    // Isolates the `!chop` term: config puts ChopSlowZ (3.0) ABOVE ZFloor (1.5), so z=2.0 passes the
    // fire z-gate AND trips CHOP (z <= 3.0 && alt >= ChopAltCount). Deleting `!chop` from the engine's
    // fire conjunction would let this fire (fires>0) — so this is a real regression guard for CHOP.
    [Fact]
    public void Chop_blocks_fire_even_when_z_passes_the_fire_gate()
    {
        var m = new ControllerStateMachine(new ControllerConfig { ChopSlowZ = 3.0 }, 0.25);
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook())); // arm at peak 120
        int fires = 0;
        for (int s = 2; s <= 8; s++)
        {
            var b = BookWithBuys(100.25, 90, s);                          // 75% eaten, fully trade-backed
            var o = m.Update(In(100.25, 30, 0, 0, delta: 40, z: 2.0, alt: 4, mid: 100.00, sec: s, book: b));
            if (o.Fired) fires++;
            Assert.True(o.Chop);        // z=2.0 <= ChopSlowZ=3.0 AND alt=4 >= ChopAltCount=3
        }
        Assert.Equal(0, fires);         // blocked solely by !chop (z=2.0 >= ZFloor=1.5 would otherwise fire)
    }

    // THE anti-flip gate: an input stream whose book skew flips every tick must produce NO fire and
    // never leave a stable non-firing state. Walls are intact (no consumption), delta/skew oscillate.
    [Fact]
    public void Does_not_oscillate_or_fire_on_flipping_book_skew()
    {
        var m = Machine();
        int fires = 0;
        for (int s = 1; s <= 40; s++)
        {
            long delta = (s % 2 == 0) ? 50 : -50;      // skew flips every snapshot
            double z = (s % 2 == 0) ? 2.0 : -0.5;      // and so does speed
            int alt = (s % 3 == 0) ? 4 : 0;
            // Walls present but INTACT (current == peak), so no consumption can arm the countdown.
            var o = m.Update(In(100.25, 120, 99.75, 120, delta, z, alt, 100.00, s, EmptyBook()));
            if (o.Fired) fires++;
            Assert.NotEqual(SideState.Countdown, o.Long);   // intact wall never enters countdown
            Assert.NotEqual(SideState.Countdown, o.Short);
        }
        Assert.Equal(0, fires);
    }

    // Reload veto: a wall being consumed that REFILLS above its running min (someone defending) -> Cooldown, not fire.
    [Fact]
    public void Reload_refill_vetoes_countdown_to_cooldown()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));           // arm peak 120
        var b2 = BookWithBuys(100.25, 40, 2);
        var o2 = m.Update(In(100.25, 80, 0, 0, 20, 2.0, 0, 100.00, 2, b2));          // eaten 120->80 (min=80), trade-backed -> Countdown
        Assert.Equal(SideState.Countdown, o2.Long);
        var b3 = BookWithBuys(100.25, 40, 3);
        var o3 = m.Update(In(100.25, 120, 0, 0, 20, 2.0, 0, 100.00, 3, b3));         // refilled 80->120 (>= min + ReloadFrac*peak) -> reload veto
        Assert.Equal(SideState.Cooldown, o3.Long);
    }

    // Fire -> reset (price crosses past the wall) -> the side can re-arm on a fresh wall.
    [Fact]
    public void Fires_then_resets_then_can_rearm()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));            // arm
        int fires = 0; ControllerOutput o = default(ControllerOutput);
        for (int s = 2; s <= 8; s++) { var b = BookWithBuys(100.25, 90, s); o = m.Update(In(100.25, 30, 0, 0, 40, 2.0, 0, 100.00, s, b)); if (o.Fired) fires++; }
        Assert.Equal(1, fires);
        Assert.Equal(SideState.Fired, o.Long);
        // Price breaks up and holds well past the wall -> reset to Waiting.
        var oReset = m.Update(In(100.25, 0, 0, 0, 0, 0, 0, 102.00, 9, EmptyBook())); // cur<=0 AND mid far above wall
        Assert.Equal(SideState.Waiting, oReset.Long);
        // A fresh dominant wall appears above the new price, after the post-fire Cooldown (10s) elapses -> re-arms.
        var oRearm = m.Update(In(103.00, 120, 0, 0, 0, 0, 0, 102.75, 20, EmptyBook()));
        Assert.Equal(SideState.Armed, oRearm.Long);
    }

    // Sub-band jitter (1-lot cancel, not trade-backed) must NOT trip the pull-veto (stays Armed).
    [Fact]
    public void Sub_band_jitter_does_not_trip_pull_veto()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));            // arm peak 120
        var o = m.Update(In(100.25, 119, 0, 0, 0, 0, 0, 100.00, 2, EmptyBook()));    // 1-lot drop, no trades, < MinDropBand -> stay Armed
        Assert.Equal(SideState.Armed, o.Long);
    }

    // Wall-identity guard: if NT's dominant ask wall hops to a different price after arming,
    // the armed Long candidate abandons to Waiting rather than mixing two walls' size history.
    [Fact]
    public void Wall_hop_abandons_armed_candidate()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));         // arm Long at 100.25
        var o = m.Update(In(100.50, 120, 0, 0, 0, 0, 0, 100.00, 2, EmptyBook())); // dominant wall now 100.50 (>=1 tick away)
        Assert.Equal(SideState.Waiting, o.Long);
    }

    // Same guard, but the hop happens while in Countdown.
    [Fact]
    public void Wall_hop_during_countdown_abandons()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));         // arm at 100.25
        var b2 = BookWithBuys(100.25, 60, 2);
        var o2 = m.Update(In(100.25, 60, 0, 0, 20, 2.0, 0, 100.00, 2, b2));       // -> Countdown
        Assert.Equal(SideState.Countdown, o2.Long);
        var o3 = m.Update(In(100.75, 60, 0, 0, 20, 2.0, 0, 100.00, 3, EmptyBook())); // wall hops to 100.75 mid-countdown
        Assert.Equal(SideState.Waiting, o3.Long);
    }

    static BookMirror BookWithSells(double wallPrice, long vol, int sec)
    {
        var b = new BookMirror(0.25, TimeSpan.FromSeconds(60));
        b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = 0, Price = wallPrice + 0.5, Volume = 20, Time = T(0) });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = wallPrice,       Volume = 5,  Time = T(0) });
        b.ApplyTrade(new TradeEvent { Price = wallPrice, Volume = vol, Time = T(sec) }); // sell aggressor at the wall
        return b;
    }

    // Fix 1 regression: a false break (price pokes past the wall, then reverses back through it) must
    // NOT leave the Fired candidate latched forever — it has to reset via the symmetric away-band check.
    [Fact]
    public void Fired_long_resets_on_false_break_reversal_then_rearms_after_cooldown()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        int fires = 0; ControllerOutput o = default(ControllerOutput);
        for (int s = 2; s <= 8; s++) { var b = BookWithBuys(100.25, 90, s); o = m.Update(In(100.25, 30, 0, 0, 40, 2.0, 0, 100.00, s, b)); if (o.Fired) fires++; }
        Assert.Equal(1, fires);
        Assert.Equal(SideState.Fired, o.Long);
        // False break: price reverses back below the wall by more than AwayTicks; wall still present (cur>0).
        var oReversed = m.Update(In(100.25, 30, 0, 0, 0, 0, 0, 98.50, 9, EmptyBook()));
        Assert.Equal(SideState.Waiting, oReversed.Long);
        // Still cooling down (10s from the reset tick) -> a fresh big wall must not re-arm yet.
        var oCooling = m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 98.50, 15, EmptyBook()));
        Assert.Equal(SideState.Waiting, oCooling.Long);
        // Cooldown elapsed -> re-arms.
        var oRearm = m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 98.50, 20, EmptyBook()));
        Assert.Equal(SideState.Armed, oRearm.Long);
    }

    // Fix 2 regression: when both candidates are latched Fired, the most recently-fired side's
    // FireEvent must win, not Long unconditionally.
    [Fact]
    public void Fire_reports_most_recent_side_when_both_are_latched()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));                // arm Long
        ControllerOutput o = default(ControllerOutput);
        for (int s = 2; s <= 8; s++) { var b = BookWithBuys(100.25, 90, s); o = m.Update(In(100.25, 30, 0, 0, 40, 2.0, 0, 100.00, s, b)); }
        Assert.Equal(SideState.Fired, o.Long);
        Assert.Equal(Side.Ask, o.Fire.Side);

        o = m.Update(In(100.25, 30, 99.75, 120, 0, 0, 0, 100.00, 9, EmptyBook()));        // arm Short, peak 120
        var bs = BookWithSells(99.75, 60, 10);
        o = m.Update(In(100.25, 30, 99.75, 60, -20, 2.0, 0, 100.00, 10, bs));             // Armed -> Countdown
        for (int s = 11; s <= 14; s++)                                                    // fires at s=13, latches
        {
            var b = BookWithSells(99.75, 90, s);
            o = m.Update(In(100.25, 30, 99.75, 30, delta: -40, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b));
        }
        Assert.Equal(SideState.Fired, o.Short);
        Assert.False(o.Fired);                    // s=14 is a non-firing tick after the fire
        Assert.Equal(Side.Bid, o.Fire.Side);       // the more recent fire, not the stale Long event
    }

    // Fix 4 regression: abandoning Armed on a wall hop must zero the reported Fraction, not leak
    // the last-eaten value while the candidate sits Waiting.
    [Fact]
    public void Armed_abandon_on_wall_hop_resets_output_fraction()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));            // arm peak 120
        var o1 = m.Update(In(100.25, 119, 0, 0, 0, 0, 0, 100.00, 2, EmptyBook()));   // small drop, stays Armed, Fraction>0
        Assert.Equal(SideState.Armed, o1.Long);
        Assert.True(o1.LongFraction > 0);
        var o2 = m.Update(In(100.50, 119, 0, 0, 0, 0, 0, 100.00, 3, EmptyBook()));   // wall hops >= 1 tick -> abandon
        Assert.Equal(SideState.Waiting, o2.Long);
        Assert.Equal(0, o2.LongFraction);
    }

    // Fix 3 regression: a hold that outlives BookMirror's trade retention must re-baseline to Waiting
    // (not silently misread Traded and fall into the pull-veto Cooldown).
    [Fact]
    public void Armed_candidate_rebaselines_when_hold_outlives_trade_retention()
    {
        var m = Machine();
        var book = EmptyBook(); // retention = 30s
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, book));                  // arm at T(1)
        var o = m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 35, book));         // T(35): 34s held, past 30s retention
        Assert.Equal(SideState.Waiting, o.Long);
        Assert.NotEqual(SideState.Cooldown, o.Long);
        var oRearm = m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 36, book));    // re-arms next tick (fresh ArmTime)
        Assert.Equal(SideState.Armed, oRearm.Long);
    }

    // K-debounce regression: a single snapshot that breaks a fire pre-condition must reset HoldCount
    // to 0 — firing requires K CONSECUTIVE snapshots, not K total across an interruption.
    [Fact]
    public void K_debounce_resets_on_a_single_broken_snapshot()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));            // arm peak 120
        var b2 = BookWithBuys(100.25, 60, 2);
        m.Update(In(100.25, 60, 0, 0, 20, 2.0, 0, 100.00, 2, b2));                    // -> Countdown

        int fires = 0; ControllerOutput o = default(ControllerOutput);
        // Two good snapshots (HoldCount 1, 2) — one short of firing (K=3).
        for (int s = 3; s <= 4; s++)
        {
            var b = BookWithBuys(100.25, 90, s);
            o = m.Update(In(100.25, 30, 0, 0, delta: 40, z: 2.0, alt: 0, mid: 100.00, sec: s, book: b));
            if (o.Fired) fires++;
        }
        Assert.Equal(SideState.Countdown, o.Long);
        Assert.Equal(0, fires);

        // Interrupt: delta drops below the floor for one snapshot -> HoldCount resets to 0.
        var bBad = BookWithBuys(100.25, 90, 5);
        o = m.Update(In(100.25, 30, 0, 0, delta: 0, z: 2.0, alt: 0, mid: 100.00, sec: 5, book: bBad));
        Assert.False(o.Fired);

        // One good snapshot after the interruption must NOT fire — the counter restarted.
        var bGood1 = BookWithBuys(100.25, 90, 6);
        o = m.Update(In(100.25, 30, 0, 0, delta: 40, z: 2.0, alt: 0, mid: 100.00, sec: 6, book: bGood1));
        Assert.False(o.Fired);

        // Two more consecutive good snapshots complete a fresh K=3 -> fires.
        var bGood2 = BookWithBuys(100.25, 90, 7);
        o = m.Update(In(100.25, 30, 0, 0, delta: 40, z: 2.0, alt: 0, mid: 100.00, sec: 7, book: bGood2));
        Assert.False(o.Fired);
        var bGood3 = BookWithBuys(100.25, 90, 8);
        o = m.Update(In(100.25, 30, 0, 0, delta: 40, z: 2.0, alt: 0, mid: 100.00, sec: 8, book: bGood3));
        Assert.True(o.Fired);
    }

    // Task 10 identity contract: ControllerOutput exposes the armed wall's price while Armed/Countdown,
    // and clears it back to 0 once the candidate abandons to Waiting. The NT layer keys its by-price
    // lookup off this field, not off whatever the recomputed "dominant wall" says this run.
    [Fact]
    public void Exposes_armed_wall_price_while_armed_then_clears_on_abandon()
    {
        var m = Machine();
        var o1 = m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));   // arms Long at 100.25
        Assert.Equal(SideState.Armed, o1.Long);
        Assert.Equal(100.25, o1.LongWallPrice);
        Assert.Equal(0, o1.ShortWallPrice);                                          // Short never armed
        var o2 = m.Update(In(100.25, 0, 0, 0, 0, 0, 0, 100.00, 2, EmptyBook()));     // wall vanishes -> abandon
        Assert.Equal(SideState.Waiting, o2.Long);
        Assert.Equal(0, o2.LongWallPrice);
    }

    // Fix 3 regression: ControllerOutput.LongTradeBacked mirrors LongFraction's lifecycle — 0 while
    // just-Armed (no drop read yet), > 0 once a trade-backed drop is read, 0 again after abandon.
    [Fact]
    public void Exposes_trade_backed_fraction_while_armed_then_clears_on_abandon()
    {
        var m = Machine();
        var o1 = m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));   // arm, no drop yet
        Assert.Equal(SideState.Armed, o1.Long);
        Assert.Equal(0, o1.LongTradeBacked);

        var book = BookWithBuys(100.25, 60, 2);                                      // 60 bought at the wall
        var o2 = m.Update(In(100.25, 60, 0, 0, 0, 0, 0, 100.00, 2, book));           // drop fully trade-backed
        Assert.Equal(SideState.Countdown, o2.Long);
        Assert.True(o2.LongTradeBacked > 0);

        var o3 = m.Update(In(100.50, 60, 0, 0, 0, 0, 0, 100.00, 3, book));           // wall hops -> abandon
        Assert.Equal(SideState.Waiting, o3.Long);
        Assert.Equal(0, o3.LongTradeBacked);
    }

    // Countdown wall-vanish abandon must reset to Waiting with a zeroed fraction and no fire.
    [Fact]
    public void Countdown_abandons_to_waiting_when_wall_vanishes()
    {
        var m = Machine();
        m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 100.00, 1, EmptyBook()));
        var b2 = BookWithBuys(100.25, 60, 2);
        var o2 = m.Update(In(100.25, 60, 0, 0, 20, 2.0, 0, 100.00, 2, b2));
        Assert.Equal(SideState.Countdown, o2.Long);
        var o3 = m.Update(In(100.25, 0, 0, 0, 20, 2.0, 0, 100.00, 3, EmptyBook())); // wall gone
        Assert.Equal(SideState.Waiting, o3.Long);
        Assert.Equal(0, o3.LongFraction);
        Assert.False(o3.Fired);
    }

    // Day-1 calibration Fix 3: per-candidate observability (HoldCount/DistTicks) surfaced on
    // ControllerOutput for the next capture — DistTicks follows *WallPrice's validity rule (held
    // while Armed/Countdown/Fired, 0 otherwise), HoldCount is verbatim.
    [Fact]
    public void Exposes_hold_count_and_dist_ticks_while_armed_then_clears_on_abandon()
    {
        var m = Machine();
        var o1 = m.Update(In(100.25, 120, 0, 0, 0, 0, 0, 98.50, 1, EmptyBook())); // arm, mid 7 ticks from wall
        Assert.Equal(SideState.Armed, o1.Long);
        Assert.Equal(0, o1.LongHoldCount);
        Assert.Equal(7.0, o1.LongDistTicks, 3);
        var o2 = m.Update(In(100.25, 0, 0, 0, 0, 0, 0, 98.50, 2, EmptyBook()));   // wall vanishes -> abandon
        Assert.Equal(SideState.Waiting, o2.Long);
        Assert.Equal(0, o2.LongDistTicks);
    }
}
