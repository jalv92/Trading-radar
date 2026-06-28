using System;

namespace TradingRadar.Engine
{
    // Every tunable from spec §6.6. NQ defaults. No literal threshold lives in logic.
    public class RadarConfig
    {
        public double TickSize = 0.25;          // NQ

        // Wall detection
        public double K_mult = 4.0;             // size multiple over baseline
        public long MinAbsSize = 40;            // absolute contract floor
        public TimeSpan BaselineWindow = TimeSpan.FromSeconds(30);
        public TimeSpan T_persist = TimeSpan.FromMilliseconds(1500);
        public double F_flicker = 6.0;          // max Add/Remove osc per second before reject

        // Episode / classification
        public int D_approach = 1;              // ticks to open an episode
        public TimeSpan T_episode = TimeSpan.FromMilliseconds(3000);
        // W_assoc: trade attribution window. Currently reserved — TradedAt sums over the full
        // episode lifetime; W_assoc will be wired + calibrated during Market Replay testing via
        // a debug data-capture path (TODO: build that capture path when testing begins; spec §6.3).
        public TimeSpan W_assoc = TimeSpan.FromMilliseconds(250);
        public double A_absorb = 1.0;           // Traded@P / S0 to call absorption
        public double RefillRatioTrigger = 3.0; // iceberg refill threshold
        public int D_pull = 1;                  // ticks quote is away when size vanishes => pull

        // Confidence / memory
        public TimeSpan H = TimeSpan.FromSeconds(30);   // confidence half-life while blind
        public double dC_confirm = 0.15;
        public double dC_grow = 0.20;
        public double dC_absorb = 0.25;
        public double ShrinkFactor = 0.6;
        public double PullPenalty = 0.2;
        public int P_max = 2;                   // pulls before node dead
        public double G_grow = 0.25;            // fractional increase to count as GREW
        public double C_floor = 0.05;
        public TimeSpan T_evict = TimeSpan.FromSeconds(300);

        // Visible band
        public int MemoryBandTicks = 25;

        // VolGovernor regime multiplier applied to time windows (×0.3..1.0); 1.0 = calm.
        public double VolGovernor = 1.0;
    }
}
