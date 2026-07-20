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
            // REPLAY-aware market-data clock (the `now` MaybeRunEngine already threads through — sourced
            // from e.Time on the instrument thread, not wall clock). Fed to RadarChartTrader.SetContext so
            // its AUTO-mode auto-cancel timer ages against market time, which is what a Market Replay
            // calibration run (variable/paused speed) actually needs — not DateTime.Now/UtcNow.
            public DateTime Now;
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
            // Reactive setup (spec §5/§9/§10): signed accel readout + the active dropdown selection + the
            // collapsed ReactiveController banner projection — all computed under _engineLock, painted by
            // the tick. Break-active frames carry Setup=Break and Banner=Waiting (the cockpit ignores it).
            public double TapeAccel;
            public SetupKind Setup;
            public ReactBanner Banner;
        }

        private readonly RadarConfig _cfg = new RadarConfig();   // NQ defaults; later: per-instrument presets
        private BookMirror  _book;
        private WallTracker _tracker;
        private TapeSpeed   _tape;
        private ControllerStateMachine _controller;
        private ReactiveController _reactive;    // spec §4: the swappable second setup (isolated from frozen Break)
        private TapeAcceleration _accel;         // spec §5: signed net-aggressor acceleration
        // The user's dropdown choice (RadarChartTrader.SetupChanged). NOT reset on instrument-switch /
        // replay-reset — that is the "re-apply the selection" contract (spec §4/§9): those sites rebuild
        // BOTH controllers but leave this field alone, so a switch can't silently revert to Break.
        private SetupKind _activeSetup = SetupKind.Break;
        // Previous engine run's ControllerOutput — read (never written) BEFORE calling _controller.Update()
        // this run, to decide the ARMED-WALL IDENTITY CONTRACT feed below. Default(Waiting/Waiting) on the
        // very first run correctly falls through to "feed the freshly computed dominant wall".
        private ControllerOutput _lastCtrl;
        private RadarVisual _visual;
        private CockpitVisual _cockpit;
        // B2 (bug-audit 2026-07-19): the Rec checkbox, promoted to a field so the Instrument setter can
        // roll the capture (uncheck -> flush+dispose+session summary) BEFORE the instrument swaps.
        private CheckBox _recChk;
        private RadarChartTrader _chartTrader;
        private PressureModel _pressure = new PressureModel(InstrumentPresets.For("ES").Pressure);
        private InstrumentSelector _selector;

        // Serializes the engine-state swap (instrument switch / replay reset, done from the UI or
        // instrument thread) against the instrument-thread depth/trade handlers. Monitor is reentrant, so
        // HandleReplayReset -> SeedFromSnapshot re-entering under the same lock is fine.
        private readonly object _engineLock = new object();
        private volatile Instrument _instrument;   // volatile: read in the handler guard on the instrument thread
        private volatile Frame _latest;       // immutable snapshot + mid + tick, swapped from instrument thread
        private volatile bool _replayResetPending;  // set on instrument thread by HandleReplayReset; consumed on the UI paint tick
        // Round-8: LATCHED fire handoff. Frame.Fired is a one-shot flag on an immutable Frame — at high
        // Market Replay speeds (~24x+) a Fired frame is replaced by the next engine run within a couple ms
        // of WALL time, while the UI paint tick only samples _latest at ~30Hz wall (~33ms period), so the
        // one-shot flag was sampled and lost almost every time (measured: 5/5 fires lost in a full-day
        // capture). _pendingFire/_pendingFireSet decouple "a fire happened" from "which Frame is current" —
        // written under _engineLock in MaybeRunEngine, consumed-and-cleared under the same lock on the next
        // paint tick, so delivery no longer depends on the paint tick's sampling rate vs. the engine's.
        private FireEvent _pendingFire;
        private volatile bool _pendingFireSet;
        private long _droppedFires;   // latest-wins overwrites of an unconsumed pending fire — should stay 0
        private DispatcherTimer _paintTimer;
        private bool _subscribed;
        private int      _depthEvents;
        private int      _tradeEvents;
        private DateTime _lastDiag      = DateTime.MinValue;
        private DateTime _lastEngineRun = DateTime.MinValue;
        private const double EngineIntervalMs = 50;   // run engine+snapshot at most ~20Hz
        // Round-6: bounded blind-trust window for the armed wall's identity feed — placeholder, calibrate next round.
        private const double BlindTrustSeconds = 1.0;
        private long _engineRunSeq;   // monotonic per-engine-run counter, for sig CSV provenance (src/seq)
        private const double ReplayResetBackwardMs = 2000;   // Playback rewind trip: real rewinds jump seconds-minutes; feed jitter is a few ms
        private const double ReplayResetForwardMs  = 60000;  // Playback scrub-ahead / session-rollover trip: bigger than any real quiet-market gap
        private volatile bool _autoCalib;
        private double _medianEwma;
        private double _autoFactor = 1.8;
        private const double EwmaAlpha = 0.0017;      // ~60s smoothing at ~20Hz engine runs
        private TextBox  _minSizeBox;
        private CheckBox _autoChk;
        private volatile bool _capture;
        // ADR 2026-07-03 Phase 0 — adaptive SignificanceBand baseline (spec §5: percentile of recent
        // depth, never a fixed count) + per-Rec-session funnel counters for lr-sessions.csv.
        private readonly DepthBaseline _depthBase = new DepthBaseline(16384);
        private DateTime _lastDepthSample = DateTime.MinValue;
        // Written on the instrument thread under _engineLock; every UI-thread read/reset also takes
        // _engineLock (review 2026-07-03: an unlocked read-then-reset could clobber an in-flight
        // increment and silently under-count the very funnel the fires=0 alarm watches).
        private int _recArms, _recFires;
        // React funnel counters (mirror the Break arms/fires; lr-sessions.csv). arms = Waiting->Watching,
        // fires = React cout.Fired, abandons = Watching->Cooldown. Only advance while React is the active
        // setup. Same _engineLock discipline as the Break counters above.
        private int _recReactArms, _recReactFires, _recReactAbandons;
        // Continuous engine state (NOT reset on Rec toggle — like _lastCtrl): previous run's React state +
        // abandon reason, so the sig writer can force a row on any React transition and the counters above
        // can detect the arm/abandon edges. Reset only where _reactive itself is rebuilt (setup swap /
        // instrument switch / replay reset).
        private ReactState _lastReactState = ReactState.Waiting;
        private AbandonReason _lastReactAbandon = AbandonReason.None;
        private System.IO.StreamWriter _capWriter;
        private System.IO.StreamWriter _sigWriter;
        private readonly Dictionary<long, NodeState> _prevStates = new Dictionary<long, NodeState>();
        private DateTime _lastMidLog = DateTime.MinValue;
        // Round-7: last-logged HoldCount per side, so the sig writer can force a row the instant
        // either one moves — see the holdChanged trigger where cout is computed. -1 so the very
        // first tick (HoldCount starts at 0) doesn't need to special-case a match.
        private int _lastLoggedHoldL = -1;
        private int _lastLoggedHoldS = -1;

        public RadarTab()
        {
            _cfg.MinAbsSize  = InstrumentPresets.For("ES").MinAbsSize;   // single source of truth; auto-overrides when _autoCalib is true, and the real per-instrument value lands on the first Instrument set
            _cfg.K_mult      = 1.5;
            _cfg.T_persist   = TimeSpan.FromMilliseconds(1000);
            _autoCalib       = true;
            _book    = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
            _tracker = new WallTracker(_cfg);
            _tape       = new TapeSpeed(0.1);
            _accel      = new TapeAcceleration(0.1);   // mirror TapeSpeed's alpha/warmup (spec §5)
            _controller = new ControllerStateMachine(InstrumentPresets.For("ES").Controller, _cfg.TickSize);
            _reactive   = new ReactiveController(new ReactiveConfig(), _cfg.TickSize);   // ponytail: no per-instrument React preset yet — ReactiveConfig defaults (uncalibrated) until React is calibrated
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
                    "ctrlWallAbovePx,ctrlWallAboveSz,ctrlWallBelowPx,ctrlWallBelowSz,tapeAlternations," +
                    "ctrlLongHold,ctrlShortHold,ctrlLongDistTicks,ctrlShortDistTicks,ctrlLongCooldownUntil,ctrlShortCooldownUntil,autoArmed," +
                    "ctrlLongPeak,ctrlLongMin,ctrlShortPeak,ctrlShortMin," +
                    "src,seq,adaptiveSig," +
                    // React telemetry — APPEND ONLY (a Break parser reads columns 0..42 by index, so
                    // appending here can never shift them). Empty/0 while Break is the active setup.
                    "setup,tapeAccel,reactState,reactWallPx,reactWallSide,reactAbandon,waOut,waValid,wbOut,wbValid");
                _sigWriter.Flush();
                _capture = true;
                // Round-7: forces a sig row the instant either side's HoldCount moves (see the
                // holdChanged trigger below) — reset on every Rec toggle-on, same as _prevStates.
                _lastLoggedHoldL = -1;
                _lastLoggedHoldS = -1;
                lock (_engineLock) { _recArms = 0; _recFires = 0; _recReactArms = 0; _recReactFires = 0; _recReactAbandons = 0; }
            };
            recChk.Unchecked += (o, e) =>
            {
                _capture = false;
                if (_capWriter != null) { _capWriter.Flush(); _capWriter.Dispose(); _capWriter = null; }
                if (_sigWriter != null) { _sigWriter.Flush(); _sigWriter.Dispose(); _sigWriter = null; }
                WriteSessionSummary();
            };
            topBar.Children.Add(recChk);
            _recChk = recChk;   // B2: reachable from the Instrument setter
            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);
            // Right column: Cockpit fills, Chart Trader docked at the bottom (spec §8).
            _chartTrader = new RadarChartTrader();
            _chartTrader.SetupChanged += OnSetupChanged;   // spec §9: swap the active controller on dropdown change
            Grid rightCol = new Grid();
            rightCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            // Auto, not a fixed height: the ticket's rows are all Auto-sized, so this row measures to
            // content — a fixed 340 clipped the position/PnL bar when the AUTO row grew the ticket.
            rightCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_cockpit, 0);     rightCol.Children.Add(_cockpit);
            // Channel branding (2026-07-03): logo + wordmark in the cockpit's slack space, as a
            // bottom-aligned OVERLAY in the same star row — it never adds height or moves a control.
            UIElement branding = BuildBranding();
            if (branding != null) { Grid.SetRow(branding, 0); rightCol.Children.Add(branding); }
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
                    _cockpit.SetFrame(f.Ctrl, f.BuyPerSec, f.SellPerSec, f.TapeZ, f.BookSkew,
                                      f.TapeAccel, f.Setup, f.Banner);   // spec §5/§9/§10 (reactive accel + banner)
                    // reuse the already-marshaled book mid + biggest-wall prices for live PnL + LMT anchoring;
                    // f.Now is the REPLAY-aware market clock AUTO mode's auto-cancel timer ages against.
                    _chartTrader.SetContext(f.Mid, f.WallAbove, f.WallBelow, f.Tick, f.Now);
                }
                // Round-8: LATCHED consume, not a Frame.Fired sample. At high replay speed the one-shot
                // Frame.Fired flag lives only ~ms of wall time before the next engine run replaces the
                // Frame — far shorter than this ~33ms paint period — so sampling f.Fired here lost
                // almost every fire (measured 5/5 in a full-day capture). _pendingFireSet instead stays
                // true across paint ticks until explicitly consumed here, under the same _engineLock the
                // writer (MaybeRunEngine) uses, so no fire can land between the read and the clear.
                // Deliberately OUTSIDE the `f != null` guard (review round-8): delivery needs only
                // _engineLock + _chartTrader — coupling it to _latest would silently stall fires if a
                // future reset path nulls the frame without clearing the latch.
                if (_pendingFireSet)
                {
                    FireEvent pf;
                    lock (_engineLock) { pf = _pendingFire; _pendingFireSet = false; }
                    _chartTrader.OnSetupFire(pf);   // ChartTrader's own FireEvent.Time dedupe still guards double-delivery
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

        // Dropdown (RadarChartTrader.SetupChanged) picked a different setup. Swap under _engineLock — the
        // same lock the depth/trade handlers hold around _controller/_reactive.Update — so no engine run
        // straddles the swap. Rebuild only the setup being switched TO (fresh per-run state); leave the
        // other frozen (selecting React must not reset Break's in-calibration state machine). Reset
        // _lastCtrl: its identity-pinned wall feed (ResolveWallFeed) belonged to the outgoing setup.
        private void OnSetupChanged(SetupKind kind)
        {
            lock (_engineLock)
            {
                _activeSetup = kind;
                var preset = InstrumentPresets.For(_instrument != null ? _instrument.MasterInstrument.Name : "ES");
                if (kind == SetupKind.Reactive) _reactive   = new ReactiveController(new ReactiveConfig(), _cfg.TickSize);
                else                            _controller = new ControllerStateMachine(preset.Controller, _cfg.TickSize);
                _lastCtrl = default(ControllerOutput);
                _lastReactState = _reactive.State; _lastReactAbandon = _reactive.LastAbandon;   // resync to the (maybe rebuilt) React controller
                // C2 (bug-audit 2026-07-19): the Instrument setter and HandleReplayReset both clear this
                // ("a pre-switch fire must not fire into the new instrument/rewind") but the setup swap
                // didn't — a React fire latched just before flipping to Break was still delivered to
                // OnSetupFire on the next paint tick and executed AUTO-AGGRESSIVELY under Break.
                _pendingFireSet = false;
            }
        }

        // Collapse the ReactiveController's (state, ReactKind) into the 4-label banner projection the
        // cockpit renders (spec §10). Called under _engineLock (reads _reactive.State + cout.Fire, both
        // freshly produced by this frame's _reactive.Update). Break-active frames pass ReactBanner.Waiting.
        private ReactBanner CurrentReactBanner(ControllerOutput cout)
        {
            switch (_reactive.State)
            {
                case ReactState.Watching: return ReactBanner.Watching;
                case ReactState.Fired:    return cout.Fire.React == ReactKind.Reject ? ReactBanner.FiredReject : ReactBanner.FiredBreak;
                default:                  return ReactBanner.Waiting;   // Waiting / Cooldown
            }
        }

        // Map the surfaced RadarNode.State (LiquidityMemory's applied episode outcome) to the per-side
        // (Outcome, valid) pair ReactiveController reads (spec §7 / plan §0-R3). Valid=false for any
        // non-terminal state (Live/Wall/Remembered) — that is "no resolved episode this side", which
        // guards the phantom default(Outcome)==Absorbed. NOTE (ceiling, D-4 step 10): a CONSUMED wall
        // that blinks out of the MBP window the same tick it resolves surfaces as Remembered (see
        // LiquidityMemory.Snapshot masking blind->Remembered), so BREAK is caught only while the eaten
        // level is still InWindow at resolution — the documented follow-up for the cluster-B upstream.
        private static void MapOutcome(NodeState st, out Outcome outcome, out bool valid)
        {
            switch (st)
            {
                case NodeState.Absorbed: outcome = Outcome.Absorbed; valid = true; break;
                case NodeState.Consumed: outcome = Outcome.Consumed; valid = true; break;
                case NodeState.Pulled:   outcome = Outcome.Pulled;   valid = true; break;
                default:                 outcome = Outcome.Absorbed; valid = false; break;   // Live/Wall/Remembered — no resolved episode
            }
        }

        // IInstrumentProvider — re-subscribe on instrument change (link-aware).
        public Instrument Instrument
        {
            get { return _instrument; }
            set
            {
                if (_instrument == value) return;
                // B2: roll the Rec capture BEFORE the swap — the Unchecked handler flushes both writers
                // and files the session summary under the OLD instrument's name (a mid-session switch
                // used to keep appending the new instrument's rows into the old instrument's CSVs and
                // credit the session row to whichever instrument was current at uncheck). Deliberately
                // outside _engineLock: WriteSessionSummary takes that lock itself.
                if (_recChk != null && _recChk.IsChecked == true) _recChk.IsChecked = false;
                Unsubscribe();
                // Swap the whole engine state atomically w.r.t. the instrument-thread handlers: a late
                // depth/trade event from the OLD instrument (still queued on its thread) is dropped by the
                // e.Instrument guard, and can never apply to the NEW book mid-swap.
                lock (_engineLock)
                {
                    _instrument = value;
                    if (_instrument != null)
                        _cfg.TickSize = _instrument.MasterInstrument.TickSize;
                    // Per-instrument DETECTION-threshold preset (compiled switch only — see
                    // Engine/InstrumentPresets.cs; ML-calibration ADR forbids a runtime config loader).
                    // Falls back to ES for a null/unrecognized instrument.
                    var preset = InstrumentPresets.For(_instrument != null ? _instrument.MasterInstrument.Name : "ES");
                    _cfg.MinAbsSize = preset.MinAbsSize;   // auto-overrides immediately when _autoCalib is true
                    // Rebuild engine for the new instrument's tick size. _tape/_controller/_pressure carry
                    // EWMA baseline + state-machine memory keyed to the OLD instrument/tick — must be
                    // rebuilt alongside _book/_tracker, or a switch leaks a stale Armed/Countdown into the
                    // new one (and _pressure was never rebuilt on switch before this — its PressureConfig
                    // could never vary per instrument).
                    _book       = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));
                    _tracker    = new WallTracker(_cfg);
                    _tape       = new TapeSpeed(0.1);
                    _accel      = new TapeAcceleration(0.1);                                   // new instrument = fresh accel EWMA
                    _controller = new ControllerStateMachine(preset.Controller, _cfg.TickSize);
                    _reactive   = new ReactiveController(new ReactiveConfig(), _cfg.TickSize); // rebuild both; _activeSetup (the selection) is deliberately untouched — that IS the re-apply
                    _pressure   = new PressureModel(preset.Pressure);
                    _lastCtrl   = default(ControllerOutput);
                    _lastReactState = _reactive.State; _lastReactAbandon = _reactive.LastAbandon;   // resync to the rebuilt React controller
                    _latest  = null;
                    _lastEngineRun = DateTime.MinValue;   // don't carry the old instrument's engine clock
                    _medianEwma    = 0;                   // (a stale high-water would spuriously reset/throttle)
                    _pendingFireSet = false;               // a pre-switch fire must not fire into the new instrument
                    _depthBase.Reset();                    // new instrument = new size distribution; don't inherit the old bar
                    _lastDepthSample = DateTime.MinValue;
                }
                // B1: blank both visuals now — the paint tick only repaints them when a frame exists
                // (_latest was just nulled), so without this the OLD instrument's last ladder/cockpit
                // frame stays on screen until the NEW instrument's first engine run.
                if (_visual != null) _visual.Clear();
                if (_cockpit != null) _cockpit.Clear();
                if (_chartTrader != null) _chartTrader.Instrument = value;
                Subscribe();
                RefreshHeader();
                NinjaTrader.Code.Output.Process(
                    "[Radar] preset: " + InstrumentPresets.For(value != null ? value.MasterInstrument.Name : "ES").Label,
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
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

        // Live resting size at a specific price, by identity (0 if that level is gone or the blind-trust
        // window has expired). Backs the ARMED-WALL IDENTITY CONTRACT in MaybeRunEngine: an armed
        // candidate must keep seeing ITS wall's size, not whichever wall happens to be biggest this run
        // once its own wall is partway eaten. Round-6: a node blinking out of the MBP-10 window
        // (InWindow=false) still gets its LastKnownSize trusted for BlindTrustSeconds — see
        // WallTracker.TrustedSize — because MarkBlind only clears InWindow, LastKnownSize is still the
        // last real observation, and feeding 0 the instant a wall blinks destroyed 95-96% of armed
        // candidates (phantom cur<=0 abandons).
        private long SizeAtPrice(IReadOnlyList<RadarNode> nodes, Side side, double price)
        {
            if (price <= 0) return 0;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].Side == side && Math.Abs(nodes[i].Price - price) < _cfg.TickSize / 2.0)
                    return WallTracker.TrustedSize(nodes[i].InWindow, nodes[i].AgeSeconds, nodes[i].LastKnownSize, BlindTrustSeconds);
            return 0;
        }

        // State twin of SizeAtPrice: the UNMASKED true state at a price by identity, through the SAME
        // bounded blind-trust (InWindow || young), else Remembered. Backs React's latched-wall feed so it
        // sees ITS wall's real resolution through a blink, not the InWindow-masked dominant state. Uses
        // RawState (not State) so the dominant path's Break/cockpit masking is untouched. Mirrors
        // WallTracker.TrustedSize's condition (Remembered is this method's "0", like TrustedSize returns 0).
        private NodeState StateAtPrice(IReadOnlyList<RadarNode> nodes, Side side, double price)
        {
            if (price <= 0) return NodeState.Remembered;
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].Side == side && Math.Abs(nodes[i].Price - price) < _cfg.TickSize / 2.0)
                    return (nodes[i].InWindow || nodes[i].AgeSeconds < BlindTrustSeconds)
                           ? nodes[i].RawState : NodeState.Remembered;
            return NodeState.Remembered;
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
            if (e.IsReset) { lock (_engineLock) { if (e.Instrument == _instrument) _book.ApplyDepth(new DepthEvent { IsReset = true }); } return; }
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
                MaybeRunEngine(e.Time, 'D');
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
                MaybeRunEngine(e.Time, 'T');
            }
        }

        // src: 'D' when triggered from the depth handler, 'T' from the trade handler — sig CSV
        // provenance column, so a capture can tell which feed drove any given engine run.
        private void MaybeRunEngine(DateTime now, char src)
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
                    // known: re-clears the throttle on sub-second out-of-order stamps (round-6 measured 0
                    // lost fires from this); do not patch until the src/seq trace confirms the physical source.
                    _lastEngineRun = now;
                    return;
                }
                else if (deltaMs < EngineIntervalMs)
                    return;                       // normal ~20Hz forward throttle (keep _lastEngineRun at the last actual run)
            }
            _lastEngineRun = now;
            long seq = ++_engineRunSeq;   // sig CSV provenance — monotonic per actual engine run
            _tracker.Update(_book, now);
            double m = (_book.MedianSize(Side.Bid) + _book.MedianSize(Side.Ask)) / 2.0;
            if (m > 0) _medianEwma = _medianEwma <= 0 ? m : EwmaAlpha * m + (1 - EwmaAlpha) * _medianEwma;
            if (_autoCalib && _medianEwma > 0)
                _cfg.MinAbsSize = Math.Max(1, (long)Math.Round(_autoFactor * _medianEwma));
            // Cockpit pressure inputs (same assembly as the lr-signals capture).
            var pBids = _book.Levels(Side.Bid); var pAsks = _book.Levels(Side.Ask);
            // ADR 2026-07-03: sample the live book's level sizes into the depth-percentile baseline —
            // one batch per second of market time, not per engine run (20Hz would resample the same
            // resting book 20x and just autocorrelate). Reset on instrument switch / replay rewind.
            if (_lastDepthSample == DateTime.MinValue || (now - _lastDepthSample).TotalSeconds >= 1.0)
            {
                _lastDepthSample = now;
                for (int i = 0; i < pBids.Count; i++) _depthBase.Add(pBids[i].Volume);
                for (int i = 0; i < pAsks.Count; i++) _depthBase.Add(pAsks[i].Volume);
                _depthBase.EndBatch();
            }
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
            // Reactive setup (spec §7 / plan §0-R3): the dominant wall's surfaced episode state per side,
            // captured on the SAME InWindow-dominant node Break uses, mapped to (Outcome, valid) below.
            NodeState wallAboveState = NodeState.Live, wallBelowState = NodeState.Live;
            for (int i = 0; i < snapNodes.Count; i++)
            {
                RadarNode wn = snapNodes[i];
                // Deliberately asymmetric with SizeAtPrice's bounded blind-trust (round-6): picking a NEW
                // wall to arm on must see it live right now, so this recompute keeps strict InWindow — an
                // already-armed candidate tracking ITS wall through a blink is a different job, handled
                // below via ResolveWallFeed/SizeAtPrice/WallTracker.TrustedSize, not here.
                if (!wn.InWindow) continue;   // blind/remembered node — frozen size, must not count as a live dominant wall
                if (wn.Price > pMid && wn.LastKnownSize > wallAboveSz) { wallAboveSz = wn.LastKnownSize; wallAbovePx = wn.Price; wallAboveState = wn.State; }
                if (wn.Price < pMid && wn.LastKnownSize > wallBelowSz) { wallBelowSz = wn.LastKnownSize; wallBelowPx = wn.Price; wallBelowState = wn.State; }
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

            // REACT LATCHED-WALL IDENTITY FEED (mirrors ResolveWallFeed for Break, but keyed off the React
            // controller's own latch instead of _lastCtrl). While React is Watching, _reactive.State /
            // LatchedWallPrice / LatchedSide hold the PREVIOUS frame's values (Update runs below), so this
            // is the exact _lastCtrl analogue: on the arming frame State is still Waiting → override skipped
            // → arms on the dominant wall (correct); from the next frame State==Watching → the latched side's
            // feed is pinned to ITS wall by price identity — size AND raw (unmasked) state — so a blink at
            // the latched price no longer reads as IdentityHop/WallVanished, and the wall's REAL resolution
            // (Absorbed/Consumed/Pulled) reaches MapOutcome below instead of the InWindow-masked Remembered.
            // Latched side only; the non-latched side keeps its dominant feed untouched.
            if (_activeSetup == SetupKind.Reactive && _reactive.State == ReactState.Watching)
            {
                double lp = _reactive.LatchedWallPrice;
                if (_reactive.LatchedSide == Side.Ask)
                {
                    ctrlWallAbovePx = lp;
                    ctrlWallAboveSz = SizeAtPrice(snapNodes, Side.Ask, lp);
                    wallAboveState  = StateAtPrice(snapNodes, Side.Ask, lp);
                }
                else
                {
                    ctrlWallBelowPx = lp;
                    ctrlWallBelowSz = SizeAtPrice(snapNodes, Side.Bid, lp);
                    wallBelowState  = StateAtPrice(snapNodes, Side.Bid, lp);
                }
            }

            // Tape speed: sample the 1s print rate into the EWMA baseline (also feeds the Rec CSV below).
            var win1s = _book.WindowSince(now.AddSeconds(-1));
            _tape.Sample(win1s.Prints, now);
            // Spec §5 / plan §0-R4: signed net-aggressor acceleration. netRate = (BuyVol - SellVol) over
            // the SAME 1s window TapeSpeed uses => already per-second; TapeAcceleration takes the
            // frame-to-frame derivative. Sign arms the reactive wall on that side (+=buyers, -=sellers).
            _accel.Sample(win1s.BuyVol - win1s.SellVol, now);

            // Reactive setup (spec §7 / plan §0-R3): map the dominant wall's surfaced state per side into
            // the (Outcome, valid) pair ReactiveController reads — valid=false for a non-terminal state
            // so a warmup frame can't phantom-fire a fade.
            Outcome waOut, wbOut; bool waValid, wbValid;
            MapOutcome(wallAboveState, out waOut, out waValid);
            MapOutcome(wallBelowState, out wbOut, out wbValid);
            ControllerInputs cin = new ControllerInputs
            {
                WallAbovePrice = ctrlWallAbovePx, WallAboveCurrent = ctrlWallAboveSz,
                WallBelowPrice = ctrlWallBelowPx, WallBelowCurrent = ctrlWallBelowSz,
                AggressorDelta = aggDelta15,
                TapeZScore = _tape.ZScore,
                TapeSpeed = win1s.Prints,   // per-fire CSV instrumentation — the raw rate behind ZScore, not just its z
                TapeAlternations = _book.RecentAlternations(8),
                Mid = pMid, Now = now, Book = _book,
                AdaptiveSignificance = _depthBase.P85,
                TapeAccel = _accel.Acceleration,                            // spec §5 (cluster A read)
                WallAboveOutcome = waOut, WallAboveOutcomeValid = waValid,   // spec §7 (per-side, valid-gated)
                WallBelowOutcome = wbOut, WallBelowOutcomeValid = wbValid
            };
            // spec §4: exactly ONE active controller is Update()-ed per frame; both consume the identical
            // cin bundle. When React is active _controller is not stepped (Break state frozen), and vice
            // versa. The fire latch below is Kind-agnostic — a reactive fire carries SetupKind.Reactive.
            ControllerOutput cout = _activeSetup == SetupKind.Reactive
                ? _reactive.Update(cin)
                : _controller.Update(cin);
            // Round-8: LATCH the fire immediately (still inside the caller's _engineLock). Overwrite
            // latest-wins if a prior pending fire wasn't consumed yet by the paint tick — this is the
            // rare/anomalous case (paint tick starved far longer than one engine run) and gets counted,
            // not silently lost.
            if (cout.Fired)
            {
                if (_pendingFireSet)
                {
                    _droppedFires++;
                    NinjaTrader.Code.Output.Process(
                        string.Format("[Radar] DROPPED FIRE #{0} @ {1:HH:mm:ss} — prior pending fire not yet consumed by the paint tick",
                            _droppedFires, now),
                        NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                }
                _pendingFire = cout.Fire;
                _pendingFireSet = true;
            }
            // Day-1 capture gap: a full arm->drop->veto cycle was observed to complete and reset between
            // two 2s heartbeat rows, leaving no trace in the CSV. Snapshot the state transition BEFORE
            // _lastCtrl is overwritten below, so the sig writer can force an immediate row on any change.
            bool ctrlStateChanged = cout.Long != _lastCtrl.Long || cout.Short != _lastCtrl.Short;
            // Round-7: HoldCount was NEVER observed >0 in 241/241 captured rows despite the engine
            // tracking it every run — the median 38-tick gap between heartbeat/state-change rows was
            // silently skipping over the exact ticks HoldCount moves on. Force a row the instant
            // either side's HoldCount changes, same "snapshot before overwrite" pattern as ctrlStateChanged.
            bool holdChanged = cout.LongHoldCount != _lastLoggedHoldL || cout.ShortHoldCount != _lastLoggedHoldS;
            // React funnel visibility: force a sig row on ANY React state OR abandon-reason transition, so a
            // whole arm->watch->abandon cycle can't hide between two 2s heartbeats (the single most
            // diagnostic requirement — the project learned this the hard way for Break's HoldCount). Compare
            // BEFORE _lastReactState/_lastReactAbandon are updated below, same snapshot-before-overwrite
            // pattern as ctrlStateChanged/holdChanged.
            bool reactStateChanged = _reactive.State != _lastReactState || _reactive.LastAbandon != _lastReactAbandon;
            // ADR 2026-07-03: per-Rec-session funnel counters (arms + fires) for lr-sessions.csv —
            // snapshot the Waiting->Armed edges BEFORE _lastCtrl is overwritten below.
            if (_capture)
            {
                if (cout.Long == SideState.Armed && _lastCtrl.Long == SideState.Waiting) _recArms++;
                if (cout.Short == SideState.Armed && _lastCtrl.Short == SideState.Waiting) _recArms++;
                if (cout.Fired) _recFires++;
                // React funnel counters — only while React is the active setup (_reactive isn't stepped
                // when Break is active, so its state is frozen and these edges can never spuriously fire).
                // arm = Waiting->Watching, abandon = Watching->Cooldown (every such edge in StepWatching is
                // an abandon), fire = React cout.Fired. Uses _lastReactState (previous run) before it's
                // overwritten below, same as the Break counters read _lastCtrl.
                if (_activeSetup == SetupKind.Reactive)
                {
                    if (_reactive.State == ReactState.Watching && _lastReactState == ReactState.Waiting) _recReactArms++;
                    if (_reactive.State == ReactState.Cooldown && _lastReactState == ReactState.Watching) _recReactAbandons++;
                    if (cout.Fired) _recReactFires++;
                }
            }
            _lastCtrl = cout;   // read by the identity-contract lookup above on the NEXT engine run
            _lastReactState = _reactive.State; _lastReactAbandon = _reactive.LastAbandon;   // React equivalents of _lastCtrl
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
                Now   = now,
                WallAbove = wallAbovePx,
                WallBelow = wallBelowPx,
                Ctrl = cout, Fired = cout.Fired, Fire = cout.Fire,
                BuyPerSec = win1s.BuyVol, SellPerSec = win1s.SellVol, TapeZ = _tape.ZScore,
                BookSkew = bookSkew,
                TapeAccel = _accel.Acceleration, Setup = _activeSetup, Banner = CurrentReactBanner(cout)   // spec §5/§9/§10
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
                    bool heartbeat = _lastMidLog == DateTime.MinValue || (now - _lastMidLog).TotalSeconds >= 2;
                    if (heartbeat)
                    {
                        _lastMidLog = now;
                        _capWriter.WriteLine(string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{0},mid,,,,,,,,,,{1:0.00},{2},{3}",
                            now.ToString("o"), mid, medBid, medAsk));
                        // B3: no Flush here — this runs on the instrument thread INSIDE _engineLock, and a
                        // blocking OS write (OneDrive/AV-scanned Documents path) stalls every UI-thread
                        // action contending the same lock (instrument switch, setup swap, Rec toggle).
                        // ponytail: trades a small crash-durability window (unflushed rows since the last
                        // dispose) for removing lock-held blocking I/O — acceptable for an opt-in diagnostic.
                    }
                    // enhanced PressureInputs snapshot (Plan D measurement) — on the 2s heartbeat, OR
                    // immediately when either side's SideState changed this run (ctrlStateChanged, captured
                    // above before _lastCtrl was overwritten), OR (round-7) when either side's HoldCount
                    // moved (holdChanged) — HoldCount had NEVER been observed >0 in 241/241 captured rows
                    // despite the engine tracking it every run; the heartbeat/state-change cadence alone
                    // was silently skipping over the exact ticks it changes on. Day-1 capture proved a full
                    // arm->drop->veto cycle can complete and vanish between two heartbeat rows; these event
                    // triggers guarantee every transition lands a row without touching the heartbeat's own
                    // cadence.
                    if (_capture && _sigWriter != null && (heartbeat || ctrlStateChanged || holdChanged || reactStateChanged))
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
                            "{24:0.00},{25},{26:0.00},{27},{28}," +
                            "{29},{30},{31:0.00},{32:0.00},{33},{34},{35}," +
                            "{36},{37},{38},{39}," +
                            "{40},{41},{42}," +
                            // React telemetry (append-only) — empty/0 while Break is active. C3 (bug-audit
                            // 2026-07-19): reactWallPx/reactWallSide/reactAbandon are now EXPLICITLY gated
                            // on React-active, like reactState — a dormant _reactive FREEZES its last
                            // values at the swap instant (it isn't stepped under Break), so "reads 0/default
                            // naturally" was wrong and the stale values leaked into Break-active rows.
                            "{43},{44:0.0000},{45},{46:0.00},{47},{48},{49},{50},{51},{52}",
                            now.ToString("o"), mid, bidMass, askMass, bestBid, bestAsk, delta15, wf, wabove, wpx,
                            wallAbovePx, wallAboveSz, wallBelowPx, wallBelowSz,
                            cout.LongFraction, cout.LongTradeBacked, cout.ShortFraction, cout.ShortTradeBacked,
                            win1s.Prints, win1s.BuyVol, win1s.SellVol, _tape.ZScore,
                            cout.Long, cout.Short,
                            ctrlWallAbovePx, ctrlWallAboveSz, ctrlWallBelowPx, ctrlWallBelowSz, cin.TapeAlternations,
                            cout.LongHoldCount, cout.ShortHoldCount, cout.LongDistTicks, cout.ShortDistTicks,
                            cout.LongCooldownUntil == DateTime.MinValue ? "" : cout.LongCooldownUntil.ToString("o"),
                            cout.ShortCooldownUntil == DateTime.MinValue ? "" : cout.ShortCooldownUntil.ToString("o"),
                            _chartTrader.IsAutoArmed,
                            cout.LongPeak, cout.LongMin, cout.ShortPeak, cout.ShortMin,
                            src, seq, cin.AdaptiveSignificance,
                            _activeSetup, cin.TapeAccel,
                            _activeSetup == SetupKind.Reactive ? _reactive.State.ToString() : "",
                            _activeSetup == SetupKind.Reactive ? _reactive.LatchedWallPrice : 0.0,
                            _activeSetup == SetupKind.Reactive ? _reactive.LatchedSide.ToString() : "",
                            _activeSetup == SetupKind.Reactive ? _reactive.LastAbandon.ToString() : "",
                            waOut, waValid, wbOut, wbValid));
                        // B3: Flush removed — same lock-held-blocking-I/O rationale as the cap-writer above.
                        _lastLoggedHoldL = cout.LongHoldCount;
                        _lastLoggedHoldS = cout.ShortHoldCount;
                    }
                }
                catch { }
            }
        }

        // Channel branding lockup — the "Traders de Futuros" logo (channel-logo.jpg, deployed next to
        // the .cs files) + the channel wordmark, landscape, sized for the gap between the cockpit's
        // book-skew strip and the Chart Trader. Fail-soft: missing/corrupt image = no branding, the
        // rest of the tab is untouched. OnLoad caching releases the file handle immediately.
        private static UIElement BuildBranding()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "bin", "Custom", "AddOns", "LiquidityRadar", "channel-logo.jpg");
                if (!System.IO.File.Exists(path)) return null;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                // Sized for the real gap under the book-skew strip (~55px in the default layout) —
                // a taller lockup rides up over the strip (2026-07-03 screenshot feedback).
                var logo = new Border
                {
                    Width = 44, Height = 44,
                    CornerRadius = new CornerRadius(8),
                    Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
                    VerticalAlignment = VerticalAlignment.Center
                };
                var ink = new SolidColorBrush(Color.FromRgb(0xf2, 0xf5, 0xfa));
                var l1 = new TextBlock { Text = "TRADERS", Foreground = ink,
                    FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 18 };
                var l2 = new TextBlock { Text = "DE FUTUROS", Foreground = ink,
                    FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 18,
                    Margin = new Thickness(0, -4, 0, 0) };
                var words = new StackPanel { Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center, Children = { l1, l2 } };
                var lockup = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { logo, words }
                };

                // Smoke layer — the logo's neon-lit smoke, recreated live: soft radial blobs in the
                // logo's blue and magenta drifting endlessly behind the lockup. Transform/opacity
                // animations run on WPF's render thread (independent animation) — no per-tick CPU work
                // lands on the paint/engine threads.
                // The blobs live on a CANVAS (not a Grid): WPF's Measure pass clamps a child's
                // DesiredSize to the available slot, so inside a Grid the 100-120px-tall blobs were
                // silently squashed to the card's 58px and the up-shifted transforms left the smoke's
                // bottom edge stranded mid-card (2026-07-03 screenshot: "cortado por la mitad"). A
                // Canvas measures children with infinite space — blobs keep their full size and are
                // centered on the card's vertical middle, overflowing both edges symmetrically.
                var smokeLayer = new Canvas();
                AddSmokeBlob(smokeLayer, Color.FromRgb(0x2f, 0x4d, 0x9e), 280, 130, -90, 0.42, 17);   // deep blue
                AddSmokeBlob(smokeLayer, Color.FromRgb(0xa0, 0x30, 0x60), 320, 140,  160, 0.40, 23);  // wine magenta
                AddSmokeBlob(smokeLayer, Color.FromRgb(0x24, 0x3a, 0x77), 240, 120,   40, 0.34, 28);  // blue, slower
                AddSmokeBlob(smokeLayer, Color.FromRgb(0xc2, 0x3d, 0x78), 260, 120,  340, 0.36, 13);  // magenta, faster
                var vignette = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.62, RadiusY = 1.05
                };
                vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.45));
                vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
                vignette.Freeze();
                smokeLayer.OpacityMask = vignette;

                var smokeHost = new Grid { ClipToBounds = true };
                smokeHost.Children.Add(smokeLayer);
                smokeHost.Children.Add(lockup);

                return new Border
                {
                    Height = 58,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(8, 0, 8, 4),
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromRgb(0x0a, 0x0e, 0x16)),
                    Child = smokeHost
                };
            }
            catch { return null; }   // branding must never take down the tab
        }

        // The branding card's fixed height — blob vertical centering below keys off it.
        private const double BrandingCardHeight = 58;

        // One drifting smoke puff: an oversized ellipse filled with a frozen radial gradient (color
        // core fading to transparent), sliding back and forth across the card on a slow sine while its
        // opacity breathes. AutoReverse + Forever = the infinite loop; staggered durations keep the
        // four blobs from ever synchronizing, which is what reads as "smoke" instead of "carousel".
        // Host must be a Canvas (infinite measure — see the caller's comment); each blob is centered
        // on the card's vertical middle so it overflows top and bottom symmetrically.
        private static void AddSmokeBlob(Canvas host, Color c, double width, double height,
            double fromX, double maxOpacity, double driftSeconds)
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, c.R, c.G, c.B), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1.0));
            brush.Freeze();
            var blob = new System.Windows.Shapes.Ellipse
            {
                Width = width, Height = height, Fill = brush,
                Opacity = maxOpacity,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(blob, 0);
            Canvas.SetTop(blob, (BrandingCardHeight - height) / 2.0);   // vertical center on the card
            var slide = new TranslateTransform(fromX, 0);
            blob.RenderTransform = slide;
            host.Children.Add(blob);

            var drift = new System.Windows.Media.Animation.DoubleAnimation(
                fromX, fromX + 380, TimeSpan.FromSeconds(driftSeconds))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                AutoReverse = true,
                EasingFunction = new System.Windows.Media.Animation.SineEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };
            slide.BeginAnimation(TranslateTransform.XProperty, drift);

            var breathe = new System.Windows.Media.Animation.DoubleAnimation(
                maxOpacity * 0.5, maxOpacity, TimeSpan.FromSeconds(driftSeconds * 0.6))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                AutoReverse = true,
                EasingFunction = new System.Windows.Media.Animation.SineEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };
            blob.BeginAnimation(UIElement.OpacityProperty, breathe);
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
            _accel      = new TapeAcceleration(0.1);   // rewound history must not inherit the pre-rewind accel EWMA
            // Same instrument as before the reset — recompute its preset rather than assuming ES, so a
            // replay reset on NQ doesn't silently drop back to ES's ControllerConfig. _pressure is left
            // alone: PressureConfig doesn't change on a same-instrument reset (only _controller carries
            // reset-sensitive per-run state).
            var resetPreset = InstrumentPresets.For(_instrument != null ? _instrument.MasterInstrument.Name : "ES");
            _controller = new ControllerStateMachine(resetPreset.Controller, _cfg.TickSize);
            _reactive   = new ReactiveController(new ReactiveConfig(), _cfg.TickSize);   // rebuild both; _activeSetup preserved (re-apply)
            _lastCtrl   = default(ControllerOutput);
            _lastReactState = _reactive.State; _lastReactAbandon = _reactive.LastAbandon;   // resync to the rebuilt React controller
            _lastDiag   = DateTime.MinValue;
            _lastMidLog = DateTime.MinValue;
            _medianEwma = 0;
            _depthBase.Reset();               // rewound history must not inherit the pre-rewind size distribution
            _lastDepthSample = DateTime.MinValue;
            _latest     = null;
            _pendingFireSet = false;   // a stale pre-rewind fire must not be delivered into replayed history
            _replayResetPending = true;   // paint tick (UI thread) drops RadarVisual's ladder memory + anchor
            NinjaTrader.Code.Output.Process(
                string.Format("[Radar] replay reset detected @ {0:HH:mm:ss} — book+tracker reseeded", now),
                NinjaTrader.NinjaScript.PrintTo.OutputTab1);
        }

        // ADR 2026-07-03 Phase 0: one summary row per Rec session (lr-sessions.csv) plus the quiet-
        // degradation alarm — fires=0 across 3 consecutive Rec sessions that still ARMED normally
        // means the fire gates (not the market) are suffocating the funnel. Report-only: it writes a
        // flag row and an Output line, it never touches the Controller or the AUTO gates.
        // Wall-clock DateTime.Now is deliberate here (session bookkeeping, not market data).
        private void WriteSessionSummary()
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "LiquidityRadar");
                System.IO.Directory.CreateDirectory(dir);
                // Per-instrument file: two concurrent windows (e.g. ES + NQ) must not share one
                // distinct-day count / fires=0 alarm streak — each instrument gets its own funnel.
                string instName = _instrument != null ? _instrument.MasterInstrument.Name : "X";
                string path = System.IO.Path.Combine(dir, "lr-sessions-" + instName + ".csv");
                bool fresh = !System.IO.File.Exists(path);
                // Snapshot + reset under the same lock the instrument thread increments with, so an
                // in-flight engine run can't have its count clobbered by this read-then-reset.
                int arms, fires, rArms, rFires, rAbandons;
                lock (_engineLock)
                {
                    arms = _recArms; fires = _recFires;
                    rArms = _recReactArms; rFires = _recReactFires; rAbandons = _recReactAbandons;
                    _recArms = 0; _recFires = 0;
                    _recReactArms = 0; _recReactFires = 0; _recReactAbandons = 0;
                }
                // This session counts as "quiet" only if it armed but never fired; then extend the
                // streak backwards over the previous rows until a firing (or arm-less) session breaks it.
                int zeroStreak = (fires == 0 && arms > 0) ? 1 : 0;
                if (!fresh && zeroStreak > 0)
                {
                    string[] lines = System.IO.File.ReadAllLines(path);
                    for (int i = lines.Length - 1; i >= 1; i--)
                    {
                        string[] p = lines[i].Split(',');
                        long rowArms, rowFires;
                        if (p.Length < 4 || !long.TryParse(p[2], out rowArms) || !long.TryParse(p[3], out rowFires)) continue;
                        if (rowFires == 0 && rowArms > 0) zeroStreak++; else break;
                    }
                }
                string alert = zeroStreak >= 3
                    ? "ALERT: fires=0 for " + zeroStreak + " consecutive Rec sessions with arms present — check the fire gates before blaming the market"
                    : "";
                using (var w = new System.IO.StreamWriter(path, true))
                {
                    // React funnel columns APPENDED after `alert` (append-only: the streak parser above
                    // only reads p[2]/p[3] = arms/fires, so a Break-only parser is unaffected).
                    if (fresh) w.WriteLine("time,instrument,arms,fires,alert,reactArms,reactFires,reactAbandons");
                    w.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7}",
                        DateTime.Now.ToString("o"),
                        instName,
                        arms, fires, alert,
                        rArms, rFires, rAbandons));   // the snapshot locals — all counters were zeroed under the lock above
                }
                if (alert.Length > 0)
                    NinjaTrader.Code.Output.Process("[Radar] " + alert, NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                _recArms = 0; _recFires = 0;
                _recReactArms = 0; _recReactFires = 0; _recReactAbandons = 0;
            }
            catch { }   // diagnostics must never take down the tab
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
                " medBid={7} medAsk={8} | MinAbs={9} Kx={10} nodes={11} dropped={12}",
                now, _depthEvents, _tradeEvents, bids.Count, asks.Count, maxB, maxA,
                _book.MedianSize(Side.Bid), _book.MedianSize(Side.Ask),
                _cfg.MinAbsSize, _cfg.K_mult, nodes, _droppedFires);
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
            Unsubscribe();
            _instrument = null; // leaked/late handlers hit the e.Instrument != _instrument guard and drop everything
            _capture = false;
            if (_capWriter != null) { _capWriter.Flush(); _capWriter.Dispose(); _capWriter = null; }
            if (_sigWriter != null) { _sigWriter.Flush(); _sigWriter.Dispose(); _sigWriter = null; }
            _selector.InstrumentChanged -= OnSelectorChanged;
            if (_paintTimer != null) { _paintTimer.Stop(); _paintTimer = null; }
            if (_chartTrader != null) _chartTrader.Cleanup();
            base.Cleanup();
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
            // Always-armed AUTO (2026-07-03, verdict doc item 6): restore the user's own AUTO intent +
            // account/ATM/HOURS/qty so a reopened workspace can re-arm itself instead of silently
            // staying off until the next manual checkbox click (52% of measured guard_skips were
            // exactly this gap). AFTER the instrument restore above, on purpose — see RestoreAutoState.
            if (_chartTrader != null)
            {
                XElement autoEl = element.Element("RadarAuto");
                if (autoEl != null)
                {
                    try
                    {
                        _chartTrader.RestoreAutoState(
                            (bool?)autoEl.Attribute("armed") ?? false,
                            (string)autoEl.Attribute("account"),
                            (string)autoEl.Attribute("atm"),
                            (int?)autoEl.Attribute("qty") ?? 1,
                            (bool?)autoEl.Attribute("hours") ?? true,
                            (string)autoEl.Attribute("hoursStart"),
                            (string)autoEl.Attribute("hoursEnd"),
                            (string)autoEl.Attribute("hoursFlat"),
                            (string)autoEl.Attribute("fireDay"),
                            (int?)autoEl.Attribute("fireCount") ?? 0,
                            (int?)autoEl.Attribute("cap") ?? 0,
                            (string)autoEl.Attribute("lossLimit"),
                            (string)autoEl.Attribute("profitLimit"),
                            (bool?)autoEl.Attribute("moneyMgmt") ?? true);
                    }
                    catch (Exception ex)
                    {
                        // Minor finding: a malformed/hand-edited RadarAuto attribute (bad bool/int cast)
                        // must not abort the whole Restore() call — fall back to defaults, same as a
                        // missing element entirely.
                        NinjaTrader.Code.Output.Process("[Radar] RadarAuto restore failed: " + ex.Message,
                            NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                    }
                }
            }
        }

        protected override void Save(XElement element)
        {
            if (element == null) return;
            if (_instrument != null)
                element.Add(new XElement("RadarInstrument", _instrument.FullName));
            if (_chartTrader != null)
                element.Add(new XElement("RadarAuto",
                    new XAttribute("armed", _chartTrader.AutoIntentArmed),
                    new XAttribute("account", _chartTrader.SelectedAccountName ?? string.Empty),
                    new XAttribute("atm", _chartTrader.SelectedAtmName ?? string.Empty),
                    new XAttribute("qty", _chartTrader.Qty),
                    new XAttribute("hours", _chartTrader.HoursEnabled),
                    new XAttribute("hoursStart", _chartTrader.HoursStartText),
                    new XAttribute("hoursEnd", _chartTrader.HoursEndText),
                    new XAttribute("hoursFlat", _chartTrader.HoursFlatText),
                    new XAttribute("fireDay", _chartTrader.AutoFireDayText),
                    new XAttribute("fireCount", _chartTrader.AutoFireCount),
                    new XAttribute("cap", _chartTrader.AutoCap),
                    new XAttribute("lossLimit", _chartTrader.LossLimitText ?? string.Empty),
                    new XAttribute("profitLimit", _chartTrader.ProfitLimitText ?? string.Empty),
                    new XAttribute("moneyMgmt", _chartTrader.MoneyMgmtEnabled)));
        }
    }
}
