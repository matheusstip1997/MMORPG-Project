using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Server to client, reliable (protocol 19, WP-M2-2; spec 9.4): the observer's own final stats, sent when its stats
    /// change (<c>StatsChanged</c>, spec 8.1), when its client connects, and when its toxicity or satiety change (not per
    /// tick). <see cref="Values"/> holds every stat in <c>StatDef</c> row order (the client loads the same table), so the
    /// HUD and later the character panel read any of them by id; <see cref="Version"/> is the server's
    /// <c>StatBlock.Version</c>. The meters are thousandths of a point when the message was built; the client decays them
    /// from the time it got it at the <c>ConsumptionMeterSettings</c> rates (R-3.4-03).
    /// MemoryPack after the <see cref="ReliableMessageId"/> byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class OwnStats
    {
        public uint Version { get; set; }
        public int[] Values { get; set; } = Array.Empty<int>();
        public int ToxicityMilli { get; set; }
        public int SatietyMilli { get; set; }
    }

    /// <summary>Encoding and checked decoding of <see cref="OwnStats"/>, through <see cref="ReliableMessageReader"/>.</summary>
    public static class StatsCodec
    {
        /// <summary>Most stats an <see cref="OwnStats"/> may carry (the guard's array bound; <c>StatDef</c> has far fewer rows).</summary>
        public const int MaxStatCount = 128;

        private static readonly MemoryPackSerializerOptions Options = MemoryPackSerializerOptions.Utf8;

        public static ReadOnlySpan<byte> Write(ReliableMessageWriter writer, OwnStats message)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            writer.Begin(ReliableMessageId.OwnStats);
            MemoryPackSerializer.Serialize(writer, message, Options);
            return writer.Written;
        }

        /// <summary>False for anything malformed: a bad body, more than <see cref="MaxStatCount"/> stats, negative meters.</summary>
        public static bool TryRead(ReadOnlySpan<byte> message, out OwnStats stats)
        {
            stats = null!;
            OwnStats? value = null;
            if (!ReliableMessageReader.TryRead(message, ReliableMessageId.OwnStats, ref value) || value?.Values == null
                || value.Values.Length > MaxStatCount || value.ToxicityMilli < 0 || value.SatietyMilli < 0)
                return false;
            stats = value;
            return true;
        }
    }
}
