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
    /// Absorption Scalper — ES Replay-SIM validation.
    /// Reuses the frozen TradingRadar.Engine (compiled together in Custom.dll).
    /// Signal: first edge into NodeState.Absorbed with PeakSize >= MinPeak.
    /// Entry: stop-confirmation one tick beyond the wall (live-until-cancelled).
    /// Bracket: stop 6t risk from entry; TP +8t; BE at +3t.
    /// Time-stop: exit at market if not +3t within TimeSecs (before BE).
    /// Hard exit: if the active wall transitions to Consumed while in trade.
    /// Guardrails: RTH 09:35–16:00, halt after DayMaxLoss stops, max DayMaxTrade entries.
    /// Engine FROZEN — no edits to Engine/.
    /// </summary>
    public class AbsorptionScalper : Strategy
    {
        // ── engine state (only touched by MaybeRunEngine, under _eLk) ────────
        private RadarConfig  _cfg;
        private BookMirror   _book;
        private WallTracker  _tracker;
        private readonly object _eLk = new object();
        private DateTime     _lastRun = DateTime.MinValue;
        private const double RunMs   = 50.0;
        private readonly Dictionary<long, NodeState> _prev = new Dictionary<long, NodeState>();

        // ── cross-thread signal hand-off (volatile — engine → strategy) ──────
        private volatile bool   _pendL;      // long signal pending
        private volatile bool   _pendS;      // short signal pending
        private double _sigPx;      // wall price of pending signal (visible via _pendL/_pendS volatile fence)
        private long   _sigPk;      // PeakSize of pending signal (same fence)
        private volatile bool   _consumed;   // active wall node went Consumed

        // ── position state machine ────────────────────────────────────────────
        // ponytail: int instead of enum so volatile works on all CLR targets
        private volatile int _ph;
        private const int PH_FLAT  = 0;
        private const int PH_PEND  = 1;
        private const int PH_TRADE = 2;

        private Order  _eo;   // entry order (stop-market, live-until-cancelled)
        private Order  _so;   // stop order  (live-until-cancelled)
        private Order  _to;   // target order (live-until-cancelled)
        private Order  _fo;   // force-exit market order (Consumed / TimeStop)
        private double _eFx;  // entry fill price
        private DateTime _eFt = DateTime.MinValue;
        private bool   _beReached;
        private bool   _exitSent;    // ForceExit already dispatched
        private bool   _tradeClosed; // exit execution logged; guard against double-log
        private string _exitReason;
        private double _wallPx;      // wall price that triggered the signal
        private long   _wallPk;      // wall PeakSize at signal time
        private long   _activeKey;   // NK(wallPx, side) — checked for Consumed transition
        private double _mfe;         // max favorable excursion in ticks (updated in OnBarUpdate)
        private double _mae;         // max adverse excursion in ticks (min of pnl, so negatives)
        private DateTime _beTime = DateTime.MinValue;  // when MFE first hit BeTks

        // ── daily guardrails ──────────────────────────────────────────────────
        private const int DayMaxLoss  = 3;
        private const int DayMaxTrade = 12;
        private volatile int  _dLoss;
        private volatile int  _dTrade;
        private volatile bool _halted;
        private DateTime _today = DateTime.MinValue;

        // ── cooldown (one signal per price per 60s) ────────────────────────────
        private DateTime _sigTime  = DateTime.MinValue;
        private double   _coolPx;
        private DateTime _coolTime = DateTime.MinValue;

        // ── CSV trade log ─────────────────────────────────────────────────────
        private System.IO.StreamWriter _log;

        // ── parameters ───────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min Peak Size", Order = 1, GroupName = "AbsScalper")]
        public int MinPeak { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Stop Ticks (risk)", Order = 2, GroupName = "AbsScalper")]
        public int StopTks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Target Ticks", Order = 3, GroupName = "AbsScalper")]
        public int TgtTks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "BE Trigger Ticks", Order = 4, GroupName = "AbsScalper")]
        public int BeTks { get; set; }

        [NinjaScriptProperty]
        [Range(5, 120)]
        [Display(Name = "Time Stop Secs", Order = 5, GroupName = "AbsScalper")]
        public int TimeSecs { get; set; }

        [NinjaScriptProperty]
        [Range(2, 30)]
        [Display(Name = "Entry Window Secs", Order = 6, GroupName = "AbsScalper")]
        public int EntryWin { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                  = "AbsorptionScalper — ES Replay-SIM (engine frozen)";
                Name                         = "AbsorptionScalper";
                Calculate                    = Calculate.OnEachTick;
                BarsRequiredToTrade          = 1;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                TraceOrders                  = false;
                MinPeak   = 100;
                StopTks   = 6;
                TgtTks    = 8;
                BeTks     = 3;
                TimeSecs  = 40;
                EntryWin  = 10;
            }
            else if (State == State.Configure)
            {
                // Match RadarTab config: same engine, same defaults
                _cfg = new RadarConfig
                {
                    K_mult     = 1.5,
                    T_persist  = TimeSpan.FromMilliseconds(1000),
                    MinAbsSize = 20
                    // TickSize set in DataLoaded once Instrument is known
                };
            }
            else if (State == State.DataLoaded)
            {
                _cfg.TickSize = Instrument.MasterInstrument.TickSize;
                _book    = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
                _tracker = new WallTracker(_cfg);
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
            if (BarsInProgress != 0) return; // primary series only

            // ── daily reset ──────────────────────────────────────────────────
            if (Time[0].Date != _today.Date)
            {
                _today  = Time[0];
                _dLoss  = 0;
                _dTrade = 0;
                _halted = false;
            }

            // ── global gates ─────────────────────────────────────────────────
            if (_halted || _dTrade >= DayMaxTrade) return;
            int tod = ToTime(Time[0]);
            if (tod < 093500 || tod >= 160000) return; // RTH 09:35–16:00 ET

            int ph = _ph;

            // ── Flat → try to submit entry ────────────────────────────────────
            if (ph == PH_FLAT && (_pendL || _pendS) && _eo == null)
            {
                double wp = _sigPx; long wk = _sigPk;

                bool expired  = (Time[0] - _sigTime).TotalSeconds > EntryWin;
                bool tooSmall = wk < MinPeak;
                bool cool     = Math.Abs(wp - _coolPx) < _cfg.TickSize * 0.5
                             && (Time[0] - _coolTime).TotalSeconds < 60;
                if (expired || tooSmall || cool) { ClearPend(); return; }

                _wallPx    = wp;
                _wallPk    = wk;
                _activeKey = NK(wp, _pendL ? Side.Bid : Side.Ask);
                _coolPx    = wp;
                _coolTime  = Time[0];

                if (_pendL)
                {
                    double stp = Instrument.MasterInstrument.RoundToTickSize(wp + TickSize);
                    _eo = EnterLongStopMarket(0, true, 1, stp, "AbsLong");
                }
                else
                {
                    double stp = Instrument.MasterInstrument.RoundToTickSize(wp - TickSize);
                    _eo = EnterShortStopMarket(0, true, 1, stp, "AbsShort");
                }
                _ph = PH_PEND;
                ClearPend();
            }
            // ── EntryPending → cancel if window expired ───────────────────────
            else if (ph == PH_PEND && _eo != null && (Time[0] - _sigTime).TotalSeconds > EntryWin)
            {
                CancelOrder(_eo);
                // _eo nulled and _ph reset in OnOrderUpdate when terminal
            }
            // ── InTrade → manage open position ────────────────────────────────
            else if (ph == PH_TRADE && !_exitSent)
            {
                double cur = CurTicks();
                if (cur > _mfe) _mfe = cur;
                if (cur < _mae) _mae = cur;

                // Break-even: move stop to entry-1t when MFE >= BeTks
                if (!_beReached && _mfe >= BeTks)
                {
                    _beReached = true;
                    _beTime    = Time[0];
                    double beStp = Position.MarketPosition == MarketPosition.Long
                        ? _eFx - TickSize
                        : _eFx + TickSize;
                    if (_so != null && !Order.IsTerminalState(_so.OrderState))
                        ChangeOrder(_so, 1, 0, beStp);
                }

                // Hard exit: wall was consumed (invalidation)
                if (_consumed) { ForceExit("Consumed"); return; }

                // Time-stop: exit at market if not at BE within TimeSecs
                if (!_beReached && _eFt != DateTime.MinValue
                    && (Time[0] - _eFt).TotalSeconds >= TimeSecs)
                    ForceExit("TimeStop");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e.Price <= 0) return;
            if (e.IsReset)
            {
                lock (_eLk)
                    _book.ResetFromSnapshot(new List<DepthLevel>(), new List<DepthLevel>());
                return;
            }
            DepthOp op = e.Operation == Operation.Add    ? DepthOp.Add
                       : e.Operation == Operation.Update ? DepthOp.Update
                                                         : DepthOp.Remove;
            lock (_eLk)
                _book.ApplyDepth(new DepthEvent
                {
                    Side     = e.MarketDataType == MarketDataType.Ask ? Side.Ask : Side.Bid,
                    Op       = op,
                    Position = e.Position,
                    Price    = e.Price,
                    Volume   = e.Volume,
                    Time     = e.Time,
                    IsReset  = false
                });
            MaybeRunEngine(e.Time);
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last || e.Price <= 0) return;
            lock (_eLk)
                _book.ApplyTrade(new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time });
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

        private void Scan(IReadOnlyList<RadarNode> snap, DateTime now)
        {
            // Called under _eLk — updates volatile signal flags only.
            for (int i = 0; i < snap.Count; i++)
            {
                RadarNode n   = snap[i];
                long      key = NK(n.Price, n.Side);
                NodeState prev; bool had = _prev.TryGetValue(key, out prev);

                // Signal: first edge into Absorbed with sufficient peak, while flat
                if (n.State == NodeState.Absorbed && n.PeakSize >= MinPeak
                    && (!had || prev != NodeState.Absorbed)
                    && _ph == PH_FLAT && !_pendL && !_pendS)
                {
                    bool cool = Math.Abs(n.Price - _coolPx) < _cfg.TickSize * 0.5
                             && (now - _coolTime).TotalSeconds < 60;
                    if (!cool)
                    {
                        _sigTime = now;
                        _sigPx   = n.Price;
                        _sigPk   = n.PeakSize;
                        if (n.Side == Side.Bid) _pendL = true; else _pendS = true;
                    }
                }

                // Consumed check: invalidation while in a trade on this node
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
            // Only null _eo on non-fill terminal states; OnExecutionUpdate owns the fill path.
            // If we null here on Filled, OnExecutionUpdate sees _eo==null and skips brackets → naked position.
            if (_eo != null && order == _eo && Order.IsTerminalState(orderState)
                && orderState != OrderState.Filled && orderState != OrderState.PartFilled)
            {
                _eo = null;
                if (_ph == PH_PEND) _ph = PH_FLAT; // cancelled / rejected
            }
            if (_so != null && order == _so && Order.IsTerminalState(orderState)) _so = null;
            if (_to != null && order == _to && Order.IsTerminalState(orderState)) _to = null;
            if (_fo != null && order == _fo && Order.IsTerminalState(orderState)) _fo = null;
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            Order o = execution.Order;

            // ── entry filled ──────────────────────────────────────────────────
            if (_eo != null && o == _eo && o.OrderState == OrderState.Filled)
            {
                _eFx = o.AverageFillPrice; _eFt = time;
                _mfe = 0; _mae = 0; _beReached = false;
                _exitSent = false; _tradeClosed = false;
                _exitReason = null; _consumed = false;
                _beTime = DateTime.MinValue;
                _ph = PH_TRADE;
                _dTrade++;

                bool isLong = marketPosition == MarketPosition.Long;
                // Stop: entry = wall±1t, initial stop = wall∓(StopTks-1)t → risk = StopTks ticks
                double stopPx = isLong
                    ? _wallPx - (StopTks - 1) * TickSize
                    : _wallPx + (StopTks - 1) * TickSize;
                double tgtPx  = isLong
                    ? _eFx + TgtTks * TickSize
                    : _eFx - TgtTks * TickSize;

                if (isLong)
                {
                    _so = ExitLongStopMarket(0, true, 1, stopPx, "Stop",   "AbsLong");
                    _to = ExitLongLimit     (0, true, 1, tgtPx,  "Target", "AbsLong");
                }
                else
                {
                    _so = ExitShortStopMarket(0, true, 1, stopPx, "Stop",   "AbsShort");
                    _to = ExitShortLimit     (0, true, 1, tgtPx,  "Target", "AbsShort");
                }
                _eo = null;
                return;
            }

            // ── exit filled ───────────────────────────────────────────────────
            if (_ph != PH_TRADE || _tradeClosed) return;

            bool isStop  = _so != null && o == _so;
            bool isTgt   = _to != null && o == _to;
            bool isForce = _fo != null && o == _fo;
            if (!isStop && !isTgt && !isForce) return;

            if (o.OrderState == OrderState.Filled)
            {
                _tradeClosed = true;
                if (_exitReason == null)
                    _exitReason = isTgt ? "TP" : (isForce ? "ForceExit" : "Stop");

                // marketPosition is the direction of this closing execution:
                //   MarketPosition.Short = selling to close a Long
                //   MarketPosition.Long  = buying to close a Short
                bool wasLong = marketPosition == MarketPosition.Short;
                double exitPx = o.AverageFillPrice;
                double pnl    = wasLong
                    ? (exitPx - _eFx) / TickSize
                    : (_eFx - exitPx) / TickSize;

                LogTrade(time, wasLong ? "Long" : "Short", exitPx, pnl);
                if (pnl < 0) { _dLoss++; if (_dLoss >= DayMaxLoss) _halted = true; }
                ResetTrade();
            }
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private void ForceExit(string reason)
        {
            if (_exitSent) return;
            _exitSent   = true;
            _exitReason = reason; // set before the fill can arrive
            if (_so != null && !Order.IsTerminalState(_so.OrderState)) CancelOrder(_so);
            if (_to != null && !Order.IsTerminalState(_to.OrderState)) CancelOrder(_to);
            bool lng = Position.MarketPosition == MarketPosition.Long;
            _fo = lng
                ? ExitLong (0, 1, "ForceExit", "AbsLong")
                : ExitShort(0, 1, "ForceExit", "AbsShort");
        }

        private double CurTicks()
        {
            if (Position.MarketPosition == MarketPosition.Long)  return (Close[0] - _eFx) / TickSize;
            if (Position.MarketPosition == MarketPosition.Short) return (_eFx - Close[0]) / TickSize;
            return 0;
        }

        private void ClearPend() { _pendL = false; _pendS = false; }

        private void ResetTrade()
        {
            _ph          = PH_FLAT;
            _exitSent    = _tradeClosed = _beReached = _consumed = false;
            _eo = _so = _to = _fo = null;
        }

        // Key = price-index * 2 + side-bit, identical to RadarTab's scheme
        private long NK(double price, Side side)
            => (long)Math.Round(price / _cfg.TickSize) * 2 + (side == Side.Ask ? 1 : 0);

        // ── CSV ──────────────────────────────────────────────────────────────

        private void OpenLog()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "LiquidityRadar");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir,
                    "scalper-trades-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                _log = new System.IO.StreamWriter(path, false);
                _log.WriteLine(
                    "time,side,entryType,wallPrice,fillPrice,peakSize," +
                    "mfeTicks,maeTicks,timeTo3tSecs,exitReason,pnlTicks");
                _log.Flush();
            }
            catch { }
        }

        private void LogTrade(DateTime time, string side, double exitPx, double pnlTicks)
        {
            if (_log == null) return;
            try
            {
                double t3 = _beTime != DateTime.MinValue
                    ? (_beTime - _eFt).TotalSeconds : -1;
                _log.WriteLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0},{1},StopConfirm,{2:0.00},{3:0.00},{4},{5:0.0},{6:0.0},{7:0.0},{8},{9:0.00}",
                    time.ToString("o"), side, _wallPx, exitPx, _wallPk,
                    _mfe, _mae, t3, _exitReason ?? "Unknown", pnlTicks));
                _log.Flush();
            }
            catch { }
        }

        private void CloseLog()
        {
            try { if (_log != null) { _log.Flush(); _log.Dispose(); _log = null; } }
            catch { }
        }
    }
}
