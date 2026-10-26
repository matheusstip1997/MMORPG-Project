#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// A ground area the client draws (spec 3.1 <c>GroundArea</c> effects, protocol 9): its effect row found from the spawn's
    /// action and effect index in the shared tables, with the hit shapes the server tests against, placed at the spawn's
    /// origin and facing.
    /// </summary>
    public sealed class ClientGroundArea
    {
        internal ClientGroundArea(in GroundAreaSpawn spawn, ActionEffectDef effect, IReadOnlyList<HitShapeDef> shapes, int durationTicks)
        {
            Id = spawn.AreaId;
            Effect = effect;
            Shapes = shapes;
            XCm = spawn.XCm;
            YCm = spawn.YCm;
            HeightCm = spawn.HeightCm;
            Facing = spawn.Facing;
            StartTick = spawn.StartTick;
            DurationTicks = durationTicks;
        }

        public uint Id { get; }

        /// <summary>The GroundArea effect row (duration, element, targets, <c>visualId</c>).</summary>
        public ActionEffectDef Effect { get; }

        /// <summary>The effect's <see cref="HitShapeDef"/> rows, placed along <see cref="Facing"/> from the origin.</summary>
        public IReadOnlyList<HitShapeDef> Shapes { get; }

        /// <summary>Origin on the MovementSim ground axes, whole cm, and its height.</summary>
        public int XCm { get; }

        public int YCm { get; }

        public int HeightCm { get; }

        /// <summary>Binary angle (65536 = one turn), the owner's locked facing.</summary>
        public ushort Facing { get; }

        public uint StartTick { get; }

        /// <summary><c>durationMs</c> rounded up to whole ticks: it covers ticks <see cref="StartTick"/> to StartTick + DurationTicks - 1.</summary>
        public int DurationTicks { get; }

        /// <summary>The tick the server ended it in (the first tick it is not drawn), once its despawn arrived.</summary>
        public uint? DespawnTick { get; internal set; }

        public GroundAreaDespawnReason Reason { get; internal set; }

        /// <summary>The render tick at which it is gone: its despawn tick, or the end of its duration if no despawn arrived.</summary>
        public double EndTick => Math.Min(DespawnTick ?? uint.MaxValue, (double)StartTick + DurationTicks);

        /// <summary>True while <paramref name="renderTick"/> is inside its span.</summary>
        public bool IsShownAt(double renderTick) => renderTick >= StartTick && renderTick < EndTick;
    }

    /// <summary>
    /// The ground areas of the hot messages <see cref="HotMessageId.GroundAreaSpawn"/> and
    /// <see cref="HotMessageId.GroundAreaDespawn"/> (spec 3.1, protocol 9), raising <see cref="Spawned"/> and
    /// <see cref="Despawned"/> so views react to events (spec 9.9).
    /// <list type="bullet">
    /// <item>The spawn names its effect by action net id and effect index, so the shapes, duration and visual come from
    /// the same tables the server uses; an area whose despawn is lost still ends at the end of its duration.</item>
    /// <item>The server repeats every event a few ticks against loss: duplicates are dropped by area id, and a spawn for an
    /// id that already ended (a late repeat) is ignored.</item>
    /// <item>A despawn is applied when the render time reaches its tick.</item>
    /// </list>
    /// Allocates only when an area spawns.
    /// </summary>
    public sealed class GroundAreaTracker
    {
        /// <summary>Ended ids remembered so late repeats of their spawn are ignored.</summary>
        public const int RecentCapacity = 256;

        private readonly ActionCatalog _actions;
        private readonly Dictionary<string, List<HitShapeDef>> _shapesByEffect = new Dictionary<string, List<HitShapeDef>>(StringComparer.Ordinal);
        private readonly Dictionary<uint, ClientGroundArea> _areas = new Dictionary<uint, ClientGroundArea>();
        private readonly HashSet<uint> _ended = new HashSet<uint>();
        private readonly uint[] _endedRing = new uint[RecentCapacity];
        private readonly List<ClientGroundArea> _scratch = new List<ClientGroundArea>();
        private int _endedNext;

        public GroundAreaTracker(ActionCatalog actions, IEnumerable<HitShapeDef> shapes)
        {
            _actions = actions;
            foreach (var shape in shapes)
            {
                if (!_shapesByEffect.TryGetValue(shape.EffectId, out var list))
                    _shapesByEffect.Add(shape.EffectId, list = new List<HitShapeDef>());
                list.Add(shape);
            }
        }

        /// <summary>The action and hit shape tables of validated tables.</summary>
        public static GroundAreaTracker From(ConfigSet set)
        {
            var shapes = new List<HitShapeDef>();
            if (set.TryGet(typeof(HitShapeDef), out var table))
            {
                foreach (var row in table.Rows)
                    shapes.Add((HitShapeDef)row);
            }
            return new GroundAreaTracker(ActionCatalog.From(set), shapes);
        }

        public event Action<ClientGroundArea>? Spawned;
        public event Action<ClientGroundArea>? Despawned;

        public IReadOnlyDictionary<uint, ClientGroundArea> Areas => _areas;

        /// <summary>Spawns naming an action or effect this client does not have, or not a ground area (tables out of date).</summary>
        public long UnknownEffects { get; private set; }

        /// <summary>Reads a ground area hot message; false (and nothing changes) when it is not one or does not decode.</summary>
        public bool Apply(ReadOnlySpan<byte> message)
        {
            if (GroundAreaSpawnReader.TryBegin(message, out var spawns))
            {
                while (spawns.TryRead(out var spawn))
                    OnSpawn(spawn);
                return true;
            }
            if (GroundAreaDespawnReader.TryBegin(message, out var despawns))
            {
                while (despawns.TryRead(out var despawn))
                    OnDespawn(despawn);
                return true;
            }
            return false;
        }

        /// <summary>Removes the areas the render time has passed the end of (their despawn tick or duration).</summary>
        public void Update(double renderTick)
        {
            _scratch.Clear();
            foreach (var area in _areas.Values)
            {
                if (renderTick >= area.EndTick)
                    _scratch.Add(area);
            }
            foreach (var area in _scratch)
                Remove(area);
            _scratch.Clear();
        }

        /// <summary>Removes everything (disconnect), raising <see cref="Despawned"/> for each.</summary>
        public void Clear()
        {
            _scratch.Clear();
            _scratch.AddRange(_areas.Values);
            _areas.Clear();
            foreach (var area in _scratch)
                Despawned?.Invoke(area);
            _scratch.Clear();
            _ended.Clear();
            Array.Clear(_endedRing, 0, _endedRing.Length);
            _endedNext = 0;
        }

        private void OnSpawn(in GroundAreaSpawn spawn)
        {
            if (_areas.ContainsKey(spawn.AreaId) || _ended.Contains(spawn.AreaId))
                return;
            if (!_actions.TryGetIndex(spawn.ActionId, out int index) || spawn.EffectIndex >= _actions[index].Effects.Length)
            {
                UnknownEffects++;
                return;
            }
            var effect = _actions[index].Effects[spawn.EffectIndex].Def;
            if (effect.Kind != EffectKind.GroundArea)
            {
                UnknownEffects++;
                return;
            }
            IReadOnlyList<HitShapeDef> shapes = _shapesByEffect.TryGetValue(effect.Id, out var list) ? list : Array.Empty<HitShapeDef>();
            var area = new ClientGroundArea(spawn, effect, shapes, Math.Max(1, _actions.TicksFromMs(effect.DurationMs)));
            _areas.Add(area.Id, area);
            Spawned?.Invoke(area);
        }

        private void OnDespawn(in GroundAreaDespawn despawn)
        {
            if (_areas.TryGetValue(despawn.AreaId, out var area) && area.DespawnTick == null)
            {
                area.DespawnTick = despawn.Tick;
                area.Reason = despawn.Reason;
            }
        }

        private void Remove(ClientGroundArea area)
        {
            _areas.Remove(area.Id);
            if (_ended.Count == RecentCapacity)
                _ended.Remove(_endedRing[_endedNext]);
            _endedRing[_endedNext] = area.Id;
            _endedNext = (_endedNext + 1) % RecentCapacity;
            _ended.Add(area.Id);
            Despawned?.Invoke(area);
        }
    }
}
