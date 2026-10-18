using System;
using Shared.Simulation.Actions;

namespace Shared.Protocol
{
    /// <summary>Snapshot flags (spec 1.7).</summary>
    [Flags]
    public enum EntityFlags : byte
    {
        None = 0,
        Armed = 1,
        /// <summary>
        /// Comes from honor. The caller must not set it for a disguised player unless the observer sees through the
        /// disguise (spec 1.7: clients never receive other players' honor when disguised).
        /// </summary>
        RedName = 2,
        Disguised = 4,
        Mounted = 8,
        Pet = 16,
    }

    /// <summary>What kind of entity a spawn creates (spec 1.5, GameServer/Entities). 0 is never valid.</summary>
    public enum EntityKind : byte
    {
        None = 0,
        Player = 1,
        Monster = 2,
        Npc = 3,
        Projectile = 4,
        LootBag = 5,
        ResourceNode = 6,
        Portal = 7,
        ShopStall = 8,
    }

    public struct SnapshotHeader
    {
        public uint ServerTick;
        /// <summary>False until the server has processed an input from this client.</summary>
        public bool HasInputAck;
        /// <summary>Newest input sequence the server has applied; the client replays the frames after it (spec 2.2).</summary>
        public ushort LastInputSequence;
        /// <summary>Origin the entity positions are relative to, in centimeters (spec 2.3).</summary>
        public int OriginXCm;
        public int OriginYCm;
        public int OriginZCm;
        /// <summary>False when the observer's entity has no resource meters (it cannot fight).</summary>
        public bool HasOwnResources;
        /// <summary>The observer's exact meters for its HUD (spec 9.4); other entities only get an HP bucket.</summary>
        public OwnResources Own;
    }

    /// <summary>
    /// The observer's own HP, MP and stamina with their maxima, in whole points (spec 9.4: the resource panel shows exact
    /// values for the own entity). Each is 0 to <see cref="Snapshot.MaxResourceValue"/>, the current one never above its max.
    /// </summary>
    public struct OwnResources
    {
        public int Hp;
        public int MaxHp;
        public int Mp;
        public int MaxMp;
        public int Stamina;
        public int MaxStamina;

        public bool IsValid =>
            Valid(Hp, MaxHp) && Valid(Mp, MaxMp) && Valid(Stamina, MaxStamina);

        private static bool Valid(int current, int max) => current >= 0 && current <= max;
    }

    /// <summary>An entity entering the observer's interest area (spec 2.3). Its state follows in the same snapshot.</summary>
    public struct SpawnEvent
    {
        public uint EntityId;
        public EntityKind Kind;
        /// <summary>Kind-specific definition id (for example the monster definition); 0 when the kind has none.</summary>
        public ushort DefId;
    }

    /// <summary>
    /// One entity in a snapshot (spec 1.7). Positions are 3D because walkable surfaces overlap (bridges, floors,
    /// caves); movement itself stays 2D. Names never travel here: identity goes through DisplayIdentity (rule 8).
    /// </summary>
    public struct EntitySnapshot
    {
        public uint EntityId;
        public int XCm;
        public int YCm;
        public int ZCm;
        /// <summary>Binary angle (65536 = one turn), sent with <see cref="Snapshot.FacingBits"/> bits.</summary>
        public ushort Facing;
        /// <summary>
        /// Action state code, a <see cref="ActionState"/> (backlog D2): 0 idle, 1-4 the phases of an action, 5 flinched,
        /// 6 staggered, 7 dead. Higher codes are refused.
        /// </summary>
        public byte State;
        /// <summary>States 1-4: the running action's <c>ActionDef.NetId</c> (1 to 4095). 0 otherwise.</summary>
        public ushort ActionId;
        /// <summary>States 1-6: ticks since the action or interrupt started, capped at 255. 0 otherwise.</summary>
        public byte StateAge;
        /// <summary>States 5-6: length of the flinch or stagger in ticks (1 to 255, capped). 0 otherwise.</summary>
        public byte StateDurationTicks;
        /// <summary>0 dead to <see cref="Snapshot.TopHpBucket"/> full; see <see cref="Quantize.Bucket"/>.</summary>
        public byte HpBucket;
        public EntityFlags Flags;
    }

