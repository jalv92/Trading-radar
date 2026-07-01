using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    public class RadarTab : NTTabPage, NinjaTrader.Gui.Tools.IInstrumentProvider
    {
        // ---- Task 4: frame bundle for the cross-thread handoff ----
        private sealed class Frame
        {
            public IReadOnlyList<RadarNode>  Nodes;
            public IReadOnlyList<DepthLevel> Bids;
            public IReadOnlyList<DepthLevel> Asks;
            public double Mid;
            public double Tick;
            public double WallAbove;   // price of the biggest wall above mid this run (0 = none found)
            public double WallBelow;   // price of the biggest wall below mid this run (0 = none found)
            // Task 10: Controller spine + tape-speed reads, rendered by CockpitVisual (Task 11) and
            // delivered to RadarChartTrader (Task 12).
            public ControllerOutput Ctrl;
            public bool Fired;
            public FireEvent Fire;
            public double BuyPerSec;
            public double SellPerSec;
            public double TapeZ;
            // Task 11: vote-less book-skew context (PressureModel.BookSkewContext) — the demoted
            // successor of the old Net% meter, rendered as a thin reference strip, never a trigger.
            public double BookSkew;
        }

        private readonly RadarConfig _cfg = new RadarConfig();   // NQ defaults; later: per-instrument presets
        private BookMirror  _book;
        private WallTracker _tracker;
        private TapeSpeed   _tape;
        private ControllerStateMachine _controller;
        // Previous engine run's ControllerOutput — read (never written) BEFORE calling _controller.Update()
        // this run, to decide the ARMED-WALL IDENTITY CONTRACT feed below. Default(Waiting/Waiting) on the
        // very first run correctly falls through to "feed the freshly computed dominant wall".
        private ControllerOutput _lastCtrl;
        private RadarVisual _visual;
        private CockpitVisual _cockpit;
        private RadarChartTrader _chartTrader;
        private PressureModel _pressure = new PressureModel(new PressureConfig());
        private InstrumentSelector _selector;

        // Serializes the engine-state swap (instrument switch / replay reset, done from the UI or
        // instrument thread) against the instrument-thread depth/trade handlers. Monitor is reentrant, so
        // HandleReplayReset -> SeedFromSnapshot re-entering under the same lock is fine.
        private readonly object _engineLock = new object();
        private volatile Instrument _instrument;   // volatile: read in the handler guard on the instrument thread
        private volatile Frame _latest;       // immutable snapshot + mid + tick, swapped from instrument thread
        private volatile bool _replayResetPending;  // set on instrument thread by HandleReplayReset; consumed on the UI paint tick
        private DispatcherTimer _paintTimer;
        private bool _subscribed;
        private int      _depthEvents;
        private int      _tradeEvents;
        private DateTime _lastDiag      = DateTime.MinValue;
        private DateTime _lastEngineRun = DateTime.MinValue;
        private const double EngineIntervalMs = 50;   // run engine+snapshot at most ~20Hz
        private const double ReplayResetBackwardMs = 2000;   // Playback rewind trip: real rewinds jump seconds-minutes; feed jitter is a few ms
        private const double ReplayResetForwardMs  = 60000;  // Playback scrub-ahead / session-rollover trip: bigger than any real quiet-market gap
        private volatile bool _autoCalib;
        private double _medianEwma;
        private double _autoFactor = 1.8;
        private const double EwmaAlpha = 0.0017;      // ~60s smoothing at ~20Hz engine runs
        private TextBox  _minSizeBox;
        private CheckBox _autoChk;
        private volatile bool _capture;
        private System.IO.StreamWriter _capWriter;
        private System.IO.StreamWriter _sigWriter;
        private readonly Dictionary<long, NodeState> _prevStates = new Dictionary<long, NodeState>();
        private DateTime _lastMidLog = DateTime.MinValue;

        public RadarTab()
        {
            _cfg.MinAbsSize  = 20;                                   // auto-overrides immediately when _autoCalib is true
            _cfg.K_mult      = 1.5;
            _cfg.T_persist   = TimeSpan.FromMilliseconds(1000);
            _autoCalib       = true;
            _book    = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
            _tracker = new WallTracker(_cfg);
            _tape       = new TapeSpeed(0.1);
            _controller = new ControllerStateMachine(new ControllerConfig(), _cfg.TickSize);
            _visual  = new RadarVisual();
            _cockpit = new CockpitVisual();

            _selector = new InstrumentSelector();
            _selector.InstrumentChanged += OnSelectorChanged;

            DockPanel root = new DockPanel();
            // Top bar: instrument selector + live threshold knobs (edit _cfg directly; engine reads live).
            StackPanel topBar = new StackPanel { Orientation = Orientation.Horizontal,
                Background = new SolidColorBrush(Color.FromRgb(0x0f, 0x14, 0x20)) };
            topBar.Children.Add(_selector);
            // MinSize input — keep ref for Auto mode to update/dim it.
            {
                var lbl = new TextBlock { Text = "MinSize:",
                    Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0xa4, 0xb2)) };
                _minSizeBox = new TextBox { Text = _cfg.MinAbsSize.ToString(), Width = 50,
                    Background  = new SolidColorBrush(Color.FromRgb(0x0f, 0x14, 0x20)),
                    Foreground  = new SolidColorBrush(Color.FromRgb(0xcf, 0xd6, 0xe2)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0xff, 0xff, 0xff)),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(3, 1, 3, 1) };
                Action commitMin = () => { if (double.TryParse(_minSizeBox.Text, out double v)) _cfg.MinAbsSize = (long)v; };
                _minSizeBox.LostFocus += (o, e) => commitMin();
                _minSizeBox.KeyDown   += (o, e) => { if (e.Key == Key.Enter) commitMin(); };
                var sp = new StackPanel { Orientation = Orientation.Horizontal,
                    Margin = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(lbl); sp.Children.Add(_minSizeBox);
                topBar.Children.Add(sp);
            }
            // Auto-calibration toggle + factor multiplier.
            _autoChk = new CheckBox { Content = "Auto",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 2, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xcf, 0xd6, 0xe2)),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11 };
            _autoChk.Checked   += (o, e) => _autoCalib = true;
            _autoChk.Unchecked += (o, e) => _autoCalib = false;
            _autoChk.IsChecked  = true;
            topBar.Children.Add(_autoChk);
            topBar.Children.Add(MakeCfgInput("×", _autoFactor.ToString("0.#"),
                v => _autoFactor = v));
            topBar.Children.Add(MakeCfgInput("K×", _cfg.K_mult.ToString("0.#"),
                v => _cfg.K_mult = v));
            topBar.Children.Add(MakeCfgInput("Persist(ms)",
                ((long)_cfg.T_persist.TotalMilliseconds).ToString(),
                v => _cfg.T_persist = TimeSpan.FromMilliseconds(v)));
            // Rec toggle — CSV capture for ES calibration (default unchecked).
            var recChk = new CheckBox { Content = "Rec",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 4, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xcf, 0xd6, 0xe2)),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11 };
            recChk.Checked += (o, e) =>
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "LiquidityRadar");
                System.IO.Directory.CreateDirectory(dir);
                string inst = _instrument != null ? _instrument.MasterInstrument.Name : "X";
                string path = System.IO.Path.Combine(dir,
                    "lr-capture-" + inst + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                _capWriter = new System.IO.StreamWriter(path, false);
                _capWriter.WriteLine("time,type,side,price,peak,last,prevState,newState,conf,inWindow,age,mid,medBid,medAsk");
                _capWriter.Flush();
                _prevStates.Clear();
                string sigPath = System.IO.Path.Combine(dir,
                    "lr-signals-" + inst + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                _sigWriter = new System.IO.StreamWriter(sigPath, false);
                _sigWriter.WriteLine("time,mid,bidMass,askMass,bestBid,bestAsk,delta15s,wallFrac,wallAbove,wallPx," +
                    "wallAbovePx,wallAboveCur,wallBelowPx,wallBelowCur,consumeFracLong,tradeBackedLong," +
                    "consumeFracShort,tradeBackedShort,printsPerSec,buyVolPerSec,sellVolPerSec,tapeZ,ctrlLong,ctrlShort," +
                    "ctrlWallAbovePx,ctrlWallAboveSz,ctrlWallBelowPx,ctrlWallBelowSz");
                _sigWriter.Flush();
                _capture = true;
            };
            recChk.Unchecked += (o, e) =>
            {
                _capture = false;
                if (_capWriter != null) { _capWriter.Flush(); _capWriter.Dispose(); _capWriter = null; }
                if (_sigWriter != null) { _sigWriter.Flush(); _sigWriter.Dispose(); _sigWriter = null; }
            };
            topBar.Children.Add(recChk);
            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);
            // Right column: Cockpit fills, Chart Trader docked at the bottom (spec §8).
            _chartTrader = new RadarChartTrader();
            Grid rightCol = new Grid();
            rightCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            rightCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(340) });
            Grid.SetRow(_cockpit, 0);     rightCol.Children.Add(_cockpit);
            Grid.SetRow(_chartTrader, 1); rightCol.Children.Add(_chartTrader);

            Grid split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            Grid.SetColumn(_visual, 0); split.Children.Add(_visual);
            Grid.SetColumn(rightCol, 1); split.Children.Add(rightCol);
            root.Children.Add(split);            // fills remaining space (DockPanel LastChildFill)
            Content = root;

            // UI-thread paint/animation clock — independent of data arrival.
            _paintTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _paintTimer.Tick += (o, e) =>
            {
                if (_replayResetPending)   // post-replay-reset (UI thread)
                {
                    _replayResetPending = false;
                    _visual.ResetLadderMemory();
                    if (_chartTrader != null) _chartTrader.OnReplayReset();   // drop a phantom limit-order marker if the Playback account was reset
                }
                Frame f = _latest;          // volatile read of the latest immutable frame
                if (f != null)
                {
                    _visual.SetFrame(f.Nodes, f.Bids, f.Asks, f.Mid, f.Tick);
                    _cockpit.SetFrame(f.Ctrl, f.BuyPerSec, f.SellPerSec, f.TapeZ, f.BookSkew);
                    // reuse the already-marshaled book mid + biggest-wall prices for live PnL + LMT anchoring
                    _chartTrader.SetContext(f.Mid, f.WallAbove, f.WallBelow, f.Tick);
                    // ControllerOutput.Fired is one-shot by engine contract (see ControllerStateMachine),
                    // so this delivers exactly once per fire even though the paint tick re-reads f.Fired
                    // every ~33ms — the next Frame swap carries Fired=false again.
                    if (f.Fired) _chartTrader.OnSetupFire(f.Fire);
                }
                // Push the Chart Trader's active working limit order onto the ladder as an overlay marker.
                double ordPx; bool ordBuy; int ordQty;
                bool hasOrd = _chartTrader.TryGetActiveOrder(out ordPx, out ordBuy, out ordQty);
                _visual.SetActiveOrder(hasOrd, ordPx, ordBuy, ordQty);
                _visual.AdvanceAnimation(); // sweep/fade clock
                if (_minSizeBox != null)
                {
                    if (_autoCalib)
                    {
                        _minSizeBox.IsReadOnly = true;
                        _minSizeBox.Foreground = new SolidColorBrush(Color.FromRgb(0x8a, 0x93, 0xa3));
                        string t = _cfg.MinAbsSize.ToString();
                        if (_minSizeBox.Text != t) _minSizeBox.Text = t;
                    }
                    else
                    {
                        _minSizeBox.IsReadOnly = false;
                        _minSizeBox.Foreground = new SolidColorBrush(Color.FromRgb(0xcf, 0xd6, 0xe2));
                    }
                }
            };
            _paintTimer.Start();
        }

        private void OnSelectorChanged(object sender, EventArgs e)
        {
            Instrument = _selector.Instrument;
        }

        // IInstrumentProvider — re-subscribe on instrument change (link-aware).
        public Instrument Instrument
        {
            get { return _instrument; }
            set
            {
                if (_instrument == value) return;
                Unsubscribe();
                // Swap the whole engine state atomically w.r.t. the instrument-thread handlers: a late
                // depth/trade event from the OLD instrument (still queued on its thread) is dropped by the
                // e.Instrument guard, and can never apply to the NEW book mid-swap.
                lock (_engineLock)
                {
                    _instrument = value;
                    if (_instrument != null)
                        _cfg.TickSize = _instrument.MasterInstrument.TickSize;
                    // Rebuild engine for the new instrument's tick size. _tape/_controller carry EWMA
                    // baseline + state-machine memory keyed to the OLD instrument/tick — must be rebuilt
                    // alongside _book/_tracker, or a switch leaks a stale Armed/Countdown into the new one.
                    _book       = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
                    _tracker    = new WallTracker(_cfg);
                    _tape       = new TapeSpeed(0.1);
                    _controller = new ControllerStateMachine(new ControllerConfig(), _cfg.TickSize);
                    _lastCtrl   = default(ControllerOutput);
                    _latest  = null;
                    _lastEngineRun = DateTime.MinValue;   // don't carry the old instrument's engine clock
                    _medianEwma    = 0;                   // (a stale high-water would spuriously reset/throttle)
                }
                if (_chartTrader != null) _chartTrader.Instrument = value;
                Subscribe();
                RefreshHeader();
            }
        }

        private void Subscribe()
        {
            if (_instrument == null || _subscribed) return;
            Instrument inst = _instrument;
            if (!inst.Dispatcher.HasShutdownStarted)
            {
                inst.Dispatcher.InvokeAsync(() =>
                {
                    inst.MarketDepth.Update += OnMarketDepth;
                    inst.MarketData.Update  += OnMarketData;
                    // Guard identity too: a rapid A→B→C switch can leave this action (queued on B's own
                    // dispatcher thread) running AFTER _book was already swapped to C, seeding C's book with
                    // B's levels. SeedFromSnapshot reads the _book field at exec time, so only seed when
                    // `inst` is still the current instrument — the same guard the depth/trade handlers use.
                    lock (_engineLock) { if (inst == _instrument) SeedFromSnapshot(inst); }   // prime the book under the engine lock
                });
                _subscribed = true;
            }
        }

        private void Unsubscribe()
        {
            if (_instrument == null || !_subscribed) return;
            Instrument inst = _instrument;
            if (!inst.Dispatcher.HasShutdownStarted)
                inst.Dispatcher.InvokeAsync(() =>
                {
                    inst.MarketDepth.Update -= OnMarketDepth;
                    inst.MarketData.Update  -= OnMarketData;
                });
            _subscribed = false;
        }

        // Build the initial ladder from the snapshot (instrument thread).
        private void SeedFromSnapshot(Instrument inst)
        {
            List<DepthLevel> bids = new List<DepthLevel>();
            List<DepthLevel> asks = new List<DepthLevel>();
            for (int i = 0; i < inst.MarketDepth.Bids.Count; i++)
                bids.Add(new DepthLevel { Price = inst.MarketDepth.Bids[i].Price, Volume = inst.MarketDepth.Bids[i].Volume });
            for (int i = 0; i < inst.MarketDepth.Asks.Count; i++)
                asks.Add(new DepthLevel { Price = inst.MarketDepth.Asks[i].Price, Volume = inst.MarketDepth.Asks[i].Volume });
            _book.ResetFromSnapshot(bids, asks);
        }

        // Helper: mid from current book state (instrument thread only).
        private double MidOf()
        {
            DepthLevel bb, ba;
            bool hb = _book.TryBestBid(out bb)  && bb.Price > 0;
            bool ha = _book.TryBestAsk(out ba)  && ba.Price > 0;
            if (hb && ha) return (bb.Price + ba.Price) / 2.0;
            if (hb) return bb.Price;
            if (ha) return ba.Price;
            return 0;
        }

        // Live resting size at a specific price, by identity (0 if that level is gone). Backs the
        // ARMED-WALL IDENTITY CONTRACT in MaybeRunEngine: an armed candidate must keep seeing ITS wall's
        // size, not whichever wall happens to be biggest this run once its own wall is partway eaten.
        private long SizeAtPrice(IReadOnlyList<RadarNode> nodes, Side side, double price)
        {
            if (price <= 0) return 0;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].InWindow && nodes[i].Side == side && Math.Abs(nodes[i].Price - price) < _cfg.TickSize / 2.0)
                    return nodes[i].LastKnownSize;
            return 0;
        }

        // Shared by the Long/Ask and Short/Bid identity-contract feeds below — mirrors
        // ControllerStateMachine's own Side-parameterized StepLong/StepShort pairing instead of
        // duplicating the branch per side.
        private void ResolveWallFeed(SideState armedState, double armedPrice, Side wallSide,
            double domPx, long domSz, IReadOnlyList<RadarNode> nodes, out double px, out long sz)
        {
            if (armedState == SideState.Armed || armedState == SideState.Countdown)
            {
                px = armedPrice;
                sz = SizeAtPrice(nodes, wallSide, armedPrice);
            }
            else { px = domPx; sz = domSz; }
        }

        // ---- instrument-thread handlers: map -> engine -> swap frame ----
        private void OnMarketDepth(object sender, MarketDepthEventArgs e)
        {
            if (e.Price <= 0) return;
            DepthOp op = e.Operation == Operation.Add    ? DepthOp.Add
                       : e.Operation == Operation.Update ? DepthOp.Update
                                                         : DepthOp.Remove;
            DepthEvent de = new DepthEvent
            {
                Side     = e.MarketDataType == MarketDataType.Ask ? Side.Ask : Side.Bid,
                Op       = op,
                Position = e.Position,
                Price    = e.Price,
                Volume   = e.Volume,
                Time     = e.Time,
                IsReset  = false
            };
            lock (_engineLock)
            {
                if (e.Instrument != _instrument) return;   // stale event from a prior instrument (mid-switch) — drop before touching the book
                _book.ApplyDepth(de);
                _depthEvents++;
                MaybeRunEngine(e.Time);
            }
        }

        private void OnMarketData(object sender, MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last || e.Price <= 0) return;
            TradeEvent te = new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time };
            lock (_engineLock)
            {
                if (e.Instrument != _instrument) return;   // stale event from a prior instrument (mid-switch) — drop before touching the book
                _book.ApplyTrade(te);
                _tradeEvents++;
                MaybeRunEngine(e.Time);
            }
        }

        private void MaybeRunEngine(DateTime now)
        {
            if (_lastEngineRun != DateTime.MinValue)
            {
                double deltaMs = (now - _lastEngineRun).TotalMilliseconds;
                // Market Replay discontinuity: e.Time jumped far BACKWARD (rewind/restart — the old
                // guard's negative-diff path froze the radar here) OR far FORWARD past any real
                // quiet-market gap (scrub-ahead / session rollover, which would otherwise paint a stale
                // book as live). Rebuild the stale book+tracker, then fall through and run the engine.
                if (deltaMs < -ReplayResetBackwardMs || deltaMs > ReplayResetForwardMs)
                    HandleReplayReset(now);
                else if (deltaMs < 0)
                {
                    // Small BACKWARD step (a sub-2s rewind, or an out-of-order feed tick): don't run this
                    // frame, but DO re-base the engine clock down to `now`. The old code left
                    // _lastEngineRun at the forward high-water mark and early-returned, so every later
                    // event still saw a negative delta and the engine stayed frozen until replay time
                    // climbed back past it. Re-basing lets the very next forward tick resume normally —
                    // without bypassing the throttle on every jittery backward tick (which running here would).
                    _lastEngineRun = now;
                    return;
                }
                else if (deltaMs < EngineIntervalMs)
                    return;                       // normal ~20Hz forward throttle (keep _lastEngineRun at the last actual run)
            }
            _lastEngineRun = now;
            _tracker.Update(_book, now);
            double m = (_book.MedianSize(Side.Bid) + _book.MedianSize(Side.Ask)) / 2.0;
            if (m > 0) _medianEwma = _medianEwma <= 0 ? m : EwmaAlpha * m + (1 - EwmaAlpha) * _medianEwma;
            if (_autoCalib && _medianEwma > 0)
                _cfg.MinAbsSize = Math.Max(1, (long)Math.Round(_autoFactor * _medianEwma));
            // Cockpit pressure inputs (same assembly as the lr-signals capture).
            var pBids = _book.Levels(Side.Bid); var pAsks = _book.Levels(Side.Ask);
            long pBidMass = 0, pAskMass = 0;
            for (int i = 0; i < pBids.Count; i++) pBidMass += pBids[i].Volume;
            for (int i = 0; i < pAsks.Count; i++) pAskMass += pAsks[i].Volume;
            DepthLevel pbb, pba;
            long pBestBid = _book.TryBestBid(out pbb) ? pbb.Volume : 0;
            long pBestAsk = _book.TryBestAsk(out pba) ? pba.Volume : 0;
            double pMid = MidOf();
            WallErosion pWall = new WallErosion();
            double pWf = 0.0;
            var pErr = _tracker.ErosionReads(_book, now);
            for (int i = 0; i < pErr.Count; i++)
                if (pErr[i].Approaching && pErr[i].Frac > pWf)
                { pWf = pErr[i].Frac; pWall = new WallErosion { Active = true, Frac = pErr[i].Frac, Above = pErr[i].Price > pMid }; }
            long aggDelta15 = _book.AggressorDelta(now.AddSeconds(-15));   // shared by pin below and cin further down
            PressureInputs pin = new PressureInputs
            {
                Bids = new List<DepthLevel>(pBids),
                Asks = new List<DepthLevel>(pAsks),
                BestBidSize = pBestBid, BestAskSize = pBestAsk,
                AggressorDelta = aggDelta15,
                Wall = pWall
            };
            var snapNodes = _tracker.GetSnapshot(now);
            // Biggest wall above/below mid this run — anchors the Chart Trader's BUY/SELL LMT (§2).
            double wallAbovePx = 0, wallBelowPx = 0;
            long   wallAboveSz = 0, wallBelowSz = 0;
            for (int i = 0; i < snapNodes.Count; i++)
            {
                RadarNode wn = snapNodes[i];
                if (!wn.InWindow) continue;   // blind/remembered node — frozen size, must not count as a live dominant wall
                if (wn.Price > pMid && wn.LastKnownSize > wallAboveSz) { wallAboveSz = wn.LastKnownSize; wallAbovePx = wn.Price; }
                if (wn.Price < pMid && wn.LastKnownSize > wallBelowSz) { wallBelowSz = wn.LastKnownSize; wallBelowPx = wn.Price; }
            }

            // ARMED-WALL IDENTITY CONTRACT: the Controller's wall-identity guard abandons Armed/Countdown
            // whenever the fed wall price moves >= 1 tick from the armed WallPrice (see
            // ControllerStateMachine). Recomputing "biggest wall above/below" from scratch every run (as
            // wallAbovePx/wallBelowPx above do) would look like the wall hopping the moment the armed wall
            // is partially eaten and stops being the single biggest level — aborting the countdown exactly
            // when the setup matures. So: per side, if the PREVIOUS run's ControllerOutput has that side
            // Armed/Countdown, feed the LIVE size AT that armed price (looked up by price identity in this
            // run's wall snapshot; 0 if the level is gone) instead of the freshly recomputed dominant wall.
            // Waiting/Fired/Cooldown have no armed identity to preserve, so they use the dominant wall.
            double ctrlWallAbovePx, ctrlWallBelowPx; long ctrlWallAboveSz, ctrlWallBelowSz;
            ResolveWallFeed(_lastCtrl.Long, _lastCtrl.LongWallPrice, Side.Ask, wallAbovePx, wallAboveSz, snapNodes, out ctrlWallAbovePx, out ctrlWallAboveSz);
            ResolveWallFeed(_lastCtrl.Short, _lastCtrl.ShortWallPrice, Side.Bid, wallBelowPx, wallBelowSz, snapNodes, out ctrlWallBelowPx, out ctrlWallBelowSz);

            // Tape speed: sample the 1s print rate into the EWMA baseline (also feeds the Rec CSV below).
            var win1s = _book.WindowSince(now.AddSeconds(-1));
            _tape.Sample(win1s.Prints, now);

            ControllerInputs cin = new ControllerInputs
            {
                WallAbovePrice = ctrlWallAbovePx, WallAboveCurrent = ctrlWallAboveSz,
                WallBelowPrice = ctrlWallBelowPx, WallBelowCurrent = ctrlWallBelowSz,
                AggressorDelta = aggDelta15,
                TapeZScore = _tape.ZScore,
                TapeAlternations = _book.RecentAlternations(8),
                Mid = pMid, Now = now, Book = _book
            };
            ControllerOutput cout = _controller.Update(cin);
            _lastCtrl = cout;   // read by the identity-contract lookup above on the NEXT engine run
            // Task 11: vote-less book-skew context for the Cockpit's demoted reference strip (spec §7) —
            // reuses the same pin assembled above; never a vote, never a trigger. `pin` itself is kept
            // only for this — PressureModel.Evaluate(pin) is no longer called per-run (nothing reads the
            // old PressureResult since the Cockpit rewrite; Evaluate stays in the engine for its own tests).
            double bookSkew = _pressure.BookSkewContext(pin);

            _latest = new Frame
            {
                Nodes = snapNodes,
                Bids  = new List<DepthLevel>(pBids),
                Asks  = new List<DepthLevel>(pAsks),
                Mid   = pMid,
                Tick  = _cfg.TickSize,
                WallAbove = wallAbovePx,
                WallBelow = wallBelowPx,
                Ctrl = cout, Fired = cout.Fired, Fire = cout.Fire,
                BuyPerSec = win1s.BuyVol, SellPerSec = win1s.SellVol, TapeZ = _tape.ZScore,
                BookSkew = bookSkew
            };
            MaybeDiag(now);
            if (_capture && _capWriter != null)
            {
                try
                {
                    var nodes  = _latest.Nodes;
                    double mid = _latest.Mid;
                    long medBid = _book.MedianSize(Side.Bid), medAsk = _book.MedianSize(Side.Ask);
                    if (nodes != null)
                    {
                        for (int i = 0; i < nodes.Count; i++)
                        {
                            RadarNode n = nodes[i];
                            long key = (long)Math.Round(n.Price / _cfg.TickSize) * 2 + (n.Side == Side.Ask ? 1 : 0);
                            NodeState prev;
                            bool had = _prevStates.TryGetValue(key, out prev);
                            if ((!had || n.State != prev) &&
                                (n.State == NodeState.Wall     || n.State == NodeState.Absorbed ||
                                 n.State == NodeState.Pulled   || n.State == NodeState.Consumed))
                            {
                                _capWriter.WriteLine(string.Format(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    "{0},evt,{1},{2:0.00},{3},{4},{5},{6},{7:0.000},{8},{9:0.0},{10:0.00},{11},{12}",
                                    now.ToString("o"),
                                    n.Side == Side.Ask ? "Ask" : "Bid",
                                    n.Price, n.PeakSize, n.LastKnownSize,
                                    had ? prev.ToString() : "", n.State,
                                    n.Confidence, n.InWindow, n.AgeSeconds, mid, medBid, medAsk));
                            }
                            _prevStates[key] = n.State;
                        }
                    }
                    if (_lastMidLog == DateTime.MinValue || (now - _lastMidLog).TotalSeconds >= 2)
                    {
                        _lastMidLog = now;
                        _capWriter.WriteLine(string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0},mid,,,,,,,,,,{1:0.00},{2},{3}",
                            now.ToString("o"), mid, medBid, medAsk));
                        _capWriter.Flush();
                        // enhanced PressureInputs snapshot (Plan D measurement)
                        if (_capture && _sigWriter != null)
                        {
                            var bids = _book.Levels(Side.Bid); var asks = _book.Levels(Side.Ask);
                            long bidMass = 0, askMass = 0;
                            for (int i = 0; i < bids.Count; i++) bidMass += bids[i].Volume;
                            for (int i = 0; i < asks.Count; i++) askMass += asks[i].Volume;
                            DepthLevel bb, ba;
                            long bestBid = _book.TryBestBid(out bb) ? bb.Volume : 0;
                            long bestAsk = _book.TryBestAsk(out ba) ? ba.Volume : 0;
                            long delta15 = _book.AggressorDelta(now.AddSeconds(-15));
                            // nearest wall erosion to the inside (max |frac|); 0 if none
                            double wf = 0.0, wpx = 0.0; bool wabove = false;
                            var er = _tracker.ErosionReads(_book, now);
                            for (int i = 0; i < er.Count; i++)
                                if (er[i].Approaching && er[i].Frac > wf)
                                { wf = er[i].Frac; wpx = er[i].Price; wabove = er[i].Price > mid; }
                            // consumeFracLong/Short and tradeBackedLong/Short are both the Controller's own
                            // authoritative reads (ControllerOutput.LongFraction/LongTradeBacked etc — already
                            // correctly zeroed on abandon, see ControllerStateMachine). wallAbovePx/wallBelowPx
                            // above are the raw recomputed dominant wall, which during Armed/Countdown is a
                            // DIFFERENT price level than the one the fractions describe (the identity-pinned
                            // armed wall) — log that identity-pinned feed too (ctrlWallAbove*/ctrlWallBelow*,
                            // the exact values passed to _controller.Update this run) so calibration can tell
                            // the two apart instead of silently mixing levels.
                            _sigWriter.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "{0},{1:0.00},{2},{3},{4},{5},{6},{7:0.000},{8},{9:0.00}," +
                                "{10:0.00},{11},{12:0.00},{13},{14:0.000},{15:0.000},{16:0.000},{17:0.000}," +
                                "{18},{19},{20},{21:0.000},{22},{23}," +
                                "{24:0.00},{25},{26:0.00},{27}",
                                now.ToString("o"), mid, bidMass, askMass, bestBid, bestAsk, delta15, wf, wabove, wpx,
                                wallAbovePx, wallAboveSz, wallBelowPx, wallBelowSz,
                                cout.LongFraction, cout.LongTradeBacked, cout.ShortFraction, cout.ShortTradeBacked,
                                win1s.Prints, win1s.BuyVol, win1s.SellVol, _tape.ZScore,
                                cout.Long, cout.Short,
                                ctrlWallAbovePx, ctrlWallAboveSz, ctrlWallBelowPx, ctrlWallBelowSz));
                            _sigWriter.Flush();
                        }
                    }
                }
                catch { }
            }
        }

        // NT8 exposes no "replay was reset" event for add-ons, so a large backward jump in the replay
        // clock (e.Time) is the most reliable, mechanism-agnostic signal that Market Replay was rewound
        // or restarted. Rebuild book + tracker exactly like a fresh Subscribe() would (reusing the same
        // ctors + SeedFromSnapshot), which clears every piece of pre-rewind state that would otherwise
        // freeze or corrupt the radar (stale trade ring, wall/episode/confidence memory, EWMA bias).
        // Runs on the instrument dispatcher thread — same as its caller — so no lock is needed.
        private void HandleReplayReset(DateTime now)
        {
            _book = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
            if (_instrument != null) SeedFromSnapshot(_instrument);   // re-seed L2 from the platform's current snapshot
            _tracker    = new WallTracker(_cfg);                      // drop stale wall/episode/confidence memory
            // A rewind must not leave stale EWMA/state-machine memory: a Countdown armed against the
            // pre-rewind tape would otherwise survive into replayed history it never actually saw.
            _tape       = new TapeSpeed(0.1);
            _controller = new ControllerStateMachine(new ControllerConfig(), _cfg.TickSize);
            _lastCtrl   = default(ControllerOutput);
            _lastDiag   = DateTime.MinValue;
            _lastMidLog = DateTime.MinValue;
            _medianEwma = 0;
            _latest     = null;
            _replayResetPending = true;   // paint tick (UI thread) drops RadarVisual's ladder memory + anchor
            NinjaTrader.Code.Output.Process(
                string.Format("[Radar] replay reset detected @ {0:HH:mm:ss} — book+tracker reseeded", now),
                NinjaTrader.NinjaScript.PrintTo.OutputTab1);
        }

        private void MaybeDiag(DateTime now)
        {
            if (_lastDiag != DateTime.MinValue && (now - _lastDiag).TotalSeconds < 2) return;
            _lastDiag = now;
            var bids = _book.Levels(Side.Bid); var asks = _book.Levels(Side.Ask);
            long maxB = 0, maxA = 0;
            for (int i = 0; i < bids.Count; i++) if (bids[i].Volume > maxB) maxB = bids[i].Volume;
            for (int i = 0; i < asks.Count; i++) if (asks[i].Volume > maxA) maxA = asks[i].Volume;
            int nodes = _latest != null && _latest.Nodes != null ? _latest.Nodes.Count : 0;
            string msg = string.Format(
                "[Radar] {0:HH:mm:ss} depth#={1} trade#={2} | bids={3} asks={4} | maxBid={5} maxAsk={6}" +
                " medBid={7} medAsk={8} | MinAbs={9} Kx={10} nodes={11}",
                now, _depthEvents, _tradeEvents, bids.Count, asks.Count, maxB, maxA,
                _book.MedianSize(Side.Bid), _book.MedianSize(Side.Ask),
                _cfg.MinAbsSize, _cfg.K_mult, nodes);
            NinjaTrader.Code.Output.Process(msg, NinjaTrader.NinjaScript.PrintTo.OutputTab1);
        }

        // Aurora-styled label+TextBox pair; assigns to a _cfg field on Enter/LostFocus.
        private static UIElement MakeCfgInput(string label, string init, Action<double> apply)
        {
            var lbl = new TextBlock { Text = label + ":",
                Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0xa4, 0xb2)) };
            var box = new TextBox { Text = init, Width = 50,
                Background  = new SolidColorBrush(Color.FromRgb(0x0f, 0x14, 0x20)),
                Foreground  = new SolidColorBrush(Color.FromRgb(0xcf, 0xd6, 0xe2)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0xff, 0xff, 0xff)),
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 1, 3, 1) };
            Action commit = () => { if (double.TryParse(box.Text, out double v)) apply(v); };
            box.LostFocus += (o, e) => commit();
            box.KeyDown   += (o, e) => { if (e.Key == Key.Enter) commit(); };
            var sp = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(lbl); sp.Children.Add(box);
            return sp;
        }

        // ---- NTTabPage members ----
        public override void Cleanup()
        {
            _capture = false;
            if (_capWriter != null) { _capWriter.Flush(); _capWriter.Dispose(); _capWriter = null; }
            if (_sigWriter != null) { _sigWriter.Flush(); _sigWriter.Dispose(); _sigWriter = null; }
            _selector.InstrumentChanged -= OnSelectorChanged;
            if (_paintTimer != null) { _paintTimer.Stop(); _paintTimer = null; }
            if (_chartTrader != null) _chartTrader.Cleanup();
            Unsubscribe();
        }

        protected override string GetHeaderPart(string variable)
        {
            return _instrument != null ? _instrument.MasterInstrument.Name : "Liquidity Radar";
        }

        protected override void Restore(XElement element)
        {
            if (element == null) return;
            XElement instEl = element.Element("RadarInstrument");
            if (instEl != null && !string.IsNullOrEmpty(instEl.Value))
            {
                Instrument inst = Instrument.GetInstrument(instEl.Value);
                if (inst != null) { _selector.Instrument = inst; Instrument = inst; }
            }
        }

        protected override void Save(XElement element)
        {
            if (element == null) return;
            if (_instrument != null)
                element.Add(new XElement("RadarInstrument", _instrument.FullName));
        }
    }
}
