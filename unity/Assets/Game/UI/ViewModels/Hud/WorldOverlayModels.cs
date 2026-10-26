#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.UI.ViewModels.Hud
{
    public enum DamageNumberStyle : byte
    {
        Normal,
        /// <summary>A critical hit (spec 9.3 "crit style").</summary>
        Crit,
        Heal,
        /// <summary>A word instead of a number: dodged, parried.</summary>
        Word,
    }

    public struct DamageNumber
    {
        public bool Active;
        public uint TargetId;
        public string Text;
        public DamageNumberStyle Style;
        public double StartMs;
    }

    /// <summary>
    /// Floating damage numbers (spec 9.3 <c>damage_numbers</c>, 9.9: pooled). A fixed pool: a new number past the pool
    /// reuses the oldest. Allocates nothing after construction.
    /// </summary>
    public sealed class DamageNumberPool
    {
        private readonly DamageNumber[] _items;

        public DamageNumberPool(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new DamageNumber[capacity];
        }

        /// <summary>A slot was (re)filled; the presenter restarts its element.</summary>
        public event Action<int>? Spawned;

        /// <summary>A slot was freed; the presenter hides its element.</summary>
        public event Action<int>? Expired;

        public int Capacity => _items.Length;

        public int ActiveCount { get; private set; }

        /// <summary>Numbers shown since the start, for checks (QA smoke).</summary>
        public long TotalSpawned { get; private set; }

        public ref readonly DamageNumber this[int index] => ref _items[index];

        /// <summary>
        /// The HUD shows the hits that concern the player: dealt or taken (spec 9.3). Other players' fights stay quiet.
        /// </summary>
        public static bool Shows(in HitEntry hit, uint ownEntityId) =>
            hit.Kind != HitKind.None && ownEntityId != 0 && (hit.AttackerId == ownEntityId || hit.TargetId == ownEntityId);

        public int Add(uint targetId, string text, DamageNumberStyle style, double nowMs)
        {
            int index = -1;
            double oldest = double.MaxValue;
            for (int i = 0; i < _items.Length; i++)
            {
                if (!_items[i].Active)
                {
                    index = i;
                    break;
                }
                if (_items[i].StartMs < oldest)
                {
                    oldest = _items[i].StartMs;
                    index = i;
                }
            }
            if (!_items[index].Active)
                ActiveCount++;
            _items[index] = new DamageNumber { Active = true, TargetId = targetId, Text = text, Style = style, StartMs = nowMs };
            TotalSpawned++;
            Spawned?.Invoke(index);
            return index;
        }

        public void Expire(double nowMs, double lifetimeMs)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (_items[i].Active && nowMs - _items[i].StartMs >= lifetimeMs)
                    Free(i);
            }
        }

        public void Clear()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (_items[i].Active)
                    Free(i);
            }
        }

        private void Free(int index)
        {
            _items[index].Active = false;
            _items[index].Text = "";
            ActiveCount--;
            Expired?.Invoke(index);
        }
    }

    /// <summary>Cached decimal strings for damage and heal amounts, so a hit allocates no string (spec 9.9 performance).</summary>
    public static class NumberText
    {
        private const int Cached = 10_000;
        private static readonly string?[] Cache = new string?[Cached];

        private static readonly string?[] TenthsCache = new string?[Cached];

        public static string Of(int value)
        {
            if (value < 0 || value >= Cached)
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Cache[value] ??= value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>A time in tenths of a second as "1.5" (cast bar time left).</summary>
        public static string Tenths(int tenths)
        {
            if (tenths < 0 || tenths >= Cached)
                return (tenths / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return TenthsCache[tenths] ??= (tenths / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public enum PlateKind : byte
    {
        Player,
        Monster,
        /// <summary>A boss: name only; its HP shows in the boss bar (spec 9.3).</summary>
        Boss,
    }

    public struct NamePlate
    {
        public bool Active;
        public uint EntityId;
        public PlateKind Kind;

        /// <summary>The display name: <c>DisplayIdentity</c> from the server for players, <c>UiStrings</c> for monsters; empty until known.</summary>
        public string Name;

        public byte HpBucket;
        public EntityFlags Flags;

        /// <summary>The target lock holds this entity (R-2.2-06): the plate shows the lock marker.</summary>
        public bool Locked;

        /// <summary>A normal monster shows its HP bar while damaged or locked (spec 9.3 <c>monster_hp_bar</c>).</summary>
        public bool ShowsHpBar => Kind == PlateKind.Monster && (HpBucket < Snapshot.TopHpBucket || Locked);
    }

    /// <summary>
    /// Name plates of the remote characters in interest (spec 9.3 <c>nameplate</c>, 9.9: pooled). Entities past the pool
    /// get no plate. <see cref="PlateChanged"/> names the slot whose text, flags or HP bar changed.
    /// </summary>
    public sealed class NamePlateSet
    {
        private readonly NamePlate[] _plates;
        private readonly Dictionary<uint, int> _indexByEntity = new Dictionary<uint, int>();
        private uint _lockedId;

        public NamePlateSet(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _plates = new NamePlate[capacity];
        }

        public event Action<int>? PlateChanged;

        public int Capacity => _plates.Length;

        public int ActiveCount => _indexByEntity.Count;

        /// <summary>Entities that got no plate because the pool was full (a sign to raise <c>HudSettings.namePlatePool</c>).</summary>
        public long Overflows { get; private set; }

        public ref readonly NamePlate this[int index] => ref _plates[index];

        public bool TryGetIndex(uint entityId, out int index) => _indexByEntity.TryGetValue(entityId, out index);

        /// <summary>A plate for an entity that came into interest; false when the pool is full.</summary>
        public bool Add(uint entityId, PlateKind kind, string name, byte hpBucket, EntityFlags flags)
        {
            if (_indexByEntity.TryGetValue(entityId, out int existing))
            {
                ref var plate = ref _plates[existing];
                plate.Kind = kind;
                plate.Name = name;
                plate.HpBucket = hpBucket;
                plate.Flags = flags;
                PlateChanged?.Invoke(existing);
                return true;
            }
            for (int i = 0; i < _plates.Length; i++)
            {
                if (_plates[i].Active)
                    continue;
                _plates[i] = new NamePlate
                {
                    Active = true, EntityId = entityId, Kind = kind, Name = name, HpBucket = hpBucket, Flags = flags,
                    Locked = entityId == _lockedId,
                };
                _indexByEntity[entityId] = i;
                PlateChanged?.Invoke(i);
                return true;
            }
            Overflows++;
            return false;
        }

        public void SetName(uint entityId, string name)
        {
            if (!_indexByEntity.TryGetValue(entityId, out int index) || _plates[index].Name == name)
                return;
            _plates[index].Name = name;
            PlateChanged?.Invoke(index);
        }

        public void SetState(uint entityId, byte hpBucket, EntityFlags flags)
        {
            if (!_indexByEntity.TryGetValue(entityId, out int index))
                return;
            ref var plate = ref _plates[index];
            if (plate.HpBucket == hpBucket && plate.Flags == flags)
                return;
            plate.HpBucket = hpBucket;
            plate.Flags = flags;
            PlateChanged?.Invoke(index);
        }

        /// <summary>The target lock moved to <paramref name="entityId"/> (0: no lock): the old plate loses its marker, the new one gets it.</summary>
        public void SetLocked(uint entityId)
        {
            if (entityId == _lockedId)
                return;
            SetLockedFlag(_lockedId, false);
            _lockedId = entityId;
            SetLockedFlag(entityId, true);
        }

        private void SetLockedFlag(uint entityId, bool locked)
        {
            if (entityId == 0 || !_indexByEntity.TryGetValue(entityId, out int index))
                return;
            _plates[index].Locked = locked;
            PlateChanged?.Invoke(index);
        }

        public void Remove(uint entityId)
        {
            if (!_indexByEntity.TryGetValue(entityId, out int index))
                return;
            _indexByEntity.Remove(entityId);
            _plates[index] = default;
            PlateChanged?.Invoke(index);
        }

        public void Clear()
        {
            for (int i = 0; i < _plates.Length; i++)
            {
                if (!_plates[i].Active)
                    continue;
                _plates[i] = default;
                PlateChanged?.Invoke(i);
            }
            _indexByEntity.Clear();
            _lockedId = 0;
        }
    }

    public struct Telegraph
    {
        public uint EntityId;
        public int ActionNetId;

        /// <summary>Server tick the attack started at, and the tick its Active phase ends (spec 9.3: shown only meanwhile).</summary>
        public uint StartTick;

        public uint EndTick;
        public ParryColor Color;
    }

    /// <summary>
    /// Monster attacks to draw on the ground (spec 9.3 <c>telegraph</c>, R-3.1-05): one per attacking monster, from the
    /// attack's start until its Active phase ends, on the render clock. Kept in a list that only grows to the most attacks
    /// seen at once.
    /// </summary>
    public sealed class TelegraphSet
    {
        private readonly List<Telegraph> _items = new List<Telegraph>();

        public int Count => _items.Count;

        public Telegraph this[int index] => _items[index];

        /// <summary>Telegraphs started since the start, for checks (QA smoke).</summary>
        public long TotalStarted { get; private set; }

        /// <summary>A monster runs an attack: adds it, or replaces that monster's older attack.</summary>
        public void Upsert(uint entityId, int actionNetId, uint startTick, uint endTick, ParryColor color)
        {
            var telegraph = new Telegraph { EntityId = entityId, ActionNetId = actionNetId, StartTick = startTick, EndTick = endTick, Color = color };
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].EntityId != entityId)
                    continue;
                if (_items[i].StartTick != startTick || _items[i].ActionNetId != actionNetId)
                    TotalStarted++;
                _items[i] = telegraph;
                return;
            }
            _items.Add(telegraph);
            TotalStarted++;
        }

        /// <summary>Drops attacks whose Active phase the render clock has passed.</summary>
        public void Expire(double renderTick)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (renderTick >= _items[i].EndTick)
                    _items.RemoveAt(i);
            }
        }

        /// <summary>Whether a telegraph is drawn at <paramref name="renderTick"/> (it starts when the attack is drawn starting).</summary>
        public static bool Shows(in Telegraph telegraph, double renderTick) =>
            renderTick >= telegraph.StartTick && renderTick < telegraph.EndTick;

        /// <summary>
        /// The monster's attack stopped early at <paramref name="tick"/> (a flinch, a stagger, death): it is drawn until the
        /// render clock reaches that tick, like the attack itself.
        /// </summary>
        public void EndAt(uint entityId, uint tick)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].EntityId != entityId || (int)(tick - _items[i].EndTick) >= 0)
                    continue;
                var telegraph = _items[i];
                telegraph.EndTick = tick;
                _items[i] = telegraph;
            }
        }

        public void Remove(uint entityId)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].EntityId == entityId)
                    _items.RemoveAt(i);
            }
        }

        public void Clear() => _items.Clear();
    }

    /// <summary>
    /// The pickup prompt (spec 9.3 <c>pickup_prompt</c>, backlog G): "Press G to pick up" at the ground drop the pickup key
    /// asks for. The world client names that drop with the server's range rule (<c>ClientInventory.NearestDrop</c>); the
    /// prompt adds that the player is alive, since the server refuses every pickup of a dead player.
    /// </summary>
    public sealed class PickupPromptViewModel : ViewModel
    {
        public bool Visible { get; private set; }

        /// <summary>The drop the prompt stands at; 0 while hidden.</summary>
        public uint DropId { get; private set; }

        /// <summary>The pickup key as the player reads it, from its binding (a rebind changes the prompt).</summary>
        public string KeyName { get; private set; } = "";

        /// <summary>The show rule: a drop in range (<paramref name="targetDropId"/> not 0) and the own character alive.</summary>
        public static bool Shows(uint targetDropId, bool ownAlive) => targetDropId != 0 && ownAlive;

        public void Set(uint targetDropId, bool ownAlive, string keyName)
        {
            bool visible = Shows(targetDropId, ownAlive);
            uint dropId = visible ? targetDropId : 0;
            if (visible == Visible && dropId == DropId && (!visible || keyName == KeyName))
                return;
            Visible = visible;
            DropId = dropId;
            if (visible)
                KeyName = keyName;
            NotifyChanged();
        }

        public void Clear() => Set(0, false, KeyName);
    }
}
