using System;

namespace TradingRadar.Engine
{
    public enum SideState { Waiting, Armed, Countdown, Fired, Cooldown }

    public struct FireEvent
    {
        public Side Side; public double WallPrice; public double EntryHint;
        public double Fraction; public long DeltaAtFire; public double ZAtFire; public DateTime Time;
    }

    public struct ControllerInputs
    {
        public double WallAbovePrice; public long WallAboveCurrent;   // dominant ask wall above (long candidate)
        public double WallBelowPrice; public long WallBelowCurrent;   // dominant bid wall below (short candidate)
        public long AggressorDelta; public double TapeZScore; public int TapeAlternations;
        public double Mid; public DateTime Now; public BookMirror Book;
    }

    public struct ControllerOutput
    {
        public bool Chop;
        public SideState Long; public double LongFraction; public double LongTradeBacked;
        public SideState Short; public double ShortFraction; public double ShortTradeBacked;
        public bool Fired; public FireEvent Fire;
        // The armed wall's identity price per side — valid (non-zero) whenever that side is
        // Armed/Countdown/Fired, 0 otherwise. The NT layer feeds THIS run's live size back in at THIS
        // price (looked up by identity), not the recomputed "current dominant wall above/below" — a
        // wall being eaten loses dominance mid-consumption, and feeding a different price would trip
        // the wall-identity guard (>= 1 tick move => abandon) exactly when the setup matures.
        public double LongWallPrice;
        public double ShortWallPrice;
        // Fix 3 (day-1 capture observability): per-candidate diagnostics, not consumed by any decision
        // logic — just surfaced for the next Rec capture. DistTicks stays valid through Cooldown too
        // (round-2: the veto row needs the judgment distance — see DistTicksValid), unlike *WallPrice
        // which is 0 unless that side is Armed/Countdown/Fired; CooldownUntil is verbatim.
        // HoldCount/ShortHoldCount report TWO different counters depending on state (round-4 K window
        // split them into separate fields on Candidate — see PassBits): while Armed, this is the
        // veto-dwell consecutive count (unchanged); while Countdown, this is the count of passing
        // ticks within the last KWindow judged ticks (so the CSV still shows a 1,2,3 progression even
        // through a jitter dip, instead of resetting to 0).
        public int LongHoldCount;
        public int ShortHoldCount;
        public double LongDistTicks;
        public double ShortDistTicks;
        public DateTime LongCooldownUntil;
        public DateTime ShortCooldownUntil;
        // Verbatim candidate Peak/Min (no IsIdentityHeld gating, unlike *WallPrice) — 0/0 before a side
        // has ever armed, whatever the tracked candidate holds otherwise (including through Waiting,
        // since Peak/Min are only ever reassigned on (re)arm, not zeroed on abandon).
        public long LongPeak;
        public long LongMin;
        public long ShortPeak;
        public long ShortMin;
    }