    /// <summary>
    /// Per-observer snapshot (spec 1.7) on the unreliable channel. Every snapshot lists every entity in interest
    /// (no delta against acked state yet), so a client that lost a spawn can still place the entity.
    /// Layout, LSB first: id 8, serverTick 32, hasInputAck 1, lastInputSequence 16, originX/Y/Z 32 each,
    /// despawnCount 10, spawnCount 10, entityCount 10, hasOwnResources 1 (then hp, maxHp, mp, maxMp, stamina, maxStamina,
    /// 31 bits each); despawns (entityId 32); spawns (entityId 32, kind 4, defId 16);
    /// entities (entityId 32, x 21, y 18, z 21 signed offsets in cm, facing 10, state 8; then for states 1-4 actionId 12
    /// and age 8, for states 5-6 age 8 and durationTicks 8; then hpBucket 6, flags 5). An entity is 121, 137 or 141 bits;
    /// <see cref="SizeFor"/> is the size with every entity idle, the smallest a snapshot with these counts can be.
    /// </summary>
    public static class Snapshot
    {
        public const int CountBits = 10;
        public const int MaxCount = (1 << CountBits) - 1;
        public const int EntityIdBits = 32;
        public const int KindBits = 4;
        public const int DefIdBits = 16;
        /// <summary>About +-10.4 km around the origin.</summary>
        public const int HorizontalOffsetBits = 21;
        /// <summary>About +-1.3 km around the origin.</summary>
        public const int VerticalOffsetBits = 18;
        public const int FacingBits = 10;
        public const int StateBits = 8;
        /// <summary>
        /// HP bucket of every entity (spec 1.7, 9.4): a protocol constant, not config, because both sides must agree on
        /// the layout. 6 bits = 64 steps (0 dead, 63 full), finer than any M1 HP bar is wide.
        /// </summary>
        public const int HpBucketBits = 6;
        public const int TopHpBucket = (1 << HpBucketBits) - 1;
        /// <summary>Bits of each own resource value: any non-negative int.</summary>
        public const int ResourceBits = 31;
        public const int MaxResourceValue = int.MaxValue;
        /// <summary>Bits the own resources add to the header when present.</summary>
        public const int OwnResourcesBits = 6 * ResourceBits;
        public const int FlagsBits = 5;
        public const int ActionIdBits = 12;
        public const int StateAgeBits = 8;
        public const int StateDurationBits = 8;
        /// <summary>Extra bits of an entity in an action phase (states 1-4).</summary>
        public const int ActionStateBits = ActionIdBits + StateAgeBits;
        /// <summary>Extra bits of a flinched or staggered entity (states 5-6).</summary>
        public const int InterruptStateBits = StateAgeBits + StateDurationBits;

        /// <summary>The header without own resources (<see cref="OwnResourcesBits"/> more when present).</summary>
        public const int HeaderBits = HotMessage.IdBits + 32 + 1 + 16 + 3 * 32 + 3 * CountBits + 1;
        public const int DespawnBits = EntityIdBits;
        public const int SpawnBits = EntityIdBits + KindBits + DefIdBits;
        public const int EntityBits = EntityIdBits + 2 * HorizontalOffsetBits + VerticalOffsetBits + FacingBits
                                        + StateBits + HpBucketBits + FlagsBits;

        public static int HeaderSize => (HeaderBits + 7) >> 3;

        /// <summary>Bytes of a header with the observer's own resources.</summary>
        public static int HeaderSizeWithOwnResources => (HeaderBits + OwnResourcesBits + 7) >> 3;

