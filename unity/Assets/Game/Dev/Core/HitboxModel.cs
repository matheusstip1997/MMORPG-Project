#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Combat;

namespace Game.Gameplay.Dev
{
    /// <summary>Where a drawn hit shape came from.</summary>
    public enum HitboxSource : byte
    {
        /// <summary>The client placed it: the local player's prediction, or a remote entity's snapshot state.</summary>
        Predicted,
        /// <summary>The server's strike trace: exactly the origin and facing <c>HitResolver</c> tested.</summary>
        Server,
    }

    /// <summary>One hit shape to draw until <see cref="ExpiresAtMs"/>.</summary>
    public readonly struct DrawnShape
    {
        public DrawnShape(HitShapeDef row, FixVec2 origin, ushort facing, HitboxSource source, uint entityId, double expiresAtMs)
        {
            Row = row;
            Origin = origin;
            Facing = facing;
            Source = source;
            EntityId = entityId;
            ExpiresAtMs = expiresAtMs;
        }

        public HitShapeDef Row { get; }
        public FixVec2 Origin { get; }
        public ushort Facing { get; }
        public HitboxSource Source { get; }
        public uint EntityId { get; }
        public double ExpiresAtMs { get; }
    }

    /// <summary>A target circle the server tested a strike against, and whether the strike's shapes touch it.</summary>
    public readonly struct TestedTarget
    {
        public TestedTarget(uint targetId, FixVec2 position, int radiusRaw, bool hit, double expiresAtMs)
        {
            TargetId = targetId;
            Position = position;
            RadiusRaw = radiusRaw;
            Hit = hit;
            ExpiresAtMs = expiresAtMs;
        }

        public uint TargetId { get; }
        public FixVec2 Position { get; }
        public int RadiusRaw { get; }
        public bool Hit { get; }
        public double ExpiresAtMs { get; }
    }

    /// <summary>
    /// What the dev hitbox gizmos draw (spec 11.7, backlog J), engine-free so dotnet tests run it: the Strike shapes of
    /// actions when their effects fire, placed from the same <see cref="HitShapeDef"/> rows and caster frame as the server,
    /// from three sources: the local player's prediction (per predicted tick), remote entities' snapshot states (at the
    /// render tick), and the server's strike trace (<c>StrikePlaced</c>, <c>StrikeTested</c> telemetry), whose tested
    /// targets are judged here with the shared <see cref="HitShape.Overlaps"/>, the server's own test. Each stays for
    /// <c>DevTools.gizmoHoldMs</c>. Times are the caller's clock in ms.
    /// </summary>
    public sealed class HitboxModel
    {
        private readonly Dictionary<string, List<HitShapeDef>> _shapesByEffect = new Dictionary<string, List<HitShapeDef>>(StringComparer.Ordinal);
        private readonly Dictionary<uint, RemoteFire> _remote = new Dictionary<uint, RemoteFire>();
        private readonly List<DrawnShape> _shapes = new List<DrawnShape>();
        private readonly List<TestedTarget> _targets = new List<TestedTarget>();
        private readonly List<ServerStrike> _strikes = new List<ServerStrike>();
        private ActionCatalog _catalog;
        private double _holdMs;

        public HitboxModel(ActionCatalog catalog, ConfigSet config, double holdMs)
        {
            _catalog = catalog;
            _holdMs = holdMs;
            IndexShapes(config);
        }

        public IReadOnlyList<DrawnShape> Shapes => _shapes;

        public IReadOnlyList<TestedTarget> Targets => _targets;

        /// <summary>Shapes added since construction, by source (QA counters).</summary>
        public long PredictedAdded { get; private set; }
        public long ServerAdded { get; private set; }
        public long TargetsAdded { get; private set; }

        /// <summary>Server strikes or targets whose action, effect or strike this client could not find (tables differ).</summary>
        public long Unmatched { get; private set; }

        /// <summary>The shapes of an effect, or none.</summary>
        public IReadOnlyList<HitShapeDef> ShapesOf(string effectId) =>
            _shapesByEffect.TryGetValue(effectId, out var list) ? (IReadOnlyList<HitShapeDef>)list : Array.Empty<HitShapeDef>();

        /// <summary>Dev hot reload: shapes added from now on come from the reloaded tables.</summary>
        public void Retune(ActionCatalog catalog, ConfigSet config, double holdMs)
        {
            _catalog = catalog;
            _holdMs = holdMs;
            _shapesByEffect.Clear();
            IndexShapes(config);
        }

        /// <summary>After each predicted tick: the local player's Strike effects that fired on it.</summary>
        public void ObserveLocal(uint entityId, ActionMachineState state, FixVec2 position, double nowMs)
        {
            var action = state.Action;
            if (action == null)
                return;
            var effects = action.Effects;
            for (int i = 0; i < effects.Length; i++)
            {
                if (effects[i].Kind == EffectKind.Strike && effects[i].FireTick == state.Elapsed)
                    AddEffect(effects[i].Def.Id, position, state.Facing, HitboxSource.Predicted, entityId, nowMs);
            }
        }