    // All placeholders — measured from Rec CSV (spec §9). No literal threshold lives in logic.
    public class ControllerConfig
    {
        public long SignificanceBand = 60;       // min wall size to arm (contracts) — MEASURED
        public double MinTradeBackedRatio = 0.6; // fraction of the drop that trades must explain
        // MEASURED rounds 3-5 ES: the only quality-neutral volume lever in the 54-cell grid; on 2
        // independently-observed days, episodes with EVERY other gate green (delta -309/+524, z>1.5,
        // trade-backed 1.0, at the wall) arrived at frac 0.62-0.68 and died on this gate alone.
        // Calibration-acceleration setting for Sim — re-derive once ~15-20 real fires exist.
        public double FireFrac = 0.6;            // consumption fraction to fire
        // MEASURED day-1 ES (2026-06-22): 8 passed ~50% of rows (no signal); 30 ≈ p66/p82 — provisional
        // until Countdown-conditional data exists
        public long DeltaFloor = 30;              // |AggressorDelta| agreeing to confirm
        public double ZFloor = 1.5;              // tape-speed z-score to confirm
        // Review round-7 arithmetic: KWindow(5) spans ~250ms at the 20Hz engine ceiling; a 1.0s latch
        // would make the z-leg "one spike per second" (~4x the window) instead of "bridge a jitter dip".
        // 0.35s ≈ 1.4x the window span and covers the measured ~236ms z swings. Placeholder — grade
        // against fired-episode forward returns once the PassBits-triggered capture exists; if quality
        // lands worse than 1:2 good:false, shorten THIS knob alone (acceptance criterion #4).
        public double ZTrustSeconds = 0.35;
        public int K = 3;                        // snapshots meeting fire pre-conditions required within KWindow
        public int KWindow = 5; // MEASURED round-4 ES: full confluence exists but breaks on single-tick z/frac jitter before 3 consecutive; 3-of-5 keeps sustained-confluence semantics while tolerating 1-2 jitter dips — placeholder, calibrate with fires
        public double ReloadFrac = 0.25;         // refill above running-min (as frac of peak) => reload veto
        public int AwayTicks = 6;                // mid this far from the wall => price fell away
        public double ChopSlowZ = -0.3;          // z at/below this = quiet tape
        public int ChopAltCount = 3;             // aggressor sign changes over the window => chop
        public TimeSpan Cooldown = TimeSpan.FromSeconds(10);
        public double MinDropBandFrac = 0.12;    // MEASURED round-2 ES: flat 3 sat AT the p90-p95 jitter ceiling (median Drop-at-veto was exactly 3.0); 12% of Peak clears p95 jitter at median wall sizes
        public int JudgeTicks = 2;               // MEASURED round-2 ES: TradeBackedFraction was 0.000 in 100% of vetoes beyond 2 ticks (TradedAt only matches AT the wall) — verdicts render only inside this radius
    }

    // The anti-flip spine. Two per-side candidates + a global CHOP gate. Pure: time via inp.Now.
    public class ControllerStateMachine
    {
        private class Candidate
        {
            public SideState State = SideState.Waiting;
            public double WallPrice;
            public long Peak;
            public long Min;
            public DateTime ArmTime;
            public int HoldCount;
            // Round-4 K-window persistence: bitmask of the last KWindow StepCountdown judged-tick
            // pass/fail results (bit 0 = most recent judged tick). Fire-path state ONLY — kept
            // separate from HoldCount, which stays the Armed-phase veto-dwell consecutive counter
            // (round-2, unchanged) so the two never share/clobber each other's semantics.
            public int PassBits;
            // Round-7 z-latch: wall-clock time of the last tick where TapeZScore alone cleared ZFloor.
            // MinValue = never passed. Reset alongside PassBits at every site that zeroes it (see the
            // 5 StepCountdown reset sites + the Armed->Countdown entry) — its lifetime pins 1:1 to the
            // PassBits invariant, no separate lifecycle.
            public DateTime LastZPassTime = DateTime.MinValue;
            public DateTime CooldownUntil = DateTime.MinValue;
            public double Fraction;
            public double TradeBackedFraction;
            // ponytail: latches the firing FireEvent so it stays readable on o.Fire for as long as
            // State == Fired (the per-call `fire` local in Update() resets to default every tick).
            public FireEvent LastFire;
        }

        private readonly ControllerConfig _cfg;
        private readonly double _tick;
        private readonly Candidate _long = new Candidate();
        private readonly Candidate _short = new Candidate();

        public ControllerStateMachine(ControllerConfig cfg, double tick) { _cfg = cfg; _tick = tick; }

