using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    public struct EpisodeResult
    {
        public Side Side;
        public double Price;
        public Outcome Outcome;
        public long Traded;
        public long Cancelled;
        public DateTime ResolvedAt;
    }

    // Attributes every size decrease at a tracked price by cross-referencing Last prints.
    // Stateful per open episode; pure (no clock, no NT).
    public class EpisodeClassifier
    {
        private class Episode
        {
            public Side Side;
            public double Price;
            public long SizeAtOpen;
            public DateTime OpenTime;
            public bool Crossed;           // inside quote ever crossed P
            public bool QuoteAwayAtVanish; // quote was >= D_pull ticks away when size hit 0
            public bool Vanished;
        }

        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly Dictionary<long, Episode> _open = new Dictionary<long, Episode>();
        private readonly Queue<EpisodeResult> _resolved = new Queue<EpisodeResult>();

        public EpisodeClassifier(RadarConfig cfg) { _cfg = cfg; _tick = cfg.TickSize; }

        private long Key(Side s, double price) { return ((long)Math.Round(price / _tick)) * 2 + (s == Side.Ask ? 1 : 0); }
        private TimeSpan Scaled(TimeSpan ts) { return TimeSpan.FromTicks((long)(ts.Ticks * _cfg.VolGovernor)); }
        private Side ConsumingAggressor(Side wallSide) { return wallSide == Side.Ask ? Side.Ask : Side.Bid; }

        public bool HasOpenEpisode(Side side, double price) { return _open.ContainsKey(Key(side, price)); }

        public void OnApproach(Side side, double price, long sizeAtOpen, DateTime now)
        {
            long k = Key(side, price);
            if (_open.ContainsKey(k)) return;
            _open[k] = new Episode { Side = side, Price = price, SizeAtOpen = sizeAtOpen, OpenTime = now };
        }

        public void Update(BookMirror book, DateTime now)
        {
            if (_open.Count == 0) return;
            var toResolve = new List<Episode>();
            foreach (var ep in _open.Values)
            {
                long displayed = CurrentVolume(book, ep.Side, ep.Price);
                bool crossedNow = QuoteCrossed(book, ep.Side, ep.Price);
                if (crossedNow) ep.Crossed = true;

                if (displayed == 0 && !ep.Vanished)
                {
                    ep.Vanished = true;
                    ep.QuoteAwayAtVanish = QuoteTicksAway(book, ep.Side, ep.Price) >= _cfg.D_pull;
                }

                bool timedOut = now - ep.OpenTime >= Scaled(_cfg.T_episode);
                if (displayed == 0 || ep.Crossed || timedOut) toResolve.Add(ep);
            }

            foreach (var ep in toResolve)
            {
                EpisodeResult r;
                if (TryClassify(ep, book, now, out r)) _resolved.Enqueue(r);
                _open.Remove(Key(ep.Side, ep.Price));
            }
        }

        // Returns true only when a real condition fired (spec §6.3). Ambiguous timeouts
        // (no trades, no cancellation, no cross) produce no outcome — memory is untouched.
        private bool TryClassify(Episode ep, BookMirror book, DateTime now, out EpisodeResult r)
        {
            long displayed = CurrentVolume(book, ep.Side, ep.Price);
            long drop = Math.Max(0, ep.SizeAtOpen - displayed);
            // ponytail: trade attribution currently sums over the whole episode lifetime, not within
            // W_assoc of each size decrease (spec §6.3); W_assoc is reserved and will be wired +
            // calibrated during Market Replay testing via a debug data-capture path (TODO).
            long traded = book.TradedAt(ep.Price, ep.OpenTime, ConsumingAggressor(ep.Side));
            long cancelled = Math.Max(0, drop - traded);

            Outcome o;
            if (ep.Crossed)
                o = Outcome.Consumed;
            else if (traded >= _cfg.A_absorb * ep.SizeAtOpen
                     && traded >= _cfg.RefillRatioTrigger * Math.Max(drop, 1))
                o = Outcome.Absorbed;
            else if (cancelled > traded && ep.QuoteAwayAtVanish)
                o = Outcome.Pulled;
            else
            {
                r = default(EpisodeResult);
                return false;   // ambiguous: do not touch memory
            }

            r = new EpisodeResult { Side = ep.Side, Price = ep.Price, Outcome = o, Traded = traded, Cancelled = cancelled, ResolvedAt = now };
            return true;
        }

        public bool TryTakeResolved(out EpisodeResult r)
        {
            if (_resolved.Count > 0) { r = _resolved.Dequeue(); return true; }
            r = default(EpisodeResult); return false;
        }

        private long CurrentVolume(BookMirror book, Side side, double price)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
                if (Math.Abs(levels[i].Price - price) < _tick / 2.0) return levels[i].Volume;
            return 0;
        }

        // For an Ask wall: crossed if best ask moved above P. For a Bid wall: best bid below P.
        private bool QuoteCrossed(BookMirror book, Side side, double price)
        {
            if (side == Side.Ask)
                return book.TryBestAsk(out var a) && a.Price > price + _tick / 2.0;
            return book.TryBestBid(out var b) && b.Price < price - _tick / 2.0;
        }

        private int QuoteTicksAway(BookMirror book, Side side, double price)
        {
            DepthLevel q;
            bool has = side == Side.Ask ? book.TryBestBid(out q) : book.TryBestAsk(out q);
            if (!has) return int.MaxValue;
            return (int)Math.Round(Math.Abs(price - q.Price) / _tick);
        }
    }
}
