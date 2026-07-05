using System;

namespace TradingRadar.Engine
{
    // Own state enum (spec §6), NOT shared with Break's SideState. Waiting -> Watching (arm) ->
    // Fired/Cooldown (resolve or abandon) -> Waiting.
    public enum ReactState { Waiting, Watching, Fired, Cooldown }

    // Why the last Watching->Cooldown abandon happened — read-only diagnostics (instrumentation, not
    // control): the funnel needs to SEE why each watch ended (0 fires with ~227 arms was invisible).
    // None = never abandoned this controller. Set at each abandon branch in StepWatching; never resets
    // on arm/fire, so it always names the MOST RECENT abandon.
    public enum AbandonReason { None, Timeout, AwayDrift, WallVanished, IdentityHop, Pulled }

    // React setup tunables — ALL placeholders, MEASURED later once >= 10 distinct days + >= 15-20 real
    // fires with logged realized-fill exits exist (spec §11/§12; decisions/2026-07-03-ml-calibration).
    // SEPARATE fields from ControllerConfig by design (spec §2/§4/§11 "reuse"): React must recalibrate
    // without disturbing Break's in-flight calibration, even where the value happens to match today.
    public class ReactiveConfig
    {
        public double AccelFloor = 1.0;            // |TapeAccel| to arm, sign toward the wall (spec §5/§6) — PLACEHOLDER
        public int WatchProximityTicks = 3;        // arm only when Mid is this near the wall (~2-3, spec §11) — PLACEHOLDER
        public double MaxWatchSeconds = 15.0;      // wait-and-see timeout (~10-20s, spec §11) — PLACEHOLDER
        public double BreakFireFrac = 0.6;         // consume-branch fire fraction (mirrors Break FireFrac) — PLACEHOLDER
        public double MinTradeBackedRatio = 0.6;   // consume-branch trade-backing (mirrors Break) — PLACEHOLDER
        public double ReactCooldownSeconds = 10.0; // cool after a fire OR an abandon (spec §6) — PLACEHOLDER
        // Mirrored from ControllerConfig by design (spec §11 "reuse"), as SEPARATE fields:
        public long SignificanceBand = 60;         // min wall size to arm (contracts) — PLACEHOLDER
        public long DeltaFloor = 30;               // |AggressorDelta| agreeing (toward the wall) to arm — PLACEHOLDER
        public int AwayTicks = 6;                  // Mid this far from the wall w/o resolving => abandon — PLACEHOLDER
    }

    // Reactive wall setup: arms on tape acceleration into a dominant wall price is near, latches ONE
    // side, and fires on that wall's resolution (Absorbed -> fade / Consumed+trade-backed -> follow).
    // Isolated from the frozen Break ControllerStateMachine; reuses ConsumptionTracker/EpisodeClassifier
    // outcomes upstream (no new consumption math). Pure: time via inp.Now only.
    public class ReactiveController
    {
        private readonly ReactiveConfig _cfg;
        private readonly double _tick;

        private ReactState _state = ReactState.Waiting;
        private Side _wallSide;                     // the latched wall's own side (Ask above / Bid below)
        private double _wallPrice;                  // latched wall identity price
        private long _peak;                         // latched wall size at arm (break-consumption denominator)
        private DateTime _watchStart;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private FireEvent _lastFire;                // latched so o.Fire stays readable through Fired (banner)

        public ReactiveController(ReactiveConfig cfg, double tick) { _cfg = cfg; _tick = tick; }

        public ReactState State { get { return _state; } }
        // The latched wall's own side (for the cockpit banner) — meaningful while Watching/Fired.
        public Side WallSide { get { return _wallSide; } }

        // Read-only diagnostics (instrumentation only — none of these are read by control logic).
        // Why the last watch ended; None until the first abandon. Set at each abandon branch below.
        public AbandonReason LastAbandon { get; private set; }
        // The latched wall's price/side while Watching/Fired; 0/default otherwise (Waiting/Cooldown have
        // no live latch). Surfaced to the sig CSV so the funnel shows what wall each watch was tracking.
        public double LatchedWallPrice
        { get { return (_state == ReactState.Watching || _state == ReactState.Fired) ? _wallPrice : 0.0; } }
        public Side LatchedSide
        { get { return (_state == ReactState.Watching || _state == ReactState.Fired) ? _wallSide : default(Side); } }

