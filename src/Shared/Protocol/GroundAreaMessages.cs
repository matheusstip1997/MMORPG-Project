using System;

namespace Shared.Protocol
{
    /// <summary>Why a ground area ended (spec 3.1), carried by <see cref="HotMessageId.GroundAreaDespawn"/>. Append only.</summary>
    public enum GroundAreaDespawnReason : byte
    {
        None = 0,
        /// <summary>It lasted its whole <c>durationMs</c>.</summary>
        Expired = 1,
        /// <summary>Its owner died (user decision 2026-10-24).</summary>
        OwnerDied = 2,
        /// <summary>Its owner left the world.</summary>
        OwnerGone = 3,
    }

    /// <summary>
    /// A ground area the player should draw (spec 3.1 <c>GroundArea</c> effects, backlog D6). It names its effect as the
    /// action's <c>ActionDef.netId</c> and the effect's index among that action's effects (the order of the shared
    /// <c>ActionCatalog</c>), so the client finds its hit shapes, duration and visual in the same tables the server uses.
    /// It covers ticks <see cref="StartTick"/> to <c>StartTick + duration - 1</c>. Ground positions are whole cm on the
    /// MovementSim axes (X, Y), plus the height.
    /// </summary>
    public struct GroundAreaSpawn
    {
        /// <summary>The area's own id (not an entity id), unique per world.</summary>
        public uint AreaId;
        /// <summary><c>ActionDef.netId</c>, 1 to <see cref="GroundAreaMessages.MaxActionId"/>.</summary>
        public ushort ActionId;
        /// <summary>Index of the GroundArea effect in its action, 0 to <see cref="GroundAreaMessages.MaxEffectIndex"/>.</summary>
        public byte EffectIndex;
        public int XCm;
        public int YCm;
        public int HeightCm;
        /// <summary>Binary angle, sent with <see cref="GroundAreaMessages.FacingBits"/> bits (the resolution FixedTrig uses).</summary>
        public ushort Facing;
        public uint StartTick;
    }

    /// <summary>A ground area ended in tick <see cref="Tick"/> (it is not drawn from that tick on).</summary>
    public struct GroundAreaDespawn
    {
        public uint AreaId;
        public uint Tick;
        public GroundAreaDespawnReason Reason;
    }

    /// <summary>
    /// Layouts of the ground area hot messages on the unreliable channel (spec 1.7, protocol 9), LSB first:
    /// <list type="bullet">
    /// <item><see cref="HotMessageId.GroundAreaSpawn"/>: id 8, count 8; per spawn areaId 32, actionId 12, effectIndex 3, x 24,
    /// y 24, height 24 (signed whole cm), facing 10, startTick 32 (161 bits).</item>
    /// <item><see cref="HotMessageId.GroundAreaDespawn"/>: id 8, count 8; per despawn areaId 32, tick 32, reason 3 (67 bits).</item>
    /// </list>
    /// Readers validate the whole packet before anything is read (exact length, known reasons, action id not 0, zero
    /// padding), so a client never applies half of a malformed message (R-2.5-04).
    /// </summary>
    public static class GroundAreaMessages
    {
        public const int CountBits = 8;
        public const int MaxCount = (1 << CountBits) - 1;
        public const int AreaIdBits = 32;
        public const int ActionIdBits = 12;
        public const int MaxActionId = (1 << ActionIdBits) - 1;
        /// <summary>An action has at most <c>ActionEffectDef.MaxEffectsPerAction</c> (8) effects.</summary>
        public const int EffectIndexBits = 3;
        public const int MaxEffectIndex = (1 << EffectIndexBits) - 1;
        public const int PositionBits = 24;
        public const int FacingBits = 10;
        public const int TickBits = 32;
        public const int ReasonBits = 3;

        public const int HeaderBits = HotMessage.IdBits + CountBits;
        public const int SpawnBits = AreaIdBits + ActionIdBits + EffectIndexBits + 3 * PositionBits + FacingBits + TickBits;
        public const int DespawnBits = AreaIdBits + TickBits + ReasonBits;

        public static int SpawnSizeFor(int count) => (HeaderBits + count * SpawnBits + 7) >> 3;

        public static int DespawnSizeFor(int count) => (HeaderBits + count * DespawnBits + 7) >> 3;

        /// <summary>How many spawns fit in <paramref name="bytes"/> (capped at <see cref="MaxCount"/>).</summary>
        public static int SpawnsThatFit(int bytes) => Math.Min(MaxCount, Math.Max(0, (bytes * 8 - HeaderBits) / SpawnBits));