        /// <summary>Size with every entity idle (<see cref="EntityBits"/> each); others add <see cref="ExtraStateBits"/>.</summary>
        public static int SizeFor(int despawns, int spawns, int entities) =>
            (int)((HeaderBits + (long)despawns * DespawnBits + (long)spawns * SpawnBits + (long)entities * EntityBits + 7) >> 3);

        /// <summary>Bits an entity in <paramref name="state"/> adds to <see cref="EntityBits"/>; -1 for an unknown code.</summary>
        public static int ExtraStateBits(byte state)
        {
            switch ((ActionState)state)
            {
                case ActionState.Idle:
                case ActionState.Dead:
                    return 0;
                case ActionState.Startup:
                case ActionState.Channel:
                case ActionState.Active:
                case ActionState.Recovery:
                    return ActionStateBits;
                case ActionState.Flinched:
                case ActionState.Staggered:
                    return InterruptStateBits;
                default:
                    return -1;
            }
        }

        /// <summary>True when every coordinate of <paramref name="entity"/> fits around the header's origin.</summary>
        public static bool FitsOrigin(in SnapshotHeader header, in EntitySnapshot entity) =>
            Quantize.TryOffset(entity.XCm, header.OriginXCm, HorizontalOffsetBits, out _)
            && Quantize.TryOffset(entity.YCm, header.OriginYCm, VerticalOffsetBits, out _)
            && Quantize.TryOffset(entity.ZCm, header.OriginZCm, HorizontalOffsetBits, out _);
    }