        // Same shape as ControllerStateMachine.Update: returns ControllerOutput carrying Fired/Fire so
        // the NT layer routes both setups through one path. Reactive fires tag Kind=Reactive + React +
        // the TRADE side (spec §3 direction table).
        public ControllerOutput Update(ControllerInputs inp)
        {
            FireEvent fire = default(FireEvent);
            bool fired = false;

            switch (_state)
            {
                case ReactState.Waiting:
                    StepWaiting(inp);
                    break;
                case ReactState.Watching:
                    fired = StepWatching(inp, ref fire);
                    break;
                case ReactState.Fired:
                    // Fired latches the verdict (banner) AND blocks re-arm/re-fire for the cooldown
                    // window (spec §6: cool ReactCooldownSeconds after a fire), then -> Waiting.
                    if (inp.Now >= _cooldownUntil) _state = ReactState.Waiting;
                    break;
                case ReactState.Cooldown:
                    if (inp.Now >= _cooldownUntil) _state = ReactState.Waiting;
                    break;
            }

            if (fired) _lastFire = fire;

            ControllerOutput o = default(ControllerOutput);
            o.Fired = fired;
            // Match Break's guard: only report Fire while latched Fired (the fire tick itself already
            // transitioned _state to Fired above) — Waiting/Watching/Cooldown must not leak a stale
            // previous-cycle FireEvent.
            o.Fire = _state == ReactState.Fired ? _lastFire : default(FireEvent);
            return o;
        }

        // Waiting -> Watching (ARM, spec §6): a dominant wall within WatchProximityTicks meeting the
        // significance bar, |TapeAccel| >= AccelFloor with its sign toward that wall, and AggressorDelta
        // agreeing (toward the wall). Ask wall above = buyers accelerating up; Bid wall below = sellers
        // accelerating down. The two per-side floors fold sign + magnitude into one comparison (mirrors
        // ControllerStateMachine's deltaOk idiom).
        private void StepWaiting(ControllerInputs inp)
        {
            long sig = Math.Max(_cfg.SignificanceBand, inp.AdaptiveSignificance);

            if (inp.WallAboveCurrent >= sig
                && Near(inp.Mid, inp.WallAbovePrice)
                && inp.TapeAccel >= _cfg.AccelFloor
                && inp.AggressorDelta >= _cfg.DeltaFloor)
            { Latch(Side.Ask, inp.WallAbovePrice, inp.WallAboveCurrent, inp.Now); return; }

            if (inp.WallBelowCurrent >= sig
                && Near(inp.Mid, inp.WallBelowPrice)
                && inp.TapeAccel <= -_cfg.AccelFloor
                && inp.AggressorDelta <= -_cfg.DeltaFloor)
            { Latch(Side.Bid, inp.WallBelowPrice, inp.WallBelowCurrent, inp.Now); return; }
        }

