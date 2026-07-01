using System;

namespace TradingRadar.Engine
{
    public struct ConsumptionRead
    {
        public double Fraction;              // 1 - current/peak, clamped [0,1]
        public long   Drop;                  // max(0, peak - current)
        public long   Traded;                // consuming-aggressor volume at the wall since armTime
        public double TradeBackedFraction;   // Drop>0 ? min(1, Traded/Drop) : 0
    }

    // Pure read: how far a wall has been eaten, and how much of that is explained by trades
    // (vs cancellation/pull). No state, no clock.
    public static class ConsumptionTracker
    {
        public static ConsumptionRead Read(Side wallSide, double wallPrice, long peak, long current, DateTime armTime, BookMirror book)
        {
            long drop = Math.Max(0, peak - current);
            double frac = peak > 0 ? 1.0 - (double)current / peak : 0.0;
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
            Side consuming = wallSide == Side.Ask ? Side.Ask : Side.Bid; // ask wall consumed by buys; bid wall by sells
            long traded = book.TradedAt(wallPrice, armTime, consuming);
            double tbf = drop > 0 ? Math.Min(1.0, (double)traded / drop) : 0.0;
            return new ConsumptionRead { Fraction = frac, Drop = drop, Traded = traded, TradeBackedFraction = tbf };
        }
    }
}
