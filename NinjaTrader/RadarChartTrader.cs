using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NinjaTrader.Cbi;

namespace TradingRadar.NT
{
    // Order-entry surface docked under the Cockpit (spec §8). MKT-only, Sim/Playback-first.
    // Real Account API: CreateOrder()+Submit() (no strategy Enter*/Exit* — this is an Add-On, not a Strategy).
    // Hard gate: submits to a non-Sim/non-Playback account are blocked unless "ARM LIVE" is checked.
    public class RadarChartTrader : Grid
    {
        private static readonly SolidColorBrush Emerald = Brush(0x34, 0xd3, 0x99);
        private static readonly SolidColorBrush Coral    = Brush(0xfb, 0x71, 0x85);
        private static readonly SolidColorBrush Ink      = Brush(0x0f, 0x14, 0x20);
        private static readonly SolidColorBrush Panel    = Brush(0x12, 0x18, 0x26);
        private static readonly SolidColorBrush Muted    = Brush(0x9a, 0xa4, 0xb2);
        private static readonly SolidColorBrush TextCol  = Brush(0xcf, 0xd6, 0xe2);
        private static readonly SolidColorBrush BorderBr = new SolidColorBrush(Color.FromArgb(30, 0xff, 0xff, 0xff));

        private static SolidColorBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

        private Instrument _instrument;
        private Account _account;
        private Account _armedFor;      // account the user explicitly armed via the checkbox — per-account, not sticky
        private double _lastPrice;
        private readonly HashSet<Order> _workingOrders = new HashSet<Order>(); // in-flight orders this control submitted

