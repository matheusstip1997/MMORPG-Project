#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Combat;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// Hitbox gizmos of the dev scene (spec 11.7, backlog J); the <c>Hotkeys</c> row <c>devHitboxes</c> (F4) shows or hides
    /// them. Lines on the ground, drawn from the same <see cref="HitShapeDef"/> rows and caster frame the server tests
    /// (<see cref="HitShapeOutline"/>), from what <see cref="HitboxModel"/> collects:
    /// <list type="bullet">
    /// <item>predicted color: the local player's strikes on their fire tick, and remote players' and monsters' strikes
    /// from their snapshot state at the render tick (interpolated position, 10-bit locked facing);</item>
    /// <item>server color: the strikes the server tested, exactly (its strike trace in telemetry, needs
    /// <c>MMORPG_DEV_TOOLS=1</c>), with every target circle it tested them against (hit color when touched);</item>
    /// <item>the ground areas, the projectiles (path so far and hit circle), and every body circle (the target radius the
    /// shapes are tested against).</item>
    /// </list>
    /// Never part of the HUD. Compiled with the dev client (editor and <c>MMORPG_DEV_LOGIN</c> builds).
    /// </summary>
    public sealed class HitboxGizmoView : MonoBehaviour
    {
        [SerializeField] private PredictionDevClient? client;
        [SerializeField] private DevTelemetryFeed? telemetry;
        [SerializeField] private EntityViews? views;

        [Tooltip("Look only: chords per full turn of the drawn arcs.")]
        [SerializeField] private int segmentsPerTurn = 64;

        [Tooltip("Look only: lines float this far above the ground so it does not hide them.")]
        [SerializeField] private float liftM = 0.04f;

        [SerializeField] private bool shown;

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<int> _indices = new List<int>();
        private readonly Dictionary<ushort, int> _classRadius = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, int> _monsterRadius = new Dictionary<ushort, int>();
        private FixVec2[] _outline = new FixVec2[0];
        private HitboxModel? _model;
        private DevTools? _settings;
        private DevKey? _key;
        private Mesh? _mesh;
        private MeshRenderer? _renderer;
        private Material? _material;
        private Color32 _predicted, _server, _serverHit, _area, _projectile, _body;

        public bool Shown => shown;

        /// <summary>QA counters: shapes added by source, targets tested, and the line segments of the last frame drawn.</summary>
        public long PredictedShapes => _model?.PredictedAdded ?? 0;
        public long ServerShapes => _model?.ServerAdded ?? 0;
        public long ServerTargets => _model?.TargetsAdded ?? 0;
        public int LinesDrawn { get; private set; }

        private void Start()
        {
            if (client == null)
                client = FindAnyObjectByType<PredictionDevClient>();
            if (telemetry == null)
                telemetry = FindAnyObjectByType<DevTelemetryFeed>();
            if (views == null)
                views = FindAnyObjectByType<EntityViews>();
            if (client?.Config == null || client.Catalog == null || client.Feed == null)
            {
                Debug.LogError("[DevTools] hitbox gizmos: no dev client with tables in the scene");
                enabled = false;
                return;
            }
            Load(client.Config);
            _model = new HitboxModel(client.Catalog, client.Config, _settings!.GizmoHoldMs);
            _outline = new FixVec2[HitShapeOutline.MaxPoints(segmentsPerTurn)];
            _key = DevKey.From(client.Config, Hotkeys.DevHitboxes);
            client.Feed.PredictedTick += OnPredictedTick;
            client.Feed.ConfigReloaded += OnConfigReloaded;
            if (client.Registry != null)
                client.Registry.Despawned += entity => _model.Forget(entity.Id);
            if (telemetry != null)
                telemetry.Received += OnTelemetry;

            var go = new GameObject("Dev Hitbox Lines", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            _mesh = new Mesh { name = "Dev hitbox lines" };
            _mesh.MarkDynamic();
            go.GetComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = go.GetComponent<MeshRenderer>();
            _material = new Material(Shader.Find("Sprites/Default")) { name = "Dev hitbox lines" }; // vertex colors, unlit
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.enabled = shown;
        }

        private void Load(ConfigSet config)
        {
            _settings = config.Single<DevTools>();
            _predicted = Color(_settings.PredictedColor);
            _server = Color(_settings.ServerColor);
            _serverHit = Color(_settings.ServerHitColor);
            _area = Color(_settings.AreaColor);
            _projectile = Color(_settings.ProjectileColor);
            _body = Color(_settings.BodyColor);
            _classRadius.Clear();
            _monsterRadius.Clear();
            if (config.TryGet(typeof(ClassDef), out var classes))
            {
                foreach (var row in classes.Rows)
                    _classRadius[(ushort)((ClassDef)row).NetId] = ((ClassDef)row).BodyRadiusCm;
            }
            if (config.TryGet(typeof(MonsterDef), out var monsters))
            {
                foreach (var row in monsters.Rows)
                    _monsterRadius[(ushort)((MonsterDef)row).NetId] = ((MonsterDef)row).BodyRadiusCm;
            }
        }

        private static Color32 Color(string text) =>
            HudColor.TryParse(text, out var c) ? new Color32(c.R, c.G, c.B, c.A) : new Color32(255, 0, 255, 255);

        private void OnConfigReloaded()
        {
            if (client?.Config == null || client.Catalog == null || _model == null)
                return;
            Load(client.Config);
            _model.Retune(client.Catalog, client.Config, _settings!.GizmoHoldMs);
        }

        private void OnPredictedTick()
        {
            var predictor = client?.Predictor;
            if (!shown || _model == null || predictor == null || !predictor.HasState)
                return;
            _model.ObserveLocal(predictor.EntityId, predictor.Actions, predictor.State.Position, NowMs);
        }

        private void OnTelemetry(DevTelemetryRecord record)
        {
            if (!shown || _model == null)
                return;
            if (record.Kind == DevTelemetryKind.StrikePlaced)
                _model.AddServerStrike(record, NowMs);
            else if (record.Kind == DevTelemetryKind.StrikeTested)
                _model.AddServerTarget(record, NowMs);
        }

        private static double NowMs => Time.realtimeSinceStartupAsDouble * 1000.0;

        private void Update()
        {
            if (_key != null && _key.WasPressedThisFrame())
            {
                shown = !shown;
                if (_renderer != null)
                    _renderer.enabled = shown;
                if (!shown)
                    _model?.Clear();
            }
        }

        /// <summary>After the views are placed for this frame.</summary>
        private void LateUpdate()
        {
            if (!shown || _model == null || _mesh == null || client?.Feed == null)
                return;
            double now = NowMs;
            var feed = client.Feed;
            var registry = client.Registry;
            if (registry != null && feed.HasRenderTick)
            {
                foreach (var entity in registry.EntityValues)
                {
                    if (entity.Kind != EntityKind.Player && entity.Kind != EntityKind.Monster)
                        continue;
                    if (!entity.Samples.TrySample(feed.RenderTick, out var pose))
                        continue;
                    _model.ObserveRemote(entity.Id, entity.ActionState, entity.ActionNetId, entity.ActionStartTick, feed.RenderTick,
                        Raw(pose.XCm, pose.YCm), (ushort)Mathf.RoundToInt(pose.Facing), now);
                }
            }
            _model.Expire(now);
            Build(feed);
        }

        private void Build(WorldClientFeed feed)
        {
            _vertices.Clear();
            _colors.Clear();
            _indices.Clear();
            var predictor = client!.Predictor;
            float localHeight = predictor != null && predictor.HasState ? predictor.HeightCm : 0f;

            foreach (var shape in _model!.Shapes)
            {
                int count = HitShapeOutline.Write(shape.Row, shape.Origin, shape.Facing, segmentsPerTurn, _outline);
                AddLoop(_outline, count, HeightOf(shape.EntityId, feed, localHeight), shape.Source == HitboxSource.Server ? _server : _predicted);
            }
            foreach (var target in _model.Targets)
                AddCircle(target.Position, target.RadiusRaw, HeightOf(target.TargetId, feed, localHeight), target.Hit ? _serverHit : _server);

            // Body circles: every shape is tested against these.
            if (predictor != null && predictor.HasState && _classRadius.TryGetValue(client.Registry?.OwnDefId ?? 0, out int ownRadius))
                AddCircle(predictor.State.Position, FixedPoint.FromCm(ownRadius), localHeight, _body);
            if (client.Registry != null && feed.HasRenderTick)
            {
                foreach (var entity in client.Registry.EntityValues)
                {
                    if (!entity.Samples.TrySample(feed.RenderTick, out var pose))
                        continue;
                    int radius = entity.Kind == EntityKind.Player && _classRadius.TryGetValue(entity.DefId, out int r) ? r
                        : entity.Kind == EntityKind.Monster && _monsterRadius.TryGetValue(entity.DefId, out r) ? r : 0;
                    if (radius > 0)
                        AddCircle(Raw(pose.XCm, pose.YCm), FixedPoint.FromCm(radius), pose.ZCm, _body);
                }
            }

            if (feed.HasRenderTick)
            {
                double renderTick = feed.RenderTick;
                if (client.GroundAreas != null)
                {
                    foreach (var area in client.GroundAreas.Areas.Values)
                    {
                        if (!area.IsShownAt(renderTick))
                            continue;
                        var origin = FixVec2.FromCm(area.XCm, area.YCm);
                        foreach (var row in area.Shapes)
                        {
                            int count = HitShapeOutline.Write(row, origin, area.Facing, segmentsPerTurn, _outline);
                            AddLoop(_outline, count, area.HeightCm, _area);
                        }
                    }
                }
                if (client.Projectiles != null)
                {
                    foreach (var projectile in client.Projectiles.Projectiles.Values)
                    {
                        if (renderTick < projectile.Path.StartTick || renderTick >= projectile.EndTick)
                            continue;
                        projectile.GetPose(renderTick, out float xCm, out float yCm);
                        var start = projectile.Path.PositionAt(0);
                        AddLine(start, Raw(xCm, yCm), projectile.HeightCm, _projectile);
                        AddCircle(Raw(xCm, yCm), FixedPoint.FromCm(projectile.Def.HitRadiusCm), projectile.HeightCm, _projectile);
                    }
                }
            }

            _mesh!.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetIndices(_indices, MeshTopology.Lines, 0);
            _mesh.RecalculateBounds();
            LinesDrawn = _indices.Count / 2;
        }

        private float HeightOf(uint entityId, WorldClientFeed feed, float fallback)
        {
            if (client!.Registry != null && feed.HasRenderTick && client.Registry.Entities.TryGetValue(entityId, out var entity)
                && entity.Samples.TrySample(feed.RenderTick, out var pose))
                return pose.ZCm;
            return fallback;
        }

        private static FixVec2 Raw(float xCm, float yCm) =>
            new FixVec2(Mathf.RoundToInt(xCm * FixedPoint.RawPerCm), Mathf.RoundToInt(yCm * FixedPoint.RawPerCm));

        private Vector3 World(FixVec2 point, float heightCm) =>
            WorldUnits.FromGroundCm(point.X / (float)FixedPoint.RawPerCm, point.Y / (float)FixedPoint.RawPerCm, heightCm)
            + Vector3.up * liftM;

        private void AddLoop(FixVec2[] points, int count, float heightCm, Color32 color)
        {
            if (count < 2)
                return;
            int first = _vertices.Count;
            for (int i = 0; i < count; i++)
            {
                _vertices.Add(World(points[i], heightCm));
                _colors.Add(color);
            }
            for (int i = 0; i < count; i++)
            {
                _indices.Add(first + i);
                _indices.Add(first + (i + 1) % count);
            }
        }

        private void AddLine(FixVec2 from, FixVec2 to, float heightCm, Color32 color)
        {
            _indices.Add(_vertices.Count);
            _vertices.Add(World(from, heightCm));
            _colors.Add(color);
            _indices.Add(_vertices.Count);
            _vertices.Add(World(to, heightCm));
            _colors.Add(color);
        }

        private void AddCircle(FixVec2 center, int radiusRaw, float heightCm, Color32 color)
        {
            int first = _vertices.Count;
            for (int i = 0; i < segmentsPerTurn; i++)
            {
                var angle = (ushort)(i * 65536 / segmentsPerTurn);
                var offset = FixedTrig.Along(angle, radiusRaw);
                _vertices.Add(World(center + offset, heightCm));
                _colors.Add(color);
                _indices.Add(first + i);
                _indices.Add(first + (i + 1) % segmentsPerTurn);
            }
        }

        private void OnDestroy()
        {
            if (client?.Feed != null)
            {
                client.Feed.PredictedTick -= OnPredictedTick;
                client.Feed.ConfigReloaded -= OnConfigReloaded;
            }
            if (telemetry != null)
                telemetry.Received -= OnTelemetry;
            _key?.Dispose();
            if (_mesh != null)
                Destroy(_mesh);
            if (_material != null)
                Destroy(_material);
        }
    }
}
#endif
