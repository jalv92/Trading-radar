using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    public class RadarVisual : FrameworkElement
    {
        // ---- Aurora palette (frozen at class init) ----
        static readonly Brush BgGrad;
        static readonly Brush Track    = FrozenBrush(Color.FromArgb(9,   0xff, 0xff, 0xff));   // ~.035
        static readonly Color BidColor = Color.FromRgb(0x34, 0xd3, 0x99);
        static readonly Color AskColor = Color.FromRgb(0xfb, 0x71, 0x85);
        static readonly Brush BidText  = FrozenBrush(Color.FromRgb(0x6e, 0xe7, 0xb7));
        static readonly Brush AskText  = FrozenBrush(Color.FromRgb(0xfd, 0xa4, 0xaf));
        static readonly Brush PriceTxt = FrozenBrush(Color.FromRgb(0xcf, 0xd6, 0xe2));
        static readonly Brush Amber    = FrozenBrush(Color.FromArgb(128, 0xff, 0xce, 0x5c));   // .5
        static readonly Brush AmberTxt = FrozenBrush(Color.FromRgb(0xff, 0xe0, 0x8a));
        static readonly Brush AbsorbBg = FrozenBrush(Color.FromRgb(0xa7, 0xf3, 0xd0));
        static readonly Brush SlateBg  = FrozenBrush(Color.FromRgb(0x94, 0xa3, 0xb8));
        static readonly Pen   Grid     = FrozenPen(Color.FromArgb(16, 0xff, 0xff, 0xff), 1);
        static readonly Pen   AmberLine= FrozenPen(Color.FromArgb(128, 0xff, 0xce, 0x5c), 1);
        static readonly Pen   PullDash = FrozenDash(Color.FromRgb(0x94, 0xa3, 0xb8), 1);
        static readonly Typeface Mono  = new Typeface(new FontFamily("Consolas"),
                                             FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface Sans  = new Typeface(new FontFamily("Segoe UI"),
                                             FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static RadarVisual()
        {
            // Background: radial token approximated as a vertical gradient.
            var gb = new LinearGradientBrush(
                Color.FromRgb(0x12, 0x18, 0x26),
                Color.FromRgb(0x0a, 0x0e, 0x16),
                90.0);
            gb.Freeze();
            BgGrad = gb;
        }

        private IReadOnlyList<RadarNode> _nodes;
        private double _mid;
        private double _tick  = 0.25;
        private int    _bandTicks = 25;
        private double _sweep;      // 0..1 animation phase

        public void SetNodes(IReadOnlyList<RadarNode> nodes, double mid, double tickSize)
        {
            _nodes = nodes;
            _mid   = mid;
            if (tickSize > 0) _tick = tickSize;
            InvalidateVisual();
        }

        public void AdvanceAnimation()
        {
            _sweep += 0.012;
            if (_sweep > 1.0) _sweep -= 1.0;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.DrawRectangle(BgGrad, null, new Rect(0, 0, w, h));

            if (_nodes == null || _mid <= 0) return;

            int    rows    = 2 * _bandTicks + 1;
            double rowH    = h / rows;
            double centerY = h / 2.0;
            double barX    = 64, barMaxW = w - barX - 96;

            // Iso-distance gridlines.
            foreach (int g in new[] { 5, 10, 25 })
            {
                double yUp = centerY - g * rowH, yDn = centerY + g * rowH;
                if (yUp > 0) dc.DrawLine(Grid, new Point(0, yUp), new Point(w, yUp));
                if (yDn < h) dc.DrawLine(Grid, new Point(0, yDn), new Point(w, yDn));
            }

            long maxSize = 1;
            for (int i = 0; i < _nodes.Count; i++)
                if (_nodes[i].LastKnownSize > maxSize) maxSize = _nodes[i].LastKnownSize;

            for (int i = 0; i < _nodes.Count; i++)
            {
                RadarNode n    = _nodes[i];
                double    y    = centerY - ((n.Price - _mid) / _tick) * rowH;
                if (y < -rowH || y > h + rowH) continue;

                bool blind  = !n.InWindow || n.State == NodeState.Remembered;
                bool isBid  = n.Side == Side.Bid;
                Color base1 = isBid ? BidColor : AskColor;
                double op   = blind ? 0.34 : 1.0;
                double frac = Math.Min(1.0, n.LastKnownSize / (double)maxSize);
                double barW = Math.Max(2.0, frac * barMaxW);
                double rowTop = y - rowH * 0.40, rowHt = rowH * 0.80;

                // Per-bar glow for Wall/Absorbed states (manual — no DropShadowEffect).
                if (!blind && (n.State == NodeState.Wall || n.State == NodeState.Absorbed))
                {
                    byte ga = isBid ? (byte)80 : (byte)90;
                    var glowBrush = new SolidColorBrush(Color.FromArgb(ga, base1.R, base1.G, base1.B));
                    dc.DrawRoundedRectangle(glowBrush, null,
                        new Rect(barX - 4, rowTop - 3, barW + 8, rowHt + 6), 5, 5);
                }

                // Bar track.
                dc.DrawRoundedRectangle(Track, null, new Rect(barX, rowTop, barMaxW, rowHt), 3, 3);

                // Bar fill.
                Brush barBrush;
                Pen   barPen = null;
                if (n.State == NodeState.Pulled)
                {
                    barBrush = new SolidColorBrush(Color.FromArgb((byte)(0.7 * 255), 0x6b, 0x72, 0x80));
                    barPen   = PullDash;
                }
                else
                {
                    barBrush = new SolidColorBrush(Color.FromArgb((byte)(op * 255), base1.R, base1.G, base1.B));
                }
                dc.DrawRoundedRectangle(barBrush, barPen, new Rect(barX, rowTop, barW, rowHt), 3, 3);

                // Price label (left, tabular).
                DrawText(dc, n.Price.ToString("0.00", CultureInfo.InvariantCulture),
                         4, y, 11, Mono, PriceTxt, dpi, op);

                // Size label (right of bar).
                DrawText(dc, n.LastKnownSize.ToString(),
                         barX + barW + 6, y, 11, Mono, isBid ? BidText : AskText, dpi, op);

                // State badge / age tag.
                string badge = BadgeFor(n.State);
                if (blind) badge = "· " + Math.Round(n.AgeSeconds) + "s";
                if (!string.IsNullOrEmpty(badge))
                    DrawText(dc, badge, w - 70, y, 10, Sans, BadgeBrush(n.State, blind), dpi, op);
            }

            // Inside-market amber line + mid chip.
            dc.DrawLine(AmberLine, new Point(0, centerY), new Point(w, centerY));
            DrawText(dc, _mid.ToString("0.00", CultureInfo.InvariantCulture),
                     4, centerY - 1, 12, Mono, AmberTxt, dpi, 1.0);

            // Refresh-pulse sweep: honest data-refresh indicator.
            double sy = _sweep * h;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(10, 0xff, 0xff, 0xff)),
                             null, new Rect(0, sy, w, 2));
        }

        // ---- helpers ----
        private static string BadgeFor(NodeState s)
        {
            switch (s)
            {
                case NodeState.Wall:     return "WALL";
                case NodeState.Absorbed: return "ABSORB";
                case NodeState.Pulled:   return "PULL";
                case NodeState.Consumed: return "BROKE";
                default:                 return "";
            }
        }

        private static Brush BadgeBrush(NodeState s, bool blind)
        {
            if (blind)                   return PriceTxt;
            if (s == NodeState.Absorbed) return AbsorbBg;
            if (s == NodeState.Pulled)   return SlateBg;
            return PriceTxt;
        }

        private static void DrawText(DrawingContext dc, string txt, double x, double yCenter,
                                     double size, Typeface tf, Brush b, double dpi, double op)
        {
            var ft = new FormattedText(txt, CultureInfo.InvariantCulture,
                                       FlowDirection.LeftToRight, tf, size, b, dpi);
            bool pushed = op < 1.0;
            if (pushed) dc.PushOpacity(op);
            dc.DrawText(ft, new Point(x, yCenter - ft.Height / 2.0));
            if (pushed) dc.Pop();
        }

        // ---- frozen-resource factories ----
        private static Brush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c); b.Freeze(); return b;
        }

        private static Pen FrozenPen(Color c, double t)
        {
            var p = new Pen(FrozenBrush(c), t); p.Freeze(); return p;
        }

        private static Pen FrozenDash(Color c, double t)
        {
            var ds = new DashStyle(new double[] { 3, 2 }, 0);
            ds.Freeze();
            var p = new Pen(FrozenBrush(c), t) { DashStyle = ds };
            p.Freeze();
            return p;
        }
    }
}
