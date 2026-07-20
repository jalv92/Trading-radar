using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    /// <summary>
    /// OF3-v2 tape leg — rolling net volume of LARGE aggressor prints.
    /// A print is classified by where it traded relative to the prevailing
    /// quote: at/above the ask = buyer-aggressed (+vol), at/below the bid =
    /// seller-aggressed (-vol), inside the spread = ignored. Only prints of
    /// at least <c>minLots</c> contracts count (institutional size; retail
    /// noise and HFT shredding stay out). <c>Net(now)</c> is the signed sum
    /// over the trailing window.
    ///
    /// New standalone module — no existing Engine file is modified (the
    /// engine freeze for AbsorptionScalper validation stays intact).
    /// </summary>
    public sealed class BigPrintTracker
    {
        private struct Item
        {
            public DateTime Time;
            public long Signed;
        }

        private readonly TimeSpan _window;
        private readonly long _minLots;
        private readonly Queue<Item> _q = new Queue<Item>();
        private long _net;

        public BigPrintTracker(long minLots, TimeSpan window)
        {
            if (minLots < 1) throw new ArgumentOutOfRangeException("minLots");
            if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("window");
            _minLots = minLots;
            _window = window;
        }

        /// <summary>Feed every Last print with the quote prevailing at trade time.</summary>
        public void OnTrade(double price, long volume, double bid, double ask, DateTime now)
        {
            if (volume >= _minLots && bid > 0 && ask > bid)
            {
                int side = price >= ask ? 1 : price <= bid ? -1 : 0;
                if (side != 0)
                {
                    _q.Enqueue(new Item { Time = now, Signed = side * volume });
                    _net += side * volume;
                }
            }
            Evict(now);
        }

        /// <summary>Signed big-print volume over the trailing window (+ = buyers).</summary>
        public long Net(DateTime now)
        {
            Evict(now);
            return _net;
        }

        public int Count { get { return _q.Count; } }

        private void Evict(DateTime now)
        {
            while (_q.Count > 0 && now - _q.Peek().Time > _window)
                _net -= _q.Dequeue().Signed;
        }
    }
}
