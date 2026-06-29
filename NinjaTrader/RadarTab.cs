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
        }

        private readonly RadarConfig _cfg = new RadarConfig();   // NQ defaults; later: per-instrument presets
        private BookMirror  _book;
        private WallTracker _tracker;
        private RadarVisual _visual;
        private InstrumentSelector _selector;

        private Instrument     _instrument;
        private volatile Frame _latest;       // immutable snapshot + mid + tick, swapped from instrument thread
        private DispatcherTimer _paintTimer;
        private bool _subscribed;
        private int      _depthEvents;
        private int      _tradeEvents;
        private DateTime _lastDiag      = DateTime.MinValue;
        private DateTime _lastEngineRun = DateTime.MinValue;
        private const double EngineIntervalMs = 50;   // run engine+snapshot at most ~20Hz
        private volatile bool _autoCalib;
        private double _medianEwma;
        private double _autoFactor = 1.8;
        private const double EwmaAlpha = 0.0017;      // ~60s smoothing at ~20Hz engine runs
        private TextBox  _minSizeBox;
        private CheckBox _autoChk;
        private volatile bool _capture;
        private System.IO.StreamWriter _capWriter;
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
            _visual  = new RadarVisual();

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
                _capture = true;
            };
            recChk.Unchecked += (o, e) =>
            {
                _capture = false;
                if (_capWriter != null) { _capWriter.Flush(); _capWriter.Dispose(); _capWriter = null; }
            };
            topBar.Children.Add(recChk);
            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);
            root.Children.Add(_visual);          // fills remaining space
            Content = root;

            // UI-thread paint/animation clock — independent of data arrival.
            _paintTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _paintTimer.Tick += (o, e) =>
            {
                Frame f = _latest;          // volatile read of the latest immutable frame
                if (f != null) _visual.SetFrame(f.Nodes, f.Bids, f.Asks, f.Mid, f.Tick);
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
                _instrument = value;
                if (_instrument != null)
                    _cfg.TickSize = _instrument.MasterInstrument.TickSize;
                // Rebuild engine for the new instrument's tick size.
                _book    = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
                _tracker = new WallTracker(_cfg);
                _latest  = null;
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
                    SeedFromSnapshot(inst);     // prime the book with the current ladder
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
            bool hb = _book.TryBestBid(out bb), ha = _book.TryBestAsk(out ba);
            if (hb && ha) return (bb.Price + ba.Price) / 2.0;
            if (hb) return bb.Price;
            if (ha) return ba.Price;
            return 0;
        }

        // ---- instrument-thread handlers: map -> engine -> swap frame ----
        private void OnMarketDepth(object sender, MarketDepthEventArgs e)
        {
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
            _book.ApplyDepth(de);
            _depthEvents++;
            MaybeRunEngine(e.Time);
        }

        private void OnMarketData(object sender, MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;
            TradeEvent te = new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time };
            _book.ApplyTrade(te);
            _tradeEvents++;
            MaybeRunEngine(e.Time);
        }

        private void MaybeRunEngine(DateTime now)
        {
            if (_lastEngineRun != DateTime.MinValue && (now - _lastEngineRun).TotalMilliseconds < EngineIntervalMs)
                return;
            _lastEngineRun = now;
            _tracker.Update(_book, now);
            double m = (_book.MedianSize(Side.Bid) + _book.MedianSize(Side.Ask)) / 2.0;
            if (m > 0) _medianEwma = _medianEwma <= 0 ? m : EwmaAlpha * m + (1 - EwmaAlpha) * _medianEwma;
            if (_autoCalib && _medianEwma > 0)
                _cfg.MinAbsSize = Math.Max(1, (long)Math.Round(_autoFactor * _medianEwma));
            _latest = new Frame
            {
                Nodes = _tracker.GetSnapshot(now),
                Bids  = new List<DepthLevel>(_book.Levels(Side.Bid)),
                Asks  = new List<DepthLevel>(_book.Levels(Side.Ask)),
                Mid   = MidOf(),
                Tick  = _cfg.TickSize
            };
            MaybeDiag(now);
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
            _selector.InstrumentChanged -= OnSelectorChanged;
            if (_paintTimer != null) { _paintTimer.Stop(); _paintTimer = null; }
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
