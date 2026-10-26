                if (entity.Kind != EntityKind.Monster || !entity.Samples.TryNewest(out var sample))
                    continue;#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using UnityEngine;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// The action timeline of the dev scene (spec 11.7, backlog J); the <c>Hotkeys</c> row <c>devTimeline</c> (F5) shows or
    /// hides it. For the local player it shows the predicted action and the action the server acknowledged in its last
    /// snapshot of the player, each as phase bars in ticks (startup, channel, active, recovery; effect fire ticks marked)
    /// with a cursor at its age, and below them the last <c>DevTools.timelineTicks</c> ticks of each row, where the
    /// server samples that reconciliation found different from the prediction are marked. The target row is the entity
    /// the player last hit or was hit by, else the nearest monster, at the render tick (what the player sees). Dev text
    /// (IMGUI), never part of the HUD; the colors are look only. Compiled with the dev client.
    /// </summary>
    public sealed class ActionTimelineView : MonoBehaviour
    {
        [SerializeField] private PredictionDevClient? client;
        [SerializeField] private bool shown;
        [SerializeField] private Color startupColor = new Color(0.95f, 0.8f, 0.2f);
        [SerializeField] private Color channelColor = new Color(0.3f, 0.6f, 1f);
        [SerializeField] private Color activeColor = new Color(0.95f, 0.3f, 0.25f);
        [SerializeField] private Color recoveryColor = new Color(0.55f, 0.55f, 0.6f);
        [SerializeField] private Color interruptColor = new Color(0.75f, 0.4f, 0.9f);
        [SerializeField] private Color mismatchColor = new Color(1f, 0.1f, 0.6f);

        private ActionTimelineModel? _model;
        private DevKey? _key;
        private uint _target;
        private uint _ownServerTick;
        private ActionWireState _ownServer;

        public bool Shown => shown;

        /// <summary>QA counters.</summary>
        public ActionTimelineModel? Model => _model;

        private void Start()
        {
            if (client == null)
                client = FindAnyObjectByType<PredictionDevClient>();
            if (client?.Config == null || client.Feed == null)
            {
                Debug.LogError("[DevTools] action timeline: no dev client with tables in the scene");
                enabled = false;
                return;
            }
            _model = new ActionTimelineModel(client.Config.Single<DevTools>().TimelineTicks);
            _key = DevKey.From(client.Config, Hotkeys.DevTimeline);
            client.Feed.PredictedTick += OnPredictedTick;
            client.Feed.SnapshotApplied += OnSnapshot;
            client.Feed.HitsReceived += OnHits;
            client.Feed.Disconnected += OnDisconnected;
            client.OwnActionReceived += OnOwnAction;
        }

        private void Update()
        {
            if (_key != null && _key.WasPressedThisFrame())
                shown = !shown;
        }

        private void OnPredictedTick()
        {
            var predictor = client!.Predictor;
            if (predictor != null && predictor.HasState)
                _model!.AddPredicted(predictor.ClientTick, predictor.Actions);
        }

        private void OnOwnAction(uint serverTick, ActionWireState wire, bool mismatch)
        {
            _ownServerTick = serverTick;
            _ownServer = wire;
            _model!.AddServer(serverTick, (ActionState)wire.State, wire.ActionId, wire.Age, mismatch);
        }

        private void OnHits(uint serverTick, System.ReadOnlySpan<HitEntry> hits)
        {
            uint own = client!.Predictor?.EntityId ?? 0;
            foreach (var hit in hits)
            {
                if (hit.AttackerId == own && hit.TargetId != own)
                    _target = hit.TargetId;
                else if (hit.TargetId == own && hit.AttackerId != own && hit.AttackerId != 0)
                    _target = hit.AttackerId;
            }
        }

        private void OnSnapshot()
        {
            var feed = client!.Feed!;
            var registry = client.Registry;
            if (registry == null || !feed.HasRenderTick)
                return;
            if (_target == 0 || !registry.Entities.ContainsKey(_target))
                _target = NearestMonster(registry, client.Predictor);
            if (_target == 0 || !registry.Entities.TryGetValue(_target, out var entity))
                return;
            uint tick = (uint)System.Math.Floor(feed.RenderTick);
            int age = entity.ActionState == ActionState.Idle ? 0 : (int)(tick - entity.ActionStartTick);
            _model!.AddTarget(entity.Id, tick, entity.ActionState, entity.ActionNetId, age);
        }

        private static uint NearestMonster(EntityRegistry registry, LocalPlayerPredictor? predictor)
        {
            if (predictor == null || !predictor.HasState)
                return 0;
            float x = predictor.State.Position.X / (float)FixedPoint.RawPerCm, y = predictor.State.Position.Y / (float)FixedPoint.RawPerCm;
            uint best = 0;
            float bestDistance = float.MaxValue;
            foreach (var entity in registry.EntityValues)
            {
                if (entity.Kind != EntityKind.Monster || !entity.Samples.TryNewest(out var sample))
                    continue;
                float distance = (sample.XCm - x) * (sample.XCm - x) + (sample.YCm - y) * (sample.YCm - y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = entity.Id;
                }
            }
            return best;
        }

        private void OnDisconnected()
        {
            _model?.Clear();
            _target = 0;
        }

        private void OnGUI()
        {
            if (!shown || _model == null || client?.Catalog == null)
                return;
            var catalog = client.Catalog;
            const float left = 10f, width = 520f, row = 18f;
            float top = 120f;
            GUI.Box(new Rect(left - 4, top - 4, width + 8, row * 12 + 8), GUIContent.none);
            GUI.Label(new Rect(left, top, width, row), $"Action timeline (F5)  mismatches {_model.Mismatches}");
            top += row;

            var predictor = client.Predictor;
            if (predictor != null)
            {
                var state = predictor.Actions;
                PhaseRow(ref top, $"predicted  c{predictor.ClientTick}", state.State, state.Action, state.Elapsed, state.InterruptTicks);
            }
            var serverAction = catalog.TryGetIndex(_ownServer.ActionId, out int index) ? catalog[index] : null;
            PhaseRow(ref top, $"server     s{_ownServerTick}", (ActionState)_ownServer.State, serverAction, _ownServer.Age,
                _ownServer.DurationTicks);
            if (_model.TargetCount > 0)
            {
                var target = _model.Target(0);
                var targetAction = catalog.TryGetIndex(target.ActionNetId, out index) ? catalog[index] : null;
                PhaseRow(ref top, $"target {target.ActionNetId,4} r{target.Tick}", target.State, targetAction, target.Elapsed, 0);
            }

            top += row * 0.5f;
            Strip(ref top, "predicted", _model.PredictedCount, _model.Predicted);
            Strip(ref top, "server", _model.ServerCount, _model.Server);
            Strip(ref top, "target", _model.TargetCount, _model.Target);
        }

        private void PhaseRow(ref float top, string label, ActionState state, ActionTiming? action, int elapsed, int interruptTicks)
        {
            const float left = 10f, labelWidth = 170f, barWidth = 340f, row = 18f;
            string name = action != null ? action.Id : state.ToString();
            GUI.Label(new Rect(left, top, labelWidth + barWidth, row), $"{label}  {name} +{elapsed}");
            top += row;
            float x = left + 10f;
            if (action != null && action.TotalTicks > 0)
            {
                float perTick = barWidth / action.TotalTicks;
                Bar(x, top, action.StartupEnd * perTick, startupColor);
                Bar(x + action.StartupEnd * perTick, top, (action.ChannelEnd - action.StartupEnd) * perTick, channelColor);
                Bar(x + action.ChannelEnd * perTick, top, (action.ActiveEnd - action.ChannelEnd) * perTick, activeColor);
                Bar(x + action.ActiveEnd * perTick, top, (action.TotalTicks - action.ActiveEnd) * perTick, recoveryColor);
                foreach (var effect in action.Effects)
                    Bar(x + effect.FireTick * perTick, top - 3f, 2f, Color.white, 16f);
                Bar(x + System.Math.Min(elapsed, action.TotalTicks) * perTick, top - 4f, 2f, Color.black, 18f);
                GUI.Label(new Rect(x + barWidth + 4f, top - 4f, 60f, row), $"{action.TotalTicks}t");
            }
            else if (state == ActionState.Flinched || state == ActionState.Staggered)
            {
                float perTick = barWidth / System.Math.Max(1, interruptTicks);
                Bar(x, top, barWidth, interruptColor);
                Bar(x + System.Math.Min(elapsed, interruptTicks) * perTick, top - 4f, 2f, Color.black, 18f);
            }
            top += row;
        }

        private void Strip(ref float top, string label, int count, System.Func<int, TimelineSample> sample)
        {
            const float left = 10f, labelWidth = 80f, row = 18f;
            GUI.Label(new Rect(left, top, labelWidth, row), label);
            int capacity = _model!.Capacity;
            float cell = 430f / capacity;
            for (int age = 0; age < count; age++)
            {
                var s = sample(age);
                float x = left + labelWidth + (capacity - 1 - age) * cell;
                Bar(x, top + 2f, System.Math.Max(1f, cell - 1f), PhaseColor(s.State), 10f);
                if (s.Mismatch)
                    Bar(x, top + 12f, System.Math.Max(1f, cell - 1f), mismatchColor, 4f);
            }
            top += row;
        }

        private Color PhaseColor(ActionState state) => state switch
        {
            ActionState.Startup => startupColor,
            ActionState.Channel => channelColor,
            ActionState.Active => activeColor,
            ActionState.Recovery => recoveryColor,
            ActionState.Flinched => interruptColor,
            ActionState.Staggered => interruptColor,
            ActionState.Dead => Color.black,
            _ => new Color(0.2f, 0.2f, 0.2f, 0.6f),
        };

        private static void Bar(float x, float y, float width, Color color, float height = 10f)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private void OnDestroy()
        {
            if (client?.Feed != null)
            {
                client.Feed.PredictedTick -= OnPredictedTick;
                client.Feed.SnapshotApplied -= OnSnapshot;
                client.Feed.HitsReceived -= OnHits;
                client.Feed.Disconnected -= OnDisconnected;
            }
            if (client != null)
                client.OwnActionReceived -= OnOwnAction;
            _key?.Dispose();
        }
    }
}
#endif
