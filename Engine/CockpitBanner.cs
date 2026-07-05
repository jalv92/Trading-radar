namespace TradingRadar.Engine
{
    // Pure banner text/sub mapping for the CockpitVisual banner card, keyed by active setup + state.
    // Lives in the engine (not CockpitVisual) so it is unit-testable without a WPF/NT reference — the
    // test project references only the engine. CockpitVisual keeps the DrawingContext + color/glow; the
    // STRINGS live here, one source of truth, covered by CockpitBannerTests. Spec §10.
    // ponytail: kind selects the method, state is the arg (two thin mappers) instead of one int-tagged
    // method — type-safe, and no System.ValueTuple in the NT-facing layer (C# 7.3 / netstandard2.0).

    // Break setup (Consumption-Break controller) banner states. Promoted out of CockpitVisual (was a
    // private enum there) so the mapping is reachable from the test project.
    public enum BannerKind { SetupLong, SetupShort, Countdown, Armed, Chop, Waiting }

    // Reactive setup (React) banner projection: the ReactiveController's Waiting/Watching/Fired(+ReactKind)
    // collapsed by RadarTab to the 4 labels the banner shows (spec §10). Cooldown reads as Waiting.
    public enum ReactBanner { Waiting, Watching, FiredReject, FiredBreak }

    public static class CockpitBanner
    {
        // Break banner text/sub — the exact strings the shipped cockpit renders today, moved verbatim
        // out of ComputeBanner's inline text + DrawBannerCard's sub switch.
        public static void BreakLabel(BannerKind kind, out string text, out string sub)
        {
            switch (kind)
            {
                case BannerKind.SetupLong:  text = "SETUP LONG";  sub = "● LATCHED · resets on break cross / fail"; break;
                case BannerKind.SetupShort: text = "SETUP SHORT"; sub = "● LATCHED · resets on break cross / fail"; break;
                case BannerKind.Countdown:  text = "COUNTDOWN";   sub = "wall being eaten by trades"; break;
                case BannerKind.Armed:      text = "ARMED";       sub = "wall intact — waiting for erosion"; break;
                case BannerKind.Chop:       text = "CHOP";        sub = "slow, alternating tape"; break;
                default:                    text = "WAITING";     sub = "no dominant wall armed"; break;   // BannerKind.Waiting
            }
        }

        // Reactive banner text/sub (spec §10). Waiting is the default (also covers the controller's Cooldown).
        public static void ReactLabel(ReactBanner state, out string text, out string sub)
        {
            switch (state)
            {
                case ReactBanner.Watching:    text = "WATCHING WALL"; sub = "waiting for resolution"; break;
                case ReactBanner.FiredReject: text = "REJECT · FADE";  sub = "● LATCHED · wall held — fading"; break;
                case ReactBanner.FiredBreak:  text = "BREAK · FOLLOW"; sub = "● LATCHED · wall consumed — following"; break;
                default:                      text = "WAITING";        sub = "no wall watched"; break;   // ReactBanner.Waiting
            }
        }
    }
}