        public ControllerOutput Update(ControllerInputs inp)
        {
            bool chop = Chop(inp);
            FireEvent fire = default(FireEvent); bool fired = false;

            if (StepLong(inp, chop, ref fire)) fired = true;
            if (!fired && StepShort(inp, chop, ref fire)) fired = true;

            ControllerOutput o;
            o.Chop = chop;
            o.Long = _long.State; o.LongFraction = _long.Fraction; o.LongTradeBacked = _long.TradeBackedFraction;
            o.Short = _short.State; o.ShortFraction = _short.Fraction; o.ShortTradeBacked = _short.TradeBackedFraction;
            o.Fired = fired;
            // ponytail: while a side is latched Fired, keep reporting its FireEvent (not just on the
            // exact firing tick) — `fire` above is a fresh per-call local and would otherwise go back
            // to default() the moment HoldCount stops advancing. When both sides are latched, recency
            // (FireEvent.Time) breaks the tie — otherwise Long's stale fire would shadow a live Short.
            o.Fire = fired ? fire
                : _long.State == SideState.Fired && _short.State == SideState.Fired
                    ? (_long.LastFire.Time >= _short.LastFire.Time ? _long.LastFire : _short.LastFire)
                : _long.State == SideState.Fired ? _long.LastFire
                : _short.State == SideState.Fired ? _short.LastFire
                : default(FireEvent);
            o.LongWallPrice = IsIdentityHeld(_long.State) ? _long.WallPrice : 0;
            o.ShortWallPrice = IsIdentityHeld(_short.State) ? _short.WallPrice : 0;
            o.LongHoldCount = _long.State == SideState.Countdown ? PassCount(_long.PassBits) : _long.HoldCount;
            o.ShortHoldCount = _short.State == SideState.Countdown ? PassCount(_short.PassBits) : _short.HoldCount;
            o.LongDistTicks = DistTicksValid(_long.State) ? Math.Abs(inp.Mid - _long.WallPrice) / _tick : 0;
            o.ShortDistTicks = DistTicksValid(_short.State) ? Math.Abs(inp.Mid - _short.WallPrice) / _tick : 0;
            o.LongCooldownUntil = _long.CooldownUntil;
            o.ShortCooldownUntil = _short.CooldownUntil;
            o.LongPeak = _long.Peak; o.LongMin = _long.Min;
            o.ShortPeak = _short.Peak; o.ShortMin = _short.Min;
            return o;
        }

        // The wall price is meaningful (an identity the NT layer must feed back by-price) only while
        // the candidate still owns an armed wall: Armed, Countdown, or the just-fired/latched Fired.
        private static bool IsIdentityHeld(SideState s)
        {
            return s == SideState.Armed || s == SideState.Countdown || s == SideState.Fired;
        }

        // Round-4 K-window persistence: number of set bits in a PassBits register (<= KWindow, tiny).
        private static int PassCount(int bits)
        {
            int n = 0;
            while (bits != 0) { n += bits & 1; bits >>= 1; }
            return n;
        }

        // DistTicks additionally stays valid through Cooldown: WallPrice is never cleared on the veto
        // transition (Armed/Countdown -> Cooldown), and inp.Mid this call is the same value the verdict
        // just used — so the veto row can log the exact judgment distance instead of reading 0.
        private static bool DistTicksValid(SideState s)
        {
            return IsIdentityHeld(s) || s == SideState.Cooldown;
        }

        private bool Chop(ControllerInputs inp)
        {
            return inp.TapeZScore <= _cfg.ChopSlowZ && inp.TapeAlternations >= _cfg.ChopAltCount;
        }

        // Long candidate = ask wall above. Task 4 implements only Waiting->Armed + intact suppression.
        // Tasks 5-7 extend the switch with Countdown/Fire/Cooldown/Reset.
        private bool StepLong(ControllerInputs inp, bool chop, ref FireEvent fire)
        {
            Candidate c = _long;
            double price = inp.WallAbovePrice; long cur = inp.WallAboveCurrent;
            switch (c.State)
            {
                case SideState.Waiting:
                    if (cur >= _cfg.SignificanceBand && inp.Now >= c.CooldownUntil)
                    {
                        c.State = SideState.Armed; c.WallPrice = price;
                        c.Peak = cur; c.Min = cur; c.ArmTime = inp.Now; c.HoldCount = 0;
                        c.Fraction = 0; c.TradeBackedFraction = 0;
                    }
                    break;
                case SideState.Armed:
                    if (cur <= 0 || System.Math.Abs(price - c.WallPrice) >= _tick)
                    { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; c.HoldCount = 0; break; }
                    AdvanceArmedOrCountdown(c, Side.Ask, cur, inp);
                    break;
                case SideState.Cooldown:
                    if (inp.Now >= c.CooldownUntil) { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; }
                    break;
                case SideState.Countdown:
                    return StepCountdown(c, Side.Ask, cur, inp, chop, ref fire);
                case SideState.Fired:
                    // Reset when the wall is gone, price confirms the break, OR the break failed and
                    // reversed back past the wall (false break) — symmetric, same metric as StepCountdown's
                    // away-band. No wall-identity check: after a fire the eaten wall vanishes and dominance
                    // hops immediately, so an identity check would un-latch the SETUP indicator instantly.
                    if (cur <= 0 || Math.Abs(inp.Mid - c.WallPrice) >= _cfg.AwayTicks * _tick)
                    { c.State = SideState.Waiting; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.Fraction = 0; c.TradeBackedFraction = 0; }
                    break;
            }
            return false;
        }

