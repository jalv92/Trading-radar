using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    // Decides whether a price is a confirmed wall *right now*. Stateful across Update() calls
    // for persistence and flicker; pure (no clock, no NT).
    public class WallDetector
    {
        private class LevelState
        {
            public DateTime? QualifyingSince;     // start of continuous qualification
            public bool PresentLastUpdate;        // for flicker transition detection
            public readonly List<DateTime> Transitions = new List<DateTime>(); // appear/disappear stamps
        }

        private struct Sample { public long Size; public DateTime Time; }

        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly Dictionary<long, LevelState> _bid = new Dictionary<long, LevelState>();
        private readonly Dictionary<long, LevelState> _ask = new Dictionary<long, LevelState>();
        private readonly List<Sample> _bidSamples = new List<Sample>();
        private readonly List<Sample> _askSamples = new List<Sample>();

        public WallDetector(RadarConfig cfg) { _cfg = cfg; _tick = cfg.TickSize; }

        private long Key(double price) { return (long)Math.Round(price / _tick); }
        private Dictionary<long, LevelState> StateMap(Side s) { return s == Side.Bid ? _bid : _ask; }
        private List<Sample> SampleList(Side s) { return s == Side.Bid ? _bidSamples : _askSamples; }
        private TimeSpan Scaled(TimeSpan ts) { return TimeSpan.FromTicks((long)(ts.Ticks * _cfg.VolGovernor)); }

        public void Update(BookMirror book, DateTime now)
        {
            UpdateSide(book, Side.Bid, now);
            UpdateSide(book, Side.Ask, now);
        }

        private void UpdateSide(BookMirror book, Side side, DateTime now)
        {
            var levels = book.Levels(side);
            var map = StateMap(side);
            var samples = SampleList(side);

            // B_time is side-wide and identical across levels; hoist before the loop.
            long bTime = TemporalMedian(samples, now);

            // Record presence set this update.
            var presentNow = new HashSet<long>();
            for (int i = 0; i < levels.Count; i++)
            {
                long k = Key(levels[i].Price);
                presentNow.Add(k);
                samples.Add(new Sample { Size = levels[i].Volume, Time = now });

                LevelState st;
                if (!map.TryGetValue(k, out st)) { st = new LevelState(); map[k] = st; }

                // Flicker: a transition is an appear after an absence.
                if (!st.PresentLastUpdate) st.Transitions.Add(now);
                st.PresentLastUpdate = true;

                // Persistence: relative + absolute must hold continuously.
                long b = Math.Max(book.MedianSizeExcluding(side, levels[i].Price), bTime);
                bool qualifies = levels[i].Volume >= _cfg.K_mult * b && levels[i].Volume >= _cfg.MinAbsSize;
                if (qualifies) { if (st.QualifyingSince == null) st.QualifyingSince = now; }
                else st.QualifyingSince = null;

                PruneTransitions(st, now);
            }

            // Mark levels that disappeared this update (also a flicker transition).
            foreach (var kv in map)
            {
                if (!presentNow.Contains(kv.Key))
                {
                    if (kv.Value.PresentLastUpdate) kv.Value.Transitions.Add(now);
                    kv.Value.PresentLastUpdate = false;
                    kv.Value.QualifyingSince = null;
                    PruneTransitions(kv.Value, now);
                }
            }

            PruneSamples(samples, now);
        }

        private void PruneTransitions(LevelState st, DateTime now)
        {
            DateTime cutoff = now - TimeSpan.FromSeconds(1);
            int i = 0;
            while (i < st.Transitions.Count && st.Transitions[i] < cutoff) i++;
            if (i > 0) st.Transitions.RemoveRange(0, i);
        }

        private void PruneSamples(List<Sample> samples, DateTime now)
        {
            DateTime cutoff = now - _cfg.BaselineWindow;
            int i = 0;
            while (i < samples.Count && samples[i].Time < cutoff) i++;
            if (i > 0) samples.RemoveRange(0, i);
        }

        public long Baseline(Side side, double price, BookMirror book, DateTime now)
        {
            long bLevel = book.MedianSizeExcluding(side, price);
            long bTime = TemporalMedian(SampleList(side), now);
            return Math.Max(bLevel, bTime);
        }

        private long TemporalMedian(List<Sample> samples, DateTime now)
        {
            DateTime cutoff = now - _cfg.BaselineWindow;
            var v = new List<long>();
            for (int i = 0; i < samples.Count; i++)
                if (samples[i].Time >= cutoff) v.Add(samples[i].Size);
            if (v.Count == 0) return 0;
            v.Sort();
            int mid = v.Count / 2;
            // Even count: lower-middle (floor) — conservative baseline a single wall can't inflate.
            return (v.Count % 2 == 1) ? v[mid] : v[mid - 1];
        }

        public bool IsConfirmed(Side side, double price, BookMirror book, DateTime now)
        {
            long k = Key(price);
            LevelState st;
            if (!StateMap(side).TryGetValue(k, out st)) return false;
            if (st.QualifyingSince == null) return false;
            if (now - st.QualifyingSince.Value < Scaled(_cfg.T_persist)) return false;

            // Flicker rate over the last second.
            PruneTransitions(st, now);
            if (st.Transitions.Count > _cfg.F_flicker) return false;

            // Re-check current size still qualifies (defensive; persistence implies it).
            long vol = CurrentVolume(book, side, price);
            long b = Baseline(side, price, book, now);
            return vol >= _cfg.K_mult * b && vol >= _cfg.MinAbsSize;
        }

        private long CurrentVolume(BookMirror book, Side side, double price)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
                if (Math.Abs(levels[i].Price - price) < _tick / 2.0) return levels[i].Volume;
            return 0;
        }
    }
}