        /// <summary>How many despawns fit in <paramref name="bytes"/> (capped at <see cref="MaxCount"/>).</summary>
        public static int DespawnsThatFit(int bytes) => Math.Min(MaxCount, Math.Max(0, (bytes * 8 - HeaderBits) / DespawnBits));

        internal static bool IsKnown(GroundAreaDespawnReason reason) =>
            reason >= GroundAreaDespawnReason.Expired && reason <= GroundAreaDespawnReason.OwnerGone;
    }

    /// <summary>Writes one <see cref="HotMessageId.GroundAreaSpawn"/> message into a caller-owned buffer without allocating.</summary>
    public ref struct GroundAreaSpawnWriter
    {
        private BitWriter _writer;
        private int _count;

        public GroundAreaSpawnWriter(Span<byte> destination)
        {
            if (destination.Length < GroundAreaMessages.SpawnSizeFor(0))
                throw new ArgumentException("Destination is too small for the message header.", nameof(destination));
            _writer = new BitWriter(destination);
            _count = 0;
            _writer.WriteBits((uint)HotMessageId.GroundAreaSpawn, HotMessage.IdBits);
            _writer.WriteBits(0, GroundAreaMessages.CountBits);
        }

        public int Count => _count;

        /// <summary>False when the spawn does not fit (the message is left as it was). Throws on values the layout cannot carry.</summary>
        public bool TryAdd(in GroundAreaSpawn spawn)
        {
            if (spawn.ActionId == 0 || spawn.ActionId > GroundAreaMessages.MaxActionId)
                throw new ArgumentOutOfRangeException(nameof(spawn), $"ActionId is 1 to {GroundAreaMessages.MaxActionId}.");
            if (spawn.EffectIndex > GroundAreaMessages.MaxEffectIndex)
                throw new ArgumentOutOfRangeException(nameof(spawn), $"EffectIndex is 0 to {GroundAreaMessages.MaxEffectIndex}.");
            CheckPosition(spawn.XCm);
            CheckPosition(spawn.YCm);
            CheckPosition(spawn.HeightCm);
            if (_count == GroundAreaMessages.MaxCount)
                return false;

            int start = _writer.BitPosition;
            _writer.WriteBits(spawn.AreaId, GroundAreaMessages.AreaIdBits);
            _writer.WriteBits(spawn.ActionId, GroundAreaMessages.ActionIdBits);
            _writer.WriteBits(spawn.EffectIndex, GroundAreaMessages.EffectIndexBits);
            _writer.WriteSigned(spawn.XCm, GroundAreaMessages.PositionBits);
            _writer.WriteSigned(spawn.YCm, GroundAreaMessages.PositionBits);
            _writer.WriteSigned(spawn.HeightCm, GroundAreaMessages.PositionBits);
            _writer.WriteBits(Quantize.AngleToWire(spawn.Facing, GroundAreaMessages.FacingBits), GroundAreaMessages.FacingBits);
            _writer.WriteBits(spawn.StartTick, GroundAreaMessages.TickBits);
            if (_writer.Overflowed)
            {
                _writer.Rewind(start);
                return false;
            }
            _count++;
            return true;
        }

        /// <summary>Writes the count and returns the message length in bytes.</summary>
        public int Finish()
        {
            _writer.PatchBits(HotMessage.IdBits, (uint)_count, GroundAreaMessages.CountBits);
            return _writer.Finish();
        }

        private static void CheckPosition(int cm)
        {
            if (cm < BitMath.SignedMin(GroundAreaMessages.PositionBits) || cm > BitMath.SignedMax(GroundAreaMessages.PositionBits))
                throw new ArgumentOutOfRangeException(nameof(cm), "Position does not fit the layout.");
        }
    }

