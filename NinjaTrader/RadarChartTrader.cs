using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NinjaTrader.Cbi;
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    // Order-entry surface docked under the Cockpit (spec §8). MKT + wall-anchored LMT, Sim/Playback-first.
    // Real Account API: CreateOrder()+Submit()/Change()/Cancel() (no strategy Enter*/Exit* — Add-On, not a Strategy).
    // Hard gate: submits to a non-Sim/non-Playback account are blocked unless "ARM LIVE" is checked.
    public class RadarChartTrader : Grid
    {
        private static readonly SolidColorBrush Emerald = Brush(0x34, 0xd3, 0x99);
        private static readonly SolidColorBrush Coral    = Brush(0xfb, 0x71, 0x85);
        private static readonly SolidColorBrush Amber    = Brush(0xf5, 0xa8, 0x23);   // AUTO-armed accent
        private static readonly SolidColorBrush Ink      = Brush(0x0f, 0x14, 0x20);
        private static readonly SolidColorBrush Panel    = Brush(0x12, 0x18, 0x26);
        private static readonly SolidColorBrush Muted    = Brush(0x9a, 0xa4, 0xb2);
        private static readonly SolidColorBrush TextCol  = Brush(0xcf, 0xd6, 0xe2);
        private static readonly SolidColorBrush BorderBr = new SolidColorBrush(Color.FromArgb(30, 0xff, 0xff, 0xff));

        // Neon Aurora tokens — translucent tint fill + glowing colored border + glowing colored text
        // (matches docs/mockups/radar-cockpit-demo.html .bigbtn.buy/.sell). NOT solid fills.
        private static readonly Brush BuyFill   = Frozen(Color.FromArgb(33,  0x34, 0xd3, 0x99));  // ~.13
        private static readonly Brush BuyHover  = Frozen(Color.FromArgb(61,  0x34, 0xd3, 0x99));  // ~.24
        private static readonly Brush BuyBorder = Frozen(Color.FromArgb(140, 0x34, 0xd3, 0x99));  // ~.55
        private static readonly Brush BuyText   = Frozen(Color.FromRgb(0x7e, 0xf0, 0xc0));
        private static readonly Brush SellFill  = Frozen(Color.FromArgb(33,  0xfb, 0x71, 0x85));
        private static readonly Brush SellHover = Frozen(Color.FromArgb(61,  0xfb, 0x71, 0x85));
        private static readonly Brush SellBorder= Frozen(Color.FromArgb(140, 0xfb, 0x71, 0x85));
        private static readonly Brush SellText  = Frozen(Color.FromRgb(0xff, 0xb0, 0xbb));
        // LMT buttons: same hue, hollow/lighter fill + no glow — visually distinct from the glowing MKT pills.
        private static readonly Brush BuyLmtFill   = Frozen(Color.FromArgb(10, 0x34, 0xd3, 0x99));
        private static readonly Brush BuyLmtHover  = Frozen(Color.FromArgb(30, 0x34, 0xd3, 0x99));
        private static readonly Brush SellLmtFill  = Frozen(Color.FromArgb(10, 0xfb, 0x71, 0x85));
        private static readonly Brush SellLmtHover = Frozen(Color.FromArgb(30, 0xfb, 0x71, 0x85));
        private static readonly Brush MgFill    = Frozen(Color.FromArgb(8,   0xff, 0xff, 0xff));  // ~.03
        private static readonly Brush MgHover   = Frozen(Color.FromArgb(18,  0xff, 0xff, 0xff));  // ~.07
        private static readonly Brush MgText    = Frozen(Color.FromRgb(0xc3, 0xca, 0xd6));
        private static readonly Brush PanelLine = Frozen(Color.FromArgb(28,  0xff, 0xff, 0xff));
        private static readonly Brush PnlBarBg  = Frozen(Color.FromArgb(6,   0xff, 0xff, 0xff));

        private static SolidColorBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private Instrument _instrument;
        private Account _account;
        private Account _armedFor;      // account the user explicitly armed via the checkbox — per-account, not sticky
        private double _lastPrice;      // book mid, pushed by RadarTab's paint timer (SetContext)
        private double _wallAbove;      // price of the biggest wall above mid this engine run (0 = none)
        private double _wallBelow;      // price of the biggest wall below mid this engine run (0 = none)
        private double _tick;
        private Order _activeLimit;     // the one working limit order this control currently has resting
        private readonly HashSet<Order> _workingOrders = new HashSet<Order>(); // in-flight orders this control submitted
        // Durable ownership: every Order this control ever created via SubmitRaw, kept until terminal.
        // OnOrderUpdate gates on this FIRST — an ATM's stop/target legs (or any other order on the same
        // account/instrument, e.g. AbsorptionScalper) are never ours and must never become _activeLimit.
        private readonly HashSet<Order> _ownOrders = new HashSet<Order>();
        private bool _atmUserPicked;    // true only once the user has actually opened+closed the ATM dropdown
                                         // with a template selected — a stale/auto-selected item never attaches

        // A same-side LMT re-anchor uses Account.Change() directly (atomic). An opposite-side flip must
        // cancel the old order first and only THEN submit the new one — stashed here and fired from
        // OnOrderUpdate once the cancel is confirmed (WaitingOn == the order we're cancelling).
        private sealed class PendingReplace
        {
            public OrderAction Action;
            public int Qty;
            public double Price;
            public string Tag;
            public Order WaitingOn;
        }
        private PendingReplace _pendingReplace;

        // Task 12: a Controller fire pre-stages (never submits) a break-direction limit for the NEXT
        // manual BUY/SELL LMT click — price+side only, no Order object, nothing touches the Account.
        private sealed class PendingSetup
        {
            public bool IsBuy;
            public double Price;
        }
        private PendingSetup _pendingSetup;
        // Dedupe guard: ControllerOutput.Fired is one-shot per engine run (~20Hz), but the UI paint tick
        // (~30Hz) can read the same _latest Frame more than once before the engine swaps it in, so
        // OnSetupFire can be called twice with the IDENTICAL FireEvent. FireEvent.Time is unique per fire.
        private DateTime _lastFireTime = DateTime.MinValue;

        // Exit-leg instrumentation (2026-07-03, multi-day verdict item 1): one open AUTO trade at a
        // time, matching TryAutoFire's own "busy" guard (a new AUTO entry never fires while a position
        // is open). Created the moment the AUTO entry order fills (OnOrderUpdate), consumed/accumulated
        // by opposite-side executions observed in OnExecutionUpdate (ATM stop/target legs included —
        // those orders are never in _ownOrders, so this is the one place that can see them), cleared
        // once ExitQty catches up to EntryQty.
        private sealed class AutoTrade
        {
            public long EntryOrderId;
            public bool IsLong;
            public double EntryPrice;
            public int EntryQty;
            public DateTime EntryTime;   // replay-aware _now at entry fill
            public int ExitQty;
            public double ExitNotional;  // sum(price*qty) across exit fills — running weighted-avg exit price
        }
        private AutoTrade _openAutoTrade;

        // AUTO mode (2026-07-01, Sim/Playback only) — auto-submits the pre-stage above through the SAME
        // SubmitLimit -> SubmitRaw path a manual click uses. See TryAutoFire/TryArmAuto below.
        // volatile: written only from the UI thread (checkbox/TryArmAuto/SetAutoArmed), but read via
        // IsAutoArmed from RadarTab's MaybeRunEngine on the INSTRUMENT thread (round-3 diagnosis: the sig
        // CSV needs "was AUTO armed" alongside every row). Same cross-thread-visibility pattern RadarTab
        // already uses for _instrument/_latest/_replayResetPending — this is a diagnostic read, not a
        // decision gate, so volatile's eventual-visibility guarantee is enough; no lock/marshal needed.
        private volatile bool _autoArmed;
        private bool _suppressAutoChkEvent;      // true while SetAutoArmed programmatically (re)sets the checkbox
        private DateTime _now;                   // replay-aware "now", pushed every SetContext tick — NOT wall clock
        private DateTime _autoFireDay = DateTime.MinValue;   // REPLAY date the fire count below is keyed to
        private int _autoFireCount;
        private Order _autoOrder;                // the one working limit this control auto-submitted (null = none)
        private DateTime _autoSubmittedAt;        // replay time of that auto-submit, for the auto-cancel timeout

        // ---- always-armed AUTO (2026-07-03, verdict doc item 6) ----
        // _autoIntent is the USER's persisted decision to run AUTO — distinct from the live _autoArmed
        // flag, which a fail-closed guard can drop at any moment (non-Sim, ATM->None, daily cap, 16:00
        // flatten). Only a genuine human uncheck of the AUTO box clears intent (see SetAutoArmed); every
        // other disarm leaves it standing so MaybeAutoRearm can re-arm the instant preconditions repair.
        private bool _autoIntent;
        private string _lastPickedAtmName;        // last ATM template the user genuinely selected (DropDownClosed) — persisted; survives an in-session ATM reset
        private string _pendingAtmRestoreName;     // set by RestoreAutoState; resolved against _atmSelector.Items on the SetContext tick once the control repopulates
        private string _pendingRestoreAccountName; // set by RestoreAutoState; resolved by PopulateAccounts once Account.All contains it
        // Blocker finding: _autoFireDay/_autoFireCount are in-memory only, but _autoIntent is persisted —
        // a mid-day workspace reopen re-armed with a FRESH daily cap, letting AUTO fire past the real
        // per-day limit. Persisted alongside the rest of RadarAuto; consumed the first time TryAutoFire
        // evaluates a fire this session, ONLY if the persisted day still matches the fire's replay date.
        private DateTime _pendingRestoreFireDay = DateTime.MinValue;
        private int _pendingRestoreFireCount;
        // Persistent AUTO decision trail (round-3 diagnosis: 3 engine fires, zero positions, no way to
        // tell why beyond NT8's Output window). Lazily opened on first AUTO log event — see LogAuto below.
        private System.IO.StreamWriter _autoLogWriter;
        private bool _cleanedUp;                 // closed for good — a late marshaled event must not reopen the log

        // Threading note: see the volatile comment on _autoArmed above.
        public bool IsAutoArmed { get { return _autoArmed; } }

        private readonly ComboBox _accountCombo = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
        private SelectionChangedEventHandler _accountSelectionHandler;   // see PopulateAccounts
        private readonly CheckBox _armChk = new CheckBox { Content = "ARM LIVE", Visibility = Visibility.Collapsed,
            Foreground = Coral, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 0) };
        private readonly TextBlock _warnText = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), Foreground = Muted };
        private readonly TextBox _qtyBox = new TextBox { Text = "1", Width = 40, TextAlignment = TextAlignment.Center,
            Background = Ink, Foreground = TextCol, BorderBrush = BorderBr,
            VerticalContentAlignment = VerticalAlignment.Center };
        // "SETUP LONG/SHORT listo" indicator for a pending Controller-fire pre-stage (Task 12). Doubles
        // as the manual-clear affordance — click it to drop the pre-stage without touching either button.
        private readonly TextBlock _setupText = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2), Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
        // AUTO arm toggle + status label (why it's off when the system disarmed it) — see TryArmAuto.
        // Foreground toggles Muted/Amber with armed state in SetAutoArmed (amber = the "live" accent).
        private readonly CheckBox _autoChk = new CheckBox { Content = "AUTO",
            Foreground = Muted, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBlock _autoStatusText = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
        // Trading-hours schedule for AUTO (2026-07-03): fires allowed only inside [start, end]; any
        // open position / working limit force-flattened once per day at the flat time. All three times
        // are judged against the REPLAY-aware clock (_now / FireEvent.Time) — i.e. the platform/feed
        // timezone (ET on this setup) — never wall clock. UI row lives directly under the AUTO toggle.
        private readonly CheckBox _hoursChk = new CheckBox { Content = "HOURS", IsChecked = true,
            Foreground = Muted, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBox _hoursStartBox = MakeTimeBox("09:30");
        private readonly TextBox _hoursEndBox   = MakeTimeBox("15:55");
        private readonly TextBox _hoursFlatBox  = MakeTimeBox("16:00");
        private TimeSpan _hoursStart = new TimeSpan(9, 30, 0);
        private TimeSpan _hoursEnd   = new TimeSpan(15, 55, 0);
        private TimeSpan _hoursFlat  = new TimeSpan(16, 0, 0);
        private DateTime _hoursFlattenDay = DateTime.MinValue;   // replay date already flatten-attempted (once per day, never a retry loop)

        private static TextBox MakeTimeBox(string text)
        {
            // No fixed Width — each box sits in a star column of the HOURS row and stretches so the
            // row fills the ticket's full width instead of clustering left.
            return new TextBox { Text = text, MinWidth = 44, Height = 22, FontSize = 11,
                TextAlignment = TextAlignment.Center, Background = Ink, Foreground = TextCol,
                BorderBrush = BorderBr, VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0) };
        }

        private static TextBlock MakeHoursLabel(string text)
        {
            return new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
                Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        }
        private readonly Button _buyBtn;
        private readonly Button _sellBtn;
        private readonly Button _buyLmtBtn;
        private readonly Button _sellLmtBtn;
        private readonly Button _upBtn;
        private readonly Button _dnBtn;
        private readonly Button _revBtn;
        private readonly Button _closeBtn;
        private readonly Button _flatBtn;
        private readonly TextBlock _posText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13,
            FontWeight = FontWeights.SemiBold, Foreground = Muted, Text = "FLAT" };
        private readonly TextBlock _pnlText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Foreground = Muted };
        // The real platform control (NinjaTrader.Gui.dll, confirmed via decompile: "AtmStrategySelector : ComboBox"
        // with settable Account/Instrument props) — it self-populates from the templates available for
        // Account+Instrument, so no manual template enumeration is needed. Unselected = "None" (no ATM).
        private readonly NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector _atmSelector =
            new NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector();

        public RadarChartTrader()
        {
            Background = Panel;
            for (int i = 0; i < 7; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _buyBtn     = MakeNeonButton("BUY MKT",  BuyFill,    BuyHover,    BuyBorder,  BuyText,  40, 14, 9, Emerald.Color);
            _sellBtn    = MakeNeonButton("SELL MKT", SellFill,   SellHover,   SellBorder, SellText, 40, 14, 9, Coral.Color);
            _buyLmtBtn  = MakeNeonButton("BUY LMT",  BuyLmtFill, BuyLmtHover, BuyBorder,  BuyText,  40, 13, 9, null);
            _sellLmtBtn = MakeNeonButton("SELL LMT", SellLmtFill,SellLmtHover,SellBorder, SellText, 40, 13, 9, null);
            _upBtn      = MakeNeonButton("▲", MgFill, MgHover, BorderBr, MgText, 40, 16, 9, null);
            _dnBtn      = MakeNeonButton("▼", MgFill, MgHover, BorderBr, MgText, 40, 16, 9, null);
            _revBtn     = MakeManageButton("Rev");
            _closeBtn   = MakeManageButton("Close");
            _flatBtn    = MakeManageButton("Flat");

            _buyBtn.Click     += (o, e) => SubmitMarket(OrderAction.Buy, "Buy");
            _sellBtn.Click    += (o, e) => SubmitMarket(OrderAction.Sell, "Sell");
            _buyLmtBtn.Click  += (o, e) => SubmitLimit(true);
            _sellLmtBtn.Click += (o, e) => SubmitLimit(false);
            _upBtn.Click      += (o, e) => MoveOrder(1);
            _dnBtn.Click      += (o, e) => MoveOrder(-1);
            _revBtn.Click     += (o, e) => Reverse();
            _closeBtn.Click   += (o, e) => ClosePosition(false);
            _flatBtn.Click    += (o, e) => ClosePosition(true, isManualKill: true);
            _setupText.MouseLeftButtonDown += (o, e) => ClearPendingSetup();   // manual clear affordance

            _accountCombo.DisplayMemberPath = "Name";
            _accountCombo.Background = Ink;
            _accountCombo.Foreground = TextCol;
            _accountCombo.HorizontalAlignment = HorizontalAlignment.Stretch;
            // Stored (not an inline lambda) so PopulateAccounts can detach/reattach it around the
            // ItemsSource reassignment blip — see PopulateAccounts.
            _accountSelectionHandler = (o, e) => OnAccountSelected();
            _accountCombo.SelectionChanged += _accountSelectionHandler;
            _armChk.Checked   += (o, e) => { _armedFor = _account; RefreshArmUi(); };
            _armChk.Unchecked += (o, e) => { _armedFor = null; RefreshArmUi(); };
            _autoChk.Checked   += (o, e) => { if (!_suppressAutoChkEvent) TryArmAuto(); };
            _autoChk.Unchecked += (o, e) => { if (!_suppressAutoChkEvent) SetAutoArmed(false, null); };

            Action commitQty = () =>
            {
                int q;
                if (!int.TryParse(_qtyBox.Text, out q) || q < 1) q = 1;
                _qtyBox.Text = q.ToString();
            };
            _qtyBox.LostFocus += (o, e) => commitQty();
            _qtyBox.KeyDown   += (o, e) => { if (e.Key == Key.Enter) commitQty(); };

            // Trading-hours boxes: parse "H:mm" on commit, revert/normalize to the effective value on
            // any invalid input (fail-closed — a typo never silently disables the schedule).
            Action<TextBox, Action<TimeSpan>, Func<TimeSpan>> wireTime = (box, set, get) =>
            {
                Action commit = () =>
                {
                    DateTime parsed;
                    if (DateTime.TryParseExact(box.Text.Trim(), "H:mm",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out parsed))
                        set(parsed.TimeOfDay);
                    TimeSpan eff = get();
                    box.Text = string.Format("{0:00}:{1:00}", eff.Hours, eff.Minutes);
                };
                box.LostFocus += (o, e) => commit();
                box.KeyDown   += (o, e) => { if (e.Key == Key.Enter) commit(); };
            };
            wireTime(_hoursStartBox, v => _hoursStart = v, () => _hoursStart);
            wireTime(_hoursEndBox,   v => _hoursEnd = v,   () => _hoursEnd);
            wireTime(_hoursFlatBox,  v => _hoursFlat = v,  () => _hoursFlat);

            // Row 0: BUY MKT / SELL MKT — the primary order entry, at the very top (like NT8's Chart Trader).
            AddRow(0, TwoColRow(_buyBtn, _sellBtn, 8, 4));

            // Row 1: BUY LMT / SELL LMT — wall-anchored (see LimitAnchorPrice()), plus the "listo"
            // pre-stage indicator (Task 12). The AUTO arm toggle lives in Row 4 next to the ATM selector.
            AddRow(1, new StackPanel { Children = { TwoColRow(_buyLmtBtn, _sellLmtBtn, 8, 0), _setupText } });

            // Row 2: ▲ / ▼ — move the active working limit 1 tick; disabled when none is working.
            AddRow(2, TwoColRow(_upBtn, _dnBtn, 8, 0));

            // Row 3: Rev / Close / Flat — 3 equal columns, wrapped in a bordered card (same treatment as
            // the PnL bar below) so it reads as a grouped panel like the rest of the surface, not bare.
            {
                var row = new Grid();
                for (int c = 0; c < 3; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _revBtn.Margin   = new Thickness(0, 0, 3, 0);
                _closeBtn.Margin = new Thickness(3, 0, 3, 0);
                _flatBtn.Margin  = new Thickness(3, 0, 0, 0);
                SetColumn(_revBtn, 0);   row.Children.Add(_revBtn);
                SetColumn(_closeBtn, 1); row.Children.Add(_closeBtn);
                SetColumn(_flatBtn, 2);  row.Children.Add(_flatBtn);
                AddRow(3, new Border { Background = PnlBarBg, BorderBrush = PanelLine, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(8, 4, 8, 4), Child = row });
            }

            // Row 4: account + qty (top row) with the ATM selector directly below the account, sharing the
            // same left column width (not full-width). Qty controls enlarged for a uniform, structured look.
            {
                var qtyLbl = new TextBlock { Text = "Qty:", Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12, Foreground = Muted };
                var minus = MakeManageButton("−"); minus.Width = 34; minus.Height = 30; minus.FontSize = 16; minus.Margin = new Thickness(0, 0, 3, 0);
                var plus  = MakeManageButton("+");  plus.Width  = 34; plus.Height  = 30; plus.FontSize  = 16; plus.Margin  = new Thickness(3, 0, 0, 0);
                minus.Click += (o, e) => StepQty(-1);
                plus.Click  += (o, e) => StepQty(1);
                _qtyBox.Width = 54; _qtyBox.Height = 30; _qtyBox.FontSize = 15;
                var qtyGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                    Children = { qtyLbl, minus, _qtyBox, plus } };

                // ATM label + selector — fills the account's (left) column so it matches the account width.
                var atmLbl = new TextBlock { Text = "ATM", Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11, Foreground = Muted };
                DockPanel.SetDock(atmLbl, Dock.Left);
                _atmSelector.Background = Ink;
                _atmSelector.Foreground = TextCol;
                _atmSelector.BorderBrush = PanelLine;
                _atmSelector.HorizontalAlignment = HorizontalAlignment.Stretch;
                // F16: only DropDownClosed (a real open+close by the user) can arm ATM attach — the
                // control's own async repopulation (on Account/Instrument push) never fires this event.
                _atmSelector.DropDownClosed += (o, e) =>
                {
                    _atmUserPicked = _atmSelector.SelectedAtmStrategy != null;
                    if (_atmUserPicked)
                    {
                        _lastPickedAtmName = _atmSelector.SelectedAtmStrategy.Name;
                        MaybeAutoRearm("atm re-pick");   // a broken ATM precondition just repaired — see method banner
                    }
                    else
                        ForceDisarmAuto("ATM en None");   // guard 2 of the AUTO arm precondition broke
                };
                // Same right margin as the account combo so both combos share the same right edge.
                var atmRow = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
                atmRow.Children.Add(atmLbl);
                atmRow.Children.Add(_atmSelector);

                _accountCombo.Margin = new Thickness(0, 0, 10, 6);   // gap to qty (right) + gap above ATM

                // AUTO toggle + status sit under the qty group, beside the ATM selector.
                var autoGroup = new WrapPanel { VerticalAlignment = VerticalAlignment.Center,
                    Children = { _autoChk, _autoStatusText } };

                var grid = new Grid { Margin = new Thickness(8, 4, 8, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(_accountCombo, 0); Grid.SetRow(_accountCombo, 0); grid.Children.Add(_accountCombo);
                Grid.SetColumn(qtyGroup, 1);      Grid.SetRow(qtyGroup, 0);      grid.Children.Add(qtyGroup);
                Grid.SetColumn(atmRow, 0);        Grid.SetRow(atmRow, 1);        grid.Children.Add(atmRow);
                Grid.SetColumn(autoGroup, 1);     Grid.SetRow(autoGroup, 1);     grid.Children.Add(autoGroup);

                // Trading-hours row — directly under the ATM/AUTO row, spanning both columns and
                // stretching edge to edge: labels take their natural width, the three time boxes each
                // fill a star column so the line covers the ticket's full width.
                var hoursRow = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                for (int c = 0; c < 6; c++)
                    hoursRow.ColumnDefinitions.Add(new ColumnDefinition {
                        Width = c % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                var dashLbl = MakeHoursLabel("–");
                dashLbl.Margin = new Thickness(4, 0, 4, 0);
                var flatLbl = MakeHoursLabel("· flat");
                flatLbl.Margin = new Thickness(8, 0, 4, 0);
                Grid.SetColumn(_hoursChk, 0);      hoursRow.Children.Add(_hoursChk);
                Grid.SetColumn(_hoursStartBox, 1); hoursRow.Children.Add(_hoursStartBox);
                Grid.SetColumn(dashLbl, 2);        hoursRow.Children.Add(dashLbl);
                Grid.SetColumn(_hoursEndBox, 3);   hoursRow.Children.Add(_hoursEndBox);
                Grid.SetColumn(flatLbl, 4);        hoursRow.Children.Add(flatLbl);
                Grid.SetColumn(_hoursFlatBox, 5);  hoursRow.Children.Add(_hoursFlatBox);
                Grid.SetColumn(hoursRow, 0); Grid.SetRow(hoursRow, 2); Grid.SetColumnSpan(hoursRow, 2);
                grid.Children.Add(hoursRow);

                var armWrap = new WrapPanel { Margin = new Thickness(8, 4, 8, 4),
                    Children = { _armChk, _warnText } };

                AddRow(4, new StackPanel { Children = { grid, armWrap } });
            }

            // Row 6: position + PnL readout — bordered bar (position left, PnL right).
            {
                _posText.VerticalAlignment = VerticalAlignment.Center;
                _pnlText.VerticalAlignment = VerticalAlignment.Center;
                _pnlText.HorizontalAlignment = HorizontalAlignment.Right;
                var pnlGrid = new Grid();
                pnlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pnlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                SetColumn(_posText, 0); pnlGrid.Children.Add(_posText);
                SetColumn(_pnlText, 1); pnlGrid.Children.Add(_pnlText);
                AddRow(6, new Border { Background = PnlBarBg, BorderBrush = PanelLine, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 7, 11, 7),
                    Margin = new Thickness(8, 4, 8, 8), Child = pnlGrid });
            }

            // ponytail: no TifSelector / Entry button / bracket SL-TP editor here — MKT+LMT(+optional
            // ATM) v1 per spec. TimeInForce hardcoded to Day.

            Account.AccountStatusUpdate += OnAccountStatusUpdate;
            PopulateAccounts();
        }

        private void AddRow(int row, UIElement content)
        {
            SetRow(content, row);
            Children.Add(content);
        }

        private static Grid TwoColRow(FrameworkElement left, FrameworkElement right, double sideMargin, double topMargin)
        {
            var row = new Grid { Margin = new Thickness(sideMargin, topMargin, sideMargin, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            left.Margin  = new Thickness(0, 0, 3, 0);
            right.Margin = new Thickness(3, 0, 0, 0);
            SetColumn(left, 0);  row.Children.Add(left);
            SetColumn(right, 1); row.Children.Add(right);
            return row;
        }

        // Rounded neon button template — overrides the default WPF button chrome so the translucent
        // fill / glowing border render cleanly; hover brightens the fill, disabled dims to 0.4.
        private static ControlTemplate PillTemplate(Brush hover, double radius)
        {
            var t  = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
            bd.SetValue(Border.BackgroundProperty,      new System.Windows.TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty,     new System.Windows.TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new System.Windows.TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(Border.CornerRadiusProperty,    new CornerRadius(radius));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
            t.Triggers.Add(hov);
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            t.Triggers.Add(dis);
            return t;
        }

        private static Button MakeNeonButton(string text, Brush fill, Brush hover, Brush border, Brush fg,
                                             double height, double fontSize, double radius, Color? glow)
        {
            var b = new Button
            {
                Content = text, Height = height,
                FontFamily = new FontFamily("Segoe UI"), FontSize = fontSize, FontWeight = FontWeights.Bold,
                Foreground = fg, Background = fill, BorderBrush = border, BorderThickness = new Thickness(1),
                Template = PillTemplate(hover, radius),
                SnapsToDevicePixels = true, Cursor = Cursors.Hand
            };
            if (glow.HasValue)
                b.Effect = new DropShadowEffect { Color = glow.Value, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.55 };
            return b;
        }

        private Button MakeManageButton(string text)
        {
            var b = MakeNeonButton(text, MgFill, MgHover, BorderBr, MgText, 28, 11, 8, null);
            b.MinWidth = 50;
            b.Margin = new Thickness(0, 0, 6, 0);
            return b;
        }

        // Pushed by RadarTab whenever the tab's instrument changes (selector or Restore()).
        public Instrument Instrument
        {
            get { return _instrument; }
            set
            {
                if (_instrument == value) return;
                CancelActiveLimitIfWorking("instrument switch");   // don't orphan a live order on switch/teardown
                _instrument = value;
                _lastPrice = 0;
                _wallAbove = 0;
                _wallBelow = 0;
                _activeLimit = null;
                _autoOrder = null;
                _pendingReplace = null;
                _workingOrders.Clear();
                _ownOrders.Clear();
                AbandonOpenAutoTrade("instrument switch");   // review finding: was never cleared, corrupting exit telemetry across a switch
                ClearPendingSetup();                // a pre-stage for the old instrument no longer applies
                _atmSelector.Instrument = value;
                _atmSelector.SelectedItem = null;   // force "None" — don't trust the control's own default
                _atmUserPicked = false;             // F16: switching instrument disarms ATM attach again
                ForceDisarmAuto("cambio de instrumento");
                // Roll the AUTO log so the next event opens a file named for the NEW instrument
                // (review round-3 minor: the name is captured at first-open only).
                if (_autoLogWriter != null) { _autoLogWriter.Flush(); _autoLogWriter.Dispose(); _autoLogWriter = null; }
                RefreshArmUi();
                RefreshPositionUi();
            }
        }

        // Called by RadarTab's UI-thread paint timer with the already-marshaled book mid + biggest-wall
        // prices above/below mid — reuses the existing instrument subscription instead of a new feed.
        // `now` is the REPLAY-aware market-data clock (RadarTab's Frame.Now, sourced from e.Time on the
        // instrument thread), not wall clock — Playback can run faster/slower/paused than real time, so
        // the AUTO auto-cancel timeout below must age against market time, not DateTime.Now/UtcNow.
        public void SetContext(double mid, double wallAbove, double wallBelow, double tick, DateTime now)
        {
            if (mid > 0) _lastPrice = mid;
            _wallAbove = wallAbove;
            _wallBelow = wallBelow;
            if (tick > 0) _tick = tick;
            _now = now;
            MaybeAutoCancel();
            MaybeHoursFlatten();
            MaybeResolveAtmRestore();
            RefreshPositionUi();
        }

        // Trading-hours forced flatten (2026-07-03): at/after the configured flat time (16:00 default),
        // any open position or working limit is flattened through the SAME native cancel-all + close
        // path as the human Flat button (ClosePosition(true) — ValidateForSubmit gates inside it, so an
        // off-Sim account fails closed). Once per replay day, marked attempted-first: a throwing Flatten
        // never becomes a retry loop. Runs on the UI thread (SetContext's paint tick), same as every
        // other order action in this control. Applies whenever the schedule is enabled — a position
        // left by a disarmed AUTO (e.g. daily cap hit at 15:50) must still be flat by 16:00.
        private void MaybeHoursFlatten()
        {
            if (_hoursChk.IsChecked != true || _now == DateTime.MinValue) return;
            if (_now.TimeOfDay < _hoursFlat || _now.Date == _hoursFlattenDay) return;
            _hoursFlattenDay = _now.Date;
            if (IsFlat(CurrentPosition()) && _activeLimit == null) return;   // nothing open — day marked, no action
            DiagAuto("hours_flatten", null, 0, _lastPrice, string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "HOURS — forced flatten at {0:00}:{1:00} (open position/working order past session end).",
                _hoursFlat.Hours, _hoursFlat.Minutes));
            ClosePosition(cancelOrdersFirst: true);   // also force-disarms AUTO inside (kill-switch semantics)
            // Re-state the disarm reason as the schedule (ClosePosition's internal disarm says "Flat manual").
            ForceDisarmAuto(string.Format("auto-flat {0:00}:{1:00}", _hoursFlat.Hours, _hoursFlat.Minutes));
        }

        // Placeholder join-tolerance for the break pre-stage price, in ticks — MEASURED later from Rec
        // CSV (spec §9), like every other Controller threshold.
        private const int SetupJoinToleranceTicks = 1;

        // AUTO mode placeholders (2026-07-01) — pending the same Rec-CSV calibration pass (spec §9).
        private const int AutoFireCapPerDay = 5;    // matches the setup's expected 0-5 fires/day
        private const int AutoStaleTicks = 2;       // MEASURED later — anti-stale tolerance at auto-fire time (F18)
        private const int AutoCancelSeconds = 15;   // MEASURED later — unfilled auto limit auto-cancels after this long

        // Called by RadarTab's UI-thread paint tick (same thread as SetContext/Instrument — no Dispatcher
        // marshal needed) when the Controller fires. PRE-STAGES ONLY: pre-fills price+side for the next
        // manual BUY/SELL LMT click and lights that button's "listo" indicator. No Order is created and
        // nothing reaches the Account here — submission still goes through the existing, Sim/Playback-
        // gated SubmitLimit -> SubmitRaw path untouched. No new NT8 Account/Order API is introduced by
        // this method, so no ilspycmd verification is needed for this task.
        //
        // Dedupe: ControllerOutput.Fired is one-shot per ENGINE run (~20Hz), but the UI paint tick
        // (~30Hz) can read the same _latest Frame more than once before the engine swaps it in — RadarTab
        // would then call this twice with the IDENTICAL FireEvent. Guard on FireEvent.Time (unique per
        // fire) so a re-delivery is a no-op instead of re-arming/re-flashing the indicator.
        //
        // Placement is a JOIN-NEAR-INSIDE limit in the break direction (spec §8) — the OPPOSITE intent of
        // LimitAnchorPrice's wall-anchored reversion LMT (which rests AT the wall to fade it, commit
        // ed46c76). A break enters WITH the move, so resting past the wall would be marketable while
        // price still sits on the near side. Real L2 best bid/ask aren't piped into this control (same
        // gap LimitAnchorPrice already documents — F14); reuse its mid ± 1 tick proxy convention.
        public void OnSetupFire(FireEvent f)
        {
            if (f.Time == _lastFireTime) return;
            _lastFireTime = f.Time;

            bool isBuy = f.Side == Side.Ask;   // ask wall above consumed by buys -> long break; bid wall below -> short
            double tick = EffectiveTick();
            double tol = tick * SetupJoinToleranceTicks;
            double price;
            if (_lastPrice <= 0)
                price = f.WallPrice;   // stale/no inside context yet — human re-checks the ticket before clicking anyway
            else if (isBuy)
                price = Math.Min(_lastPrice + tick + tol, f.WallPrice);    // never above the wall (would be marketable below it)
            else
                price = Math.Max(_lastPrice - tick - tol, f.WallPrice);    // never below the wall

            _pendingSetup = new PendingSetup { IsBuy = isBuy, Price = RoundToTick(price, tick) };
            ApplyPendingSetupUi();
            // Logged unconditionally (armed or not) — round-3's blind spot was never knowing whether an
            // engine fire even reached this layer. CSV-only: no Output spam for a routine, frequent event.
            LogAuto("prestage", isBuy ? "Buy" : "Sell", _pendingSetup.Price, _lastPrice,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "wall {0:0.00}, fraction {1:0.00}.", f.WallPrice, f.Fraction));
            TryAutoFire(f, isBuy, tick);   // AUTO mode (2026-07-01) — guards 1-4, evaluated after the pre-stage above is stored
        }

        // ---- AUTO mode (2026-07-01, Sim/Playback only) ----
        // Armed only via TryArmAuto/SetAutoArmed below. When armed, a Controller fire auto-submits the
        // pre-stage OnSetupFire just stored, through the SAME SubmitLimit -> SubmitRaw path a human LMT
        // click uses (ValidateForSubmit/CanTrade/ATM-attach/ownership all apply unchanged) — no parallel
        // order path, no new NT8 Account/Order API. Each skip below Diag's and leaves the pre-stage lit
        // for manual use (only the daily-cap skip also force-disarms).
        // `tick` is passed in from OnSetupFire's own EffectiveTick() call — same fire event, same tick.
        private void TryAutoFire(FireEvent f, bool isBuy, double tick)
        {
            string side = isBuy ? "Buy" : "Sell";
            if (!_autoArmed)                                              // guard 1a — was a silent return; the #1 blind spot of round-3
            {
                DiagAuto("guard_skip", side, f.WallPrice, _lastPrice, "AUTO skip — not armed at fire time.");
                return;
            }
            if (!IsSimAccount(_account))                                  // guard 1b: re-assert Sim at fire time
            {
                DiagAuto("guard_skip", side, f.WallPrice, _lastPrice, "AUTO skip — account no longer Sim at fire time."); // fail-closed, mirrors CanTrade's per-submit re-check
                ForceDisarmAuto("cuenta no-Sim");
                return;
            }
            // guard 1c (2026-07-03): trading-hours window — with the schedule enabled, AUTO only fires
            // inside [start, end]. Judged on the fire's own replay timestamp (the same clock the 16:00
            // forced flatten uses), so Playback speed and wall clock are irrelevant. Skip-only: the
            // pre-stage stays lit for a deliberate manual click outside hours.
            if (_hoursChk.IsChecked == true)
            {
                TimeSpan tod = f.Time.TimeOfDay;
                if (tod < _hoursStart || tod > _hoursEnd)
                {
                    DiagAuto("guard_skip", side, f.WallPrice, _lastPrice, string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "AUTO skip — outside trading hours ({0:00}:{1:00}-{2:00}:{3:00}).",
                        _hoursStart.Hours, _hoursStart.Minutes, _hoursEnd.Hours, _hoursEnd.Minutes));
                    return;
                }
            }
            if (!ValidateForSubmit()) return;                            // no instrument/account, order in flight, not Sim/armed (Diag'd inside — shared with the manual path, not routed to the AUTO CSV)
            if (_activeLimit != null || !IsFlat(CurrentPosition()))         // guard 2: busy
            {
                DiagAuto("guard_skip", side, f.WallPrice, _lastPrice, "AUTO skip — busy (working limit or open position).");
                return;
            }

            DateTime day = f.Time.Date;                                  // REPLAY time — a new replay day resets the count
            if (day != _autoFireDay)
            {
                _autoFireDay = day;
                // Blocker fix: if a persisted daily-cap count survived a workspace restore and is for
                // THIS SAME replay day, seed from it instead of zeroing — a mid-day reopen must not grant
                // a fresh cap. A persisted day that doesn't match (or none) is stale/inapplicable — 0.
                _autoFireCount = (_pendingRestoreFireDay == day) ? _pendingRestoreFireCount : 0;
                _pendingRestoreFireDay = DateTime.MinValue;   // consumed — a later new day must reset normally
            }
            if (_autoFireCount >= AutoFireCapPerDay)                      // guard 3: daily cap (burns on every ATTEMPT below,
            {                                                             // including one whose submit throws — conservative, deliberate)
                string capStatus = _autoFireCount + "/" + AutoFireCapPerDay;
                DiagAuto("guard_skip", side, f.WallPrice, _lastPrice, "AUTO skip — daily cap " + capStatus + " reached.");
                ForceDisarmAuto("cap " + capStatus);   // route through the one disarm gate
                return;
            }

            // guard 4: degenerate-quote guard, not F18's click-time re-clamp — at synchronous auto-submit
            // the pre-stage price is derived from this SAME _lastPrice a moment earlier in OnSetupFire, so
            // marketableThrough is false by construction; this only catches _lastPrice<=0 (no context yet).
            // F18's actual click-time re-clamp is for the MANUAL path and is still unimplemented — required
            // before any real-account port (see docs/plans/2026-06-30-chart-trader.md).
            bool marketableThrough = isBuy
                ? _pendingSetup.Price - _lastPrice > AutoStaleTicks * tick
                : _lastPrice - _pendingSetup.Price > AutoStaleTicks * tick;
            if (_lastPrice <= 0 || marketableThrough)
            {
                DiagAuto("guard_skip", side, _pendingSetup.Price, _lastPrice,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "AUTO skip — stale at fire (setup {0:0.00} vs mid {1:0.00}).", _pendingSetup.Price, _lastPrice));
                return;
            }

            // guard 5 (F20): the ATM template must still be selected at the actual fire moment, not just
            // at arm time — if it deselected since (async repopulate, or the selector reset after some
            // other ATM completed), an auto entry must NEVER go out naked. SubmitRaw enforces this again
            // at the actual submit instant (defense in depth); this is the earlier, cheaper trip.
            if (_atmSelector.SelectedAtmStrategy == null)
            {
                DiagAuto("guard_skip", side, f.WallPrice, _lastPrice, "AUTO skip — ATM lost at fire time.");
                ForceDisarmAuto("ATM perdido");
                return;
            }

            _autoFireCount++;                                            // all clear — fire
            SubmitLimit(isBuy, isAuto: true);   // the "submit" log row (order id/qty/price) is written from SubmitRaw once the order exists
        }

        // Auto-cancel of an unfilled AUTO-submitted limit — never a manual one (reference-checked against
        // _autoOrder, which only SubmitRaw's isAuto branch sets). Checked every SetContext tick (the
        // ~30Hz UI feed) against the REPLAY-aware _now (see SetContext) — see that comment for why wall
        // clock would be wrong here (Playback speed != 1x, or paused, decouples wall time from market time).
        private void MaybeAutoCancel()
        {
            if (_autoOrder == null || !ReferenceEquals(_activeLimit, _autoOrder)) return;
            double elapsedSec = (_now - _autoSubmittedAt).TotalSeconds;
            if (elapsedSec < AutoCancelSeconds) return;
            DiagAuto("auto_cancel", _autoOrder.OrderAction.ToString(), _autoOrder.LimitPrice, _lastPrice,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "AUTO cancel — unfilled auto limit aged out after {0:0.0}s (limit {1}s).", elapsedSec, AutoCancelSeconds));
            CancelActiveLimitIfWorking("auto-cancel");   // same cancel path Rev/Close/opposite-side use
        }

        // One definition of "flat", 4 users: guard 2 below, RefreshPositionUi, Reverse, ClosePosition —
        // all paired with their own CurrentPosition() call (some need the non-flat Position afterward).
        private static bool IsFlat(Position pos)
        {
            return pos == null || pos.MarketPosition == MarketPosition.Flat || pos.Quantity == 0;
        }

        // armNote: non-null only from MaybeAutoRearm — appended to the "arm" CSV row so the decision
        // trail distinguishes a human checkbox click from an automatic re-arm (verdict doc item 6).
        private void TryArmAuto(string armNote = null)
        {
            if (!IsSimAccount(_account)) { SetAutoArmed(false, "cuenta no-Sim"); return; }
            if (!_atmUserPicked || _atmSelector.SelectedAtmStrategy == null) { SetAutoArmed(false, "selecciona ATM"); return; }
            SetAutoArmed(true, null, armNote);
        }

        // Force-disarm from a broken precondition or the Flat kill-switch. No-op if already disarmed, so
        // routine account/instrument switches don't spam Diag when AUTO was never armed.
        private void ForceDisarmAuto(string reason)
        {
            if (!_autoArmed) return;
            SetAutoArmed(false, reason);
        }

        private void SetAutoArmed(bool armed, string reason, string armNote = null)
        {
            _autoArmed = armed;
            // _autoIntent (2026-07-03, always-armed AUTO): the user's own persisted decision. Only a
            // genuine human uncheck (reason == null — see the _autoChk.Unchecked handler above) turns
            // it off; every forced disarm (reason != null: non-Sim, ATM->None, cap, 16:00 flatten)
            // leaves it standing so MaybeAutoRearm can re-arm once the precondition it broke repairs.
            if (armed) _autoIntent = true;
            else if (reason == null) _autoIntent = false;
            _suppressAutoChkEvent = true;   // re-sync the checkbox without re-entering Checked/Unchecked
            _autoChk.IsChecked = armed;
            _suppressAutoChkEvent = false;
            if (armed)
                DiagAuto("arm", null, 0, _lastPrice, string.Format("AUTO armed — account {0}, ATM {1}{2}.",
                    _account != null ? _account.Name : "?",
                    _atmSelector.SelectedAtmStrategy != null ? _atmSelector.SelectedAtmStrategy.Name : "?",
                    armNote != null ? " " + armNote : ""));
            else if (reason != null)
                DiagAuto("disarm", null, 0, _lastPrice, "AUTO disarmed — " + reason + ".");
            SolidColorBrush accent = armed ? Amber : Muted;
            _autoChk.Foreground = accent;
            // The checkbox already says "AUTO" — the status only adds the state, so it reads "AUTO armed".
            _autoStatusText.Text = armed ? "armed" : reason != null ? reason : string.Empty;
            _autoStatusText.Foreground = accent;
        }

        // Whenever a broken AUTO precondition (Sim account + ATM picked) repairs and the user's own
        // persisted intent is still on, re-arm without waiting for another checkbox click — recovers
        // the 52%-of-guard_skips "not armed at fire time" loss (verdict doc item 6). Pre-checks both
        // preconditions itself so a partial repair (e.g. account fixed, ATM still missing) doesn't emit
        // a redundant "disarmed — selecciona ATM" row through TryArmAuto. Never re-arms on the SAME
        // replay day the 16:00 auto-flat already fired — that stays flattened until the next
        // session/day (Restore, or a fresh day via the day-keyed guard below); see MaybeHoursFlatten.
        private void MaybeAutoRearm(string context)
        {
            if (_autoArmed || !_autoIntent) return;
            if (!IsSimAccount(_account) || !_atmUserPicked || _atmSelector.SelectedAtmStrategy == null) return;
            if (_now != DateTime.MinValue && _now.Date == _hoursFlattenDay) return;
            TryArmAuto("(auto-rearm on " + context + ")");
        }

        // Lights the pre-staged LMT button in the Aurora fire color (same glow the MKT buttons already
        // use — LMT buttons are built with glow:null, so no Effect competes) and shows/hides the "listo"
        // text. Never touches SetActiveOrder — that marker is for a LIVE working order, not a suggestion.
        private void ApplyPendingSetupUi()
        {
            bool buyLive = _pendingSetup != null && _pendingSetup.IsBuy;
            bool sellLive = _pendingSetup != null && !_pendingSetup.IsBuy;
            _buyLmtBtn.Effect = buyLive ? SetupGlow(Emerald.Color) : null;
            _sellLmtBtn.Effect = sellLive ? SetupGlow(Coral.Color) : null;
            _setupText.Visibility = _pendingSetup != null ? Visibility.Visible : Visibility.Collapsed;
            _setupText.Text = buyLive ? "SETUP LONG listo · calibrando" : sellLive ? "SETUP SHORT listo · calibrando" : string.Empty;
            _setupText.Foreground = buyLive ? Emerald : Coral;
        }

        private static DropShadowEffect SetupGlow(Color c)
        {
            return new DropShadowEffect { Color = c, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.55 };
        }

        // Clears the pre-stage + its glow/text — consumed by a matching LMT click, invalidated by the
        // opposite one, or dropped by account/instrument switch, a Replay reset, or clicking _setupText.
        private void ClearPendingSetup()
        {
            if (_pendingSetup == null) return;
            _pendingSetup = null;
            ApplyPendingSetupUi();
        }

        // Exposes the one active working limit order for RadarVisual's ladder marker.
        public bool TryGetActiveOrder(out double price, out bool isBuy, out int qty)
        {
            Order ord = _activeLimit;
            if (ord == null) { price = 0; isBuy = false; qty = 0; return false; }
            price = ord.LimitPrice;
            isBuy = ord.OrderAction == OrderAction.Buy;
            qty = ord.Quantity;
            return true;
        }

        // Fail-closed: Sim is recognized ONLY via the authoritative Provider enum. Any account whose
        // Provider can't be read, or isn't Simulator/Playback, is treated as real — arming required.
        // (A real broker/prop account happening to be named "Sim..." must not slip through on name alone.)
        private static bool IsSimAccount(Account acct)
        {
            if (acct == null) return false;
            try
            {
                return acct.Provider == Provider.Playback || acct.Provider == Provider.Simulator;
            }
            catch
            {
                return false;
            }
        }

        // Narrower than IsSimAccount on purpose: ONLY the Market Replay Playback account has its orders wiped
        // by a rewind/restart. A Simulator (Sim101) or real account runs against a LIVE feed, where the
        // replay-reset heuristic (a big e.Time gap) is a false trip, not an account reset. Fail-closed on any
        // account whose Provider can't be read.
        private static bool IsPlaybackAccount(Account acct)
        {
            if (acct == null) return false;
            try { return acct.Provider == Provider.Playback; }
            catch { return false; }
        }

        private bool CanTrade()
        {
            if (_instrument == null || _account == null) return false;
            if (_workingOrders.Count > 0) return false; // an order from this control is still in flight
            if (IsSimAccount(_account)) return true;
            return _armChk.IsChecked == true && ReferenceEquals(_armedFor, _account); // arming is per-account
        }

        private void OnAccountStatusUpdate(object sender, AccountStatusEventArgs e)
        {
            Dispatcher.InvokeAsync((Action)PopulateAccounts);
        }

        // Fires on every AccountStatusUpdate (connection blips included), not just a real account list
        // change. WPF transiently resets SelectedItem -> null when ItemsSource is reassigned to a fresh
        // collection; left attached through that, the blip's SelectionChanged ran OnAccountSelected's
        // full teardown (Cancel/Unsubscribe/ForceDisarmAuto) even though the very next line below just
        // restores the SAME account — silently disarming AUTO on a mere connection blip. Detach for the
        // reassignment, then re-assert once: a no-op if the settled selection == _account (the blip
        // case), or the real switch teardown+setup if it's genuinely different (account actually removed).
        private void PopulateAccounts()
        {
            List<Account> accts;
            lock (Account.All) accts = Account.All.ToList();
            Account keep = _accountCombo.SelectedItem as Account;
            _accountCombo.SelectionChanged -= _accountSelectionHandler;
            try
            {
                _accountCombo.ItemsSource = accts;
                if (keep != null && accts.Contains(keep))
                    _accountCombo.SelectedItem = keep;
                else
                    _accountCombo.SelectedItem = accts.FirstOrDefault(IsSimAccount) ?? accts.FirstOrDefault();
            }
            finally
            {
                _accountCombo.SelectionChanged += _accountSelectionHandler;
            }
            OnAccountSelected();
            TryApplyPendingRestoreAccount();   // a Restore-persisted account may have just connected — see RestoreAutoState
        }

        private void OnAccountSelected()
        {
            Account acct = _accountCombo.SelectedItem as Account;
            if (acct == _account) return;
            CancelActiveLimitIfWorking("account switch");   // on the OLD account, before we lose the reference
            UnsubscribeAccount();
            _account = acct;
            _armedFor = null;           // arming never carries over to a newly selected account
            _armChk.IsChecked = false;
            _activeLimit = null;        // order tracking is per-account (orders/positions differ)
            _autoOrder = null;
            _pendingReplace = null;
            _workingOrders.Clear();
            _ownOrders.Clear();
            AbandonOpenAutoTrade("account switch");   // review finding: was never cleared, corrupting exit telemetry across a switch
            ClearPendingSetup();        // a pre-stage for the old account no longer applies
            _atmSelector.Account = _account;
            _atmSelector.SelectedItem = null;   // force "None" — don't trust the control's own default
            _atmUserPicked = false;             // F16: switching account disarms ATM attach again
            ForceDisarmAuto("cambio de cuenta");
            SubscribeAccount();
            RefreshArmUi();
            RefreshPositionUi();
            MaybeAutoRearm("account switch");   // usually a no-op here — ATM always resets alongside the account (below) — but cheap and correct if that ever changes
        }

        // Called on the UI thread (RadarTab's paint tick) after a Market Replay rewind/restart. The
        // Playback account is reset — working orders vanish and the position flattens — but NT8 doesn't
        // reliably fire a terminal OrderUpdate for each, which can leave _activeLimit a phantom that paints
        // a ladder marker for an order that no longer exists. Drop any tracked order that isn't provably
        // still working, and clear the in-flight guards the reset invalidated. Runs on the UI thread, same
        // as every other mutation of this state, so no marshaling/lock is needed.
        //
        // FAIL CLOSED to the Playback account: the trigger is a market-DATA-clock heuristic (a big gap in
        // e.Time), fired independently of which account is selected here. ONLY the Market Replay Playback
        // account (Provider.Playback) has its orders wiped by a rewind/restart. A Simulator (Sim101) or real
        // account runs against a LIVE feed, where a >60s quiet-market gap is NOT an account reset — touching
        // its order tracking there would re-enable the buttons mid-flight (double-submit) and silently drop a
        // pending opposite-side flip. So gate specifically on Provider.Playback, NOT the broader IsSimAccount
        // (which answers a different question — "does this account need ARM LIVE?").
        public void OnReplayReset()
        {
            if (!IsPlaybackAccount(_account)) return;
            _pendingReplace = null;
            _hoursFlattenDay = DateTime.MinValue;   // a rewind replays the same date — the 16:00 flatten must be able to fire again
            ClearPendingSetup();      // a pre-stage keyed off the pre-reset book/wall is stale after a rewind
            // Rewind + replay reproduces the SAME FireEvent.Time — reset the dedupe guard so the
            // legitimately re-fired setup isn't silently swallowed by OnSetupFire's dedupe check.
            _lastFireTime = DateTime.MinValue;
            _workingOrders.Clear();   // any in-flight submit is void after a Playback reset — re-enable the buttons
            AbandonOpenAutoTrade("replay reset");   // review finding: the position flattens on rewind — any open trade record is stale
            Order ord = _activeLimit;
            if (ord != null)
            {
                if (!IsStillWorking(ord))
                {
                    _ownOrders.Remove(ord);
                    _activeLimit = null;   // TryGetActiveOrder now returns false → the paint tick clears the marker
                    if (ReferenceEquals(_autoOrder, ord)) _autoOrder = null;
                }
                else
                    // Still present + non-terminal after a reset trip: either a false trip (order genuinely
                    // still resting — keep it, don't orphan the marker) or NT8 left a stale phantom in
                    // Account.Orders. Log so the Market Replay pass can tell which (see docs checklist).
                    Diag("replay reset: active limit still in Account.Orders (" + ord.OrderState + ") — kept.");
            }
            RefreshArmUi();
            RefreshPositionUi();
        }

        // True only if the order is non-terminal AND still present in the account's live order collection.
        // After a Playback rewind the order is gone from Account.Orders even if its cached OrderState still
        // reads Working, so membership — not just state — is what actually kills the phantom marker.
        private bool IsStillWorking(Order ord)
        {
            if (ord == null || _account == null || Order.IsTerminalState(ord.OrderState)) return false;
            lock (_account.Orders)
                return _account.Orders.Contains(ord);
        }

        // Cancels the currently-tracked working limit if it's still alive — used on teardown/context
        // switch (instrument, account, Cleanup) so a live order is never left orphaned/untracked.
        private void CancelActiveLimitIfWorking(string context)
        {
            Order ord = _activeLimit;
            if (ord == null || _account == null) return;
            if (Order.IsTerminalState(ord.OrderState)) return;
            try { _account.Cancel(new[] { ord }); }
            catch (Exception ex) { Diag(context + ": cancel failed: " + ex.Message); }
        }

        private void SubscribeAccount()
        {
            if (_account == null) return;
            _account.OrderUpdate     += OnOrderUpdate;
            _account.ExecutionUpdate += OnExecutionUpdate;
            _account.PositionUpdate  += OnPositionUpdate;
        }

        private void UnsubscribeAccount()
        {
            if (_account == null) return;
            _account.OrderUpdate     -= OnOrderUpdate;
            _account.ExecutionUpdate -= OnExecutionUpdate;
            _account.PositionUpdate  -= OnPositionUpdate;
        }

        // ---- account-thread handlers: marshal any WPF mutation to the UI thread ----
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            Instrument inst = _instrument;   // snapshot once — avoid a concurrent reassignment race
            Order ord = e.Order;
            // F15: gate on ownership FIRST — an ATM's own stop/target legs (or anything else on this
            // account/instrument, e.g. AbsorptionScalper) are never ours and must never reach the
            // tracking below. _ownOrders is seeded the moment SubmitRaw creates an order.
            if (ord == null || ord.Instrument != inst || !_ownOrders.Contains(ord)) return;
            if (e.OrderState == OrderState.Rejected)
                Diag("order rejected: " + e.Error + " " + e.Comment);
            OrderState state = e.OrderState;
            OrderType  type  = ord.OrderType;
            Dispatcher.InvokeAsync((Action)(() =>
            {
                // Captured BEFORE any mutation below can null _autoOrder on a terminal state — round-3's
                // schema wants order_update logged "for auto-tracked orders" specifically (CSV-only, no
                // Output spam: every state transition of an auto order lands here, not just Rejected).
                if (ReferenceEquals(_autoOrder, ord))
                    LogAuto("order_update", ord.OrderAction.ToString(), ord.LimitPrice, _lastPrice,
                        "order #" + ord.Id + " -> " + state + ".");
                if (Order.IsTerminalState(state))
                {
                    // ADR 2026-07-03 Phase 0: realized-fill telemetry for EVERY own order (manual and
                    // AUTO) — the future calibration label needs AverageFillPrice + filled qty, which
                    // the order_update rows above (limit price only) cannot provide. Logged BEFORE the
                    // bookkeeping below nulls _autoOrder, so the [auto] tag stays accurate.
                    LogAuto("fill", ord.OrderAction.ToString(), ord.AverageFillPrice, _lastPrice,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "order #{0} {1} — filled {2}/{3} @ {4:0.00}{5}",
                            ord.Id, state, ord.Filled, ord.Quantity, ord.AverageFillPrice,
                            ReferenceEquals(_autoOrder, ord) ? " [auto]" : ""));
                    // Exit-leg instrumentation (verdict doc item 1): the AUTO entry order just filled
                    // (fully or partially-then-cancelled — either way a position opened) — open the one
                    // live AUTO trade record. Captured here, BEFORE _autoOrder is nulled below.
                    if (ReferenceEquals(_autoOrder, ord) && ord.Filled > 0)
                        _openAutoTrade = new AutoTrade
                        {
                            EntryOrderId = ord.Id, IsLong = ord.OrderAction == OrderAction.Buy,
                            EntryPrice = ord.AverageFillPrice, EntryQty = ord.Filled, EntryTime = _now
                        };
                    _workingOrders.Remove(ord);   // HashSet.Remove is a no-op if already gone
                    _ownOrders.Remove(ord);
                    if (ReferenceEquals(_activeLimit, ord)) _activeLimit = null;
                    if (ReferenceEquals(_autoOrder, ord)) _autoOrder = null;
                    // Opposite-side flip: the old order we cancelled just reached its terminal state —
                    // fire the stashed replacement now (only if it actually got Cancelled, not Filled/Rejected).
                    if (_pendingReplace != null && ReferenceEquals(_pendingReplace.WaitingOn, ord))
                    {
                        PendingReplace p = _pendingReplace;
                        _pendingReplace = null;
                        if (state == OrderState.Cancelled)
                            SubmitRaw(p.Action, OrderType.Limit, p.Qty, p.Price, p.Tag, isEntry: true);
                        else
                            Diag("pending replace dropped — old order reached " + state + " instead of Cancelled.");
                    }
                }
                else if (state == OrderState.Working && type == OrderType.Limit)
                {
                    _workingOrders.Remove(ord);   // resting limit no longer counts as "in flight"
                    _activeLimit = ord;           // the one tracked working limit (v1: one at a time)
                }
                RefreshPositionUi();
                RefreshArmUi();   // re-enables buttons / refreshes ▲▼ once state settles
            }));
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            Execution exec = e.Execution;
            if (exec == null || exec.Instrument != _instrument) return;
            // Deliberately NOT gated on _ownOrders (F15's ownership filter, see OnOrderUpdate's comment
            // at :62-64) — ATM stop/target legs are never in that set, and this is the one place that
            // needs to see them anyway (exit-leg instrumentation, verdict doc item 1). Order management
            // semantics are untouched: this handler only reads/logs, it never mutates _activeLimit/
            // _workingOrders/_ownOrders. Marshaled to the UI thread, same pattern as the rest of this
            // control's account-thread handlers.
            Dispatcher.InvokeAsync((Action)(() =>
            {
                RefreshPositionUi();
                HandlePossibleExitFill(exec);
            }));
        }

        // Review finding (major): _openAutoTrade previously survived an instrument switch, account
        // switch, or Playback rewind — any of which can strand it on a context this control no longer
        // observes (its remaining exits land on the wrong instrument/account, or never fire an event at
        // all on rewind). Rather than silently drop the record (losing the round-trip from the CSV
        // entirely), close the books with a best-effort "trade_summary (abandoned)" row so the fill/fire
        // count in the CSV stays honest, then clear it so a NEW auto trade doesn't inherit stale state.
        private void AbandonOpenAutoTrade(string context)
        {
            AutoTrade trade = _openAutoTrade;
            if (trade == null) return;
            _openAutoTrade = null;
            LogAuto("trade_summary", trade.IsLong ? "Buy" : "Sell", trade.EntryPrice, _lastPrice, string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "entry #{0} @ {1:0.00}, abandoned ({2}), {3}/{4} exited.",
                trade.EntryOrderId, trade.EntryPrice, context, trade.ExitQty, trade.EntryQty));
        }

        // Observes executions on the OWN instrument while an AUTO trade is open and logs any fill on the
        // opposite side of the entry (ATM stop/target leg, manual Close/Rev, or a platform Flatten()/
        // 16:00 auto-flat close) as an "exit" row, plus a "trade_summary" row once ExitQty catches up to
        // EntryQty. Self-contained qty bookkeeping (not a live Position lookup) — simpler and avoids any
        // assumption about Positions-vs-Execution event ordering; a Reverse() (close+flip in one fill)
        // is a known ponytail approximation — the oversized closing+opening execution still closes out
        // the old trade correctly, it just doesn't further track the newly-opened opposite position.
        private void HandlePossibleExitFill(Execution exec)
        {
            AutoTrade trade = _openAutoTrade;
            if (trade == null) return;
            Order execOrder = exec.Order;
            if (execOrder == null) return;
            bool isExitSide = trade.IsLong ? execOrder.OrderAction == OrderAction.Sell : execOrder.OrderAction == OrderAction.Buy;
            if (!isExitSide)
            {
                // Minor finding: nothing blocks a manual same-side scale-in while an AUTO trade + ATM
                // brackets are open (ValidateForSubmit only checks _workingOrders, not open position).
                // Fold its qty into EntryQty so `remaining` below doesn't hit 0 before the real position
                // is actually flat. Guarded against the entry order's OWN execution (already captured
                // into EntryQty/EntryPrice by OnOrderUpdate) so it isn't double-counted here. Qty-only:
                // EntryPrice deliberately stays the original fill's price (ponytail — full weighted-avg
                // re-pricing is a bigger change; upgrade if scale-ins turn out to be common in the CSV).
                if (execOrder.Id != trade.EntryOrderId) trade.EntryQty += exec.Quantity;
                return;
            }

            double tick = EffectiveTick();
            string reason = ClassifyExitReason(execOrder);
            double realizedTicksThisFill = (trade.IsLong ? exec.Price - trade.EntryPrice : trade.EntryPrice - exec.Price) / tick;
            trade.ExitQty += exec.Quantity;
            trade.ExitNotional += exec.Price * exec.Quantity;
            int remaining = Math.Max(0, trade.EntryQty - trade.ExitQty);

            // Raw order name included so any future reason misclassification is self-diagnosing
            // from the CSV alone (the day-1 "flatten" mislabel needed a code dive to explain).
            LogAuto("exit", execOrder.OrderAction.ToString(), exec.Price, _lastPrice, string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "entry #{0}, reason {1}, {2:0.#}t this fill, cum pos {3}, order '{4}'.",
                trade.EntryOrderId, reason, realizedTicksThisFill, remaining, execOrder.Name));

            if (remaining <= 0)
            {
                double avgExit = trade.ExitNotional / trade.ExitQty;
                double realizedTicksNet = (trade.IsLong ? avgExit - trade.EntryPrice : trade.EntryPrice - avgExit) / tick;
                double durationSec = (_now - trade.EntryTime).TotalSeconds;
                LogAuto("trade_summary", trade.IsLong ? "Buy" : "Sell", avgExit, _lastPrice, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "entry #{0} @ {1:0.00}, exit @ {2:0.00}, {3:0.#}t gross, {4:0.0}s, reason {5}.",
                    trade.EntryOrderId, trade.EntryPrice, avgExit, realizedTicksNet, durationSec, reason));
                _openAutoTrade = null;
            }
        }

        // Best-effort exit-reason classifier — the official ATM predicates first (authoritative for
        // stop/target legs), then this control's own order-name tags for its manual Close/Rev buttons.
        // Account.Flatten() (16:00 auto-flat, or a future manual Flat->reason split) doesn't let us name
        // the resulting order, so anything left over is bucketed "flatten" (ponytail: refine from the
        // CSV if this ever shows up misclassifying a real case).
        private static string ClassifyExitReason(Order execOrder)
        {
            if (NinjaTrader.NinjaScript.AtmStrategy.IsStopLoss(execOrder)) return "stop";
            if (NinjaTrader.NinjaScript.AtmStrategy.IsProfitTarget(execOrder)) return "target";
            if (string.Equals(execOrder.Name, "Rev", StringComparison.OrdinalIgnoreCase)) return "manual";
            if (string.Equals(execOrder.Name, "Close", StringComparison.OrdinalIgnoreCase)) return "manual";
            // Day-1 CSV (2026-07-03 21:54): both ATM target fills at the exact TP prices classified
            // "flatten" — the official predicates above return false for the Order instances
            // Account.ExecutionUpdate delivers (obfuscated internals, likely instance-identity based).
            // Fall back to NT8's ATM/strategy leg naming: "Stop1"/"Target1", "Stop loss"/"Profit target".
            string name = execOrder.Name ?? string.Empty;
            if (name.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0) return "target";
            if (name.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0) return "stop";
            return "flatten";
        }

        private void OnPositionUpdate(object sender, PositionEventArgs e)
        {
            if (e.Position == null || e.Position.Instrument != _instrument) return;
            Dispatcher.InvokeAsync((Action)RefreshPositionUi);
        }

        private void RefreshArmUi()
        {
            bool sim = IsSimAccount(_account);
            _armChk.Visibility = sim ? Visibility.Collapsed : Visibility.Visible;
            if (sim) _armChk.IsChecked = false; // re-selecting a real account always requires re-arming
            bool canTrade = CanTrade();
            _buyBtn.IsEnabled = canTrade;
            _sellBtn.IsEnabled = canTrade;
            _buyLmtBtn.IsEnabled = canTrade;
            _sellLmtBtn.IsEnabled = canTrade;
            bool canMove = canTrade && _activeLimit != null;
            _upBtn.IsEnabled = canMove;
            _dnBtn.IsEnabled = canMove;
            _revBtn.IsEnabled = canTrade;
            _closeBtn.IsEnabled = canTrade;
            _flatBtn.IsEnabled = canTrade;
            _warnText.Text = _account == null ? "NO ACCOUNT SELECTED"
                : sim ? string.Empty
                : (canTrade ? "LIVE ARMED" : "REAL ACCOUNT — BLOCKED");
            _warnText.Foreground = !sim && canTrade ? Coral : Muted;
        }

        private void RefreshPositionUi()
        {
            if (_account == null || _instrument == null)
            {
                _posText.Text = "—";
                _posText.Foreground = Muted;
                _pnlText.Text = string.Empty;
                return;
            }
            Position pos = CurrentPosition();

            if (IsFlat(pos))
            {
                _posText.Text = "FLAT";
                _posText.Foreground = Muted;
                _pnlText.Text = string.Empty;
                return;
            }

            string side = pos.MarketPosition == MarketPosition.Long ? "LONG" : "SHORT";
            _posText.Text = string.Format("{0} {1} @ {2:0.00}", side, pos.Quantity, pos.AveragePrice);
            _posText.Foreground = pos.MarketPosition == MarketPosition.Long ? Emerald : Coral;

            double refPrice = _lastPrice > 0 ? _lastPrice : pos.AveragePrice;
            double pnlUsd   = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency, refPrice);
            double pnlTicks = pos.GetUnrealizedProfitLoss(PerformanceUnit.Ticks, refPrice);
            _pnlText.Text = string.Format("{0}{1:0.00} $  ({2}{3:0.#} t)",
                pnlUsd >= 0 ? "+" : "", pnlUsd, pnlTicks >= 0 ? "+" : "", pnlTicks);
            _pnlText.Foreground = pnlUsd >= 0 ? Emerald : Coral;
        }

        private void StepQty(int delta)
        {
            int q;
            if (!int.TryParse(_qtyBox.Text, out q)) q = 1;
            q = Math.Max(1, q + delta);
            _qtyBox.Text = q.ToString();
        }

        private int GetQty()
        {
            int q;
            if (!int.TryParse(_qtyBox.Text, out q) || q < 1) q = 1;
            return q;
        }

        private void Diag(string msg)
        {
            NinjaTrader.Code.Output.Process("[ChartTrader] " + msg, NinjaTrader.NinjaScript.PrintTo.OutputTab1);
        }

        // ---- persistent AUTO decision trail (round-3 diagnosis) ----
        // 5-day AUTO run, 3 engine fires, ZERO positions, and no way to tell why beyond NT8's Output
        // window (which nobody was watching and which persists nothing). This CSV is INDEPENDENT of the
        // Rec toggle — every event below writes here whenever it happens, Rec on or off. Schema:
        // time,event,side,price,mid,detail. Mirrors RadarTab's Rec-writer dir-creation + invariant-culture
        // StreamWriter pattern (same MyDocuments/NinjaTrader 8/LiquidityRadar folder).
        private void EnsureAutoLogWriter()
        {
            // A late Dispatcher-marshaled order_update can land AFTER Cleanup() disposed the writer —
            // without this latch it would silently reopen a brand-new orphaned file that nothing ever
            // disposes (review round-3).
            if (_cleanedUp) return;
            if (_autoLogWriter != null) return;
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "LiquidityRadar");
                System.IO.Directory.CreateDirectory(dir);
                string inst = _instrument != null ? _instrument.MasterInstrument.Name : "X";
                string path = System.IO.Path.Combine(dir,
                    "lr-auto-" + inst + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                _autoLogWriter = new System.IO.StreamWriter(path, false);
                _autoLogWriter.WriteLine("time,event,side,price,mid,detail");
                _autoLogWriter.Flush();
            }
            catch (Exception ex) { Diag("AUTO log open failed: " + ex.Message); }
        }

        private static string CsvField(string s)
        {
            return string.IsNullOrEmpty(s) ? string.Empty : "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        // event in {arm, disarm, prestage, guard_skip, submit, atm_attach, order_update, auto_cancel}.
        // Time is the replay-aware market clock (_now, pushed by SetContext) where available — the same
        // instant OnSetupFire/TryAutoFire's caller already used — with a wall-clock fallback (noted in the
        // detail column) for the few AUTO-path call sites that can run off-tick (checkbox clicks, account/
        // instrument switches) before any SetContext tick has ever landed.
        private void LogAuto(string evt, string side, double price, double mid, string detail)
        {
            EnsureAutoLogWriter();
            if (_autoLogWriter == null) return;
            bool haveReplayTime = _now != DateTime.MinValue;
            string time = (haveReplayTime ? _now : DateTime.Now).ToString("o");
            string d = haveReplayTime ? detail : detail + " [wall-clock — no replay time yet]";
            try
            {
                _autoLogWriter.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3:0.00},{4:0.00},{5}", time, evt, side ?? "", price, mid, CsvField(d)));
                _autoLogWriter.Flush();   // events are rare — flush-per-write is cheap insurance against losing the trail
            }
            catch (Exception ex) { Diag("AUTO log write failed: " + ex.Message); }
        }

        // Every AUTO-path Diag also lands a durable CSV row — Diag alone only reaches NT8's Output
        // window, which round-3 proved is not enough on its own.
        private void DiagAuto(string evt, string side, double price, double mid, string detail)
        {
            Diag(detail);
            LogAuto(evt, side, price, mid, detail);
        }

        private bool ValidateForSubmit()
        {
            if (_instrument == null)      { Diag("blocked — no instrument."); return false; }
            if (_account == null)         { Diag("blocked — no account selected."); return false; }
            if (_workingOrders.Count > 0) { Diag("blocked — an order is still in flight."); return false; }
            if (!CanTrade())               { Diag("blocked — real account not armed (check ARM LIVE)."); return false; }
            return true;
        }

        private void SubmitMarket(OrderAction action, string tag)
        {
            if (!ValidateForSubmit()) return;
            SubmitRaw(action, OrderType.Market, GetQty(), 0, tag, isEntry: true);
        }

        private double EffectiveTick()
        {
            if (_tick > 0) return _tick;
            return _instrument != null ? _instrument.MasterInstrument.TickSize : 0.25;
        }

        private static double RoundToTick(double price, double tick)
        {
            return tick > 0 ? Math.Round(price / tick) * tick : price;
        }

        // Single, clear anchor rule for BUY/SELL LMT (Javier's call to tune further):
        // SELL → biggest wall ABOVE mid + 1 tick (non-marketable). BUY → biggest wall BELOW mid − 1 tick.
        // No wall on the required side → fall back to a mid ± 1 tick proxy for best ask / best bid (Diag'd).
        // ponytail: F13 (deferred to the real-account gate) — this fallback silently rests the order near
        // market instead of blocking/confirming when no wall exists. F14 — LMT submit/move isn't gated on
        // a fresh-quote/connection check and clamps against the possibly-stale mid, not real best bid/ask
        // (needs L2 quotes piped into this control — bigger change).
        private double LimitAnchorPrice(bool isBuy)
        {
            double tick = EffectiveTick();
            // Rest just IN FRONT of the wall (toward price): BUY = 1 tick ABOVE the support wall below mid,
            // SELL = 1 tick BELOW the resistance wall above mid. Both stay non-marketable when the wall is
            // >1 tick from mid (the normal case). ponytail: F14 — no fresh-quote clamp on submit yet.
            if (isBuy)
            {
                if (_wallBelow > 0) return RoundToTick(_wallBelow + tick, tick);
                Diag("BUY LMT: no wall below mid — anchoring at mid - 1 tick (best-bid proxy).");
                return RoundToTick(_lastPrice - tick, tick);
            }
            if (_wallAbove > 0) return RoundToTick(_wallAbove - tick, tick);
            Diag("SELL LMT: no wall above mid — anchoring at mid + 1 tick (best-ask proxy).");
            return RoundToTick(_lastPrice + tick, tick);
        }

        // isAuto=true only from TryAutoFire (AUTO mode) — guard 2 there guarantees _activeLimit is null,
        // so the re-anchor/flip branches below are unreachable on that path; it always falls through to
        // the fresh SubmitRaw call at the bottom, which is where isAuto is threaded to _autoOrder.
        private void SubmitLimit(bool isBuy, bool isAuto = false)
        {
            if (!ValidateForSubmit()) return;
            if (_lastPrice <= 0) { Diag("blocked — no price context yet."); return; }

            // A live pre-stage for THIS side (Task 12) wins over the wall-anchored default and is
            // consumed by this one click. A pre-stage for the OTHER side is stale the moment its
            // opposite button is clicked — drop it either way.
            PendingSetup setup = _pendingSetup;
            bool consumedSetup = setup != null && setup.IsBuy == isBuy;
            double price = consumedSetup ? setup.Price : LimitAnchorPrice(isBuy);
            if (setup != null) ClearPendingSetup();
            if (price <= 0) { Diag("blocked — invalid anchor price."); return; }
            // Log-only (feeds the Rec calibration pass / future F18) — the click can land seconds after
            // the fire, so the pre-staged price can have gone marketable against the current proxy quote
            // by the time the human clicks. _lastPrice > 0 already holds (checked above). No block.
            if (consumedSetup && (isBuy ? setup.Price >= _lastPrice : setup.Price <= _lastPrice))
                Diag(string.Format("pre-stage consumed marketable at click — setup {0:0.00} vs mid {1:0.00}.", setup.Price, _lastPrice));
            OrderAction action = isBuy ? OrderAction.Buy : OrderAction.Sell;
            string tag = isBuy ? "BuyLmt" : "SellLmt";
            int qty = GetQty();

            if (_activeLimit != null)
            {
                if (_activeLimit.OrderAction == action)
                {
                    // Same side: re-anchor in place — Account.Change is atomic, no cancel/submit race.
                    ChangeActiveLimitPrice(price, "re-anchor");
                    return;
                }
                // Opposite side (flip): sequence it. Cancel first; the replacement fires from
                // OnOrderUpdate once the cancel is confirmed (see PendingReplace above).
                Order old = _activeLimit;
                _workingOrders.Add(old);   // block a second submit while the cancel is in flight
                _pendingReplace = new PendingReplace { Action = action, Qty = qty, Price = price, Tag = tag, WaitingOn = old };
                try
                {
                    _account.Cancel(new[] { old });
                    RefreshArmUi();
                }
                catch (Exception ex)
                {
                    // Invariant broken if we proceed: Diag + bail. Do NOT null _activeLimit (old order is
                    // presumably still resting) and do NOT submit the replacement — that would compound it.
                    Diag("cancel-before-replace failed: " + ex.Message);
                    _workingOrders.Remove(old);
                    _pendingReplace = null;
                    RefreshArmUi();
                }
                return;
            }

            SubmitRaw(action, OrderType.Limit, qty, price, tag, isEntry: true, isAuto: isAuto);
        }

        // Change()'s the active working limit to a new price — used by both the ▲/▼ move and a
        // same-side LMT re-anchor. Guards the order isn't already Filled/Cancelled/Rejected before
        // calling Change (OnOrderUpdate is async-marshaled, so _activeLimit can lag reality briefly).
        private void ChangeActiveLimitPrice(double newPrice, string context)
        {
            Order ord = _activeLimit;
            if (ord == null) { Diag(context + ": no working limit order."); return; }
            if (Order.IsTerminalState(ord.OrderState))
            {
                Diag(context + ": order already " + ord.OrderState + " — refusing to Change.");
                return;
            }
            try
            {
                ord.LimitPriceChanged = newPrice;
                _account.Change(new[] { ord });
            }
            catch (Exception ex)
            {
                Diag(context + " failed: " + ex.Message);
            }
        }

        // ▲/▼ — move the active working limit 1 tick via Account.Change() (preserves queue priority;
        // does NOT cancel+resubmit). Confirmed API: Order.LimitPriceChanged + Account.Change(orders).
        private void MoveOrder(int deltaTicks)
        {
            if (!ValidateForSubmit()) return;
            Order ord = _activeLimit;
            if (ord == null) { Diag("no working limit order to move."); return; }
            double tick = EffectiveTick();
            double newPrice = RoundToTick(ord.LimitPrice + deltaTicks * tick, tick);
            bool isBuy = ord.OrderAction == OrderAction.Buy;
            // Keep non-marketable: clamp against mid as a best-bid/best-ask proxy (exact L2 quotes
            // aren't piped into this control — see LimitAnchorPrice). Diag + no-op at the boundary.
            if (isBuy && newPrice >= _lastPrice)  { Diag("blocked — move would cross the market (buy limit >= mid)."); return; }
            if (!isBuy && newPrice <= _lastPrice) { Diag("blocked — move would cross the market (sell limit <= mid)."); return; }
            ChangeActiveLimitPrice(newPrice, "move");
        }

        private Position CurrentPosition()
        {
            lock (_account.Positions)
                return _account.Positions.FirstOrDefault(p => p.Instrument == _instrument);
        }

        private void Reverse()
        {
            if (!ValidateForSubmit()) return;
            Position pos = CurrentPosition();
            if (IsFlat(pos))
            {
                Diag("Rev: no open position to reverse.");
                return;
            }
            OrderAction action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
            SubmitRaw(action, OrderType.Market, pos.Quantity * 2, 0, "Rev");
        }

        // isManualKill: true only from the Flat button's own click handler — the human kill-switch also
        // clears the persisted _autoIntent so AUTO never silently re-arms afterward (major finding: it
        // previously left intent standing, same as any other forced disarm, contradicting the "a manual
        // Flat always disarms AUTO" contract). The scheduled 16:00 auto-flatten (MaybeHoursFlatten) calls
        // this with the default false — that path is meant to repair and re-arm the next session.
        private void ClosePosition(bool cancelOrdersFirst, bool isManualKill = false)
        {
            if (!ValidateForSubmit()) return;
            if (cancelOrdersFirst)
            {
                ForceDisarmAuto("Flat manual");   // kill-switch semantics — a manual Flat always disarms AUTO
                if (isManualKill) _autoIntent = false;   // human Flat also clears intent — never silently re-arms later
                // Flat = platform-native cancel-all-orders + close-position for this instrument/account.
                try { _account.Flatten(new[] { _instrument }); }
                catch (Exception ex) { Diag("Flat failed: " + ex.Message); }
                return;
            }
            // A resting limit from this control could otherwise fill later and silently re-open the position.
            CancelActiveLimitIfWorking("close");
            Position pos = CurrentPosition();
            if (IsFlat(pos))
                return;
            OrderAction action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
            SubmitRaw(action, OrderType.Market, pos.Quantity, 0, "Close");
        }

        // Single choke point for every order this control sends — guarded, try/catch, diagnostic on failure.
        // ponytail: bracket SL/TP editor / TIF selector deferred. MKT + LMT (+ optional ATM) only.
        // isEntry=true (MKT/LMT buttons only — never Rev/Close) is the only path allowed to attach the
        // selected ATM template; Rev/Close always submit plain regardless of what's in the ATM selector.
        // isAuto=true (AUTO mode, threaded from SubmitLimit only) seeds _autoOrder/_autoSubmittedAt so
        // MaybeAutoCancel can later age this specific order out — a manual order never sets it.
        private void SubmitRaw(OrderAction action, OrderType type, int qty, double limitPrice, string tag, bool isEntry = false, bool isAuto = false)
        {
            if (qty < 1) { Diag("blocked — qty < 1."); return; }
            // F16: ATM only attaches once the user has actually picked one via the dropdown (DropDownClosed) —
            // a stale/auto-selected SelectedAtmStrategy from the control's own async repopulate never attaches.
            NinjaTrader.NinjaScript.AtmStrategy atm = (isEntry && _atmUserPicked) ? _atmSelector.SelectedAtmStrategy : null;
            // F20 (defense in depth): TryAutoFire's guard 5 already checks the ATM at fire time, but this
            // is the actual submit instant — an AUTO entry must NEVER go out naked. Abort before CreateOrder
            // (no order at all), unlike the atm-attach-failure branch below, which only degrades a MANUAL
            // entry to plain because a human is watching it.
            if (isAuto && atm == null)
            {
                DiagAuto("guard_skip", action.ToString(), limitPrice, _lastPrice, "AUTO blocked — no ATM at submit; refusing a naked auto entry.");
                return;
            }
            // ATM requires the entry order's CreateOrder "name" argument to be EXACTLY "Entry" (NT8 docs).
            string orderName = atm != null ? "Entry" : tag;
            try
            {
                // gtd is unused for TimeInForce.Day, but must be a real, in-range DateTime — NT8 adds an
                // exchange/UTC offset during order processing, so DateTime.MaxValue can overflow and throw.
                Order o = _account.CreateOrder(_instrument, action, type, OrderEntry.Manual,
                    TimeInForce.Day, qty, limitPrice, 0, string.Empty, orderName, NinjaTrader.Core.Globals.MaxDate, null);
                _ownOrders.Add(o);   // F15: seed ownership the moment we create it — every order this control originates
                if (isAuto) { _autoOrder = o; _autoSubmittedAt = _now; }
                // "submit" carries the order id — this is the one place it exists (round-3 schema).
                if (isAuto)
                    DiagAuto("submit", action.ToString(), limitPrice, _lastPrice,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "AUTO submit — {0} LMT @ {1:0.00}, order #{2}, qty {3} ({4}/{5} today).",
                            action, limitPrice, o.Id, qty, _autoFireCount, AutoFireCapPerDay));
                if (atm != null)
                {
                    try
                    {
                        // StartAtmStrategy submits the entry order itself — do not also call Account.Submit.
                        NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(atm, o);
                        // Bracket distances at attach time (verdict doc item 1) — without this, no AUTO
                        // fire can ever be graded by realized R. Encoded in the detail text so the CSV
                        // schema stays 6 columns; kept parseable (BracketSummary's format).
                        if (isAuto) LogAuto("atm_attach", action.ToString(), limitPrice, _lastPrice,
                            "ATM " + atm.Name + " attached to order #" + o.Id + " (" + BracketSummary(atm) + ").");
                    }
                    catch (Exception atmEx)
                    {
                        // F17: only fall back to a plain submit if the entry was never actually sent —
                        // if StartAtmStrategy already dispatched it before throwing, resubmitting would duplicate it.
                        if (o.OrderState == OrderState.Initialized)
                        {
                            Diag("ATM attach failed (" + atm.Name + "): " + atmEx.Message + " — submitting plain entry instead.");
                            if (isAuto) LogAuto("atm_attach", action.ToString(), limitPrice, _lastPrice,
                                "FAILED (" + atm.Name + "): " + atmEx.Message + " — plain entry submitted instead.");
                            _account.Submit(new[] { o });
                        }
                        else
                        {
                            Diag("ATM attach failed after entry was already sent (" + o.OrderState + ") — NOT resubmitting (avoid duplicate).");
                            if (isAuto) LogAuto("atm_attach", action.ToString(), limitPrice, _lastPrice,
                                "FAILED after entry already sent (" + o.OrderState + ") — not resubmitted.");
                        }
                    }
                }
                else
                {
                    _account.Submit(new[] { o });
                }
                _workingOrders.Add(o);   // synchronous in-flight guard — closes the double-click window
                RefreshArmUi();
            }
            catch (Exception ex)
            {
                Diag("submit failed (" + tag + "): " + ex.Message);
                if (isAuto) LogAuto("submit", action.ToString(), limitPrice, _lastPrice, "AUTO submit FAILED: " + ex.Message);
            }
        }

        // ADR 2026-07-03 Phase 1 (exit-leg instrumentation): the ATM's bracket distances (ticks) at
        // attach time — without this, no AUTO fire can ever be graded by realized R (verdict doc item
        // 1). NinjaTrader.Cbi.Bracket.StopLoss/Target carry no unit in the API itself, but every ATM
        // template NT8 ships stores these as TICK distances (confirmed via ilspycmd decompile of
        // NinjaTrader.Core.dll — Bracket.{Quantity,StopLoss,StopStrategy,Target}; matches the ATM
        // Strategy Designer's own "Stop Loss"/"Target" columns, which are ticks by convention).
        private static string BracketSummary(NinjaTrader.NinjaScript.AtmStrategy atm)
        {
            Bracket[] brackets = atm.Brackets;
            if (brackets == null || brackets.Length == 0) return "atm " + atm.Name + ", no brackets";
            string[] parts = new string[brackets.Length];
            for (int i = 0; i < brackets.Length; i++)
                parts[i] = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "sl {0:0.#}t, tp {1:0.#}t, qty {2}", brackets[i].StopLoss, brackets[i].Target, brackets[i].Quantity);
            return "atm " + atm.Name + ", " + string.Join("; ", parts);
        }

        // ---- persistence surface (2026-07-03, always-armed AUTO, verdict doc item 6) ----
        // Read by RadarTab.Save() into its own RadarAuto XElement, alongside the pre-existing
        // RadarInstrument element — same Save/Restore(XElement) pattern RadarTab already uses.
        public bool AutoIntentArmed { get { return _autoIntent; } }
        public string SelectedAccountName { get { return _account != null ? _account.Name : null; } }
        public string SelectedAtmName { get { return _lastPickedAtmName; } }
        public int Qty { get { return GetQty(); } }
        public bool HoursEnabled { get { return _hoursChk.IsChecked == true; } }
        public string HoursStartText { get { return _hoursStartBox.Text; } }
        public string HoursEndText { get { return _hoursEndBox.Text; } }
        public string HoursFlatText { get { return _hoursFlatBox.Text; } }
        // Blocker fix: persisted alongside the rest of RadarAuto so a restore can seed the daily-cap
        // count instead of resurrecting a fresh one — see _pendingRestoreFireDay/TryAutoFire.
        public string AutoFireDayText
        {
            get
            {
                return _autoFireDay == DateTime.MinValue ? string.Empty
                    : _autoFireDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        public int AutoFireCount { get { return _autoFireCount; } }

        // Called once by RadarTab.Restore(), AFTER Instrument has already been pushed to this control
        // (the Instrument setter resets ATM pick + disarms — restoring account/ATM only makes sense
        // after that settles). Qty/HOURS apply immediately; account/ATM resolve asynchronously (the
        // platform's own account-connect and ATM-selector item-list population) — see
        // TryApplyPendingRestoreAccount and MaybeResolveAtmRestore. Auto-rearm itself only fires once
        // BOTH resolve, via MaybeAutoRearm, gated on the restored _autoIntent.
        public void RestoreAutoState(bool intentArmed, string accountName, string atmName, int qty,
            bool hoursEnabled, string hoursStart, string hoursEnd, string hoursFlat,
            string fireDay, int fireCount)
        {
            _autoIntent = intentArmed;
            if (qty > 0) _qtyBox.Text = qty.ToString();
            _hoursChk.IsChecked = hoursEnabled;
            ApplyRestoredTime(_hoursStartBox, hoursStart, v => _hoursStart = v);
            ApplyRestoredTime(_hoursEndBox, hoursEnd, v => _hoursEnd = v);
            ApplyRestoredTime(_hoursFlatBox, hoursFlat, v => _hoursFlat = v);
            _pendingAtmRestoreName = string.IsNullOrEmpty(atmName) ? null : atmName;
            DateTime parsedFireDay;
            if (!string.IsNullOrEmpty(fireDay) && DateTime.TryParseExact(fireDay, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsedFireDay))
            {
                _pendingRestoreFireDay = parsedFireDay;
                _pendingRestoreFireCount = fireCount;
            }
            if (!string.IsNullOrEmpty(accountName)) SelectAccountByName(accountName);
        }

        // Values round-trip from HoursStartText/HoursEndText/HoursFlatText (always "H:mm"-formatted by
        // the existing commit logic in the constructor), so this only needs to guard against a missing/
        // corrupt saved workspace, not user typos.
        private static void ApplyRestoredTime(TextBox box, string text, Action<TimeSpan> set)
        {
            DateTime parsed;
            if (string.IsNullOrEmpty(text)) return;
            if (DateTime.TryParseExact(text.Trim(), "H:mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out parsed))
            {
                set(parsed.TimeOfDay);
                box.Text = text;
            }
        }

        private void SelectAccountByName(string name)
        {
            _pendingRestoreAccountName = name;
            TryApplyPendingRestoreAccount();
        }

        // Consumed once the named account actually exists in Account.All — immediately if it's already
        // connected (the common case: accounts connect before a saved workspace reopens its windows),
        // else on the next PopulateAccounts() call, which already re-runs on every AccountStatusUpdate
        // (the platform's own account-connect event) — no separate timer/poll needed.
        private void TryApplyPendingRestoreAccount()
        {
            if (_pendingRestoreAccountName == null) return;
            Account acct;
            lock (Account.All) acct = Account.All.FirstOrDefault(a => a.Name == _pendingRestoreAccountName);
            if (acct == null) return;
            _pendingRestoreAccountName = null;
            _accountCombo.SelectedItem = acct;   // fires _accountSelectionHandler -> OnAccountSelected()
        }

        // Checked every SetContext tick (cheap: one field null-check + an Items scan, only while a
        // restore is pending) so it converges regardless of when the ATM selector's own async Account/
        // Instrument-driven repopulate actually completes — AtmStrategySelector is a plain ComboBox
        // (confirmed via ilspycmd decompile of NinjaTrader.Gui.dll) so its inherited Items collection is
        // the one verifiable way to find the persisted template and select it ourselves; we don't rely
        // on the control's own (undocumented, obfuscated) default-selection behavior. Setting
        // _atmUserPicked here mirrors DropDownClosed on purpose — a restored template IS the user's own
        // prior explicit intent, not a stale/auto-selected leak (F16's concern). If the persisted
        // template no longer exists for this account, this simply never resolves (ponytail: no timeout —
        // a permanently-pending Items scan is cheap; add a missing-template diagnostic if the CSV ever
        // shows this stuck in practice).
        private void MaybeResolveAtmRestore()
        {
            if (_pendingAtmRestoreName == null) return;
            // Review finding (major): ATM templates aren't unique per account. Without this guard, a
            // same-named template on whatever account happens to be selected while the persisted
            // account is still connecting would match here FIRST, consuming _pendingAtmRestoreName
            // against the WRONG account — and OnAccountSelected's later correct-account switch has
            // nothing left to re-resolve against. Wait for the persisted account itself to resolve.
            if (_pendingRestoreAccountName != null) return;
            foreach (object item in _atmSelector.Items)
            {
                NinjaTrader.NinjaScript.AtmStrategy cand = item as NinjaTrader.NinjaScript.AtmStrategy;
                if (cand == null || cand.Name != _pendingAtmRestoreName) continue;
                _atmSelector.SelectedItem = item;
                _pendingAtmRestoreName = null;
                _atmUserPicked = true;
                _lastPickedAtmName = cand.Name;
                MaybeAutoRearm("restore");
                return;
            }
        }

        public void Cleanup()
        {
            _cleanedUp = true;   // set FIRST — see EnsureAutoLogWriter
            CancelActiveLimitIfWorking("cleanup");   // don't leave a live order orphaned on teardown
            _workingOrders.Clear();
            _ownOrders.Clear();
            Account.AccountStatusUpdate -= OnAccountStatusUpdate;
            UnsubscribeAccount();
            if (_autoLogWriter != null) { _autoLogWriter.Flush(); _autoLogWriter.Dispose(); _autoLogWriter = null; }
        }
    }
}
