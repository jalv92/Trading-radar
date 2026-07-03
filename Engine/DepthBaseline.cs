using System;

namespace TradingRadar.Engine
{
    // Rolling percentile of observed live depth-level sizes. Spec §5 mandate (2026-07-01): the
    // Controller's SignificanceBand must be "a percentile of recent depth (measured), never a fixed
    // contract count" — the hardcoded 60 was a divergence. ADR 2026-07-03 (Phase 0) closes it with
    // this estimator: the adaptive band is max(P85, compiled floor), so it can only RAISE the arm
    // bar. A percentile of a real distribution is bounded above by the max observed size, so
    // "never arm" is not in the reachable output set — the anti-collapse property the ADR requires.
    //
    // Pure: no clock — the caller controls sampling cadence (RadarTab feeds one batch per second of
    // replay time). The percentile is recomputed once per batch (copy + sort of <= Capacity longs),
    // never in the per-tick hot path.
    public class DepthBaseline
    {
        // Warm-up: below this many samples the baseline abstains (P85 = 0) and the compiled floor
        // rules alone. ~300 samples ≈ 15 s of book at 20 levels/s — enough to stop a 3-row fluke
        // from setting the bar, short enough to be live within the first replay minute.
        public const int MinSamples = 300;

        private readonly long[] _ring;
        private int _next, _count;
        private long _p85;   // cached at EndBatch; 0 while warming up

        public DepthBaseline(int capacity)
        {
            if (capacity < MinSamples) throw new ArgumentOutOfRangeException("capacity");
            _ring = new long[capacity];
        }

        public long P85 { get { return _p85; } }
        public int SampleCount { get { return _count; } }

        public void Add(long size)
        {
            if (size <= 0) return;   // empty levels carry no depth information
            _ring[_next] = size;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }

        // Call once per sampling batch — recomputes the cached percentile.
        public void EndBatch()
        {
            if (_count < MinSamples) { _p85 = 0; return; }
            long[] copy = new long[_count];
            Array.Copy(_ring, copy, _count);
            Array.Sort(copy);
            _p85 = copy[(int)Math.Round(0.85 * (_count - 1))];
        }

        // Instrument switch / replay rewind: a new size distribution must not inherit the old one's bar.
        public void Reset() { _next = 0; _count = 0; _p85 = 0; }
    }
}
