using System;

namespace Shared.Protocol
{
    /// <summary>Why a projectile ended (spec 3.2), carried by <see cref="HotMessageId.ProjectileDespawn"/>. Append only.</summary>
    public enum ProjectileDespawnReason : byte
    {
        None = 0,
        /// <summary>It hit its last target (<c>ProjectileDef.maxHits</c>).</summary>
        Hit = 1,
        /// <summary>It flew its whole range.</summary>
        Range = 2,
        /// <summary>A wall of the world grid stopped it.</summary>
        World = 3,
        /// <summary>Its owner left the world.</summary>
        OwnerGone = 4,
    }

    /// <summary>
    /// A projectile the player should draw (spec 3.2 spawn event: type, origin, direction, speed, start tick). The client
    /// rebuilds the shared <c>ProjectilePath</c> from it and the <c>ProjectileDef</c> its <see cref="DefId"/> names, so it
    /// draws the same straight path the server simulates; a late spawn is fast-forwarded from <see cref="StartTick"/>.
    /// Ground positions are whole cm on the MovementSim axes (X, Y), plus the height.
    /// </summary>
    public struct ProjectileSpawn
    {
        /// <summary>The projectile's own id (not an entity id), unique per world.</summary>
        public uint ProjectileId;
        /// <summary><c>ProjectileDef.netId</c>, 1 to <see cref="ProjectileMessages.MaxDefId"/>.</summary>
        public ushort DefId;
        public int XCm;
        public int YCm;
        public int HeightCm;
        /// <summary>Binary angle, sent with <see cref="ProjectileMessages.FacingBits"/> bits (the resolution FixedTrig uses).</summary>
        public ushort Facing;
        public int SpeedCmPerSec;
        public uint StartTick;
    }

    /// <summary>A projectile ended at the end of phase 4 of <see cref="Tick"/> (spec 3.2 hit and despawn events).</summary>
    public struct ProjectileDespawn
    {
        public uint ProjectileId;
        public uint Tick;
        public ProjectileDespawnReason Reason;
    }

    /// <summary>
    /// Layouts of the projectile hot messages on the unreliable channel (spec 1.7, 3.2), LSB first:
    /// <list type="bullet">
    /// <item><see cref="HotMessageId.ProjectileSpawn"/>: id 8, count 8; per spawn projectileId 32, defId 12, x 24, y 24,
    /// height 24 (signed whole cm: exactly the Q24.8 coordinate range), facing 10, speed 17 (cm/s), startTick 32 (175 bits).</item>
    /// <item><see cref="HotMessageId.ProjectileDespawn"/>: id 8, count 8; per despawn projectileId 32, tick 32, reason 3 (67 bits).</item>
    /// </list>
    /// Readers validate the whole packet before anything is read (exact length, known reasons, def id not 0, zero padding),
    /// so a client never applies half of a malformed message (R-2.5-04).
    /// </summary>
    public static class ProjectileMessages
    {
        public const int CountBits = 8;
        public const int MaxCount = (1 << CountBits) - 1;
        public const int ProjectileIdBits = 32;
        public const int DefIdBits = 12;
        public const int MaxDefId = (1 << DefIdBits) - 1;
        public const int PositionBits = 24;
        public const int FacingBits = 10;
        public const int SpeedBits = 17;
        public const int MaxSpeedCmPerSec = (1 << SpeedBits) - 1;
        public const int TickBits = 32;
        public const int ReasonBits = 3;

        public const int HeaderBits = HotMessage.IdBits + CountBits;
        public const int SpawnBits = ProjectileIdBits + DefIdBits + 3 * PositionBits + FacingBits + SpeedBits + TickBits;
        public const int DespawnBits = ProjectileIdBits + TickBits + ReasonBits;

        public static int SpawnSizeFor(int count) => (HeaderBits + count * SpawnBits + 7) >> 3;

        public static int DespawnSizeFor(int count) => (HeaderBits + count * DespawnBits + 7) >> 3;

        /// <summary>How many spawns fit in <paramref name="bytes"/> (capped at <see cref="MaxCount"/>).</summary>
        public static int SpawnsThatFit(int bytes) => Math.Min(MaxCount, Math.Max(0, (bytes * 8 - HeaderBits) / SpawnBits));

