using System;

namespace TradingRadar.Engine
{
    // Stateful EWMA baseline of a sampled print rate -> z-score. Pure: time via timestamps only.
    public class TapeSpeed
    {
        private readonly double _alpha;     // EWMA weight for the newest sample
        private const int MinSamples = 20;  // before Ready
        private double _mean;
        private double _var;
        private int _n;

        public TapeSpeed(double alpha) { _alpha = alpha <= 0 || alpha > 1 ? 0.1 : alpha; }

        public bool Ready { get { return _n >= MinSamples; } }
        public double ZScore { get; private set; }

        public void Sample(double rate, DateTime now)
        {
            if (_n == 0) { _mean = rate; _var = 0; _n = 1; ZScore = 0; return; }
            double prevMean = _mean;
            _mean = _alpha * rate + (1 - _alpha) * _mean;
            // EWMA variance (West/Welford-style incremental for exponential weighting).
            _var = (1 - _alpha) * (_var + _alpha * (rate - prevMean) * (rate - prevMean));
            _n++;
            double std = Math.Sqrt(_var);
            ZScore = _n >= MinSamples && std > 1e-9 ? (rate - _mean) / std : 0.0;
        }
    }
}
