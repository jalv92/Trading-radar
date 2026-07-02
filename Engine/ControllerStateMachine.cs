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
        // logic — just surfaced for the next Rec capture. DistTicks follows the same validity rule as
        // *WallPrice (0 unless that side is Armed/Countdown/Fired); HoldCount/CooldownUntil are verbatim.
        public int LongHoldCount;
        public int ShortHoldCount;
        public double LongDistTicks;
        public double ShortDistTicks;
        public DateTime LongCooldownUntil;
        public DateTime ShortCooldownUntil;
    }

    // All placeholders — measured from Rec CSV (spec §9). No literal threshold lives in logic.
    public class ControllerConfig
    {
        public long SignificanceBand = 60;       // min wall size to arm (contracts) — MEASURED
        public double MinTradeBackedRatio = 0.6; // fraction of the drop that trades must explain
        public double FireFrac = 0.7;            // consumption fraction to fire
        // MEASURED day-1 ES (2026-06-22): 8 passed ~50% of rows (no signal); 30 ≈ p66/p82 — provisional
        // until Countdown-conditional data exists
        public long DeltaFloor = 30;              // |AggressorDelta| agreeing to confirm
        public double ZFloor = 1.5;              // tape-speed z-score to confirm
        public int K = 3;                        // consecutive snapshots meeting fire pre-conditions
        public double ReloadFrac = 0.25;         // refill above running-min (as frac of peak) => reload veto
        public int AwayTicks = 6;                // mid this far from the wall => price fell away
        public double ChopSlowZ = -0.3;          // z at/below this = quiet tape
        public int ChopAltCount = 3;             // aggressor sign changes over the window => chop
        public TimeSpan Cooldown = TimeSpan.FromSeconds(10);
        public long MinDropBand = 3;             // ignore sub-band size jitter before the pull-veto decision — MEASURED
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
            o.LongHoldCount = _long.HoldCount;
            o.ShortHoldCount = _short.HoldCount;
            o.LongDistTicks = IsIdentityHeld(_long.State) ? Math.Abs(inp.Mid - _long.WallPrice) / _tick : 0;
            o.ShortDistTicks = IsIdentityHeld(_short.State) ? Math.Abs(inp.Mid - _short.WallPrice) / _tick : 0;
            o.LongCooldownUntil = _long.CooldownUntil;
            o.ShortCooldownUntil = _short.CooldownUntil;
            return o;
        }

        // The wall price is meaningful (an identity the NT layer must feed back by-price) only while
        // the candidate still owns an armed wall: Armed, Countdown, or the just-fired/latched Fired.
        private static bool IsIdentityHeld(SideState s)
        {
            return s == SideState.Armed || s == SideState.Countdown || s == SideState.Fired;
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
            { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; c.HoldCount = 0; return false; }
            // The trade ring can no longer prove trades back to ArmTime — re-baseline instead of
            // silently under-counting Traded (which would misroute clean consumption into the pull veto).
            if (inp.Now - c.ArmTime >= inp.Book.TradeRetention)
            { c.State = SideState.Waiting; c.Fraction = 0; c.TradeBackedFraction = 0; c.HoldCount = 0; return false; }
            if (cur > c.Peak) c.Peak = cur;
            if (cur < c.Min) c.Min = cur;

            // Reload veto: refilled well above the running min (someone defending). Fraction/TradeBackedFraction
            // are deliberately NOT zeroed here — Cooldown->Waiting (above) zeroes them once the veto elapses,
            // and the frozen value stays readable through Cooldown by the same design as c.Fraction.
            if (cur - c.Min >= _cfg.ReloadFrac * c.Peak)
            { c.State = SideState.Cooldown; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.HoldCount = 0; return false; }

            // Price fell away from the wall (mid too far) => abandon.
            if (Math.Abs(inp.Mid - c.WallPrice) >= _cfg.AwayTicks * _tick)
            { c.State = SideState.Waiting; c.HoldCount = 0; c.Fraction = 0; c.TradeBackedFraction = 0; return false; }

            ConsumptionRead r = ConsumptionTracker.Read(wallSide, c.WallPrice, c.Peak, cur, c.ArmTime, inp.Book);
            c.Fraction = r.Fraction; c.TradeBackedFraction = r.TradeBackedFraction;

            bool deltaOk = wallSide == Side.Ask ? inp.AggressorDelta >= _cfg.DeltaFloor
                                                : inp.AggressorDelta <= -_cfg.DeltaFloor;
            bool pre = r.Fraction >= _cfg.FireFrac
                       && r.TradeBackedFraction >= _cfg.MinTradeBackedRatio
                       && deltaOk && inp.TapeZScore >= _cfg.ZFloor && !chop;

            if (!pre) { c.HoldCount = 0; return false; }

            c.HoldCount++;
            if (c.HoldCount < _cfg.K) return false;

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
            { c.Peak = cur; c.Min = cur; c.Fraction = 0; c.TradeBackedFraction = 0; return; }
            if (cur > c.Peak) c.Peak = cur;
            if (cur < c.Min) c.Min = cur;
            ConsumptionRead r = ConsumptionTracker.Read(wallSide, c.WallPrice, c.Peak, cur, c.ArmTime, inp.Book);
            c.Fraction = r.Fraction; c.TradeBackedFraction = r.TradeBackedFraction;
            if (r.Fraction <= 0 || r.Drop < _cfg.MinDropBand) return; // nothing eaten yet, or sub-band jitter — stay Armed
            if (r.TradeBackedFraction >= _cfg.MinTradeBackedRatio)
                c.State = SideState.Countdown;
            else
            {
                // Thinning NOT explained by trades = pull/spoof: veto + cooldown.
                c.State = SideState.Cooldown; c.CooldownUntil = inp.Now + _cfg.Cooldown; c.HoldCount = 0;
            }
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