        private readonly ComboBox _accountCombo = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 6, 0) };
        private readonly CheckBox _armChk = new CheckBox { Content = "ARM LIVE", Visibility = Visibility.Collapsed,
            Foreground = Coral, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBlock _warnText = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
        private readonly TextBox _qtyBox = new TextBox { Text = "1", Width = 40, TextAlignment = TextAlignment.Center,
            Background = Ink, Foreground = TextCol, BorderBrush = BorderBr,
            VerticalContentAlignment = VerticalAlignment.Center };
        private readonly Button _buyBtn;
        private readonly Button _sellBtn;
        private readonly Button _revBtn;
        private readonly Button _closeBtn;
        private readonly Button _flatBtn;
        private readonly TextBlock _posText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13,
            FontWeight = FontWeights.SemiBold, Foreground = Muted, Text = "FLAT" };
        private readonly TextBlock _pnlText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Foreground = Muted };

        public RadarChartTrader()
        {
            Background = Panel;
            for (int i = 0; i < 5; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _buyBtn  = MakePrimaryButton("BUY MKT", Emerald);
            _sellBtn = MakePrimaryButton("SELL MKT", Coral);
            _revBtn   = MakeManageButton("Rev");
            _closeBtn = MakeManageButton("Close");
            _flatBtn  = MakeManageButton("Flat");

            _buyBtn.Click  += (o, e) => SubmitMarket(OrderAction.Buy, "Buy");
            _sellBtn.Click += (o, e) => SubmitMarket(OrderAction.Sell, "Sell");
            _revBtn.Click   += (o, e) => Reverse();
            _closeBtn.Click += (o, e) => ClosePosition(false);
            _flatBtn.Click  += (o, e) => ClosePosition(true);

            _accountCombo.DisplayMemberPath = "Name";
            _accountCombo.Background = Ink;
            _accountCombo.Foreground = TextCol;
            _accountCombo.SelectionChanged += (o, e) => OnAccountSelected();
            _armChk.Checked   += (o, e) => { _armedFor = _account; RefreshArmUi(); };
            _armChk.Unchecked += (o, e) => { _armedFor = null; RefreshArmUi(); };

            Action commitQty = () =>
            {
                int q;
                if (!int.TryParse(_qtyBox.Text, out q) || q < 1) q = 1;
                _qtyBox.Text = q.ToString();
            };
            _qtyBox.LostFocus += (o, e) => commitQty();
            _qtyBox.KeyDown   += (o, e) => { if (e.Key == Key.Enter) commitQty(); };

            // Row 0: account + arm gate
            AddRow(0, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 2),
                Children = { _accountCombo, _armChk, _warnText } });

            // Row 1: qty stepper
            {
                var lbl = new TextBlock { Text = "Qty:", Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11, Foreground = Muted };
                var minus = MakeManageButton("−"); minus.Width = 22;
                var plus  = MakeManageButton("+");  plus.Width = 22;
                minus.Click += (o, e) => StepQty(-1);
                plus.Click  += (o, e) => StepQty(1);
                AddRow(1, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 2),
                    Children = { lbl, minus, _qtyBox, plus } });
            }

            // Row 2: BUY / SELL
            {
                var row = new Grid { Margin = new Thickness(6, 4, 6, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _buyBtn.Margin = new Thickness(0, 0, 3, 0);
                _sellBtn.Margin = new Thickness(3, 0, 0, 0);
                SetColumn(_buyBtn, 0);  row.Children.Add(_buyBtn);
                SetColumn(_sellBtn, 1); row.Children.Add(_sellBtn);
                AddRow(2, row);
            }

            // Row 3: Rev / Close / Flat
            AddRow(3, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 4),
                Children = { _revBtn, _closeBtn, _flatBtn } });

            // Row 4: position + PnL readout
            AddRow(4, new StackPanel { Margin = new Thickness(6, 2, 6, 6),
                Children = { _posText, _pnlText } });

            // ponytail: no TifSelector / AtmStrategySelector / Entry button / SL-TP lines here —
            // MKT-only v1 per spec §8/§12. TimeInForce hardcoded to Day in SubmitRaw().

            Account.AccountStatusUpdate += OnAccountStatusUpdate;
            PopulateAccounts();
        }

        private void AddRow(int row, UIElement content)
        {
            SetRow(content, row);
            Children.Add(content);
        }

        private Button MakePrimaryButton(string text, SolidColorBrush glowColor)
        {
            var b = new Button
            {
                Content = text,
                Height = 34,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Ink,
                Background = glowColor,
                BorderThickness = new Thickness(0),
                Effect = new DropShadowEffect { Color = glowColor.Color, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.5 }
            };
            return b;
        }

        private Button MakeManageButton(string text)
        {
            return new Button
            {
                Content = text,
                Height = 24,
                MinWidth = 50,
                Margin = new Thickness(0, 0, 6, 0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = TextCol,
                Background = Ink,
                BorderBrush = BorderBr
            };
        }

        // Pushed by RadarTab whenever the tab's instrument changes (selector or Restore()).
        public Instrument Instrument
        {
            get { return _instrument; }
            set
            {
                if (_instrument == value) return;
                _instrument = value;
                _lastPrice = 0;
                _workingOrders.Clear(); // ponytail: stale in-flight orders from the old instrument stop being tracked
                RefreshArmUi();
                RefreshPositionUi();
            }
        }

        // Called by RadarTab's UI-thread paint timer with the already-marshaled book mid —
        // reuses the existing instrument subscription instead of opening a second MarketData feed.
        public void SetLastPrice(double mid)
        {
            if (mid <= 0) return;
            _lastPrice = mid;
            RefreshPositionUi();
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

        private void PopulateAccounts()
        {
            List<Account> accts;
            lock (Account.All) accts = Account.All.ToList();
            Account keep = _accountCombo.SelectedItem as Account;
            _accountCombo.ItemsSource = accts;
            if (keep != null && accts.Contains(keep))
                _accountCombo.SelectedItem = keep;
            else
                _accountCombo.SelectedItem = accts.FirstOrDefault(IsSimAccount) ?? accts.FirstOrDefault();
        }

        private void OnAccountSelected()
        {
            Account acct = _accountCombo.SelectedItem as Account;
            if (acct == _account) return;
            UnsubscribeAccount();
            _account = acct;
            _armedFor = null;           // arming never carries over to a newly selected account
            _armChk.IsChecked = false;
            _workingOrders.Clear();     // in-flight tracking is per-account (positions/orders differ)
            SubscribeAccount();
            RefreshArmUi();
            RefreshPositionUi();
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

        private static bool IsTerminal(OrderState s)
        {
            return s == OrderState.Filled || s == OrderState.Cancelled || s == OrderState.Rejected;
        }

        // ---- account-thread handlers: marshal any WPF mutation to the UI thread ----
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            Instrument inst = _instrument;   // snapshot once — avoid a concurrent reassignment race
            Order ord = e.Order;
            if (ord == null || ord.Instrument != inst) return;   // ignore other instruments
            if (e.OrderState == OrderState.Rejected)
                Diag("order rejected: " + e.Error + " " + e.Comment);
            OrderState state = e.OrderState;
            Dispatcher.InvokeAsync((Action)(() =>
            {
                if (IsTerminal(state)) _workingOrders.Remove(ord);   // HashSet.Remove is a no-op if already gone
                RefreshPositionUi();
                RefreshArmUi();   // re-enables buttons once the in-flight count drops to 0
            }));
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (e.Execution == null || e.Execution.Instrument != _instrument) return;
            Dispatcher.InvokeAsync((Action)RefreshPositionUi);
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
            Position pos;
            lock (_account.Positions)
                pos = _account.Positions.FirstOrDefault(p => p.Instrument == _instrument);

            if (pos == null || pos.MarketPosition == MarketPosition.Flat || pos.Quantity == 0)
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
            SubmitRaw(action, GetQty(), tag);
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
            if (pos == null || pos.MarketPosition == MarketPosition.Flat || pos.Quantity == 0)
            {
                Diag("Rev: no open position to reverse.");
                return;
            }
            OrderAction action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
            SubmitRaw(action, pos.Quantity * 2, "Rev");
        }

        private void ClosePosition(bool cancelOrdersFirst)
        {
            if (!ValidateForSubmit()) return;
            if (cancelOrdersFirst)
            {
                // Flat = platform-native cancel-all-orders + close-position for this instrument/account.
                try { _account.Flatten(new[] { _instrument }); }
                catch (Exception ex) { Diag("Flat failed: " + ex.Message); }
                return;
            }
            Position pos = CurrentPosition();
            if (pos == null || pos.MarketPosition == MarketPosition.Flat || pos.Quantity == 0)
                return;
            OrderAction action = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
            SubmitRaw(action, pos.Quantity, "Close");
        }

        // Single choke point for every order this control sends — guarded, try/catch, diagnostic on failure.
        // ponytail: OrderType.Market / TimeInForce.Day hardcoded — limit/stop/bracket/ATM deferred (spec §8/§12).
        private void SubmitRaw(OrderAction action, int qty, string tag)
        {
            if (qty < 1) { Diag("blocked — qty < 1."); return; }
            try
            {
                // gtd is unused for TimeInForce.Day, but must be a real, in-range DateTime — NT8 adds an
                // exchange/UTC offset during order processing, so DateTime.MaxValue can overflow and throw.
                Order o = _account.CreateOrder(_instrument, action, OrderType.Market, OrderEntry.Manual,
                    TimeInForce.Day, qty, 0, 0, string.Empty, tag, NinjaTrader.Core.Globals.MaxDate, null);
                _account.Submit(new[] { o });
                _workingOrders.Add(o);   // synchronous in-flight guard — closes the double-click window
                RefreshArmUi();
            }
            catch (Exception ex)
            {
                Diag("submit failed (" + tag + "): " + ex.Message);
            }
        }

        public void Cleanup()
        {
            Account.AccountStatusUpdate -= OnAccountStatusUpdate;
            UnsubscribeAccount();
        }
    }
}
