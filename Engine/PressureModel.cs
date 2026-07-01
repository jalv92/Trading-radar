using System;
using System.Collections.Generic;

namespace TradingRadar.Engine
{
    public enum SignalId { Imbalance, InsideThin, AirPocket, Delta, WallErosion }

    public struct WallErosion { public bool Active; public double Frac; public bool Above; }

    public struct PressureInputs
    {
        public IReadOnlyList<DepthLevel> Bids;   // descending (best first)
        public IReadOnlyList<DepthLevel> Asks;   // ascending (best first)
        public long BestBidSize;
        public long BestAskSize;
        public long AggressorDelta;
        public WallErosion Wall;
    }

    public struct SignalRead
    {
        public SignalId Id;
        public double Lean;     // -1 short .. +1 long
        public double Weight;
        public bool Active;
        // True when the signal is inactive AND near-zero lean (idle wall/flat delta) — display only.
        public bool Idle() { return !Active && Math.Abs(Lean) <= 0.12; }
    }

    // PLACEHOLDER weights/thresholds (spec §6/§9 — measured in Plan D). No literal lives in logic.
    public class PressureConfig
    {
        public double WImbalance = 3.0;
        public double WInsideThin = 2.0;
        public double WAirPocket = 2.0;
        public double WDelta = 2.0;
        public double WWallErosion = 4.0;

        public double ImbalanceGain = 2.6;   // scales raw skew into lean
        public double InsideThinGain = 1.6;
        public double DeltaScale = 14.0;      // contracts mapping delta -> [-1,1]
        public int AirRange = 3;              // nearest levels per side scanned for the pocket
        public int AirThinSize = 9;           // a near level below this counts as a hole
        public double AirThinPenalty = 0.4;   // lean nudged toward the void when a hole is present

        public double ActiveFloor = 0.12;     // |lean| below this = inactive
        public double ConvictionFloor = 0.30; // |lean| to count toward conviction (Task 4)
        public double GreenNet = 0.55;        // |net| threshold for a green-light (Task 4)
        public int GreenConviction = 4;       // agreeing signals needed (Task 4)
        public double OpposingVeto = 0.55;    // an opposing active lean above this blocks green (Task 4)
    }

    public struct PressureResult
    {
        public SignalRead[] Signals;
        public double Net;
        public int Conviction;
        public int Sign;
        public bool Green;
    }

    // Pure: takes a snapshot of the zone + flow, emits per-signal leans. No NT, no clock.
    public class PressureModel
    {
        private readonly PressureConfig _cfg;
        public PressureModel(PressureConfig cfg) { _cfg = cfg; }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static long Mass(IReadOnlyList<DepthLevel> l)
        {
            long s = 0; if (l != null) for (int i = 0; i < l.Count; i++) s += l[i].Volume; return s;
        }
        private static long NearMass(IReadOnlyList<DepthLevel> l, int n)
        {
            long s = 0; if (l != null) for (int i = 0; i < l.Count && i < n; i++) s += l[i].Volume; return s;
        }
        private static bool HasHole(IReadOnlyList<DepthLevel> l, int n, int thin)
        {
            if (l == null) return false;
            for (int i = 0; i < l.Count && i < n; i++) if (l[i].Volume < thin) return true;
            return false;
        }

        public SignalRead[] Signals(PressureInputs inp)
        {
            long bidMass = Mass(inp.Bids), askMass = Mass(inp.Asks);
            double imb = (bidMass + askMass) > 0
                ? Clamp(((double)(bidMass - askMass) / (bidMass + askMass)) * _cfg.ImbalanceGain, -1, 1) : 0;

            double thin = (inp.BestBidSize + inp.BestAskSize) > 0
                ? Clamp(((double)(inp.BestBidSize - inp.BestAskSize) / (inp.BestBidSize + inp.BestAskSize)) * _cfg.InsideThinGain, -1, 1) : 0;

            long bidsNear = NearMass(inp.Bids, _cfg.AirRange), asksNear = NearMass(inp.Asks, _cfg.AirRange);
            bool holeBelow = HasHole(inp.Bids, _cfg.AirRange, _cfg.AirThinSize);
            double air = (bidsNear + asksNear) > 0
                ? Clamp((double)(bidsNear - asksNear) / (bidsNear + asksNear) - (holeBelow ? _cfg.AirThinPenalty : 0), -1, 1)
                : (holeBelow ? -_cfg.AirThinPenalty : 0);

            double delta = Clamp(inp.AggressorDelta / _cfg.DeltaScale, -1, 1);

            double wallLean = 0; bool wallActive = false;
            if (inp.Wall.Active)
            {
                double mag = Clamp(inp.Wall.Frac, 0, 1);
                wallLean = inp.Wall.Above ? mag : -mag;   // ask wall eroding => long; bid wall eroding => short
                wallActive = true;
            }

            return new SignalRead[]
            {
                Mk(SignalId.Imbalance,   imb,      _cfg.WImbalance,   true),
                Mk(SignalId.InsideThin,  thin,     _cfg.WInsideThin,  true),
                Mk(SignalId.AirPocket,   air,      _cfg.WAirPocket,   true),
                Mk(SignalId.Delta,       delta,    _cfg.WDelta,       true),
                Mk(SignalId.WallErosion, wallLean, _cfg.WWallErosion, wallActive)
            };
        }

        public PressureResult Evaluate(PressureInputs inp)
        {
            SignalRead[] sig = Signals(inp);

            double num = 0, den = 0;
            for (int i = 0; i < sig.Length; i++)
                if (sig[i].Active) { num += sig[i].Lean * sig[i].Weight; den += sig[i].Weight; }
            double net = den > 0 ? Clamp(num / den, -1, 1) : 0;
            int sign = net > 0 ? 1 : (net < 0 ? -1 : 0);

            int conviction = 0;
            bool opposed = false;
            for (int i = 0; i < sig.Length; i++)
            {
                if (!sig[i].Active) continue;
                int ls = sig[i].Lean > 0 ? 1 : (sig[i].Lean < 0 ? -1 : 0);
                if (sign != 0 && ls == sign && Math.Abs(sig[i].Lean) > _cfg.ConvictionFloor) conviction++;
                if (sign != 0 && ls == -sign && Math.Abs(sig[i].Lean) > _cfg.OpposingVeto) opposed = true;
            }

            bool green = sign != 0 && conviction >= _cfg.GreenConviction
                         && Math.Abs(net) >= _cfg.GreenNet && !opposed;

            PressureResult r;
            r.Signals = sig; r.Net = net; r.Conviction = conviction; r.Sign = sign; r.Green = green;
            return r;
        }

        private SignalRead Mk(SignalId id, double lean, double weight, bool baseActive)
        {
            SignalRead r;
            r.Id = id; r.Lean = lean; r.Weight = weight;
            r.Active = baseActive && Math.Abs(lean) > _cfg.ActiveFloor;
            return r;
        }

        // Vote-less "book-skew context": the collapse of imbalance/inside-thin/air-pocket into one read.
        // Reference only — never fires anything (spec §7). Reuses the imbalance mass calc.
        public double BookSkewContext(PressureInputs inp)
        {
            long bidMass = Mass(inp.Bids), askMass = Mass(inp.Asks);
            if (bidMass + askMass <= 0) return 0;
            double raw = (double)(bidMass - askMass) / (bidMass + askMass) * _cfg.ImbalanceGain;
            return raw < -1 ? -1 : (raw > 1 ? 1 : raw);
        }
    }
}
