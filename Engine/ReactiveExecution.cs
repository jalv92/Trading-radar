namespace TradingRadar.Engine
{
    // Pure routing decision for a fire event: which side, MKT-vs-LMT, and whether it is auto-aggressive
    // (may fire even with the AUTO checkbox unchecked). No NinjaTrader deps => unit-testable via
    // `dotnet test` (the WPF RadarChartTrader wiring that CONSUMES it is not). Spec §3 (direction table)
    // + §8 (entry mechanics). Reads the real contract fields FireEvent.Kind (SetupKind) + FireEvent.React
    // (ReactKind) — see Engine/Primitives.cs / ControllerStateMachine.cs.
    public struct FireRouting
    {
        public bool IsBuy;
        public bool Marketable;     // true => MKT entry (chase the printed move); false => wall-anchored LMT
        public bool AutoAggressive; // true => eligible to auto-submit even if the AUTO box is unchecked
    }

    public static class ReactiveExecution
    {
        public static FireRouting Route(FireEvent f)
        {
            FireRouting r = default(FireRouting);
            if (f.Kind == SetupKind.Reactive)
            {
                if (f.React == ReactKind.Reject)
                {
                    // Wall holds -> fade. FireEvent.Side is the TRADE side (ReactiveController already
                    // resolved Ask-wall-above -> SELL/Bid, Bid-wall-below -> BUY/Ask). Marketable: the
                    // rejection already printed, so chase it — no resting limit to pre-stage. §8.
                    r.IsBuy = f.Side == Side.Ask;
                    r.Marketable = true;
                }
                else
                {
                    // Wall consumed -> follow. Wall-anchored LMT, identical mechanics to the Break setup's
                    // pre-staged limit; ReactiveController emits the trade side directly. §8.
                    r.IsBuy = f.Side == Side.Ask;
                    r.Marketable = false;
                }
                r.AutoAggressive = true;   // §8 auto-aggressive (all hard gates still apply in TryAutoFire)
            }
            else
            {
                // Break setup (frozen): unchanged routing — isBuy = Side==Ask, wall-anchored LMT, and it
                // honors the AUTO checkbox (not auto-aggressive). default(FireEvent) lands here too.
                r.IsBuy = f.Side == Side.Ask;
                r.Marketable = false;
                r.AutoAggressive = false;
            }
            return r;
        }
    }
}
