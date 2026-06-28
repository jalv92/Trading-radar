using System;
using System.Windows.Controls;
using System.Xml.Linq;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;

namespace TradingRadar.NT
{
    public class RadarWindow : NTWindow, IWorkspacePersistence
    {
        public RadarWindow()
        {
            Caption = "Liquidity Radar";
            Width   = 460;
            Height  = 820;

            TabControl tc = new TabControl();
            TabControlManager.SetIsMovable(tc, true);
            TabControlManager.SetCanAddTabs(tc, true);
            TabControlManager.SetCanRemoveTabs(tc, true);
            TabControlManager.SetFactory(tc, new RadarTabFactory());
            Content = tc;

            tc.AddNTTabPage(new RadarTab());

            Loaded += (o, e) =>
            {
                if (WorkspaceOptions == null)
                    WorkspaceOptions = new WorkspaceOptions("LiquidityRadar-" + Guid.NewGuid().ToString("N"), this);
            };
        }

        public void Restore(XDocument document, XElement element)
        {
            if (MainTabControl != null)
                MainTabControl.RestoreFromXElement(element);
        }

        public void Save(XDocument document, XElement element)
        {
            if (MainTabControl != null)
                MainTabControl.SaveToXElement(element);
        }

        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    public class RadarTabFactory : INTTabFactory
    {
        public NTWindow CreateParentWindow() { return new RadarWindow(); }
        public NTTabPage CreateTabPage(string typeName, bool isNewWindow = false) { return new RadarTab(); }
    }
}
