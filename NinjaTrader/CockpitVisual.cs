using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    // Render-only Cockpit: directional pressure gauge + 5-signal conditions panel (Aurora).
    // Reads a PressureResult (+ PressureInputs for reason text). No trading logic.
    public class CockpitVisual : FrameworkElement
    {
        static readonly Brush PanelBg = B(Color.FromRgb(0x0d, 0x12, 0x1c));
        static readonly Brush CardBg  = B(Color.FromArgb(10, 0xff, 0xff, 0xff));
        static readonly Pen   CardLn  = P(Color.FromArgb(18, 0xff, 0xff, 0xff), 1);
        static readonly Brush Muted   = B(Color.FromRgb(0x8a, 0x93, 0xa3));
        static readonly Brush Muted2  = B(Color.FromRgb(0x6b, 0x72, 0x80));
        static readonly Brush Txt     = B(Color.FromRgb(0xdd, 0xe3, 0xec));
        static readonly Color Bid     = Color.FromRgb(0x34, 0xd3, 0x99);
        static readonly Color Ask     = Color.FromRgb(0xfb, 0x71, 0x85);
        static readonly Brush BidTxt  = B(Color.FromRgb(0x6e, 0xe7, 0xb7));
        static readonly Brush AskTxt  = B(Color.FromRgb(0xfd, 0xa4, 0xaf));
        static readonly Brush Slate   = B(Color.FromRgb(0x94, 0xa3, 0xb8));
        static readonly Brush Track   = B(Color.FromArgb(16, 0xff, 0xff, 0xff));
        static readonly Brush White   = B(Colors.White);
        static readonly Brush Divider = B(Color.FromArgb(60, 0xff, 0xff, 0xff));
        static readonly Typeface Sans = new Typeface("Segoe UI");

        private PressureResult _res;
        private PressureInputs _in;
        private bool _has;

        public CockpitVisual() { ClipToBounds = true; }

        public void SetFrame(PressureInputs inp, PressureResult res)
        { _in = inp; _res = res; _has = true; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.DrawRectangle(PanelBg, null, new Rect(0, 0, w, h));
            if (!_has || _res.Signals == null) return;

            double pad = 14, x = pad, y = pad, cw = w - 2 * pad;
            double net = _res.Net;
            int pct = (int)Math.Round(net * 100);
            bool lng = net >= 0;
            Color fill = lng ? Bid : Ask;
            Brush netBr = Math.Abs(net) < 0.08 ? Slate : (lng ? BidTxt : AskTxt);

            // ---------- gauge card ----------
            double gh = 100;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, gh), 8, 8);
            Left(dc, "PRESIÓN DIRECCIONAL", x + 12, y + 16, 11, Muted, dpi);
            Right(dc, (pct > 0 ? "+" : "") + pct + "%", x + cw - 12, y + 12, 22, netBr, dpi);

            double bx = x + 12, by = y + 46, bw = cw - 24, bh = 16;
            dc.DrawRoundedRectangle(Track, CardLn, new Rect(bx, by, bw, bh), 8, 8);
            double half = Math.Abs(net) * (bw / 2.0);
            Brush fb = new SolidColorBrush(Color.FromArgb(150, fill.R, fill.G, fill.B));
            double fx = lng ? bx + bw / 2.0 : bx + bw / 2.0 - half;
            if (half > 0.5) dc.DrawRoundedRectangle(fb, null, new Rect(fx, by, half, bh), 6, 6);
            dc.DrawRectangle(Divider, null, new Rect(bx + bw / 2.0 - 0.5, by - 3, 1, bh + 6));
            double nx = bx + bw / 2.0 + net * (bw / 2.0);
            dc.DrawRoundedRectangle(White, null, new Rect(nx - 1.5, by - 4, 3, bh + 8), 2, 2);
            Left(dc, "◀ SHORT", bx, by + bh + 4, 9, Muted2, dpi);
            Right(dc, "LONG ▶", bx + bw, by + bh + 4, 9, Muted2, dpi);

            double dy = y + gh - 24;
            for (int i = 0; i < 5; i++)
            {
                Brush b = i < _res.Conviction ? new SolidColorBrush(fill) : B(Color.FromArgb(30, 0xff, 0xff, 0xff));
                dc.DrawEllipse(b, null, new Point(x + 18 + i * 14, dy + 6), 4.5, 4.5);
            }
            string pill = _res.Green ? (lng ? "▶ SEMÁFORO LONG" : "◀ SEMÁFORO SHORT") : "SIN TRIGGER";
            Brush pillTx = _res.Green ? (lng ? BidTxt : AskTxt) : Slate;
            Brush pillBg = _res.Green ? new SolidColorBrush(Color.FromArgb(40, fill.R, fill.G, fill.B))
                                      : B(Color.FromArgb(36, 0x94, 0xa3, 0xb8));
            var pf = FT(pill, 11, pillTx, dpi);
            double pw = pf.Width + 18, ph = pf.Height + 8, px = x + 100;
            dc.DrawRoundedRectangle(pillBg, null, new Rect(px, dy, pw, ph), 6, 6);
            dc.DrawText(pf, new Point(px + 9, dy + (ph - pf.Height) / 2));

            // ---------- conditions card ----------
            y += gh + 12;
            double ch = h - y - pad;
            if (ch < 40) return;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, ch), 8, 8);
            Left(dc, "CONDICIONES · EN VIVO", x + 12, y + 15, 11, Muted, dpi);

            var S = _res.Signals;
            double ry = y + 32, rh = 34;
            for (int i = 0; i < S.Length; i++)
            {
                if (ry + rh > y + ch) break;
                SignalRead s = S[i];
                int sign = s.Idle() ? 0 : (s.Lean > 0.12 ? 1 : (s.Lean < -0.12 ? -1 : 0));
                bool fire = s.Active && Math.Abs(s.Lean) > 0.3;
                Brush chipTx = sign > 0 ? BidTxt : (sign < 0 ? AskTxt : Slate);
                Brush chipBg = sign > 0 ? B(Color.FromArgb(36, 0x34, 0xd3, 0x99))
                              : sign < 0 ? B(Color.FromArgb(36, 0xfb, 0x71, 0x85))
                                         : B(Color.FromArgb(28, 0x94, 0xa3, 0xb8));
                string lean = sign > 0 ? "LONG" : (sign < 0 ? "SHORT" : "—");
                var cf = FT(lean, 9, chipTx, dpi);
                dc.DrawRoundedRectangle(chipBg, null, new Rect(x + 12, ry + 3, 44, 16), 4, 4);
                dc.DrawText(cf, new Point(x + 12 + (44 - cf.Width) / 2, ry + 3 + (16 - cf.Height) / 2));

                Left(dc, SignalName(s.Id), x + 64, ry + 7, 12.5, Txt, dpi);
                Left(dc, Reason(s.Id, _in), x + 64, ry + 21, 10.5, Muted2, dpi);

                double wbx = x + cw - 92, wbw = 60;
                dc.DrawRoundedRectangle(Track, null, new Rect(wbx, ry + 11, wbw, 5), 3, 3);
                double frac = s.Idle() ? 0.15 : Math.Min(1.0, Math.Abs(s.Lean));
                Brush wb = sign > 0 ? new SolidColorBrush(Bid) : (sign < 0 ? new SolidColorBrush(Ask) : Slate);
                dc.DrawRoundedRectangle(wb, null, new Rect(wbx, ry + 11, Math.Max(2.0, wbw * frac), 5), 3, 3);

                Left(dc, fire ? "✓" : "·", x + cw - 20, ry + 7, 13, fire ? Txt : Muted2, dpi);
                ry += rh;
            }
        }

        // ---- signal display metadata ----
        private static string SignalName(SignalId id)
        {
            switch (id)
            {
                case SignalId.Imbalance:  return "Imbalance de reposo";
                case SignalId.InsideThin: return "Inside fino";
                case SignalId.AirPocket:  return "Air-pocket";
                case SignalId.Delta:      return "Delta de agresor (15s)";
                default:                  return "Erosión del muro";
            }
        }
        private static string Reason(SignalId id, PressureInputs inp)
        {
            long b = 0, a = 0;
            if (inp.Bids != null) for (int i = 0; i < inp.Bids.Count; i++) b += inp.Bids[i].Volume;
            if (inp.Asks != null) for (int i = 0; i < inp.Asks.Count; i++) a += inp.Asks[i].Volume;
            switch (id)
            {
                case SignalId.Imbalance:  return b + "/" + a + (a > b ? "  oferta arriba" : "  demanda abajo");
                case SignalId.InsideThin: return "bid " + inp.BestBidSize + " vs ask " + inp.BestAskSize;
                case SignalId.AirPocket:  return "huecos cerca del precio";
                case SignalId.Delta:      return Math.Abs(inp.AggressorDelta) < 4 ? "plano, nadie golpea"
                                              : (inp.AggressorDelta > 0 ? ("+" + inp.AggressorDelta + " compra")
                                                                        : (inp.AggressorDelta + " venta"));
                default:                  return inp.Wall.Active
                                              ? ((int)Math.Round(inp.Wall.Frac * 100) + "% sin trades → " + (inp.Wall.Above ? "techo" : "soporte") + " falso")
                                              : "sin erosión activa";
            }
        }

        // ---- text + brush helpers ----
        private FormattedText FT(string s, double size, Brush b, double dpi)
        { return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Sans, size, b, dpi); }
        private void Left(DrawingContext dc, string s, double x, double yTop, double size, Brush b, double dpi)
        { dc.DrawText(FT(s, size, b, dpi), new Point(x, yTop)); }
        private void Right(DrawingContext dc, string s, double xRight, double yTop, double size, Brush b, double dpi)
        { var ft = FT(s, size, b, dpi); dc.DrawText(ft, new Point(xRight - ft.Width, yTop)); }
        private static Brush B(Color c) { var br = new SolidColorBrush(c); br.Freeze(); return br; }
        private static Pen P(Color c, double t) { var p = new Pen(B(c), t); p.Freeze(); return p; }
    }
}