        private bool StepCountdown(Candidate c, Side wallSide, long cur, ControllerInputs inp, bool chop, ref FireEvent fire)
        {
            double curWallPrice = wallSide == Side.Ask ? inp.WallAbovePrice : inp.WallBelowPrice;
            if (cur <= 0 || System.Math.Abs(curWallPrice - c.WallPrice) >= _tick)
            { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; c.HoldCount = 0; c.PassBits = 0; c.LastZPassTime = DateTime.MinValue; return false; }
            // The trade ring can no longer prove trades back to ArmTime — re-baseline instead of
            // silently under-counting Traded (which would misroute clean consumption into the pull veto).
            if (inp.Now - c.ArmTime >= inp.Book.TradeRetention)
            { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; c.HoldCount = 0; c.PassBits = 0; c.LastZPassTime = DateTime.MinValue; return false; }
            if (cur > c.Peak) c.Peak = cur;
            if (cur < c.Min) c.Min = cur;

            // Reload veto: refilled well above the running min (someone defending). Fraction/TradeBackedFraction
            // are deliberately NOT zeroed here — Cooldown->Waiting (above) zeroes them once the veto elapses,
            // and the frozen value stays readable through Cooldown by the same design as c.Fraction.
            if (cur - c.Min >= _cfg.ReloadFrac * c.Peak)
            { c.State = SideState.Cooldown; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.HoldCount = 0; c.PassBits = 0; c.LastZPassTime = DateTime.MinValue; return false; }

            // Price fell away from the wall (mid too far) => abandon.
            if (Math.Abs(inp.Mid - c.WallPrice) >= _cfg.AwayTicks * _tick)
            { c.State = SideState.Waiting; c.HoldCount = 0; c.PassBits = 0; c.LastZPassTime = DateTime.MinValue; c.Fraction = 0; c.TradeBackedFraction = 0; return false; }

            ConsumptionRead r = ConsumptionTracker.Read(wallSide, c.WallPrice, c.Peak, cur, c.ArmTime, inp.Book);
            c.Fraction = r.Fraction; c.TradeBackedFraction = r.TradeBackedFraction;

            bool deltaOk = wallSide == Side.Ask ? inp.AggressorDelta >= _cfg.DeltaFloor
                                                : inp.AggressorDelta <= -_cfg.DeltaFloor;
            // Fire must be judged where the order will actually be staged — at the wall. Reuses JudgeTicks
            // (no new knob): without this, the window could keep accumulating while price drifts away
            // mid-dwell and fire anywhere up to AwayTicks out (round-3 real fire: entered Countdown at 1.5
            // ticks, fired at 5.0 after a fast snap during the K-dwell — the worst of the 3 fires).
            bool nearWall = Math.Abs(inp.Mid - c.WallPrice) / _tick < _cfg.JudgeTicks;
            // Price drift is NOT jitter (review round-4): the 3-of-5 window tolerates single-tick
            // indicator dips (z/frac — the measured killer), but a wall snap-away must HARD-reset the
            // register, or one good tick after a snap-back completes the window — reviving the exact
            // round-3 bad-fire pattern (entered Countdown at 1.5 ticks, fired at 5.0 mid-dwell). Also
            // hard-resets the round-7 z-latch (below) — a pass from before the drift must not survive it.
            if (!nearWall) { c.PassBits = 0; c.LastZPassTime = DateTime.MinValue; return false; }

            // Round-7 z-latch: the 1s-trailing TapeZScore window can dip below ZFloor for a single tick
            // (measured: swings up to -1.86 within ~236ms — a window-statistic edge effect, not a real
            // tape-speed change) while every other confluence term stays green — 63% of killed episodes
            // stalled on exactly this coincidence. Bridge a genuine ZFloor pass across a bounded
            // wall-clock window (mirrors the shipped BlindTrustSeconds precedent) instead of requiring
            // the SAME tick. LastZPassTime == MinValue ("never passed") makes the subtraction a huge
            // TimeSpan, which correctly fails the comparison below with no separate guard needed.
            if (inp.TapeZScore >= _cfg.ZFloor) c.LastZPassTime = inp.Now;
            // Monotonic guard (review round-7): a backward tick (sub-2s feed jitter passes RadarTab's
            // small-backward branch without a reset) would make the subtraction negative — trivially
            // <= ZTrustSeconds — sticking zPass open regardless of the real z. Fail closed instead.
            bool zPass = inp.Now >= c.LastZPassTime
                         && (inp.Now - c.LastZPassTime).TotalSeconds <= _cfg.ZTrustSeconds;
            bool pre = r.Fraction >= _cfg.FireFrac
                       && r.TradeBackedFraction >= _cfg.MinTradeBackedRatio
                       && deltaOk && zPass && !chop;

            // Round-4 K-window persistence: every surviving NEAR-WALL Countdown tick is judged and
            // shifts the register, pass or fail. This tolerates 1-2 jitter dips inside KWindow instead
            // of the old "any single miss zeroes everything" consecutive-hold rule (measured: full
            // confluence existed but never held 3 CONSECUTIVE 20Hz ticks). Fire still requires THIS
            // tick to be a passing tick — the order stages off the CURRENT tick's values, a sustained-
            // but-stale window must not fire on a tick that itself failed.
            int mask = (1 << Math.Min(_cfg.KWindow, 30)) - 1;   // shift guard: KWindow >= 31 would silently break (int shift is mod-32)
            c.PassBits = ((c.PassBits << 1) | (pre ? 1 : 0)) & mask;
            if (!pre || PassCount(c.PassBits) < _cfg.K) return false;

            // FIRE — one-shot, latch.
            c.State = SideState.Fired;
            fire = new FireEvent {
                Side = wallSide, WallPrice = c.WallPrice, EntryHint = c.WallPrice,
                Fraction = r.Fraction, DeltaAtFire = inp.AggressorDelta, ZAtFire = inp.TapeZScore, Time = inp.Now };
            c.LastFire = fire;
            return true;
        }

