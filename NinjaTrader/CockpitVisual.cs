using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TradingRadar.Engine;

namespace TradingRadar.NT
{
    // Render-only Cockpit: the Consumption-Break Controller state + tape-speed reads (Aurora).
    // Reads a ControllerOutput + tape/context scalars off the Frame. No trading logic.
    // Supersedes the old flipping 5-condition PressureModel panel (spec 2026-07-01 §7): BookSkewContext
    // survives only as the demoted, vote-less "book-skew" strip at the bottom (item 6).
    public class CockpitVisual : FrameworkElement
    {
        static readonly Brush PanelBg = B(Color.FromRgb(0x0d, 0x12, 0x1c));
        static readonly Brush CardBg  = B(Color.FromArgb(10, 0xff, 0xff, 0xff));
        static readonly Pen   CardLn  = P(Color.FromArgb(18, 0xff, 0xff, 0xff), 1);
        static readonly Brush Muted   = B(Color.FromRgb(0x8a, 0x93, 0xa3));
        static readonly Brush Muted2  = B(Color.FromRgb(0x6b, 0x72, 0x80));
        static readonly Color Bid     = Color.FromRgb(0x34, 0xd3, 0x99);
        static readonly Color Ask     = Color.FromRgb(0xfb, 0x71, 0x85);
        static readonly Brush BidTxt  = B(Color.FromRgb(0x6e, 0xe7, 0xb7));
        static readonly Brush AskTxt  = B(Color.FromRgb(0xfd, 0xa4, 0xaf));
        static readonly Color SlateC  = Color.FromRgb(0x94, 0xa3, 0xb8);
        static readonly Brush Slate   = B(SlateC);
        static readonly Color AmberC  = Color.FromRgb(0xff, 0xce, 0x5c);   // same hex as RadarVisual's amber (inside-market accent)
        static readonly Brush AmberTxt= B(Color.FromRgb(0xff, 0xe0, 0x8a));
        static readonly Brush Track   = B(Color.FromArgb(16, 0xff, 0xff, 0xff));
        static readonly Brush Divider = B(Color.FromArgb(60, 0xff, 0xff, 0xff));
        static readonly Typeface Sans = new Typeface("Segoe UI");
        // Mono tabular numbers (Aurora identity) — same Consolas construction as RadarVisual's price/volume column.
        static readonly Typeface Mono = new Typeface(new FontFamily("Consolas"),
                                             FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Mirrors the live Controller's FireFrac default — RadarTab owns the real ControllerConfig
        // instance fed into ControllerStateMachine; this reads the same class's default rather than
        // hardcoding "0.7" here, so the countdown's fire-threshold marker can't silently drift from it.
        // TODO: if RadarTab ever parameterizes ControllerConfig (e.g. a UI knob), pass the live value
        // into SetFrame instead of mirroring the class default here.
        private static readonly double FireFracDefault = new ControllerConfig().FireFrac;
        // ponytail: fixed display-scale cap for the signed velocity bar (not an engine threshold, purely
        // how much of the bar 1 contract/sec occupies) — deterministic per frame, no rolling state.
        // Retune once Rec shows typical buy/sell-per-sec magnitudes.
        private const double VelMaxPerSec = 40.0;
        private const double ZGaugeMax = 3.0;   // tape z-score gauge range per spec §6 ("~0..3+, clamp display")
        // ponytail: display-scale cap for the signed accel bar (net contracts/s²), not an engine threshold
        // — deterministic per frame, retune from Rec like VelMaxPerSec once typical magnitudes are known.
        private const double AccelMaxPerSec2 = 20.0;
        private const double Gap = 10;

        private ControllerOutput _ctrl;
        private double _buyPerSec, _sellPerSec, _tapeZ, _bookSkew, _tapeAccel;
        private SetupKind _setup;
        private ReactBanner _react;
        private bool _has;

        public CockpitVisual() { ClipToBounds = true; }

        // tapeAccel = signed net-aggressor acceleration (spec §5); setup = active dropdown selection
        // (spec §9); react = the ReactiveController's banner projection — RadarTab collapses
        // Watching / Fired+ReactKind / idle -> ReactBanner. Break-setup frames pass react = ReactBanner.Waiting.
        public void SetFrame(ControllerOutput ctrl, double buyPerSec, double sellPerSec, double tapeZ, double bookSkew,
                             double tapeAccel, SetupKind setup, ReactBanner react)
        {
            _ctrl = ctrl; _buyPerSec = buyPerSec; _sellPerSec = sellPerSec; _tapeZ = tapeZ; _bookSkew = bookSkew;
            _tapeAccel = tapeAccel; _setup = setup; _react = react;
            _has = true; InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.DrawRectangle(PanelBg, null, new Rect(0, 0, w, h));
            if (!_has) return;

            double pad = 14, x = pad, y = pad, cw = w - 2 * pad;
            y = DrawBannerCard(dc, x, y, cw, h, pad, dpi);
            y = DrawCountdownCard(dc, x, y, cw, h, pad, dpi);
            y = DrawVelocityCard(dc, x, y, cw, h, pad, dpi);
            y = DrawTapeZCard(dc, x, y, cw, h, pad, dpi);
            y = DrawTapeAccelCard(dc, x, y, cw, h, pad, dpi);
            y = DrawChopCard(dc, x, y, cw, h, pad, dpi);
            DrawBookSkewStrip(dc, x, y, cw, h, pad, dpi);
        }

        // ---- 1. Controller state banner ----
        // BannerKind now lives in the engine (TradingRadar.Engine.CockpitBanner) so its text/sub mapping
        // is unit-testable; the struct keeps only the draw-side state (Kind selects the strings + color + glow).
        private struct BannerState { public BannerKind Kind; public Color Color; public double Glow; }

        // Which side (if any) is "live" this frame — shared by the banner (state label) AND the
        // countdown card (which side's Fraction to paint), so they can never disagree: a side latched
        // Fired always wins (Fraction = its frozen final value); else whichever side is Armed/Countdown
        // (Countdown outranks Armed, ties break toward the higher Fraction). Cooldown/Waiting read as
        // idle — a reload-vetoed side deliberately keeps its frozen Fraction in the engine (see
        // ControllerStateMachine), and must never paint as live progress once it's no longer developing.
        private struct ActiveSide { public bool Any; public bool IsLong; public double Fraction; }

        private static ActiveSide ResolveActiveSide(ControllerOutput c)
        {
            if (c.Long == SideState.Fired || c.Short == SideState.Fired)
            {
                bool isLong = c.Fire.Side == Side.Ask;
                return new ActiveSide { Any = true, IsLong = isLong, Fraction = isLong ? c.LongFraction : c.ShortFraction };
            }
            int longRank = Rank(c.Long), shortRank = Rank(c.Short);
            if (longRank > 0 || shortRank > 0)
            {
                bool pickLong = longRank != shortRank ? longRank > shortRank : c.LongFraction >= c.ShortFraction;
                return new ActiveSide { Any = true, IsLong = pickLong, Fraction = pickLong ? c.LongFraction : c.ShortFraction };
            }
            return new ActiveSide { Any = false };
        }

        private static int Rank(SideState s)
        {
            switch (s) { case SideState.Countdown: return 2; case SideState.Armed: return 1; default: return 0; }
        }

        // Precedence (documented once, here):
        //  1. A latched Fired side always wins the banner ("SETUP LONG"/"SETUP SHORT", full glow). The
        //     side comes from Ctrl.Fire.Side — ControllerStateMachine.Update() already recency-tiebreaks
        //     Fire when BOTH sides are latched Fired simultaneously, so trusting it here can never show
        //     the stale side.
        //  2. Otherwise, whichever side is developing (Armed/Countdown) drives the banner, side-colored
        //     at reduced glow. Countdown outranks Armed; a same-rank tie breaks toward the side that has
        //     eaten more of its wall (higher Fraction). Cooldown reads as idle here (a timed veto, not a
        //     live setup) — same as Waiting — so a vetoed candidate never flashes a stale "SETUP" label.
        //  3. If neither side is developing and the tape reads CHOP, show "CHOP" — a display-only
        //     awareness light (Javier's 2026-07-01 decision: it does not itself gate the fire path;
        //     ZFloor already does that in the engine).
        //  4. Otherwise "WAITING".
        private static BannerState ComputeBanner(ControllerOutput c)
        {
            ActiveSide a = ResolveActiveSide(c);
            if (!a.Any)
            {
                if (c.Chop) return new BannerState { Kind = BannerKind.Chop, Color = SlateC, Glow = 0.35 };
                return new BannerState { Kind = BannerKind.Waiting, Color = SlateC, Glow = 0.15 };
            }
            Color col = a.IsLong ? Bid : Ask;
            if (c.Long == SideState.Fired || c.Short == SideState.Fired)
                return new BannerState {
                    Kind = a.IsLong ? BannerKind.SetupLong : BannerKind.SetupShort, Color = col, Glow = 1.0 };
            SideState st = a.IsLong ? c.Long : c.Short;
            return new BannerState {
                Kind = st == SideState.Countdown ? BannerKind.Countdown : BannerKind.Armed, Color = col, Glow = 0.45 };
        }

        private double DrawBannerCard(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double bh = 72;
            if (y + bh > h - pad) return y;

            Color col; double glow; string text, sub;
            if (_setup == SetupKind.Reactive)
            {
                CockpitBanner.ReactLabel(_react, out text, out sub);
                // ponytail: draw-only color/glow (not spec-mandated). REJECT vs BREAK is distinguished by
                // TEXT, not color — the fade/follow direction is ambiguous from the banner alone (it
                // depends on which side the wall is on, spec §3). A colored anchor is a later Replay call.
                switch (_react)
                {
                    case ReactBanner.Watching:    col = AmberC; glow = 0.5; break;
                    case ReactBanner.FiredReject:
                    case ReactBanner.FiredBreak:  col = AmberC; glow = 1.0; break;
                    default:                      col = SlateC; glow = 0.15; break;   // ReactBanner.Waiting
                }
            }
            else
            {
                BannerState bs = ComputeBanner(_ctrl);
                col = bs.Color; glow = bs.Glow;
                CockpitBanner.BreakLabel(bs.Kind, out text, out sub);
            }

            Brush cardBg = new SolidColorBrush(Color.FromArgb((byte)(22 + 70 * glow), col.R, col.G, col.B));
            dc.DrawRoundedRectangle(cardBg, CardLn, new Rect(x, y, cw, bh), 8, 8);
            Left(dc, "CONTROLLER", x + 12, y + 10, 11, Muted, dpi);
            Brush txtBr = new SolidColorBrush(Color.FromArgb((byte)(150 + 105 * glow), col.R, col.G, col.B));
            dc.DrawText(FT(text, 26, txtBr, dpi), new Point(x + 14, y + 28));
            Left(dc, sub, x + 14, y + 56, 10, Muted2, dpi);
            return y + bh + Gap;
        }

        // ---- 4b. Tape acceleration — signed derivative of the net aggressor rate (spec §5). Renders
        //          every frame regardless of setup (spec §10). Positive = buyers accelerating (green,
        //          right of center), negative = sellers accelerating (coral, left). Mirrors the small
        //          TAPE Z-SCORE card but signed/bipolar like the velocity bar. ----
        private double DrawTapeAccelCard(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double ch = 52;
            if (y + ch > h - pad) return y;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, ch), 8, 8);
            Left(dc, "TAPE ACCEL", x + 12, y + 10, 11, Muted, dpi);
            double a = _tapeAccel;
            bool up = a >= 0;
            string txt = (up ? "+" : "-") + Math.Abs(a).ToString("0.0", CultureInfo.InvariantCulture) + "/s²";
            RightM(dc, txt, x + cw - 12, y + 6, 15, up ? BidTxt : AskTxt, dpi);
            double bx = x + 12, by = y + 32, bw = cw - 24, bh2 = 10, cx = bx + bw / 2.0, half = bw / 2.0;
            dc.DrawRoundedRectangle(Track, CardLn, new Rect(bx, by, bw, bh2), 5, 5);
            double mag = half * Clamp01(Math.Abs(a) / AccelMaxPerSec2);
            if (mag > 1)
            {
                Color tint = up ? Bid : Ask;
                double segX = up ? cx : cx - mag;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, tint.R, tint.G, tint.B)), null, new Rect(segX, by, mag, bh2), 5, 5);
            }
            dc.DrawRectangle(Divider, null, new Rect(cx - 0.5, by - 2, 1, bh2 + 4));
            return y + ch + Gap;
        }

        // ---- 2. Consumption countdown ----
        private double DrawCountdownCard(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double ch = 62;
            if (y + ch > h - pad) return y;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, ch), 8, 8);
            Left(dc, "WALL CONSUMPTION", x + 12, y + 10, 11, Muted, dpi);
            // Same rank-based side resolution as the banner (ResolveActiveSide) — an idle side (Waiting
            // or a reload-vetoed Cooldown, which deliberately keeps its frozen Fraction in the engine)
            // paints as 0, never a stale bar the banner itself reads as inactive.
            ActiveSide a = ResolveActiveSide(_ctrl);
            double frac = a.Any ? Clamp01(a.Fraction) : 0.0;
            Color fillColor = a.Any ? (a.IsLong ? Bid : Ask) : SlateC;
            int pct = (int)Math.Round(frac * 100);
            RightM(dc, pct + "% eaten", x + cw - 12, y + 6, 16, a.Any ? new SolidColorBrush(fillColor) : Slate, dpi);
            double bx = x + 12, by = y + 34, bw = cw - 24, bh2 = 14;
            dc.DrawRoundedRectangle(Track, CardLn, new Rect(bx, by, bw, bh2), 7, 7);
            double fillW = bw * frac;
            if (fillW > 1) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, fillColor.R, fillColor.G, fillColor.B)), null, new Rect(bx, by, fillW, bh2), 6, 6);
            // Fire-threshold marker — mirrors ControllerConfig.FireFrac (see FireFracDefault), never a bare literal.
            double markX = bx + bw * Clamp01(FireFracDefault);
            dc.DrawRectangle(Divider, null, new Rect(markX - 0.5, by - 2, 1, bh2 + 4));
            return y + ch + Gap;
        }

        // ---- 3. Signed velocity bar ----
        private double DrawVelocityCard(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double ch = 76;
            if (y + ch > h - pad) return y;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, ch), 8, 8);
            Left(dc, "TAPE SPEED", x + 12, y + 10, 11, Muted, dpi);
            double buy = Math.Max(0, _buyPerSec), sell = Math.Max(0, _sellPerSec);
            LeftM(dc, "-" + sell.ToString("0", CultureInfo.InvariantCulture) + "/s", x + 12, y + 24, 13, AskTxt, dpi);
            RightM(dc, "+" + buy.ToString("0", CultureInfo.InvariantCulture) + "/s", x + cw - 12, y + 24, 13, BidTxt, dpi);
            double bx = x + 12, by = y + 46, bw = cw - 24, bh2 = 14, cx = bx + bw / 2.0, half = bw / 2.0;
            dc.DrawRoundedRectangle(Track, CardLn, new Rect(bx, by, bw, bh2), 7, 7);
            double buyW = half * Clamp01(buy / VelMaxPerSec);
            double sellW = half * Clamp01(sell / VelMaxPerSec);
            if (buyW > 1) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, Bid.R, Bid.G, Bid.B)), null, new Rect(cx, by, buyW, bh2), 6, 6);
            if (sellW > 1) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, Ask.R, Ask.G, Ask.B)), null, new Rect(cx - sellW, by, sellW, bh2), 6, 6);
            dc.DrawRectangle(Divider, null, new Rect(cx - 0.5, by - 2, 1, bh2 + 4));
            return y + ch + Gap;
        }

        // ---- 4. Tape-speed z-score gauge ----
        private double DrawTapeZCard(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double ch = 52;
            if (y + ch > h - pad) return y;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, ch), 8, 8);
            Left(dc, "TAPE Z-SCORE", x + 12, y + 10, 11, Muted, dpi);
            double z = _tapeZ;
            RightM(dc, "z " + z.ToString("0.0", CultureInfo.InvariantCulture), x + cw - 12, y + 6, 15, AmberTxt, dpi);
            double bx = x + 12, by = y + 32, bw = cw - 24, bh2 = 10;
            dc.DrawRoundedRectangle(Track, CardLn, new Rect(bx, by, bw, bh2), 5, 5);
            double frac = Clamp01(Clamp(z, 0, ZGaugeMax) / ZGaugeMax);
            if (frac > 0.02) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(90 + 110 * frac), AmberC.R, AmberC.G, AmberC.B)), null, new Rect(bx, by, bw * frac, bh2), 5, 5);
            return y + ch + Gap;
        }

        // ---- 5. CHOP light (display-only awareness, per Javier's 2026-07-01 decision) ----
        private double DrawChopCard(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double ch = 32;
            if (y + ch > h - pad) return y;
            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(x, y, cw, ch), 8, 8);
            bool lit = _ctrl.Chop;
            dc.DrawEllipse(lit ? new SolidColorBrush(AmberC) : B(Color.FromArgb(40, 0x94, 0xa3, 0xb8)), null, new Point(x + 22, y + ch / 2), 6, 6);
            Left(dc, lit ? "CHOP · slow, alternating tape" : "CHOP", x + 38, y + (ch - 14) / 2, 12, lit ? AmberTxt : Muted2, dpi);
            Right(dc, "display-only", x + cw - 12, y + (ch - 11) / 2, 9.5, Muted2, dpi);
            return y + ch + Gap;
        }

        // ---- 6. Book-skew context strip — thin, low-contrast, reference only. Never fires anything. ----
        private void DrawBookSkewStrip(DrawingContext dc, double x, double y, double cw, double h, double pad, double dpi)
        {
            double sh = 24;
            if (y + sh > h - pad) return;
            Left(dc, "BOOK SKEW · context only, never fires", x, y, 9.5, Muted2, dpi);
            double by = y + 13, bw = cw, bh2 = 5, half = bw / 2.0;
            dc.DrawRoundedRectangle(Track, null, new Rect(x, by, bw, bh2), 3, 3);
            double v = Clamp(_bookSkew, -1, 1);
            Color tint = v >= 0 ? Bid : Ask;
            double segW = Math.Abs(v) * half;
            if (segW > 1)
            {
                double segX = v >= 0 ? x + half : x + half - segW;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(55, tint.R, tint.G, tint.B)), null, new Rect(segX, by, segW, bh2), 3, 3);
            }
            dc.DrawRectangle(Divider, null, new Rect(x + half - 0.5, by - 2, 1, bh2 + 4));
        }

        // ---- numeric helpers ----
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static double Clamp01(double v) { return Clamp(v, 0, 1); }

        // ---- text + brush helpers ----
        private FormattedText FT(string s, double size, Brush b, double dpi)
        { return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Sans, size, b, dpi); }
        // Mono tabular numbers (Aurora identity) — used for the numeric readouts only; labels stay Sans.
        private FormattedText FTM(string s, double size, Brush b, double dpi)
        { return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, b, dpi); }
        private void Left(DrawingContext dc, string s, double x, double yTop, double size, Brush b, double dpi)
        { dc.DrawText(FT(s, size, b, dpi), new Point(x, yTop)); }
        private void Right(DrawingContext dc, string s, double xRight, double yTop, double size, Brush b, double dpi)
        { var ft = FT(s, size, b, dpi); dc.DrawText(ft, new Point(xRight - ft.Width, yTop)); }
        private void LeftM(DrawingContext dc, string s, double x, double yTop, double size, Brush b, double dpi)
        { dc.DrawText(FTM(s, size, b, dpi), new Point(x, yTop)); }
        private void RightM(DrawingContext dc, string s, double xRight, double yTop, double size, Brush b, double dpi)
        { var ft = FTM(s, size, b, dpi); dc.DrawText(ft, new Point(xRight - ft.Width, yTop)); }
        private static Brush B(Color c) { var br = new SolidColorBrush(c); br.Freeze(); return br; }
        private static Pen P(Color c, double t) { var p = new Pen(B(c), t); p.Freeze(); return p; }
    }
}
