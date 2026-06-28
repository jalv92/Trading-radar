using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    // The single public engine entry point. Wires BookMirror (passed in) through
    // WallDetector -> EpisodeClassifier -> LiquidityMemory and emits RadarNode[].
    public class WallTracker
    {
        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly WallDetector _detector;
        private readonly EpisodeClassifier _classifier;
        private readonly LiquidityMemory _memory;
        private double _lastBestBid;
        private double _lastBestAsk;

        public WallTracker(RadarConfig cfg)
        {
            _cfg = cfg; _tick = cfg.TickSize;
            _detector = new WallDetector(cfg);
            _classifier = new EpisodeClassifier(cfg);
            _memory = new LiquidityMemory(cfg);
        }

        public void Update(BookMirror book, DateTime now)
        {
            _detector.Update(book, now);

            DepthLevel bb, ba;
            bool hasBid = book.TryBestBid(out bb);
            bool hasAsk = book.TryBestAsk(out ba);
            _lastBestBid = hasBid ? bb.Price : 0;
            _lastBestAsk = hasAsk ? ba.Price : 0;

            // 1) Promote / observe confirmed walls; track which prices are currently visible.
            var visible = new HashSet<long>();
            UpdateSide(book, Side.Bid, now, visible);
            UpdateSide(book, Side.Ask, now, visible);

            // 2) Mark blind every tracked node not in the current visible set.
            MarkAbsentBlind(visible);

            // 3) Approach detection -> open episodes for tracked, visible walls near the inside.
            OpenApproaching(book, Side.Bid, now);
            OpenApproaching(book, Side.Ask, now);

            // 4) Advance & resolve episodes -> feed outcomes to memory.
            _classifier.Update(book, now);
            EpisodeResult r;
            while (_classifier.TryTakeResolved(out r)) _memory.ApplyOutcome(r, now);

            // 5) Eviction.
            _memory.Evict(now);
        }

        private void UpdateSide(BookMirror book, Side side, DateTime now, HashSet<long> visible)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
            {
                long k = Key(side, levels[i].Price);
                visible.Add(k);
                bool confirmed = _detector.IsConfirmed(side, levels[i].Price, book, now);
                if (_memory.Contains(side, levels[i].Price))
                    _memory.ObserveLive(side, levels[i].Price, levels[i].Volume, confirmed, now);
                else if (confirmed)
                {
                    long baseline = _detector.Baseline(side, levels[i].Price, book, now);
                    _memory.Promote(side, levels[i].Price, levels[i].Volume, baseline, now);
                }
            }
        }

        private void MarkAbsentBlind(HashSet<long> visible)
        {
            // Iterate every tracked node; blind any whose key is not in the visible set.
            // This covers price gaps larger than MemoryBandTicks that a band-scan would miss.
            var tracked = _memory.TrackedLevels();
            for (int i = 0; i < tracked.Count; i++)
            {
                var kv = tracked[i];
                if (!visible.Contains(Key(kv.Key, kv.Value)))
                    _memory.MarkBlind(kv.Key, kv.Value);
            }
        }

        private void OpenApproaching(BookMirror book, Side side, DateTime now)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
            {
                double price = levels[i].Price;
                if (!_memory.Contains(side, price)) continue;
                if (_classifier.HasOpenEpisode(side, price)) continue;
                if (NearInside(book, side, price))
                    _classifier.OnApproach(side, price, levels[i].Volume, now);
            }
        }

        // Ask wall tested when the bid is within D_approach ticks; bid wall when the ask is.
        private bool NearInside(BookMirror book, Side side, double price)
        {
            if (side == Side.Ask)
            {
                DepthLevel bid;
                if (!book.TryBestBid(out bid)) return false;
                return (price - bid.Price) <= _cfg.D_approach * _tick + _tick / 2.0;
            }
            else
            {
                DepthLevel ask;
                if (!book.TryBestAsk(out ask)) return false;
                return (ask.Price - price) <= _cfg.D_approach * _tick + _tick / 2.0;
            }
        }

        public void OnReset(DateTime now) { _memory.MarkAllBlind(); }

        public IReadOnlyList<RadarNode> GetSnapshot(DateTime now)
        {
            return _memory.Snapshot(_lastBestBid, _lastBestAsk, now);
        }

        // Test seam: lets a test supply the mid explicitly so the band filter is centred on the
        // node being observed rather than on the gap quote cached by the last Update call.
        public IReadOnlyList<RadarNode> GetSnapshot(double bestBid, double bestAsk, DateTime now)
        {
            return _memory.Snapshot(bestBid, bestAsk, now);
        }

        private long Key(Side s, double price) { return ((long)Math.Round(price / _tick)) * 2 + (s == Side.Ask ? 1 : 0); }
    }
}
