using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    // Absorb setup tunables (OF3-v2, frozen 2026-07-20 — see MFF-Sim/round12_confirm.py).
    // NQ-day-1 values; per-instrument presets can land later without touching control flow.
    public class AbsorbConfig
    {
        public long BigLots = 10;                 // institutional print size (outright contracts)
        public double BigWindowSeconds = 300.0;   // tape-net rolling window (5 min)
        public int SweepBandTicks = 8;            // wall must sit near the 15-min extreme
        public double EventGateMult = 1.5;        // session range >= mult x median of last 20 sessions
        public int MinDayRangeTicks = 320;        // fallback gate until 10 sessions of history exist
        public long MinWallSize = 60;             // arm floor, max'ed with AdaptiveSignificance
        public double CooldownSeconds = 60.0;     // per-side refire cooldown
        public int WindowStartMinutes = 9 * 60 + 35;  // 09:35 exchange wall-clock
        public int WindowEndMinutes = 12 * 60;        // 12:00
    }

    /// <summary>
    /// OF3-v2 "event-day absorption" as a radar setup: fires when a dominant
    /// wall resolves to ABSORBED at a 15-minute sweep extreme, with big-print
    /// aggressor flow agreeing, on a day that has already proven violent
    /// (event gate) and only during the morning window. Emits the same
    /// FireEvent contract as Break/React (Kind = SetupKind.Absorb, Side =
    /// TRADE side: Ask=buy the absorbed bid wall, Bid=sell the absorbed ask
    /// wall). Self-contained minute context (session range history, 15-min
    /// extremes) built from OnTrade — pure C#, no NinjaTrader deps.
    /// STATUS: frozen, UNVALIDATED hypothesis — Sim/Playback grading only.
    /// </summary>
    public class AbsorbController
    {
        private readonly AbsorbConfig _cfg;
        private readonly double _tick;
        private readonly BigPrintTracker _bp;

        // minute-bar context (built from trades)
        private long _curMinute = long.MinValue;
        private double _barHi, _barLo;
        private readonly Queue<double> _h15 = new Queue<double>();
        private readonly Queue<double> _l15 = new Queue<double>();
        private double _min15 = double.MaxValue;
        private double _max15 = double.MinValue;
        private DateTime _curTradingDay = DateTime.MinValue;
        private double _sessHi = double.MinValue;
        private double _sessLo = double.MaxValue;
        private readonly List<double> _rangeHist = new List<double>();

        // outcome edge detection + cooldowns
        private bool _prevBelowAbsorbed, _prevAboveAbsorbed;
        private DateTime _coolLongUntil = DateTime.MinValue;
        private DateTime _coolShortUntil = DateTime.MinValue;

        public AbsorbController(AbsorbConfig cfg, double tick)
        {
            _cfg = cfg;
            _tick = tick;
            _bp = new BigPrintTracker(cfg.BigLots, TimeSpan.FromSeconds(cfg.BigWindowSeconds));
        }

        // Read-only diagnostics for the cockpit / sig CSV.
        public bool EventGateOpen { get; private set; }
        public long BigNet { get; private set; }
        public int SessionsSeen { get { return _rangeHist.Count; } }

        /// <summary>Feed every Last print with the inside quote at trade time.</summary>
        public void OnTrade(double price, long volume, double bid, double ask, DateTime now)
        {
            _bp.OnTrade(price, volume, bid, ask, now);

            // trading day rolls at 18:00 (shift +6h -> date)
            DateTime tday = now.AddHours(6).Date;
            if (tday != _curTradingDay)
            {
                if (_sessHi > _sessLo)
                {
                    _rangeHist.Add(_sessHi - _sessLo);
                    if (_rangeHist.Count > 40) _rangeHist.RemoveAt(0);
                }
                _curTradingDay = tday;
                _sessHi = double.MinValue;
                _sessLo = double.MaxValue;
            }
            if (price > _sessHi) _sessHi = price;
            if (price < _sessLo) _sessLo = price;

            // 1-min bars for the 15-min sweep extremes
            long minute = now.Ticks / TimeSpan.TicksPerMinute;
            if (minute != _curMinute)
            {
                if (_curMinute != long.MinValue)
                {
                    _h15.Enqueue(_barHi);
                    _l15.Enqueue(_barLo);
                    if (_h15.Count > 15) { _h15.Dequeue(); _l15.Dequeue(); }
                    if (_h15.Count == 15)
                    {
                        double mn = double.MaxValue, mx = double.MinValue;
                        foreach (double v in _h15) mx = Math.Max(mx, v);
                        foreach (double v in _l15) mn = Math.Min(mn, v);
                        _min15 = mn;
                        _max15 = mx;
                    }
                }
                _curMinute = minute;
                _barHi = price;
                _barLo = price;
            }
            else
            {
                if (price > _barHi) _barHi = price;
                if (price < _barLo) _barLo = price;
            }
        }

        private bool ComputeGate()
        {
            if (_sessHi <= _sessLo) return false;
            double range = _sessHi - _sessLo;
            if (_rangeHist.Count >= 10)
            {
                var s = new List<double>(_rangeHist);
                if (s.Count > 20) s.RemoveRange(0, s.Count - 20);
                s.Sort();
                double med = s.Count % 2 == 1 ? s[s.Count / 2]
                    : 0.5 * (s[s.Count / 2 - 1] + s[s.Count / 2]);
                return range >= _cfg.EventGateMult * med;
            }
            return range >= _cfg.MinDayRangeTicks * _tick;
        }

        public ControllerOutput Update(ControllerInputs inp)
        {
            ControllerOutput o = default(ControllerOutput);
            DateTime now = inp.Now;

            bool gate = ComputeGate();
            EventGateOpen = gate;
            long net = _bp.Net(now);
            BigNet = net;

            int mins = now.Hour * 60 + now.Minute;
            bool windowOk = mins >= _cfg.WindowStartMinutes && mins < _cfg.WindowEndMinutes;
            bool ctxOk = gate && windowOk && _h15.Count == 15;
            long armFloor = Math.Max(_cfg.MinWallSize, inp.AdaptiveSignificance);
            double band = _cfg.SweepBandTicks * _tick;

            bool belowAbs = inp.WallBelowOutcomeValid && inp.WallBelowOutcome == Outcome.Absorbed;
            bool aboveAbs = inp.WallAboveOutcomeValid && inp.WallAboveOutcome == Outcome.Absorbed;

            // LONG: bid wall below just resolved Absorbed at a 15-min low, big buyers agreeing
            if (belowAbs && !_prevBelowAbsorbed && ctxOk
                && inp.WallBelowCurrent >= armFloor
                && net > 0
                && inp.WallBelowPrice <= _min15 + band
                && now >= _coolLongUntil)
            {
                o.Fired = true;
                o.Fire = new FireEvent
                {
                    Side = Side.Ask,               // TRADE side: buy
                    WallSide = Side.Bid,
                    WallPrice = inp.WallBelowPrice,
                    EntryHint = inp.WallBelowPrice,
                    Kind = SetupKind.Absorb,
                    Time = now,
                    WallSizeAtFire = inp.WallBelowCurrent,
                    DeltaAtFire = net,
                    TapeAccel = inp.TapeAccel,
                    TapeSpeed = inp.TapeSpeed,
                    ZAtFire = inp.TapeZScore
                };
                _coolLongUntil = now.AddSeconds(_cfg.CooldownSeconds);
            }
            // SHORT: ask wall above absorbed at a 15-min high, big sellers agreeing
            else if (aboveAbs && !_prevAboveAbsorbed && ctxOk
                && inp.WallAboveCurrent >= armFloor
                && net < 0
                && inp.WallAbovePrice >= _max15 - band
                && now >= _coolShortUntil)
            {
                o.Fired = true;
                o.Fire = new FireEvent
                {
                    Side = Side.Bid,               // TRADE side: sell
                    WallSide = Side.Ask,
                    WallPrice = inp.WallAbovePrice,
                    EntryHint = inp.WallAbovePrice,
                    Kind = SetupKind.Absorb,
                    Time = now,
                    WallSizeAtFire = inp.WallAboveCurrent,
                    DeltaAtFire = net,
                    TapeAccel = inp.TapeAccel,
                    TapeSpeed = inp.TapeSpeed,
                    ZAtFire = inp.TapeZScore
                };
                _coolShortUntil = now.AddSeconds(_cfg.CooldownSeconds);
            }
            _prevBelowAbsorbed = belowAbs;
            _prevAboveAbsorbed = aboveAbs;

            // cockpit projection: Armed while the context gates are open, Fired on a fire
            SideState st = ctxOk ? SideState.Armed : SideState.Waiting;
            o.Long = o.Fired && o.Fire.Side == Side.Ask ? SideState.Fired : st;
            o.Short = o.Fired && o.Fire.Side == Side.Bid ? SideState.Fired : st;
            return o;
        }
    }
}