    /// <summary>
    /// Writes one snapshot into a caller-owned buffer without allocating. Add despawns, then spawns, then entities.
    /// A TryAdd that does not fit returns false and leaves the snapshot as it was, so the caller can send what fits.
    /// </summary>
    public ref struct SnapshotWriter
    {
        private BitWriter _writer;
        private readonly SnapshotHeader _header;
        private readonly int _countsPosition;
        private int _despawns;
        private int _spawns;
        private int _entities;
        private int _section;

        public SnapshotWriter(Span<byte> destination, in SnapshotHeader header)
        {
            if (destination.Length < (header.HasOwnResources ? Snapshot.HeaderSizeWithOwnResources : Snapshot.HeaderSize))
                throw new ArgumentException("Destination is too small for a snapshot header.", nameof(destination));
            if (header.HasOwnResources && !header.Own.IsValid)
                throw new ArgumentOutOfRangeException(nameof(header), "Own resources must be 0 to their max.");

            _writer = new BitWriter(destination);
            _header = header;
            _despawns = 0;
            _spawns = 0;
            _entities = 0;
            _section = 0;

            _writer.WriteBits((uint)HotMessageId.Snapshot, HotMessage.IdBits);
            _writer.WriteBits(header.ServerTick, 32);
            _writer.WriteBool(header.HasInputAck);
            _writer.WriteBits(header.LastInputSequence, 16);
            _writer.WriteSigned(header.OriginXCm, 32);
            _writer.WriteSigned(header.OriginYCm, 32);
            _writer.WriteSigned(header.OriginZCm, 32);
            _countsPosition = _writer.BitPosition;
            _writer.WriteBits(0, Snapshot.CountBits);
            _writer.WriteBits(0, Snapshot.CountBits);
            _writer.WriteBits(0, Snapshot.CountBits);
            _writer.WriteBool(header.HasOwnResources);
            if (header.HasOwnResources)
            {
                var own = header.Own;
                _writer.WriteBits((uint)own.Hp, Snapshot.ResourceBits);
                _writer.WriteBits((uint)own.MaxHp, Snapshot.ResourceBits);
                _writer.WriteBits((uint)own.Mp, Snapshot.ResourceBits);
                _writer.WriteBits((uint)own.MaxMp, Snapshot.ResourceBits);
                _writer.WriteBits((uint)own.Stamina, Snapshot.ResourceBits);
                _writer.WriteBits((uint)own.MaxStamina, Snapshot.ResourceBits);
            }
        }

        public int DespawnCount => _despawns;
        public int SpawnCount => _spawns;
        public int EntityCount => _entities;

        public bool TryAddDespawn(uint entityId)
        {
            EnterSection(0);
            if (_despawns == Snapshot.MaxCount)
                return false;
            int start = _writer.BitPosition;
            _writer.WriteBits(entityId, Snapshot.EntityIdBits);
            if (!Commit(start))
                return false;
            _despawns++;
            return true;
        }

        public bool TryAddSpawn(in SpawnEvent spawn)
        {
            EnterSection(1);
            if (spawn.Kind == EntityKind.None || spawn.Kind > EntityKind.ShopStall)
                throw new ArgumentOutOfRangeException(nameof(spawn), "Unknown entity kind.");
            if (_spawns == Snapshot.MaxCount)
                return false;
            int start = _writer.BitPosition;
            _writer.WriteBits(spawn.EntityId, Snapshot.EntityIdBits);
            _writer.WriteBits((uint)spawn.Kind, Snapshot.KindBits);
            _writer.WriteBits(spawn.DefId, Snapshot.DefIdBits);
            if (!Commit(start))
                return false;
            _spawns++;
            return true;
        }

        /// <summary>Throws when a coordinate does not fit around the origin; check with <see cref="Snapshot.FitsOrigin"/>.</summary>
        public bool TryAddEntity(in EntitySnapshot entity)
        {
            EnterSection(2);
            if (!Quantize.TryOffset(entity.XCm, _header.OriginXCm, Snapshot.HorizontalOffsetBits, out int x)
                || !Quantize.TryOffset(entity.YCm, _header.OriginYCm, Snapshot.VerticalOffsetBits, out int y)
                || !Quantize.TryOffset(entity.ZCm, _header.OriginZCm, Snapshot.HorizontalOffsetBits, out int z))
                throw new ArgumentOutOfRangeException(nameof(entity), "Position is too far from the snapshot origin.");
            if (entity.HpBucket > Snapshot.TopHpBucket)
                throw new ArgumentOutOfRangeException(nameof(entity), $"HpBucket is 0 to {Snapshot.TopHpBucket}.");
            if ((uint)entity.Flags >> Snapshot.FlagsBits != 0)
                throw new ArgumentOutOfRangeException(nameof(entity), "Unknown entity flags.");
            int extra = Snapshot.ExtraStateBits(entity.State);
            if (extra < 0)
                throw new ArgumentOutOfRangeException(nameof(entity), "Unknown action state code.");
            if (extra == Snapshot.ActionStateBits && (entity.ActionId == 0 || entity.ActionId >> Snapshot.ActionIdBits != 0))
                throw new ArgumentOutOfRangeException(nameof(entity), "An action state needs an ActionId of 1 to 4095.");
            if (extra == Snapshot.InterruptStateBits && entity.StateDurationTicks == 0)
                throw new ArgumentOutOfRangeException(nameof(entity), "A flinch or stagger needs a duration.");
            if (_entities == Snapshot.MaxCount)
                return false;

            int start = _writer.BitPosition;
            _writer.WriteBits(entity.EntityId, Snapshot.EntityIdBits);
            _writer.WriteSigned(x, Snapshot.HorizontalOffsetBits);
            _writer.WriteSigned(y, Snapshot.VerticalOffsetBits);
            _writer.WriteSigned(z, Snapshot.HorizontalOffsetBits);
            _writer.WriteBits(Quantize.AngleToWire(entity.Facing, Snapshot.FacingBits), Snapshot.FacingBits);
            _writer.WriteBits(entity.State, Snapshot.StateBits);
            if (extra == Snapshot.ActionStateBits)
            {
                _writer.WriteBits(entity.ActionId, Snapshot.ActionIdBits);
                _writer.WriteBits(entity.StateAge, Snapshot.StateAgeBits);
            }
            else if (extra == Snapshot.InterruptStateBits)
            {
                _writer.WriteBits(entity.StateAge, Snapshot.StateAgeBits);
                _writer.WriteBits(entity.StateDurationTicks, Snapshot.StateDurationBits);
            }
            _writer.WriteBits(entity.HpBucket, Snapshot.HpBucketBits);
            _writer.WriteBits((uint)entity.Flags, Snapshot.FlagsBits);
            if (!Commit(start))
                return false;
            _entities++;
            return true;
        }

        /// <summary>Writes the counts and returns the snapshot length in bytes.</summary>
        public int Finish()
        {
            _writer.PatchBits(_countsPosition, (uint)_despawns, Snapshot.CountBits);
            _writer.PatchBits(_countsPosition + Snapshot.CountBits, (uint)_spawns, Snapshot.CountBits);
            _writer.PatchBits(_countsPosition + 2 * Snapshot.CountBits, (uint)_entities, Snapshot.CountBits);
            return _writer.Finish();
        }

        private void EnterSection(int section)
        {
            if (section < _section)
                throw new InvalidOperationException("Add despawns, then spawns, then entities.");
            _section = section;
        }

        private bool Commit(int start)
        {
            if (!_writer.Overflowed)
                return true;
            _writer.Rewind(start);
            return false;
        }
    }

