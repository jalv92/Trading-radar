#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using TradingRadar.Engine;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// Absorption EVENT Scalper — the OF3-v2 hypothesis on the radar chassis.
    /// Sibling of AbsorptionScalper (that file is untouched); same frozen
    /// Engine, plus the four OF3-v2 gates found in the 2026-07-20 autopsy:
    ///   1. EVENT-DAY GATE: session range so far >= EventGateMult x the
    ///      median of the last 20 session ranges (fallback MinDayRangeTicks
    ///      until 10 sessions of history exist). Quiet days: no trades.
    ///   2. MORNING WINDOW: entries 09:35-12:00 ET only.
    ///   3. TAPE CONFIRMATION: net BIG-print aggressor volume (>= BigLots,
    ///      classified at bid/ask) over BigWindowSecs must agree with the
    ///      wall side (buyers absorbing a low / sellers capping a high).
    ///   4. SWEEP CONTEXT: the wall must sit at a 15-min extreme
    ///      (within SweepBandTicks of MIN/MAX of the last 15 one-min bars).
    /// Bracket: stop = AtrStopMult x ATR(1m,14); NO profit target; hard
    /// time-stop at TimeoutMin; hard exit if the wall goes Consumed.
    /// STATUS: unvalidated frozen hypothesis (train t=+2.83 came from 5
    /// event days; holdout failed unconditioned). Sim/Playback ONLY — this
    /// strategy exists to grade the hypothesis, not to trade money.
    /// </summary>
    public class AbsorptionEventScalper : Strategy
    {
        // ── engine state (touched under _eLk) ────────────────────────────────
        private RadarConfig _cfg;
        private BookMirror _book;
        private WallTracker _tracker;
        private BigPrintTracker _bp;
        private readonly object _eLk = new object();
        private DateTime _lastRun = DateTime.MinValue;
        private const double RunMs = 50.0;
        private readonly Dictionary<long, NodeState> _prev = new Dictionary<long, NodeState>();
        private double _lastBid, _lastAsk;

        // ── cross-thread signal hand-off ─────────────────────────────────────
        private volatile bool _pendL;
        private volatile bool _pendS;
        private double _sigPx;
        private long _sigPk;
        private double _sigAtr;          // ATR in points captured at signal
        private volatile bool _consumed;

        // ── minute-series context (written on BarsInProgress==1) ─────────────
        private double _min15 = double.MaxValue;
        private double _max15 = double.MinValue;
        private double _atrPts;                 // Wilder ATR(14) on 1-min bars, in points
        private int _atrN;
        private double _prevClose;
        private readonly Queue<double> _h15 = new Queue<double>();
        private readonly Queue<double> _l15 = new Queue<double>();
        private double _sessHigh = double.MinValue;
        private double _sessLow = double.MaxValue;
        private readonly List<double> _rangeHist = new List<double>();  // finished session ranges (points)

        // ── position state machine ───────────────────────────────────────────
        private volatile int _ph;
        private const int PH_FLAT = 0;
        private const int PH_PEND = 1;
        private const int PH_TRADE = 2;

        private Order _eo, _so, _fo;
        private double _eFx;
        private DateTime _eFt = DateTime.MinValue;
        private double _mfe, _mae;
        private bool _beMoved;
        private bool _exitSent;
        private bool _tradeClosed;
        private string _exitReason;
        private double _wallPx;
        private long _wallPk;
        private long _activeKey;
        private double _stopPts;         // stop distance in points for this trade

        // ── daily guardrails ─────────────────────────────────────────────────
        private const int DayMaxLoss = 2;
        private const int DayMaxTrade = 4;   // event strategy: few, bigger swings
        private const int MaxChase = 3;
        private volatile int _dLoss;
        private volatile int _dTrade;
        private volatile bool _halted;
        private DateTime _today = DateTime.MinValue;

        // ── cooldown ─────────────────────────────────────────────────────────
        private DateTime _sigTime = DateTime.MinValue;
        private double _coolPx;
        private DateTime _coolTime = DateTime.MinValue;

        private System.IO.StreamWriter _log;

        // ── parameters ───────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min Peak Size (wall)", Order = 1, GroupName = "AbsEvent")]
        public int MinPeak { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Big Print Lots", Order = 2, GroupName = "AbsEvent")]
        public int BigLots { get; set; }

        [NinjaScriptProperty]
        [Range(30, 1800)]
        [Display(Name = "Big Print Window Secs", Order = 3, GroupName = "AbsEvent")]
        public int BigWindowSecs { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Sweep Band Ticks", Order = 4, GroupName = "AbsEvent")]
        public int SweepBandTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "Event Gate Mult (x median20)", Order = 5, GroupName = "AbsEvent")]
        public double EventGateMult { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Min Day Range Ticks (fallback)", Order = 6, GroupName = "AbsEvent")]
        public int MinDayRangeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "ATR Stop Mult", Order = 7, GroupName = "AbsEvent")]
        public double AtrStopMult { get; set; }

        [NinjaScriptProperty]
        [Range(5, 120)]
        [Display(Name = "Timeout Minutes", Order = 8, GroupName = "AbsEvent")]
        public int TimeoutMin { get; set; }

        [NinjaScriptProperty]
        [Range(2, 60)]
        [Display(Name = "Entry Window Secs", Order = 9, GroupName = "AbsEvent")]
        public int EntryWin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Breakeven (at +1 ATR)", Order = 10, GroupName = "AbsEvent")]
        public bool UseBreakeven { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "AbsorptionEventScalper — OF3-v2 event-day absorption (Sim/Playback)";
                Name = "AbsorptionEventScalper";
                Calculate = Calculate.OnEachTick;
                BarsRequiredToTrade = 1;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                TraceOrders = false;
                MinPeak = 100;
                BigLots = 10;
                BigWindowSecs = 300;
                SweepBandTicks = 8;
                EventGateMult = 1.5;
                MinDayRangeTicks = 320;   // 80 NQ points — manual fallback
                AtrStopMult = 2.0;
                TimeoutMin = 30;
                EntryWin = 15;
                UseBreakeven = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 1);
                _cfg = new RadarConfig
                {
                    K_mult = 1.5,
                    T_persist = TimeSpan.FromMilliseconds(1000),
                    MinAbsSize = 20
                };
            }
            else if (State == State.DataLoaded)
            {
                _cfg.TickSize = Instrument.MasterInstrument.TickSize;
                _book = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
                _tracker = new WallTracker(_cfg);
                _bp = new BigPrintTracker(BigLots, TimeSpan.FromSeconds(BigWindowSecs));
                OpenLog();
            }
            else if (State == State.Terminated)
            {
                CloseLog();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        protected override void OnBarUpdate()
        {
            // ── minute series: context refresh (once per COMPLETED 1-min bar) ─
            if (BarsInProgress == 1)
            {
                if (!IsFirstTickOfBar || CurrentBars[1] < 1) return;
                double h = Highs[1][1], l = Lows[1][1], c = Closes[1][1];

                if (Bars.IsFirstBarOfSession)
                {
                    if (_sessHigh > _sessLow)
                    {
                        _rangeHist.Add(_sessHigh - _sessLow);
                        if (_rangeHist.Count > 40) _rangeHist.RemoveAt(0);
                    }
                    _sessHigh = double.MinValue;
                    _sessLow = double.MaxValue;
                }
                _sessHigh = Math.Max(_sessHigh, h);
                _sessLow = Math.Min(_sessLow, l);

                _h15.Enqueue(h);
                _l15.Enqueue(l);
                if (_h15.Count > 15) { _h15.Dequeue(); _l15.Dequeue(); }
                if (_h15.Count == 15)
                {
                    double mn = double.MaxValue, mx = double.MinValue;
                    foreach (double v in _h15) mx = Math.Max(mx, v);
                    foreach (double v in _l15) mn = Math.Min(mn, v);
                    _min15 = mn;
                    _max15 = mx;
                }

                // Wilder ATR(14), hand-rolled (no generated indicator wrappers
                // outside the NT8 editor)
                double pc = _atrN == 0 ? c : _prevClose;
                double tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
                if (_atrN < 14) { _atrPts = (_atrPts * _atrN + tr) / (_atrN + 1); _atrN++; }
                else _atrPts = (_atrPts * 13.0 + tr) / 14.0;
                _prevClose = c;
                return;
            }
            if (BarsInProgress != 0) return;

            // ── daily reset ──────────────────────────────────────────────────
            if (Time[0].Date != _today.Date)
            {
                _today = Time[0];
                _dLoss = 0;
                _dTrade = 0;
                _halted = false;
            }

            // ── dead-man's switch: never leave a naked position ──────────────
            if (_ph == PH_TRADE && !_exitSent && !_tradeClosed
                && Position.MarketPosition != MarketPosition.Flat)
            {
                bool dmsLong = Position.MarketPosition == MarketPosition.Long;
                if (_so == null || Order.IsTerminalState(_so.OrderState))
                {
                    double stopPx = dmsLong ? _eFx - _stopPts : _eFx + _stopPts;
                    stopPx = Instrument.MasterInstrument.RoundToTickSize(stopPx);
                    bool breached = dmsLong ? (stopPx >= GetCurrentBid(0))
                                           : (stopPx <= GetCurrentAsk(0));
                    if (breached)
                        ForceExit("StopBreached");
                    else
                        _so = dmsLong
                            ? ExitLongStopMarket(0, true, Position.Quantity, stopPx, "Stop", "AbsEvL")
                            : ExitShortStopMarket(0, true, Position.Quantity, stopPx, "Stop", "AbsEvS");
                }
            }

            // ── global gates ─────────────────────────────────────────────────
            if (_halted || _dTrade >= DayMaxTrade) return;
            int tod = ToTime(Time[0]);
            if (tod < 093500 || tod >= 120000) return;   // OF3-v2 morning window

            int ph = _ph;

            // ── Flat → submit entry (wall stop-confirmation) ─────────────────
            if (ph == PH_FLAT && (_pendL || _pendS) && _eo == null)
            {
                double wp = _sigPx; long wk = _sigPk;

                bool expired = (Time[0] - _sigTime).TotalSeconds > EntryWin;
                bool tooSmall = wk < MinPeak;
                bool cool = Math.Abs(wp - _coolPx) < _cfg.TickSize * 0.5
                         && (Time[0] - _coolTime).TotalSeconds < 60;
                if (expired || tooSmall || cool || _sigAtr <= 0) { ClearPend(); return; }

                _wallPx = wp;
                _wallPk = wk;
                _stopPts = AtrStopMult * _sigAtr;
                _activeKey = NK(wp, _pendL ? Side.Bid : Side.Ask);
                _coolPx = wp;
                _coolTime = Time[0];

                double ask = GetCurrentAsk(0), bid = GetCurrentBid(0);
                if (_pendL)
                {
                    double trig = Math.Max(wp + TickSize, ask + TickSize);
                    if (trig - wp > MaxChase * TickSize) { ClearPend(); _ph = PH_FLAT; return; }
                    _eo = EnterLongStopMarket(0, true, 1,
                        Instrument.MasterInstrument.RoundToTickSize(trig), "AbsEvL");
                }
                else
                {
                    double trig = Math.Min(wp - TickSize, bid - TickSize);
                    if (wp - trig > MaxChase * TickSize) { ClearPend(); _ph = PH_FLAT; return; }
                    _eo = EnterShortStopMarket(0, true, 1,
                        Instrument.MasterInstrument.RoundToTickSize(trig), "AbsEvS");
                }
                _ph = PH_PEND;
                ClearPend();
            }
            else if (ph == PH_PEND && _eo != null
                     && (Time[0] - _sigTime).TotalSeconds > EntryWin)
            {
                CancelOrder(_eo);
            }
            else if (ph == PH_TRADE && !_exitSent)
            {
                double cur = CurTicks();
                if (cur > _mfe) _mfe = cur;
                if (cur < _mae) _mae = cur;

                double beTicks = _sigAtr / TickSize;   // BE trigger = +1 ATR
                if (UseBreakeven && !_beMoved && _mfe >= beTicks)
                {
                    _beMoved = true;
                    double beStp = Position.MarketPosition == MarketPosition.Long
                        ? _eFx - TickSize
                        : _eFx + TickSize;
                    if (_so != null && !Order.IsTerminalState(_so.OrderState))
                        ChangeOrder(_so, 1, 0, Instrument.MasterInstrument.RoundToTickSize(beStp));
                }

                if (_consumed) { ForceExit("Consumed"); return; }

                // OF3-v2 frozen spec: unconditional timeout exit
                if (_eFt != DateTime.MinValue
                    && (Time[0] - _eFt).TotalMinutes >= TimeoutMin)
                    ForceExit("Timeout");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e.IsReset)
            {
                lock (_eLk)
                    _book.ResetFromSnapshot(new List<DepthLevel>(), new List<DepthLevel>());
                return;
            }
            if (e.Price <= 0) return;
            DepthOp op = e.Operation == Operation.Add ? DepthOp.Add
                       : e.Operation == Operation.Update ? DepthOp.Update
                                                         : DepthOp.Remove;
            lock (_eLk)
                _book.ApplyDepth(new DepthEvent
                {
                    Side = e.MarketDataType == MarketDataType.Ask ? Side.Ask : Side.Bid,
                    Op = op,
                    Position = e.Position,
                    Price = e.Price,
                    Volume = e.Volume,
                    Time = e.Time,
                    IsReset = false
                });
            MaybeRunEngine(e.Time);
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.Price <= 0) return;
            if (e.MarketDataType == MarketDataType.Bid) { _lastBid = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Ask) { _lastAsk = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;
            lock (_eLk)
            {
                _book.ApplyTrade(new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time });
                _bp.OnTrade(e.Price, e.Volume, _lastBid, _lastAsk, e.Time);
            }
            MaybeRunEngine(e.Time);
        }

        private void MaybeRunEngine(DateTime now)
        {
            lock (_eLk)
            {
                if (_lastRun != DateTime.MinValue && (now - _lastRun).TotalMilliseconds < RunMs) return;
                _lastRun = now;
                _tracker.Update(_book, now);
                Scan(_tracker.GetSnapshot(now), now);
            }
        }

        private bool EventGateOpen()
        {
            if (_sessHigh <= _sessLow) return false;
            double rangeSoFar = _sessHigh - _sessLow;
            if (_rangeHist.Count >= 10)
            {
                var s = new List<double>(_rangeHist);
                if (s.Count > 20) s.RemoveRange(0, s.Count - 20);
                s.Sort();
                double med = s.Count % 2 == 1 ? s[s.Count / 2]
                    : 0.5 * (s[s.Count / 2 - 1] + s[s.Count / 2]);
                return rangeSoFar >= EventGateMult * med;
            }
            return rangeSoFar >= MinDayRangeTicks * _cfg.TickSize;
        }

        private void Scan(IReadOnlyList<RadarNode> snap, DateTime now)
        {
            // Called under _eLk — updates volatile signal flags only.
            long bigNet = _bp.Net(now);
            bool gate = EventGateOpen();
            double min15 = _min15, max15 = _max15, atr = _atrPts;
            double band = SweepBandTicks * _cfg.TickSize;

            for (int i = 0; i < snap.Count; i++)
            {
                RadarNode n = snap[i];
                long key = NK(n.Price, n.Side);
                NodeState prev; bool had = _prev.TryGetValue(key, out prev);

                if (n.State == NodeState.Absorbed && n.PeakSize >= MinPeak
                    && (!had || prev != NodeState.Absorbed)
                    && _ph == PH_FLAT && !_pendL && !_pendS
                    && gate && atr > 0 && min15 < double.MaxValue)
                {
                    // OF3-v2 gates: tape agreement + sweep-extreme context
                    bool okLong = n.Side == Side.Bid && bigNet > 0
                                  && n.Price <= min15 + band;
                    bool okShort = n.Side == Side.Ask && bigNet < 0
                                   && n.Price >= max15 - band;
                    if (okLong || okShort)
                    {
                        bool cool = Math.Abs(n.Price - _coolPx) < _cfg.TickSize * 0.5
                                 && (now - _coolTime).TotalSeconds < 60;
                        if (!cool)
                        {
                            _sigTime = now;
                            _sigPx = n.Price;
                            _sigPk = n.PeakSize;
                            _sigAtr = atr;
                            if (okLong) _pendL = true; else _pendS = true;
                        }
                    }
                }

                if (_ph == PH_TRADE && key == _activeKey && n.State == NodeState.Consumed)
                    _consumed = true;

                _prev[key] = n.State;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice,
            OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (_eo != null && order == _eo && Order.IsTerminalState(orderState)
                && orderState != OrderState.Filled && orderState != OrderState.PartFilled)
            {
                _eo = null;
                if (_ph == PH_PEND) _ph = PH_FLAT;
            }
            if (_so != null && order == _so && Order.IsTerminalState(orderState)) _so = null;
            if (_fo != null && order == _fo && Order.IsTerminalState(orderState)) _fo = null;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            Order o = execution.Order;

            if (_eo != null && o == _eo
                && (o.OrderState == OrderState.Filled || o.OrderState == OrderState.PartFilled))
            {
                _eFx = o.AverageFillPrice; _eFt = time;
                _mfe = 0; _mae = 0; _beMoved = false;
                _exitSent = false; _tradeClosed = false;
                _exitReason = null; _consumed = false;
                _ph = PH_TRADE;
                _dTrade++;

                bool isLong = marketPosition == MarketPosition.Long;
                double stopPx = isLong ? _eFx - _stopPts : _eFx + _stopPts;
                stopPx = Instrument.MasterInstrument.RoundToTickSize(stopPx);
                _so = isLong
                    ? ExitLongStopMarket(0, true, Position.Quantity, stopPx, "Stop", "AbsEvL")
                    : ExitShortStopMarket(0, true, Position.Quantity, stopPx, "Stop", "AbsEvS");

                if (o.OrderState == OrderState.Filled) _eo = null;
                return;
            }

            // ── closing fill ─────────────────────────────────────────────────
            if (_ph == PH_TRADE && !_tradeClosed
                && Position.MarketPosition == MarketPosition.Flat)
            {
                _tradeClosed = true;
                string sig = o != null ? o.FromEntrySignal : "";
                double sgn = sig == "AbsEvS" ? -1.0 : 1.0;
                double pnlPts = sgn * (price - _eFx);
                if (pnlPts < 0) _dLoss++;
                if (_dLoss >= DayMaxLoss) _halted = true;
                Log(string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3:0.00},{4:0.00},{5:0.00},{6:0.0},{7:0.0},{8}",
                    time, sig, _exitReason ?? "Stop", _eFx, price, pnlPts,
                    _mfe, _mae, _wallPk));
                _ph = PH_FLAT;
                _eFt = DateTime.MinValue;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private void ForceExit(string reason)
        {
            if (_exitSent) return;
            _exitSent = true;
            _exitReason = reason;
            if (_so != null && !Order.IsTerminalState(_so.OrderState)) CancelOrder(_so);
            if (Position.MarketPosition == MarketPosition.Long)
                _fo = ExitLong(0, Position.Quantity, "Force", "AbsEvL");
            else if (Position.MarketPosition == MarketPosition.Short)
                _fo = ExitShort(0, Position.Quantity, "Force", "AbsEvS");
        }

        private double CurTicks()
        {
            if (Position.MarketPosition == MarketPosition.Long)
                return (GetCurrentBid(0) - _eFx) / TickSize;
            if (Position.MarketPosition == MarketPosition.Short)
                return (_eFx - GetCurrentAsk(0)) / TickSize;
            return 0;
        }

        private void ClearPend() { _pendL = false; _pendS = false; }

        private long NK(double price, Side side)
        {
            return ((long)Math.Round(price / _cfg.TickSize) << 1) | (side == Side.Ask ? 1L : 0L);
        }

        private void OpenLog()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "TradingRadar");
                System.IO.Directory.CreateDirectory(dir);
                bool fresh = !System.IO.File.Exists(System.IO.Path.Combine(dir, "abs-event-trades.csv"));
                _log = new System.IO.StreamWriter(
                    System.IO.Path.Combine(dir, "abs-event-trades.csv"), true);
                if (fresh)
                    _log.WriteLine("time,signal,exit_reason,entry,exit,pnl_pts,mfe_t,mae_t,wall_peak");
                _log.Flush();
            }
            catch { _log = null; }
        }

        private void Log(string line)
        {
            try { if (_log != null) { _log.WriteLine(line); _log.Flush(); } }
            catch { }
        }

        private void CloseLog()
        {
            try { if (_log != null) { _log.Dispose(); _log = null; } }
            catch { }
        }
    }
}
