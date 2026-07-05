using System;

namespace TradingRadar.Engine
{
    public enum Side { Bid, Ask }
    public enum DepthOp { Add, Update, Remove }
    public enum NodeState { Live, Wall, Absorbed, Pulled, Consumed, Remembered }
    public enum Outcome { Absorbed, Pulled, Consumed }

    // React setup contract (spec §2/§13; defined once here per plan §0-R1 — never redefine in another
    // file, that is a CS0101 duplicate-type bug). Break = 0 so every existing Break fire — built by
    // object-initializer WITHOUT setting Kind — self-tags Break with zero logic change.
    public enum SetupKind { Break, Reactive }

    // Which reactive branch resolved: Reject = wall held/absorbed -> fade, Break = wall consumed ->
    // follow. None is value 0, the value every Break-setup fire carries.
    public enum ReactKind { None, Reject, Break }

    // One depth-stream operation, mapped from NT's MarketDepthEventArgs by the NT layer.
    public struct DepthEvent
    {
        public Side Side;
        public DepthOp Op;
        public int Position;   // 0 = inside (best); ladder index on its side
        public double Price;
        public long Volume;    // price-aggregated resting size (MBP), never an order count
        public DateTime Time;
        public bool IsReset;   // true => feed reset; wipe + rebuild, do not read as real pulls
    }

    // One Last print.
    public struct TradeEvent
    {
        public double Price;
        public long Volume;
        public DateTime Time;
    }

    // One ladder row.
    public struct DepthLevel
    {
        public double Price;
        public long Volume;
    }

    // The engine→view contract. Immutable snapshot row.
    public struct RadarNode
    {
        public double Price;
        public Side Side;
        public long LastKnownSize;
        public long PeakSize;
        public NodeState State;
        public double Confidence;  // 0..1
        public bool InWindow;      // within the live 10 levels
        public double AgeSeconds;  // since lastSeen
    }
}
