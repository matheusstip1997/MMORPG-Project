using System;

namespace Shared.Protocol
{
    /// <summary>What a hit did to its target, as clients see it (spec 3.1 HitResolver outcomes). Append only, never renumber.</summary>
    public enum HitKind : byte
    {
        None = 0,
        /// <summary>The target lost <see cref="HitEntry.Amount"/> HP and lives.</summary>
        Damaged = 1,
        /// <summary>The hit took the target's HP to 0.</summary>
        Killed = 2,
        /// <summary>Dodge i-frames covered the hit (R-3.3-02): no damage.</summary>
        Dodged = 3,
        /// <summary>A parry met the hit (R-3.3-03, R-3.3-04); an uncolored attack may still deal reduced damage.</summary>
        Parried = 4,
    }

    /// <summary>One resolved hit for observers (the damage numbers of spec 9, backlog I).</summary>
    public struct HitEntry
    {
        public uint AttackerId;
        public uint TargetId;
        /// <summary>HP the target lost, 0 to <see cref="HitMessage.MaxAmount"/>.</summary>
        public int Amount;
        public bool Crit;
        public HitKind Kind;
    }

    /// <summary>
    /// The hits of one server tick that an observer can see (hot message, unreliable channel, spec 1.7): the targets in
    /// its interest. Sent in tick phase 10 after the snapshot; the client shows them (backlog I) and changes no state
    /// from them, the snapshot stays authoritative.
    /// Layout, LSB first: id 8, server tick 32, count 6 (1 to <see cref="MaxHits"/>); per hit: attacker id 32,
    /// target id 32, amount 31, crit 1, kind 3.
    /// </summary>
    public static class HitMessage
    {
        public const int ServerTickBits = 32;
        public const int CountBits = 6;
        public const int MaxHits = (1 << CountBits) - 1;
        public const int AmountBits = 31;
        public const int MaxAmount = int.MaxValue;
        public const int KindBits = 3;

        public const int HeaderBits = HotMessage.IdBits + ServerTickBits + CountBits;
        public const int EntryBits = Snapshot.EntityIdBits * 2 + AmountBits + 1 + KindBits;

        public static int SizeFor(int count) => (HeaderBits + count * EntryBits + 7) >> 3;

        /// <summary>Most hits one message of at most <paramref name="maxBytes"/> bytes holds (0 if not even one).</summary>
        public static int MaxHitsFor(int maxBytes)
        {
            long bits = (long)maxBytes * 8 - HeaderBits;
            return bits < EntryBits ? 0 : (int)Math.Min(MaxHits, bits / EntryBits);
        }

        /// <summary>Writes 1 to <see cref="MaxHits"/> hits and returns the bytes written.</summary>
        public static int Write(Span<byte> destination, uint serverTick, ReadOnlySpan<HitEntry> hits)
        {
            int count = hits.Length;
            if (count < 1 || count > MaxHits)
                throw new ArgumentException($"A hit message carries 1 to {MaxHits} hits.", nameof(hits));
            if (destination.Length < SizeFor(count))
                throw new ArgumentException("Destination is too small.", nameof(destination));

            var writer = new BitWriter(destination);
            writer.WriteBits((uint)HotMessageId.Hit, HotMessage.IdBits);
            writer.WriteBits(serverTick, ServerTickBits);
            writer.WriteBits((uint)count, CountBits);
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.Amount < 0)
                    throw new ArgumentOutOfRangeException(nameof(hits), "Amount must not be negative.");
                if (!IsKind(hit.Kind))
                    throw new ArgumentOutOfRangeException(nameof(hits), $"{hit.Kind} is not a hit kind.");
                writer.WriteBits(hit.AttackerId, Snapshot.EntityIdBits);
                writer.WriteBits(hit.TargetId, Snapshot.EntityIdBits);
                writer.WriteBits((uint)hit.Amount, AmountBits);
                writer.WriteBool(hit.Crit);
                writer.WriteBits((uint)hit.Kind, KindBits);
            }
            return writer.Finish();
        }

        /// <summary>
        /// Reads a message into <paramref name="hits"/>. Returns false, without throwing, for anything malformed: wrong id,
        /// a count of 0 or more than the destination holds, wrong length, an unknown kind, a target id of 0, or non-zero
        /// padding (R-2.5-04).
        /// </summary>
        public static bool TryRead(ReadOnlySpan<byte> source, out uint serverTick, Span<HitEntry> hits, out int count)
        {
            serverTick = 0;
            count = 0;
            var reader = new BitReader(source);
            if (reader.ReadBits(HotMessage.IdBits) != (uint)HotMessageId.Hit)
                return false;
            uint tick = reader.ReadBits(ServerTickBits);
            int n = (int)reader.ReadBits(CountBits);
            if (reader.Failed || n < 1 || n > hits.Length || source.Length != SizeFor(n))
                return false;
            for (int i = 0; i < n; i++)
            {
                HitEntry hit;
                hit.AttackerId = reader.ReadBits(Snapshot.EntityIdBits);
                hit.TargetId = reader.ReadBits(Snapshot.EntityIdBits);
                hit.Amount = (int)reader.ReadBits(AmountBits);
                hit.Crit = reader.ReadBool();
                hit.Kind = (HitKind)reader.ReadBits(KindBits);
                if (hit.TargetId == 0 || !IsKind(hit.Kind))
                    return false;
                hits[i] = hit;
            }
            if (!reader.IsAtCleanEnd())
                return false;
            serverTick = tick;
            count = n;
            return true;
        }

        private static bool IsKind(HitKind kind) => kind >= HitKind.Damaged && kind <= HitKind.Parried;
    }
}