    /// <summary>Reads a <see cref="HotMessageId.GroundAreaSpawn"/> message without allocating.</summary>
    public ref struct GroundAreaSpawnReader
    {
        private BitReader _reader;
        private int _left;

        public int Count { get; private set; }

        public static bool TryBegin(ReadOnlySpan<byte> source, out GroundAreaSpawnReader message)
        {
            message = default;
            var reader = new BitReader(source);
            if (reader.ReadBits(HotMessage.IdBits) != (uint)HotMessageId.GroundAreaSpawn)
                return false;
            int count = (int)reader.ReadBits(GroundAreaMessages.CountBits);
            if (reader.Failed || source.Length != GroundAreaMessages.SpawnSizeFor(count))
                return false;
            message._reader = reader;
            message._left = count;
            message.Count = count;

            GroundAreaSpawnReader scan = message; // a copy: validating does not move the real reader
            while (scan.TryRead(out _)) { }
            if (scan._left != 0 || !scan._reader.IsAtCleanEnd())
            {
                message = default;
                return false;
            }
            return true;
        }

        public bool TryRead(out GroundAreaSpawn spawn)
        {
            spawn = default;
            if (_left == 0 || _reader.Failed)
                return false;
            _left--;
            spawn.AreaId = _reader.ReadBits(GroundAreaMessages.AreaIdBits);
            spawn.ActionId = (ushort)_reader.ReadBits(GroundAreaMessages.ActionIdBits);
            spawn.EffectIndex = (byte)_reader.ReadBits(GroundAreaMessages.EffectIndexBits);
            spawn.XCm = _reader.ReadSigned(GroundAreaMessages.PositionBits);
            spawn.YCm = _reader.ReadSigned(GroundAreaMessages.PositionBits);
            spawn.HeightCm = _reader.ReadSigned(GroundAreaMessages.PositionBits);
            spawn.Facing = Quantize.AngleFromWire(_reader.ReadBits(GroundAreaMessages.FacingBits), GroundAreaMessages.FacingBits);
            spawn.StartTick = _reader.ReadBits(GroundAreaMessages.TickBits);
            if (spawn.ActionId == 0)
                _reader.Fail();
            return !_reader.Failed;
        }
    }

    /// <summary>Writes one <see cref="HotMessageId.GroundAreaDespawn"/> message into a caller-owned buffer without allocating.</summary>
    public ref struct GroundAreaDespawnWriter
    {
        private BitWriter _writer;
        private int _count;

        public GroundAreaDespawnWriter(Span<byte> destination)
        {
            if (destination.Length < GroundAreaMessages.DespawnSizeFor(0))
                throw new ArgumentException("Destination is too small for the message header.", nameof(destination));
            _writer = new BitWriter(destination);
            _count = 0;
            _writer.WriteBits((uint)HotMessageId.GroundAreaDespawn, HotMessage.IdBits);
            _writer.WriteBits(0, GroundAreaMessages.CountBits);
        }

        public int Count => _count;

        public bool TryAdd(in GroundAreaDespawn despawn)
        {
            if (!GroundAreaMessages.IsKnown(despawn.Reason))
                throw new ArgumentOutOfRangeException(nameof(despawn), "Unknown despawn reason.");
            if (_count == GroundAreaMessages.MaxCount)
                return false;
            int start = _writer.BitPosition;
            _writer.WriteBits(despawn.AreaId, GroundAreaMessages.AreaIdBits);
            _writer.WriteBits(despawn.Tick, GroundAreaMessages.TickBits);
            _writer.WriteBits((uint)despawn.Reason, GroundAreaMessages.ReasonBits);
            if (_writer.Overflowed)
            {
                _writer.Rewind(start);
                return false;
            }
            _count++;
            return true;
        }

        public int Finish()
        {
            _writer.PatchBits(HotMessage.IdBits, (uint)_count, GroundAreaMessages.CountBits);
            return _writer.Finish();
        }
    }

    /// <summary>Reads a <see cref="HotMessageId.GroundAreaDespawn"/> message without allocating.</summary>
    public ref struct GroundAreaDespawnReader
    {
        private BitReader _reader;
        private int _left;

        public int Count { get; private set; }

        public static bool TryBegin(ReadOnlySpan<byte> source, out GroundAreaDespawnReader message)
        {
            message = default;
            var reader = new BitReader(source);
            if (reader.ReadBits(HotMessage.IdBits) != (uint)HotMessageId.GroundAreaDespawn)
                return false;
            int count = (int)reader.ReadBits(GroundAreaMessages.CountBits);
            if (reader.Failed || source.Length != GroundAreaMessages.DespawnSizeFor(count))
                return false;
            message._reader = reader;
            message._left = count;
            message.Count = count;

            GroundAreaDespawnReader scan = message;
            while (scan.TryRead(out _)) { }
            if (scan._left != 0 || !scan._reader.IsAtCleanEnd())
            {
                message = default;
                return false;
            }
            return true;
        }

        public bool TryRead(out GroundAreaDespawn despawn)
        {
            despawn = default;
            if (_left == 0 || _reader.Failed)
                return false;
            _left--;
            despawn.AreaId = _reader.ReadBits(GroundAreaMessages.AreaIdBits);
            despawn.Tick = _reader.ReadBits(GroundAreaMessages.TickBits);
            despawn.Reason = (GroundAreaDespawnReason)_reader.ReadBits(GroundAreaMessages.ReasonBits);
            if (!GroundAreaMessages.IsKnown(despawn.Reason))
                _reader.Fail();
            return !_reader.Failed;
        }
    }
}
