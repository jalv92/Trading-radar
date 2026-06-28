using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
            public IReadOnlyList<RadarNode> Nodes;
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

        public RadarTab()
        {
            _book    = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
            _tracker = new WallTracker(_cfg);
            _visual  = new RadarVisual();

            _selector = new InstrumentSelector();
            _selector.InstrumentChanged += OnSelectorChanged;

            DockPanel root = new DockPanel();
            DockPanel.SetDock(_selector, Dock.Top);
            root.Children.Add(_selector);
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
                if (f != null) _visual.SetNodes(f.Nodes, f.Mid, f.Tick);
                _visual.AdvanceAnimation(); // sweep/fade clock
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
            _tracker.Update(_book, e.Time);
            _latest = new Frame { Nodes = _tracker.GetSnapshot(e.Time), Mid = MidOf(), Tick = _cfg.TickSize };
        }

        private void OnMarketData(object sender, MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;
            TradeEvent te = new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time };
            _book.ApplyTrade(te);
            _tracker.Update(_book, e.Time);
            _latest = new Frame { Nodes = _tracker.GetSnapshot(e.Time), Mid = MidOf(), Tick = _cfg.TickSize };
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
