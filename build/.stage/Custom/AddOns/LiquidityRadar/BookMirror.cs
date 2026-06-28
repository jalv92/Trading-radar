using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    // Mirrors the positional MBP depth stream and a short ring of recent trades.
    // Pure: no NT, no clock. All time arrives via event/parameter timestamps.
    public class BookMirror
    {
        private struct Trade { public double Price; public long Volume; public DateTime Time; public Side Aggressor; }

        private readonly double _tick;
        private readonly TimeSpan _tradeRetention;
        // Bids kept descending by price, asks ascending — same order NT delivers by Position.
        private readonly List<DepthLevel> _bids = new List<DepthLevel>();
        private readonly List<DepthLevel> _asks = new List<DepthLevel>();
        private readonly List<Trade> _trades = new List<Trade>();

        public BookMirror(double tickSize, TimeSpan tradeRetention)
        {
            _tick = tickSize;
            _tradeRetention = tradeRetention;
        }

        private List<DepthLevel> SideList(Side s) { return s == Side.Bid ? _bids : _asks; }
        private bool SamePrice(double a, double b) { return Math.Abs(a - b) < _tick / 2.0; }

        public void ApplyDepth(DepthEvent e)
        {
            if (e.IsReset) { _bids.Clear(); _asks.Clear(); return; }
            var list = SideList(e.Side);
            switch (e.Op)
            {
                case DepthOp.Add:
                    {
                        int pos = e.Position < 0 ? 0 : (e.Position > list.Count ? list.Count : e.Position);
                        list.Insert(pos, new DepthLevel { Price = e.Price, Volume = e.Volume });
                        break;
                    }
                case DepthOp.Update:
                    {
                        if (e.Position >= 0 && e.Position < list.Count)
                            list[e.Position] = new DepthLevel { Price = e.Price, Volume = e.Volume };
                        break;
                    }
                case DepthOp.Remove:
                    {
                        if (e.Position >= 0 && e.Position < list.Count)
                            list.RemoveAt(e.Position);
                        break;
                    }
            }
        }

        public void ApplyTrade(TradeEvent t)
        {
            Side aggressor = InferAggressor(t.Price);
            _trades.Add(new Trade { Price = t.Price, Volume = t.Volume, Time = t.Time, Aggressor = aggressor });
            // Prune relative to the newest trade time (deterministic, no clock).
            DateTime cutoff = t.Time - _tradeRetention;
            int i = 0;
            while (i < _trades.Count && _trades[i].Time < cutoff) i++;
            if (i > 0) _trades.RemoveRange(0, i);
        }

        // Last >= best ask => buy aggressor (lifted the offer); Last <= best bid => sell aggressor.
        private Side InferAggressor(double price)
        {
            if (_asks.Count > 0 && price >= _asks[0].Price - _tick / 2.0) return Side.Ask; // hit the ask = buy aggressor
            if (_bids.Count > 0 && price <= _bids[0].Price + _tick / 2.0) return Side.Bid; // hit the bid = sell aggressor
            // Inside the spread / unknown: attribute by nearest touch.
            if (_asks.Count > 0 && _bids.Count > 0)
                return Math.Abs(price - _asks[0].Price) <= Math.Abs(price - _bids[0].Price) ? Side.Ask : Side.Bid;
            return Side.Ask;
        }

        public void ResetFromSnapshot(IList<DepthLevel> bids, IList<DepthLevel> asks)
        {
            _bids.Clear(); _asks.Clear();
            if (bids != null) _bids.AddRange(bids);
            if (asks != null) _asks.AddRange(asks);
        }

        public IReadOnlyList<DepthLevel> Levels(Side side) { return SideList(side); }

        public bool TryBestBid(out DepthLevel best)
        {
            if (_bids.Count > 0) { best = _bids[0]; return true; }
            best = default(DepthLevel); return false;
        }

        public bool TryBestAsk(out DepthLevel best)
        {
            if (_asks.Count > 0) { best = _asks[0]; return true; }
            best = default(DepthLevel); return false;
        }

        public long MedianSize(Side side) { return MedianOf(SideList(side), double.NaN); }

        public long MedianSizeExcluding(Side side, double price) { return MedianOf(SideList(side), price); }

        private long MedianOf(List<DepthLevel> list, double excludePrice)
        {
            var v = new List<long>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                if (!double.IsNaN(excludePrice) && SamePrice(list[i].Price, excludePrice)) continue;
                v.Add(list[i].Volume);
            }
            if (v.Count == 0) return 0;
            v.Sort();
            int mid = v.Count / 2;
            // Even count: lower-middle (floor) — conservative baseline, matches test expectations.
            return (v.Count % 2 == 1) ? v[mid] : v[mid - 1];
        }

        public long TradedAt(double price, DateTime since, Side? aggressorFilter)
        {
            long sum = 0;
            for (int i = 0; i < _trades.Count; i++)
            {
                var tr = _trades[i];
                if (tr.Time < since) continue;
                if (!SamePrice(tr.Price, price)) continue;
                if (aggressorFilter.HasValue && tr.Aggressor != aggressorFilter.Value) continue;
                sum += tr.Volume;
            }
            return sum;
        }
    }
}