    /// <summary>
    /// Reads a snapshot without allocating. <see cref="TryBegin"/> validates the whole packet first (exact length,
    /// counts, entity kinds, padding), so a client never applies half of a malformed snapshot (R-2.5-04).
    /// Then read despawns, spawns and entities in that order; each TryRead returns false when its section is done.
    /// </summary>
    public ref struct SnapshotReader
    {
        private BitReader _reader;
        private int _despawnsLeft;
        private int _spawnsLeft;
        private int _entitiesLeft;
        private SnapshotHeader _header;

        public SnapshotHeader Header => _header;
        public int DespawnCount { get; private set; }
        public int SpawnCount { get; private set; }
        public int EntityCount { get; private set; }

        public static bool TryBegin(ReadOnlySpan<byte> source, out SnapshotReader snapshot)
        {
            snapshot = default;
            var reader = new BitReader(source);
            if (reader.ReadBits(HotMessage.IdBits) != (uint)HotMessageId.Snapshot)
                return false;

            SnapshotHeader header;
            header.ServerTick = reader.ReadBits(32);
            header.HasInputAck = reader.ReadBool();
            header.LastInputSequence = (ushort)reader.ReadBits(16);
            header.OriginXCm = reader.ReadSigned(32);
            header.OriginYCm = reader.ReadSigned(32);
            header.OriginZCm = reader.ReadSigned(32);
            int despawns = (int)reader.ReadBits(Snapshot.CountBits);
            int spawns = (int)reader.ReadBits(Snapshot.CountBits);
            int entities = (int)reader.ReadBits(Snapshot.CountBits);
            header.HasOwnResources = reader.ReadBool();
            header.Own = default;
            if (header.HasOwnResources)
            {
                header.Own.Hp = (int)reader.ReadBits(Snapshot.ResourceBits);
                header.Own.MaxHp = (int)reader.ReadBits(Snapshot.ResourceBits);
                header.Own.Mp = (int)reader.ReadBits(Snapshot.ResourceBits);
                header.Own.MaxMp = (int)reader.ReadBits(Snapshot.ResourceBits);
                header.Own.Stamina = (int)reader.ReadBits(Snapshot.ResourceBits);
                header.Own.MaxStamina = (int)reader.ReadBits(Snapshot.ResourceBits);
                if (!header.Own.IsValid)
                    return false;
            }
            // Entities in an action carry extra bits, so the exact length is checked by the scan below (clean end).
            if (reader.Failed || source.Length < Snapshot.SizeFor(despawns, spawns, entities))
                return false;

            snapshot._reader = reader;
            snapshot._header = header;
            snapshot._despawnsLeft = despawns;
            snapshot._spawnsLeft = spawns;
            snapshot._entitiesLeft = entities;
            snapshot.DespawnCount = despawns;
            snapshot.SpawnCount = spawns;
            snapshot.EntityCount = entities;

            // Validation pass on a copy: the reader is a value, so this does not move the real one.
            SnapshotReader scan = snapshot;
            while (scan.TryReadDespawn(out _)) { }
            while (scan.TryReadSpawn(out _)) { }
            while (scan.TryReadEntity(out _)) { }
            if (!scan._reader.IsAtCleanEnd())
            {
                snapshot = default;
                return false;
            }
            return true;
        }

        public bool TryReadDespawn(out uint entityId)
        {
            entityId = 0;
            if (_despawnsLeft == 0 || _reader.Failed)
                return false;
            _despawnsLeft--;
            entityId = _reader.ReadBits(Snapshot.EntityIdBits);
            return !_reader.Failed;
        }

        /// <summary>Skips any despawns not read yet.</summary>
        public bool TryReadSpawn(out SpawnEvent spawn)
        {
            spawn = default;
            while (TryReadDespawn(out _)) { }
            if (_spawnsLeft == 0 || _reader.Failed)
                return false;
            _spawnsLeft--;
            spawn.EntityId = _reader.ReadBits(Snapshot.EntityIdBits);
            spawn.Kind = (EntityKind)_reader.ReadBits(Snapshot.KindBits);
            spawn.DefId = (ushort)_reader.ReadBits(Snapshot.DefIdBits);
            if (spawn.Kind == EntityKind.None || spawn.Kind > EntityKind.ShopStall)
                _reader.Fail();
            return !_reader.Failed;
        }

        /// <summary>Skips any despawns and spawns not read yet.</summary>
        public bool TryReadEntity(out EntitySnapshot entity)
        {
            entity = default;
            while (TryReadSpawn(out _)) { }
            if (_entitiesLeft == 0 || _reader.Failed)
                return false;
            _entitiesLeft--;
            entity.EntityId = _reader.ReadBits(Snapshot.EntityIdBits);
            entity.XCm = ReadPosition(_header.OriginXCm, Snapshot.HorizontalOffsetBits);
            entity.YCm = ReadPosition(_header.OriginYCm, Snapshot.VerticalOffsetBits);
            entity.ZCm = ReadPosition(_header.OriginZCm, Snapshot.HorizontalOffsetBits);
            entity.Facing = Quantize.AngleFromWire(_reader.ReadBits(Snapshot.FacingBits), Snapshot.FacingBits);
            entity.State = (byte)_reader.ReadBits(Snapshot.StateBits);
            int extra = Snapshot.ExtraStateBits(entity.State);
            if (extra == Snapshot.ActionStateBits)
            {
                entity.ActionId = (ushort)_reader.ReadBits(Snapshot.ActionIdBits);
                entity.StateAge = (byte)_reader.ReadBits(Snapshot.StateAgeBits);
                if (entity.ActionId == 0)
                    _reader.Fail();
            }
            else if (extra == Snapshot.InterruptStateBits)
            {
                entity.StateAge = (byte)_reader.ReadBits(Snapshot.StateAgeBits);
                entity.StateDurationTicks = (byte)_reader.ReadBits(Snapshot.StateDurationBits);
                if (entity.StateDurationTicks == 0)
                    _reader.Fail();
            }
            else if (extra < 0)
            {
                _reader.Fail();
            }
            entity.HpBucket = (byte)_reader.ReadBits(Snapshot.HpBucketBits);
            entity.Flags = (EntityFlags)_reader.ReadBits(Snapshot.FlagsBits);
            return !_reader.Failed;
        }

        /// <summary>A position that leaves the int range can only come from a forged packet.</summary>
        private int ReadPosition(int originCm, int bitCount)
        {
            long position = (long)originCm + _reader.ReadSigned(bitCount);
            if (position < int.MinValue || position > int.MaxValue)
            {
                _reader.Fail();
                return 0;
            }
            return (int)position;
        }
    }
}