        /// <summary>How many despawns fit in <paramref name="bytes"/> (capped at <see cref="MaxCount"/>).</summary>
        public static int DespawnsThatFit(int bytes) => Math.Min(MaxCount, Math.Max(0, (bytes * 8 - HeaderBits) / DespawnBits));

        internal static bool IsKnown(ProjectileDespawnReason reason) =>
            reason >= ProjectileDespawnReason.Hit && reason <= ProjectileDespawnReason.OwnerGone;
    }

    /// <summary>Writes one <see cref="HotMessageId.ProjectileSpawn"/> message into a caller-owned buffer without allocating.</summary>
    public ref struct ProjectileSpawnWriter
    {
        private BitWriter _writer;
        private int _count;

        public ProjectileSpawnWriter(Span<byte> destination)
        {
            if (destination.Length < ProjectileMessages.SpawnSizeFor(0))
                throw new ArgumentException("Destination is too small for the message header.", nameof(destination));
            _writer = new BitWriter(destination);
            _count = 0;
            _writer.WriteBits((uint)HotMessageId.ProjectileSpawn, HotMessage.IdBits);
            _writer.WriteBits(0, ProjectileMessages.CountBits);
        }

        public int Count => _count;

        /// <summary>False when the spawn does not fit (the message is left as it was). Throws on values the layout cannot carry.</summary>
        public bool TryAdd(in ProjectileSpawn spawn)
        {
            if (spawn.DefId == 0 || spawn.DefId > ProjectileMessages.MaxDefId)
                throw new ArgumentOutOfRangeException(nameof(spawn), $"DefId is 1 to {ProjectileMessages.MaxDefId}.");
            if (spawn.SpeedCmPerSec <= 0 || spawn.SpeedCmPerSec > ProjectileMessages.MaxSpeedCmPerSec)
                throw new ArgumentOutOfRangeException(nameof(spawn), "Speed does not fit the layout.");
            CheckPosition(spawn.XCm);
            CheckPosition(spawn.YCm);
            CheckPosition(spawn.HeightCm);
            if (_count == ProjectileMessages.MaxCount)
                return false;

            int start = _writer.BitPosition;
            _writer.WriteBits(spawn.ProjectileId, ProjectileMessages.ProjectileIdBits);
            _writer.WriteBits(spawn.DefId, ProjectileMessages.DefIdBits);
            _writer.WriteSigned(spawn.XCm, ProjectileMessages.PositionBits);
            _writer.WriteSigned(spawn.YCm, ProjectileMessages.PositionBits);
            _writer.WriteSigned(spawn.HeightCm, ProjectileMessages.PositionBits);
            _writer.WriteBits(Quantize.AngleToWire(spawn.Facing, ProjectileMessages.FacingBits), ProjectileMessages.FacingBits);
            _writer.WriteBits((uint)spawn.SpeedCmPerSec, ProjectileMessages.SpeedBits);
            _writer.WriteBits(spawn.StartTick, ProjectileMessages.TickBits);
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
            _writer.PatchBits(HotMessage.IdBits, (uint)_count, ProjectileMessages.CountBits);
            return _writer.Finish();
        }

        private static void CheckPosition(int cm)
        {
            if (cm < BitMath.SignedMin(ProjectileMessages.PositionBits) || cm > BitMath.SignedMax(ProjectileMessages.PositionBits))
                throw new ArgumentOutOfRangeException(nameof(cm), "Position does not fit the layout.");
        }
    }