        // Watching (spec §6): resolve the latched wall (fire), else abandon. Returns true only on a fire.
        private bool StepWatching(ControllerInputs inp, ref FireEvent fire)
        {
            long cur; double wallPriceNow; Outcome outcome; bool valid;
            if (_wallSide == Side.Ask)
            {
                cur = inp.WallAboveCurrent; wallPriceNow = inp.WallAbovePrice;
                outcome = inp.WallAboveOutcome; valid = inp.WallAboveOutcomeValid;
            }
            else
            {
                cur = inp.WallBelowCurrent; wallPriceNow = inp.WallBelowPrice;
                outcome = inp.WallBelowOutcome; valid = inp.WallBelowOutcomeValid;
            }

            // ABANDON (identity) — MUST run BEFORE resolve (fix pass 2). When React is active, the
            // NT-layer wall feed (RadarTab.ResolveWallFeed) is a passthrough of the CURRENT dominant
            // wall on this side, not the latched one. If the latched wall shrinks/hops and a DIFFERENT,
            // already-terminal wall becomes dominant on the same side, its outcome/valid belongs to
            // THAT wall — reading it here would fire anchored at the stale latched _wallPrice, on the
            // wrong wall (spec §6: identity-hop must always abandon, never fire).
            if (cur <= 0) { LastAbandon = AbandonReason.WallVanished; EnterCooldown(inp.Now); return false; }                                   // latched wall vanished
            if (Math.Abs(wallPriceNow - _wallPrice) >= _tick) { LastAbandon = AbandonReason.IdentityHop; EnterCooldown(inp.Now); return false; } // dominant-wall identity hops

            // RESOLVE — read ONLY the latched side's outcome, and ONLY when its Valid flag is set
            // (plan §0-R3: default(Outcome)==Absorbed would phantom-fire a fade on a warmup frame).
            // Reaching here means the current dominant wall on this side still IS the latched wall
            // (identity confirmed above), so this outcome genuinely belongs to _wallPrice.
            if (valid)
            {
                if (outcome == Outcome.Consumed)
                {
                    // BREAK / follow: consumed AND the drop is genuinely trade-backed (not a pull).
                    // Reuses ConsumptionTracker.Read (no new consumption math, spec §6).
                    ConsumptionRead r = ConsumptionTracker.Read(_wallSide, _wallPrice, _peak, cur, _watchStart, inp.Book);
                    if (r.Fraction >= _cfg.BreakFireFrac && r.TradeBackedFraction >= _cfg.MinTradeBackedRatio)
                    {
                        fire = MakeFire(FollowSide(_wallSide), ReactKind.Break, r.Fraction, inp);
                        EnterFired(inp.Now);
                        return true;
                    }
                    // Consumed but not trade-backed (cancellation, not real follow): do not follow.
                    // Fix pass 1: fall through to the ABANDON checks below instead of returning here — a
                    // real upstream case (sweep prints a tick off the wall, or aged out of the trade
                    // ring) can hold "valid Consumed, never trade-backed" for many frames; returning
                    // unconditionally made MaxWatchSeconds unreachable and stuck Watching forever.
                }
                else if (outcome == Outcome.Absorbed)
                {
                    // REJECT / fade: wall held/refilled, quote did not cross.
                    fire = MakeFire(FadeSide(_wallSide), ReactKind.Reject, 0.0, inp);
                    EnterFired(inp.Now);
                    return true;
                }
                else
                {
                    // Outcome.Pulled (spoof: cancelled, no cross) -> abstain, no trade (spec §3/§6).
                    LastAbandon = AbandonReason.Pulled;
                    EnterCooldown(inp.Now);
                    return false;
                }
            }

            // ABANDON — no resolution this tick (spec §6, remaining cases; identity/vanish already
            // checked above so they can never be shadowed by a fire on a stale wall):
            if (Math.Abs(inp.Mid - _wallPrice) >= _cfg.AwayTicks * _tick) { LastAbandon = AbandonReason.AwayDrift; EnterCooldown(inp.Now); return false; } // price left (accel fizzled)
            if ((inp.Now - _watchStart).TotalSeconds >= _cfg.MaxWatchSeconds) { LastAbandon = AbandonReason.Timeout; EnterCooldown(inp.Now); return false; } // wait-and-see timeout

            return false; // keep watching
        }

        private bool Near(double mid, double wallPrice)
        {
            return Math.Abs(mid - wallPrice) <= _cfg.WatchProximityTicks * _tick;
        }

        private void Latch(Side side, double price, long size, DateTime now)
        {
            _state = ReactState.Watching;
            _wallSide = side; _wallPrice = price; _peak = size; _watchStart = now;
        }

        // Direction table (spec §3). REJECT fades: Ask wall above -> SELL (Bid), Bid wall below -> BUY
        // (Ask). BREAK follows: Ask wall above -> BUY (Ask), Bid wall below -> SELL (Bid). FireEvent.Side
        // is the TRADE side (Ask => BUY / Bid => SELL for the NT layer).
        private static Side FadeSide(Side wall) { return wall == Side.Ask ? Side.Bid : Side.Ask; }
        private static Side FollowSide(Side wall) { return wall; }

        private FireEvent MakeFire(Side tradeSide, ReactKind react, double frac, ControllerInputs inp)
        {
            return new FireEvent {
                Side = tradeSide, WallPrice = _wallPrice, EntryHint = _wallPrice,
                Fraction = frac, DeltaAtFire = inp.AggressorDelta, ZAtFire = inp.TapeZScore, Time = inp.Now,
                Kind = SetupKind.Reactive, React = react };
        }

        private void EnterFired(DateTime now)
        { _state = ReactState.Fired; _cooldownUntil = now + TimeSpan.FromSeconds(_cfg.ReactCooldownSeconds); }

        private void EnterCooldown(DateTime now)
        { _state = ReactState.Cooldown; _cooldownUntil = now + TimeSpan.FromSeconds(_cfg.ReactCooldownSeconds); }
    }
}