        /// <summary>
        /// Each frame for a remote entity at the render tick: the Strike effects whose fire tick the render time passed since
        /// the last call, placed at its interpolated position and the snapshot's locked facing.
        /// </summary>
        public void ObserveRemote(uint entityId, ActionState state, int actionNetId, uint actionStartTick, double renderTick,
            FixVec2 position, ushort facing, double nowMs)
        {
            bool running = state >= ActionState.Startup && state <= ActionState.Recovery;
            if (!running || !_catalog.TryGetIndex(actionNetId, out int index))
            {
                _remote.Remove(entityId);
                return;
            }
            int elapsed = (int)Math.Floor(renderTick - actionStartTick);
            if (elapsed < 0)
                return;
            if (!_remote.TryGetValue(entityId, out var fire) || fire.StartTick != actionStartTick || fire.NetId != actionNetId)
                fire = new RemoteFire(actionStartTick, actionNetId, -1);
            var effects = _catalog[index].Effects;
            for (int i = 0; i < effects.Length; i++)
            {
                int at = effects[i].FireTick;
                if (effects[i].Kind == EffectKind.Strike && at > fire.LastElapsed && at <= elapsed)
                    AddEffect(effects[i].Def.Id, position, facing, HitboxSource.Predicted, entityId, nowMs);
            }
            _remote[entityId] = new RemoteFire(actionStartTick, actionNetId, elapsed);
        }

        /// <summary>A <c>StrikePlaced</c> record of the server's strike trace.</summary>
        public void AddServerStrike(in DevTelemetryRecord record, double nowMs)
        {
            int netId = DevTelemetryLayout.ActionNetIdOf(record.Target);
            int effectIndex = DevTelemetryLayout.EffectIndexOf(record.Target);
            if (!_catalog.TryGetIndex(netId, out int index) || effectIndex >= _catalog[index].Effects.Length)
            {
                Unmatched++;
                return;
            }
            string effectId = _catalog[index].Effects[effectIndex].Def.Id;
            var origin = new FixVec2((int)record.V1, (int)record.V2);
            ushort facing = (ushort)record.V3;
            AddEffect(effectId, origin, facing, HitboxSource.Server, (uint)record.Subject, nowMs);
            _strikes.Add(new ServerStrike((uint)record.Subject, (uint)record.V0, effectId, origin, facing, nowMs + _holdMs));
        }

        /// <summary>
        /// A <c>StrikeTested</c> record: the target circle as the server tested it, judged against the matching strike's shapes
        /// with <see cref="HitShape.Overlaps"/>.
        /// </summary>
        public void AddServerTarget(in DevTelemetryRecord record, double nowMs)
        {
            uint attacker = (uint)record.Subject;
            uint tick = (uint)record.V0;
            var position = new FixVec2((int)record.V1, (int)record.V2);
            int radius = (int)Math.Min(Math.Max(record.V3, 0), HitShape.MaxTargetRadiusRaw);
            for (int s = _strikes.Count - 1; s >= 0; s--)
            {
                var strike = _strikes[s];
                if (strike.Attacker != attacker || strike.Tick != tick)
                    continue;
                bool hit = false;
                foreach (var row in ShapesOf(strike.EffectId))
                    hit |= HitShape.From(row).Overlaps(strike.Origin, strike.Facing, position, radius);
                _targets.Add(new TestedTarget((uint)record.Target, position, radius, hit, nowMs + _holdMs));
                TargetsAdded++;
                return;
            }
            Unmatched++;
        }

        /// <summary>Drops what has been shown for its hold time.</summary>
        public void Expire(double nowMs)
        {
            _shapes.RemoveAll(s => s.ExpiresAtMs <= nowMs);
            _targets.RemoveAll(t => t.ExpiresAtMs <= nowMs);
            _strikes.RemoveAll(s => s.ExpiresAtMs <= nowMs);
        }

        public void Clear()
        {
            _shapes.Clear();
            _targets.Clear();
            _strikes.Clear();
            _remote.Clear();
        }

        /// <summary>The entity left: its next action starts its fire tracking over.</summary>
        public void Forget(uint entityId) => _remote.Remove(entityId);

        private void AddEffect(string effectId, FixVec2 origin, ushort facing, HitboxSource source, uint entityId, double nowMs)
        {
            foreach (var row in ShapesOf(effectId))
            {
                _shapes.Add(new DrawnShape(row, origin, facing, source, entityId, nowMs + _holdMs));
                if (source == HitboxSource.Server)
                    ServerAdded++;
                else
                    PredictedAdded++;
            }
        }

        private void IndexShapes(ConfigSet config)
        {
            if (!config.TryGet(typeof(HitShapeDef), out var table))
                return;
            foreach (var row in table.Rows)
            {
                var shape = (HitShapeDef)row;
                if (!_shapesByEffect.TryGetValue(shape.EffectId, out var list))
                    _shapesByEffect.Add(shape.EffectId, list = new List<HitShapeDef>());
                list.Add(shape);
            }
        }

        private readonly struct RemoteFire
        {
            public RemoteFire(uint startTick, int netId, int lastElapsed)
            {
                StartTick = startTick;
                NetId = netId;
                LastElapsed = lastElapsed;
            }

            public uint StartTick { get; }
            public int NetId { get; }
            public int LastElapsed { get; }
        }

        private readonly struct ServerStrike
        {
            public ServerStrike(uint attacker, uint tick, string effectId, FixVec2 origin, ushort facing, double expiresAtMs)
            {
                Attacker = attacker;
                Tick = tick;
                EffectId = effectId;
                Origin = origin;
                Facing = facing;
                ExpiresAtMs = expiresAtMs;
            }

            public uint Attacker { get; }
            public uint Tick { get; }
            public string EffectId { get; }
            public FixVec2 Origin { get; }
            public ushort Facing { get; }
            public double ExpiresAtMs { get; }
        }
    }
}