    /// <summary>Reads a <see cref="HotMessageId.ProjectileSpawn"/> message without allocating.</summary>
    public ref struct ProjectileSpawnReader
    {
        private BitReader _reader;
        private int _left;

        public int Count { get; private set; }

        public static bool TryBegin(ReadOnlySpan<byte> source, out ProjectileSpawnReader message)
        {
            message = default;
            var reader = new BitReader(source);
            if (reader.ReadBits(HotMessage.IdBits) != (uint)HotMessageId.ProjectileSpawn)
                return false;
            int count = (int)reader.ReadBits(ProjectileMessages.CountBits);
            if (reader.Failed || source.Length != ProjectileMessages.SpawnSizeFor(count))
                return false;
            message._reader = reader;
            message._left = count;
            message.Count = count;

            ProjectileSpawnReader scan = message; // a copy: validating does not move the real reader
            while (scan.TryRead(out _)) { }
            if (scan._left != 0 || !scan._reader.IsAtCleanEnd())
            {
                message = default;
                return false;
            }
            return true;
        }

        public bool TryRead(out ProjectileSpawn spawn)
        {
            spawn = default;
            if (_left == 0 || _reader.Failed)
                return false;
            _left--;
            spawn.ProjectileId = _reader.ReadBits(ProjectileMessages.ProjectileIdBits);
            spawn.DefId = (ushort)_reader.ReadBits(ProjectileMessages.DefIdBits);
            spawn.XCm = _reader.ReadSigned(ProjectileMessages.PositionBits);
            spawn.YCm = _reader.ReadSigned(ProjectileMessages.PositionBits);
            spawn.HeightCm = _reader.ReadSigned(ProjectileMessages.PositionBits);
            spawn.Facing = Quantize.AngleFromWire(_reader.ReadBits(ProjectileMessages.FacingBits), ProjectileMessages.FacingBits);
            spawn.SpeedCmPerSec = (int)_reader.ReadBits(ProjectileMessages.SpeedBits);
            spawn.StartTick = _reader.ReadBits(ProjectileMessages.TickBits);
            if (spawn.DefId == 0 || spawn.SpeedCmPerSec == 0)
                _reader.Fail();
            return !_reader.Failed;
        }
    }

    /// <summary>Writes one <see cref="HotMessageId.ProjectileDespawn"/> message into a caller-owned buffer without allocating.</summary>
    public ref struct ProjectileDespawnWriter
    {
        private BitWriter _writer;
        private int _count;

        public ProjectileDespawnWriter(Span<byte> destination)
        {
            if (destination.Length < ProjectileMessages.DespawnSizeFor(0))
                throw new ArgumentException("Destination is too small for the message header.", nameof(destination));
            _writer = new BitWriter(destination);
            _count = 0;
            _writer.WriteBits((uint)HotMessageId.ProjectileDespawn, HotMessage.IdBits);
            _writer.WriteBits(0, ProjectileMessages.CountBits);
        }

        public int Count => _count;

        public bool TryAdd(in ProjectileDespawn despawn)
        {
            if (!ProjectileMessages.IsKnown(despawn.Reason))
                throw new ArgumentOutOfRangeException(nameof(despawn), "Unknown despawn reason.");
            if (_count == ProjectileMessages.MaxCount)
                return false;
            int start = _writer.BitPosition;
            _writer.WriteBits(despawn.ProjectileId, ProjectileMessages.ProjectileIdBits);
            _writer.WriteBits(despawn.Tick, ProjectileMessages.TickBits);
            _writer.WriteBits((uint)despawn.Reason, ProjectileMessages.ReasonBits);
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
            _writer.PatchBits(HotMessage.IdBits, (uint)_count, ProjectileMessages.CountBits);
            return _writer.Finish();
        }
    }

    /// <summary>Reads a <see cref="HotMessageId.ProjectileDespawn"/> message without allocating.</summary>
    public ref struct ProjectileDespawnReader
    {
        private BitReader _reader;
        private int _left;

        public int Count { get; private set; }

        public static bool TryBegin(ReadOnlySpan<byte> source, out ProjectileDespawnReader message)
        {
            message = default;
            var reader = new BitReader(source);
            if (reader.ReadBits(HotMessage.IdBits) != (uint)HotMessageId.ProjectileDespawn)
                return false;
            int count = (int)reader.ReadBits(ProjectileMessages.CountBits);
            if (reader.Failed || source.Length != ProjectileMessages.DespawnSizeFor(count))
                return false;
            message._reader = reader;
            message._left = count;
            message.Count = count;

            ProjectileDespawnReader scan = message;
            while (scan.TryRead(out _)) { }
            if (scan._left != 0 || !scan._reader.IsAtCleanEnd())
            {
                message = default;
                return false;
            }
            return true;
        }

        public bool TryRead(out ProjectileDespawn despawn)
        {
            despawn = default;
            if (_left == 0 || _reader.Failed)
                return false;
            _left--;
            despawn.ProjectileId = _reader.ReadBits(ProjectileMessages.ProjectileIdBits);
            despawn.Tick = _reader.ReadBits(ProjectileMessages.TickBits);
            despawn.Reason = (ProjectileDespawnReason)_reader.ReadBits(ProjectileMessages.ReasonBits);
            if (!ProjectileMessages.IsKnown(despawn.Reason))
                _reader.Fail();
            return !_reader.Failed;
        }
    }
}