        private void AdvanceArmedOrCountdown(Candidate c, Side wallSide, long cur, ControllerInputs inp)
        {
            // The trade ring can no longer prove trades back to ArmTime — re-baseline instead of
            // silently under-counting Traded (which would misroute clean consumption into the pull veto).
            if (inp.Now - c.ArmTime >= inp.Book.TradeRetention)
            { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; c.HoldCount = 0; return; }
            // ponytail: reuse AwayTicks (no new knob) — too far from the wall to judge trade-backing
            // (TradedAt only matches prints AT the wall). Re-baseline Peak/Min to the CURRENT size while
            // far instead of letting them accumulate across the whole far period: without this, the FIRST
            // near-touch tick reads Drop spanning the entire far window (where trades structurally
            // couldn't print at the wall) and instant pull-vetoes on arrival — the same day-1 tautology,
            // just deferred to the approach. Peak==Min==cur makes Fraction/TradeBackedFraction
            // deterministically 0 (Drop=0) — skip the trade-ring lookup and assign directly.
            if (Math.Abs(inp.Mid - c.WallPrice) >= _cfg.AwayTicks * _tick)
            // HoldCount too: a one-tick gap from inside the judge radius straight past AwayTicks must
            // not bank dwell ticks across the excursion (review round-2: veto committed K-n ticks early).
            { c.Peak = cur; c.Min = cur; c.Fraction = 0; c.TradeBackedFraction = 0; c.HoldCount = 0; return; }
            if (cur > c.Peak) c.Peak = cur;
            if (cur < c.Min) c.Min = cur;
            ConsumptionRead r = ConsumptionTracker.Read(wallSide, c.WallPrice, c.Peak, cur, c.ArmTime, inp.Book);
            c.Fraction = r.Fraction; c.TradeBackedFraction = r.TradeBackedFraction;

            // Judge radius: verdicts (Countdown vs veto-dwell) only render within JudgeTicks of the
            // wall — TradedAt only matches prints AT the wall, so judging trade-backing beyond that
            // radius is tautologically 0 (round-2 measured: 100% of vetoes beyond 2 ticks). Between
            // JudgeTicks and AwayTicks, Peak/Min keep accumulating and telemetry stays live, but no
            // verdict fires — thinning on approach is evidence the at-touch judgment will use.
            double distTicks = Math.Abs(inp.Mid - c.WallPrice) / _tick;
            if (r.Fraction <= 0 || r.Drop < _cfg.MinDropBandFrac * c.Peak || distTicks >= _cfg.JudgeTicks)
            { c.HoldCount = 0; return; } // nothing eaten, noise-band jitter, or too far for TradedAt to be trusted — stay Armed, reset veto dwell
            if (r.TradeBackedFraction >= _cfg.MinTradeBackedRatio)
            // Entering Countdown zeroes BOTH counters: HoldCount (Armed veto-dwell, unrelated once
            // here) and PassBits (the fresh K-window fire register — round-4), plus (round-7) the
            // z-latch — a stale pass from a previous Countdown dwell on this same candidate must not
            // carry into a fresh one.
            { c.State = SideState.Countdown; c.HoldCount = 0; c.PassBits = 0; c.LastZPassTime = DateTime.MinValue; return; }
            // Thinning NOT explained by trades = pull/spoof candidate. Require K CONSECUTIVE judgeable
            // sub-ratio ticks (not one) before conceding the veto — a single sub-ratio read can be a
            // print-matching miss, not a real pull; HoldCount is reset above the instant any tick fails
            // to reproduce it, so only a sustained sub-ratio run reaches Cooldown.
            c.HoldCount++;
            if (c.HoldCount < _cfg.K) return; // require K consecutive judgeable sub-ratio ticks before conceding pull/spoof
            c.State = SideState.Cooldown; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.HoldCount = 0;
        }

