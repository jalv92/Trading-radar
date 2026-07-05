using System;

namespace TradingRadar.Engine
{
    // Signed derivative d(netRate)/dt of the aggressor net rate (BuyVol - SellVol per second,
    // from BookMirror.WindowSince), EWMA-smoothed to reject single-frame noise. Pure: time via
    // timestamps only. Mirrors TapeSpeed's EWMA-with-warmup shape + MinSamples Ready gate (spec §5).
    // Sign: positive = buyers accelerating (arms a wall ABOVE); negative = sellers accelerating (arms BELOW).
    public class TapeAcceleration
    {
        private readonly double _alpha;     // EWMA weight for the newest derivative
        private const int MinSamples = 20;  // total samples before Ready (mirrors TapeSpeed)
        private double _accel;              // EWMA of the frame-to-frame d(netRate)/dt
        private double _prevRate;
        private DateTime _prevTime;
        private int _n;

        public TapeAcceleration(double alpha) { _alpha = alpha <= 0 || alpha >= 1 ? 0.1 : alpha; }

        public bool Ready { get { return _n >= MinSamples; } }

        // Gated to 0 until warmed up, exactly like TapeSpeed.ZScore — the EWMA still smooths
        // internally through the warmup so the first Ready read is already converged.
        public double Acceleration { get { return _n >= MinSamples ? _accel : 0.0; } }

        public void Sample(double netRate, DateTime now)
        {
            if (double.IsNaN(netRate) || double.IsInfinity(netRate)) return; // same poison guard as TapeSpeed
            if (_n == 0) { _prevRate = netRate; _prevTime = now; _n = 1; return; } // first sample: baseline only, no derivative yet
            double dt = (now - _prevTime).TotalSeconds;
            if (dt <= 0) return; // drop non-forward samples: no divide-by-zero / negative-dt derivative
            _n++;                // count only samples that actually produce a derivative (after the baseline)
            double deriv = (netRate - _prevRate) / dt;
            _accel = _alpha * deriv + (1 - _alpha) * _accel;
            _prevRate = netRate; _prevTime = now;
        }
    }
}
