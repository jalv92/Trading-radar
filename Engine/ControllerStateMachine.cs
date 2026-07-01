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
        public SideState Long; public double LongFraction;
        public SideState Short; public double ShortFraction;
        public bool Fired; public FireEvent Fire;
    }

    // All placeholders — measured from Rec CSV (spec §9). No literal threshold lives in logic.
    public class ControllerConfig
    {
        public long SignificanceBand = 60;       // min wall size to arm (contracts) — MEASURED
        public double MinTradeBackedRatio = 0.6; // fraction of the drop that trades must explain
        public double FireFrac = 0.7;            // consumption fraction to fire
        public long DeltaFloor = 8;              // |AggressorDelta| agreeing to confirm
        public double ZFloor = 1.5;              // tape-speed z-score to confirm
        public int K = 3;                        // consecutive snapshots meeting fire pre-conditions
        public double ReloadFrac = 0.25;         // refill above running-min (as frac of peak) => reload veto
        public int AwayTicks = 6;                // mid this far from the wall => price fell away
        public double ChopSlowZ = -0.3;          // z at/below this = quiet tape
        public int ChopAltCount = 3;             // aggressor sign changes over the window => chop
        public TimeSpan Cooldown = TimeSpan.FromSeconds(10);
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
            o.Long = _long.State; o.LongFraction = _long.Fraction;
            o.Short = _short.State; o.ShortFraction = _short.Fraction;
            o.Fired = fired; o.Fire = fire;
            return o;
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
                        c.Peak = cur; c.Min = cur; c.ArmTime = inp.Now; c.HoldCount = 0; c.Fraction = 0;
                    }
                    break;
                case SideState.Armed:
                    // Suppression: an INTACT wall never advances on book skew. Track peak/min only.
                    if (cur <= 0) { c.State = SideState.Waiting; break; }
                    if (cur > c.Peak) c.Peak = cur;
                    if (cur < c.Min) c.Min = cur;
                    // Countdown transition added in Task 5.
                    break;
            }
            return false;
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
                        c.Peak = cur; c.Min = cur; c.ArmTime = inp.Now; c.HoldCount = 0; c.Fraction = 0;
                    }
                    break;
                case SideState.Armed:
                    if (cur <= 0) { c.State = SideState.Waiting; break; }
                    if (cur > c.Peak) c.Peak = cur;
                    if (cur < c.Min) c.Min = cur;
                    break;
            }
            return false;
        }
    }
}
