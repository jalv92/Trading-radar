using System.Windows;
using NinjaTrader.Core;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;        // NTMenuItem, ControlCenter
using NinjaTrader.NinjaScript;

namespace TradingRadar.NT
{
    public class RadarAddOn : NinjaTrader.NinjaScript.AddOnBase
    {
        private NTMenuItem _radarMenuItem;
        private NTMenuItem _existingMenuItem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "LiquidityRadar";
                Description = "Floating Level-2 liquidity radar (sonar ladder + tracked walls).";
            }
        }

        // Called on the thread of each new NTWindow (including on recompile).
        protected override void OnWindowCreated(Window window)
        {
            ControlCenter cc = window as ControlCenter;
            if (cc == null)
                return;

            _existingMenuItem = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (_existingMenuItem == null)
                return;

            _radarMenuItem = new NTMenuItem
            {
                Header = "Liquidity Radar",
                Style  = Application.Current.TryFindResource("MainMenuItem") as Style
            };
            _existingMenuItem.Items.Add(_radarMenuItem);
            _radarMenuItem.Click += OnMenuItemClick;
        }

        // Recompile-safe cleanup: pull our item back out.
        protected override void OnWindowDestroyed(Window window)
        {
            if (_radarMenuItem != null && window is ControlCenter)
            {
                if (_existingMenuItem != null && _existingMenuItem.Items.Contains(_radarMenuItem))
                    _existingMenuItem.Items.Remove(_radarMenuItem);
                _radarMenuItem.Click -= OnMenuItemClick;
                _radarMenuItem = null;
            }
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            Globals.RandomDispatcher.InvokeAsync(new System.Action(() => new RadarWindow().Show()));
        }
    }
}
