using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    public class LiquidityMemory
    {
        private class MemoryNode
        {
            public Side Side;
            public double Price;
            public long LastKnownSize;
            public long PeakSize;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public int TimesConfirmed;
            public int AbsorbedCount;
            public int PulledCount;
            public bool Consumed;
            public double Confidence;   // last observed value, pre-decay
            public bool InWindow;
            public NodeState State;
            public bool Phantom;
        }

        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly Dictionary<long, MemoryNode> _nodes = new Dictionary<long, MemoryNode>();

        public LiquidityMemory(RadarConfig cfg) { _cfg = cfg; _tick = cfg.TickSize; }

        private long Key(Side s, double price) { return ((long)Math.Round(price / _tick)) * 2 + (s == Side.Ask ? 1 : 0); }
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private TimeSpan ScaledH() { return TimeSpan.FromTicks((long)(_cfg.H.Ticks * _cfg.VolGovernor)); }

        public bool Contains(Side side, double price) { return _nodes.ContainsKey(Key(side, price)); }

        public void Promote(Side side, double price, long size, long baseline, DateTime now)
        {
            long k = Key(side, price);
            if (_nodes.ContainsKey(k)) { ObserveLive(side, price, size, true, now); return; }
            double ratio = baseline > 0 ? size / (double)baseline : _cfg.K_mult;
            double c0 = Clamp(0.4 + 0.1 * (ratio - _cfg.K_mult), 0.4, 0.8);
            _nodes[k] = new MemoryNode
            {
                Side = side, Price = price, LastKnownSize = size, PeakSize = size,
                FirstSeen = now, LastSeen = now, TimesConfirmed = 1,
                Confidence = c0, InWindow = true, State = NodeState.Wall
            };
        }

        public void ObserveLive(Side side, double price, long size, bool stillConfirmedWall, DateTime now)
        {
            MemoryNode n;
            if (!_nodes.TryGetValue(Key(side, price), out n)) return;
            bool wasBlind = !n.InWindow;
            if (n.LastKnownSize > 0)
            {
                double change = (size - n.LastKnownSize) / (double)n.LastKnownSize;
                if (change >= _cfg.G_grow) n.Confidence += _cfg.dC_grow;
                else if (change <= -_cfg.G_grow) n.Confidence *= _cfg.ShrinkFactor;
            }
            n.LastKnownSize = size;
            if (size > n.PeakSize) n.PeakSize = size;
            n.LastSeen = now;
            n.InWindow = true;
            if (stillConfirmedWall)
            {
                n.State = NodeState.Wall;
                // dC_confirm is a discrete revisit event (spec §6.4): only when going blind→live.
                // A continuously-live wall must not ratchet to 1.0 every tick.
                if (wasBlind) { n.TimesConfirmed++; n.Confidence += _cfg.dC_confirm; }
            }
            else n.State = NodeState.Live;
            n.Confidence = Clamp(n.Confidence, 0.0, 1.0);
        }

        public void MarkBlind(Side side, double price)
        {
            MemoryNode n;
            if (_nodes.TryGetValue(Key(side, price), out n)) n.InWindow = false;
        }

        public void MarkAllBlind() { foreach (var n in _nodes.Values) n.InWindow = false; }

        public void ApplyOutcome(EpisodeResult r, DateTime now)
        {
            MemoryNode n;
            if (!_nodes.TryGetValue(Key(r.Side, r.Price), out n)) return;
            n.LastSeen = now;
            switch (r.Outcome)
            {
                case Outcome.Absorbed:
                    n.AbsorbedCount++;
                    n.Confidence = Clamp(n.Confidence + _cfg.dC_absorb, 0.0, 1.0);
                    n.State = NodeState.Absorbed;
                    break;
                case Outcome.Pulled:
                    n.PulledCount++;
                    n.Confidence = Clamp(n.Confidence * _cfg.PullPenalty, 0.0, 1.0);
                    n.Phantom = true;
                    n.State = NodeState.Pulled;
                    break;
                case Outcome.Consumed:
                    n.Consumed = true;
                    n.Confidence = Clamp(n.Confidence * 0.5, 0.0, 1.0); // demote to a flipped S/R reference
                    n.State = NodeState.Consumed;
                    break;
            }
        }

        public void Evict(DateTime now)
        {
            var dead = new List<long>();
            foreach (var kv in _nodes)
            {
                var n = kv.Value;
                if (n.PulledCount >= _cfg.P_max) { dead.Add(kv.Key); continue; }
                double c = DecayedConfidence(n, now);
                double ageSec = (now - n.LastSeen).TotalSeconds;
                if (c < _cfg.C_floor && ageSec > _cfg.T_evict.TotalSeconds) dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++) _nodes.Remove(dead[i]);
        }

        private double DecayedConfidence(MemoryNode n, DateTime now)
        {
            if (n.InWindow) return n.Confidence;
            double dt = (now - n.LastSeen).TotalSeconds;
            double h = ScaledH().TotalSeconds;
            if (h <= 0) return n.Confidence;
            return n.Confidence * Math.Exp(-Math.Log(2.0) / h * dt);
        }

        public IReadOnlyList<KeyValuePair<Side, double>> TrackedLevels()
        {
            var list = new List<KeyValuePair<Side, double>>(_nodes.Count);
            foreach (var n in _nodes.Values) list.Add(new KeyValuePair<Side, double>(n.Side, n.Price));
            return list;
        }

        public IReadOnlyList<RadarNode> Snapshot(double bestBid, double bestAsk, DateTime now)
        {
            double mid = (bestBid > 0 && bestAsk > 0) ? (bestBid + bestAsk) / 2.0
                       : (bestBid > 0 ? bestBid : bestAsk);
            double band = _cfg.MemoryBandTicks * _tick;
            var outList = new List<RadarNode>();
            foreach (var n in _nodes.Values)
            {
                if (mid > 0 && Math.Abs(n.Price - mid) > band + _tick / 2.0) continue;
                outList.Add(new RadarNode
                {
                    Price = n.Price,
                    Side = n.Side,
                    LastKnownSize = n.LastKnownSize,
                    PeakSize = n.PeakSize,
                    State = n.InWindow ? n.State : NodeState.Remembered,
                    Confidence = DecayedConfidence(n, now),
                    InWindow = n.InWindow,
                    AgeSeconds = (now - n.LastSeen).TotalSeconds
                });
            }
            return outList;
        }
    }
}