        private bool StepShort(ControllerInputs inp, bool chop, ref FireEvent fire)
        {
            Candidate c = _short;
            double price = inp.WallBelowPrice; long cur = inp.WallBelowCurrent;
            switch (c.State)
            {
                case SideState.Waiting:
                    if (cur >= _cfg.SignificanceBand && inp.Now >= c.CooldownUntil)
                    {
                        c.State = SideState.Armed; c.WallPrice = price;
                        c.Peak = cur; c.Min = cur; c.ArmTime = inp.Now; c.HoldCount = 0;
                        c.Fraction = 0; c.TradeBackedFraction = 0;
                    }
                    break;
                case SideState.Armed:
                    if (cur <= 0 || System.Math.Abs(price - c.WallPrice) >= _tick)
                    { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; c.HoldCount = 0; break; }
                    AdvanceArmedOrCountdown(c, Side.Bid, cur, inp);
                    break;
                case SideState.Cooldown:
                    if (inp.Now >= c.CooldownUntil) { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; }
                    break;
                case SideState.Countdown:
                    return StepCountdown(c, Side.Bid, cur, inp, chop, ref fire);
                case SideState.Fired:
                    // Reset when the wall is gone, price confirms the break, OR the break failed and
                    // reversed back past the wall (false break) — symmetric, same metric as StepCountdown's
                    // away-band. No wall-identity check: after a fire the eaten wall vanishes and dominance
                    // hops immediately, so an identity check would un-latch the SETUP indicator instantly.
                    if (cur <= 0 || Math.Abs(inp.Mid - c.WallPrice) >= _cfg.AwayTicks * _tick)
                    { c.State = SideState.Waiting; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.Fraction = 0; c.TradeBackedFraction = 0; }
                    break;
            }
            return false;
        }
    }
}
