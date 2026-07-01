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
        static readonly Pen   AmberLine= FrozenPen(Color.FromArgb(128, 0xff, 0xce, 0x5c), 1);
        static readonly Pen   PullDash = FrozenDash(Color.FromRgb(0x94, 0xa3, 0xb8), 1);
        static readonly Brush BidBook      = FrozenBrush(Color.FromArgb(115, 0x34, 0xd3, 0x99));  // ~.45
        static readonly Brush AskBook      = FrozenBrush(Color.FromArgb(115, 0xfb, 0x71, 0x85));  // ~.45
        static readonly Brush MidChipBg    = FrozenBrush(Color.FromArgb(230, 0x14, 0x18, 0x22));
        static readonly Pen   MidChipBorder= FrozenPen(Color.FromArgb(89, 0xff, 0xce, 0x5c), 1); // ~.35
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

        public RadarVisual() { ClipToBounds = true; }

        private IReadOnlyList<RadarNode>  _nodes;
        private IReadOnlyList<DepthLevel> _bids;
        private IReadOnlyList<DepthLevel> _asks;
        private double _mid;
        private double _tick           = 0.25;
        private double _anchorTop      = double.NaN;   // price of the top visible row; persists across frames
        private const int    DESIRED_ROWS = 27;        // price levels the ladder targets (mid ± ~13 ticks → covers the book)
        private const double MIN_ROW_PX   = 16.0;      // ponytail: row-height clamp; widen the band if bars look too thin/fat
        private const double MAX_ROW_PX   = 40.0;

        public void SetFrame(IReadOnlyList<RadarNode> nodes, IReadOnlyList<DepthLevel> bids,
                             IReadOnlyList<DepthLevel> asks, double mid, double tickSize)
        {
            _nodes = nodes; _bids = bids; _asks = asks;
            _mid   = mid;
            if (tickSize > 0) _tick = tickSize;
            InvalidateVisual();
        }

        public void AdvanceAnimation() { }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.DrawRectangle(BgGrad, null, new Rect(0, 0, w, h));

            if (_mid <= 0) return;

            // Row grid — row height scales so the ladder FILLS the window at any size
            // (clamped so bars never get absurdly thin/fat). rowH depends only on panel
            // height → constant per frame, changes only when the user resizes → no jitter.
            double rowH = Math.Min(MAX_ROW_PX, Math.Max(MIN_ROW_PX, h / DESIRED_ROWS));
            int    rows = Math.Max(9, (int)Math.Floor(h / rowH));
            double barX = 88, barMaxW = w - barX - 96;

            // Anchor so mid sits centered, snapped to the tick grid. Re-anchor (recenter)
            // ONLY when mid drifts out of the middle band, or on first frame / instrument
            // change / discontinuity. Between re-anchors the grid is stationary: a given
            // price keeps its y, bars grow/shrink in place, only the mid marker glides
            // (DOM-standard scroll-in-place — no whole-profile reflow every tick).
            double midRowNow = double.IsNaN(_anchorTop) ? double.NaN : (_anchorTop - _mid) / _tick;
            double band = rows / 4.0;   // recenter once mid leaves the middle half of the ladder
            if (double.IsNaN(midRowNow) || midRowNow < band || midRowNow > rows - 1 - band)
                _anchorTop = RoundToTick(_mid) + Math.Floor(rows / 2.0) * _tick;

            // Anchored y-mapping: price of the top row is _anchorTop; rows go downward by _tick each.
            double Y(double price) { return ((_anchorTop - price) / _tick) * rowH; }

            // maxSize spans book levels + wall nodes so bar widths are proportional across both layers.
            long maxSize = 1;
            if (_nodes != null) for (int i = 0; i < _nodes.Count; i++) if (_nodes[i].LastKnownSize > maxSize) maxSize = _nodes[i].LastKnownSize;
            if (_bids  != null) for (int i = 0; i < _bids.Count;  i++) if (_bids[i].Volume  > maxSize) maxSize = _bids[i].Volume;
            if (_asks  != null) for (int i = 0; i < _asks.Count;  i++) if (_asks[i].Volume  > maxSize) maxSize = _asks[i].Volume;

            // Live-node price keys — skip book levels that coincide with a live wall node (prevents double bar + double size label).
            var liveNodeKeys = new HashSet<long>();
            if (_nodes != null)
                for (int i = 0; i < _nodes.Count; i++)
                    if (_nodes[i].InWindow)
                        liveNodeKeys.Add((long)Math.Round(_nodes[i].Price / _tick));

            // ---- faint book ladder (drawn first; wall nodes overlay on top) ----
            if (_bids != null)
                for (int i = 0; i < _bids.Count; i++)
                {
                    if (liveNodeKeys.Contains((long)Math.Round(_bids[i].Price / _tick))) continue;
                    double y = Y(_bids[i].Price);
                    if (y < 0 || y > h) continue;
                    double barW   = Math.Max(2.0, (_bids[i].Volume / (double)maxSize) * barMaxW);
                    double rowTop = y - rowH * 0.35, rowHt = rowH * 0.70;
                    dc.DrawRoundedRectangle(BidBook, null, new Rect(barX, rowTop, barW, rowHt), 3, 3);
                    DrawText(dc, _bids[i].Price.ToString("0.00", CultureInfo.InvariantCulture), 4, y, 14, Mono, PriceTxt, dpi, 1.0);
                    DrawText(dc, _bids[i].Volume.ToString(), barX + barW + 6, y, 13, Mono, BidText, dpi, 1.0);
                }
            if (_asks != null)
                for (int i = 0; i < _asks.Count; i++)
                {
                    if (liveNodeKeys.Contains((long)Math.Round(_asks[i].Price / _tick))) continue;
                    double y = Y(_asks[i].Price);
                    if (y < 0 || y > h) continue;
                    double barW   = Math.Max(2.0, (_asks[i].Volume / (double)maxSize) * barMaxW);
                    double rowTop = y - rowH * 0.35, rowHt = rowH * 0.70;
                    dc.DrawRoundedRectangle(AskBook, null, new Rect(barX, rowTop, barW, rowHt), 3, 3);
                    DrawText(dc, _asks[i].Price.ToString("0.00", CultureInfo.InvariantCulture), 4, y, 14, Mono, PriceTxt, dpi, 1.0);
                    DrawText(dc, _asks[i].Volume.ToString(), barX + barW + 6, y, 13, Mono, AskText, dpi, 1.0);
                }

            double lastBadgeY = double.NaN;
            if (_nodes != null)
            for (int i = 0; i < _nodes.Count; i++)
            {
                RadarNode n    = _nodes[i];
                if (!Visible(n)) continue;
                double    y    = Y(n.Price);
                if (y < 0 || y > h) continue;

                bool blind  = !n.InWindow || n.State == NodeState.Remembered;
                bool isBid  = n.Side == Side.Bid;
                Color base1 = isBid ? BidColor : AskColor;
                double op   = blind ? 0.34 : 1.0;
                double frac = Math.Min(1.0, n.LastKnownSize / (double)maxSize);
                double barW = Math.Max(2.0, frac * barMaxW);
                double rowTop = y - rowH * 0.35, rowHt = rowH * 0.70;

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

                // Price-gutter highlight band for live Wall/Absorbed rows.
                bool wallHighlight = !blind && (n.State == NodeState.Wall || n.State == NodeState.Absorbed);
                if (wallHighlight)
                {
                    var hi = new SolidColorBrush(Color.FromArgb(80, base1.R, base1.G, base1.B));
                    dc.DrawRoundedRectangle(hi, null, new Rect(0, rowTop, barX, rowHt), 3, 3);
                }

                // Price label (left, tabular); bright side color on highlighted rows.
                DrawText(dc, n.Price.ToString("0.00", CultureInfo.InvariantCulture),
                         4, y, 14, Mono, wallHighlight ? (isBid ? BidText : AskText) : PriceTxt, dpi, op);

                // Size label (right of bar).
                DrawText(dc, n.LastKnownSize.ToString(),
                         barX + barW + 6, y, 13, Mono, isBid ? BidText : AskText, dpi, op);

                // State badge / age tag (deduped: skip if too close to previous badge).
                string badge = BadgeFor(n.State);
                if (blind) badge = "· " + Math.Round(n.AgeSeconds) + "s";
                if (!string.IsNullOrEmpty(badge) && (double.IsNaN(lastBadgeY) || Math.Abs(y - lastBadgeY) >= 12))
                { DrawText(dc, badge, w - 70, y, 10, Sans, BadgeBrush(n.State, blind), dpi, op); lastBadgeY = y; }
            }

            // Inside-market amber line + mid chip — glide to Y(_mid) (anchored, not fixed center).
            double midY = Y(_mid);
            dc.DrawLine(AmberLine, new Point(0, midY), new Point(w, midY));
            var midFt = new FormattedText(_mid.ToString("0.00", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 16, AmberTxt, dpi);
            double chipW = midFt.Width + 10, chipH = midFt.Height + 4;
            double chipY = midY - chipH / 2.0;
            dc.DrawRoundedRectangle(MidChipBg, MidChipBorder, new Rect(2, chipY, chipW, chipH), 3, 3);
            dc.DrawText(midFt, new Point(7, midY - midFt.Height / 2.0));

            // Edge markers for Visible nodes whose row is off-screen (remembered walls outside the live band).
            if (_nodes != null)
            {
                var above = new List<RadarNode>();
                var below = new List<RadarNode>();
                for (int i = 0; i < _nodes.Count; i++)
                {
                    if (!Visible(_nodes[i])) continue;
                    double ny = Y(_nodes[i].Price);
                    if      (ny < 0) above.Add(_nodes[i]);
                    else if (ny > h) below.Add(_nodes[i]);
                }
                // Above: ascending price = index 0 is nearest to the top edge.
                above.Sort((a, b) => a.Price.CompareTo(b.Price));
                for (int i = 0; i < above.Count && i < 3; i++)
                {
                    RadarNode n = above[i];
                    string txt = "↑ " + n.Price.ToString("0.00", CultureInfo.InvariantCulture) +
                                 "  " + n.LastKnownSize + "  ·" + (int)Math.Round(n.AgeSeconds) + "s";
                    DrawText(dc, txt, 4, 10 + i * 14, 11, Mono, n.Side == Side.Bid ? BidText : AskText, dpi, 0.8);
                }
                if (above.Count > 3)
                    DrawText(dc, "+" + (above.Count - 3), 4, 10 + 3 * 14, 11, Sans, PriceTxt, dpi, 0.6);
                // Below: descending price = index 0 is nearest to the bottom edge.
                below.Sort((a, b) => b.Price.CompareTo(a.Price));
                for (int i = 0; i < below.Count && i < 3; i++)
                {
                    RadarNode n = below[i];
                    string txt = "↓ " + n.Price.ToString("0.00", CultureInfo.InvariantCulture) +
                                 "  " + n.LastKnownSize + "  ·" + (int)Math.Round(n.AgeSeconds) + "s";
                    DrawText(dc, txt, 4, h - 10 - i * 14, 11, Mono, n.Side == Side.Bid ? BidText : AskText, dpi, 0.8);
                }
                if (below.Count > 3)
                    DrawText(dc, "+" + (below.Count - 3), 4, h - 10 - 3 * 14, 11, Sans, PriceTxt, dpi, 0.6);
            }

        }

        private double RoundToTick(double p) { return Math.Round(p / _tick) * _tick; }

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

        private static bool Visible(RadarNode n)
        {
            return n.InWindow || n.Confidence >= 0.25;
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
