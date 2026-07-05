using TradingRadar.Engine;
using Xunit;

// Cluster F: the cockpit banner text/sub mapping is pure (no WPF/DrawingContext), lifted into the
// engine so it is reachable from this test project (which references only the engine — CockpitVisual is
// never compiled by dotnet test). Proves every Break and Reactive banner state maps to the exact string
// the render layer draws (spec §10). One multi-assert [Fact] (house style — no [Theory]).
public class CockpitBannerTests
{
    [Fact]
    public void Break_and_React_banner_states_map_to_their_exact_strings()
    {
        // Break — verbatim the shipped cockpit strings (extraction must change no user-visible Break text)
        AssertBreak(BannerKind.SetupLong,  "SETUP LONG",  "● LATCHED · resets on break cross / fail");
        AssertBreak(BannerKind.SetupShort, "SETUP SHORT", "● LATCHED · resets on break cross / fail");
        AssertBreak(BannerKind.Countdown,  "COUNTDOWN",   "wall being eaten by trades");
        AssertBreak(BannerKind.Armed,      "ARMED",       "wall intact — waiting for erosion");
        AssertBreak(BannerKind.Chop,       "CHOP",        "slow, alternating tape");
        AssertBreak(BannerKind.Waiting,    "WAITING",     "no dominant wall armed");

        // Reactive — spec §10's mandated labels
        AssertReact(ReactBanner.Watching,    "WATCHING WALL", "waiting for resolution");
        AssertReact(ReactBanner.FiredReject, "REJECT · FADE",  "● LATCHED · wall held — fading");
        AssertReact(ReactBanner.FiredBreak,  "BREAK · FOLLOW", "● LATCHED · wall consumed — following");
        AssertReact(ReactBanner.Waiting,     "WAITING",        "no wall watched");
    }

    static void AssertBreak(BannerKind kind, string text, string sub)
    {
        CockpitBanner.BreakLabel(kind, out var t, out var s);
        Assert.Equal(text, t);
        Assert.Equal(sub, s);
    }

    static void AssertReact(ReactBanner state, string text, string sub)
    {
        CockpitBanner.ReactLabel(state, out var t, out var s);
        Assert.Equal(text, t);
        Assert.Equal(sub, s);
    }
}
