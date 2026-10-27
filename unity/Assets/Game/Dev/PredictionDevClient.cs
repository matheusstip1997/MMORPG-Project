#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Input;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.Net;
using Game.Net.Dev;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// Dev client for backlog D1 (scene <c>Assets/Game/Dev/PredictionDev.unity</c>): connects to a locally running
    /// GameServer with the dev ticket, predicts the local player with reconciliation, interpolates everyone else, and
    /// draws placeholder capsules under the 45-degree camera. WASD moves (screen-relative), the mouse sets the facing (or
    /// the locked monster while the target lock is on, Tab, R-2.2-06), C and V turn the camera, G picks up the nearest
    /// ground drop (backlog G). A status line shows the ack, pending inputs,
    /// corrections and the own HP, MP and stamina; the bag shows in the inventory window (WP16), whose bag moves go out here.
    /// Compiled only in the editor and in builds with <c>MMORPG_DEV_LOGIN</c>, like <see cref="DevTicket"/>.
    /// </summary>
    public sealed class PredictionDevClient : MonoBehaviour, IClientConnectionListener, IWorldClientFeedSource, IWorldRequests
    {
        [Tooltip("Dev ticket file; empty means unity/UserSettings/DevTicket.json.")]
        [SerializeField] private string ticketPath = string.Empty;

        [Tooltip("Exported world under data/world to predict collision with; the server must run the same world (MMORPG_WORLD_DIR).")]
        [SerializeField] private string worldId = "m1";

        [SerializeField] private TopDownCameraRig? cameraRig;
        [SerializeField] private EntityViews? views;
        [SerializeField] private ProjectileViews? projectileViews;
        [SerializeField] private GroundAreaViews? groundAreaViews;

        private PredictionSettings _settings;
        private LocalPlayerPredictor? _predictor;
        private LocalPlayerMotion? _motion;
        private TickClock? _ticks;
        private InputPacketBuilder? _packets;
        private EntityRegistry? _registry;
        private ProjectileTracker? _projectiles;
        private GroundAreaTracker? _groundAreas;
        private ClientInventory? _inventory;
        private ActionPresentationAdapter? _actionViews;
        private RemoteActionTracker? _remoteActions;
        private InterpolationClock? _interpolation;
        private GameInput? _input;
        private readonly InputPressLatch _presses = new InputPressLatch(); // taps between two ticks (R-2.2-01, R-3.1-04)
        private TargetLock? _targetLock;
        private readonly List<LockCandidate> _lockCandidates = new List<LockCandidate>();
        private ClientConnection? _connection;
        private byte[] _packetBuffer = Array.Empty<byte>();
        private ushort _facing;
        private int _confirmedXCm;
        private int _confirmedYCm;
        private string _status = "starting";
        private double _lastErrorCm;
        private SnapshotHeader _lastHeader;
        private Shared.Data.ConfigSet? _config;
        private ActionCatalog? _catalog;
        private ushort _classNetId;
        private readonly HitEntry[] _hits = new HitEntry[HitMessage.MaxHits];
        private readonly ReliableMessageWriter _chatWriter = new ReliableMessageWriter(ChatCodec.MaxSendSize);
        private readonly ChatSendMessage _chatSend = new ChatSendMessage();

        /// <summary>What the HUD binds to (backlog I): raised from the messages and ticks below.</summary>
        public WorldClientFeed? Feed { get; private set; }

        /// <summary>The local player's predictor (null until connected), read by the QA smoke for its predicted cooldowns.</summary>
        public LocalPlayerPredictor? Predictor => _predictor;

        // Dev tools (spec 11.7): what the hitbox gizmos, the action timeline and the table reload read.

        public EntityRegistry? Registry => _registry;

        public ProjectileTracker? Projectiles => _projectiles;

        public GroundAreaTracker? GroundAreas => _groundAreas;

        /// <summary>The tables the client runs (replaced by <see cref="Retune"/>).</summary>
        public Shared.Data.ConfigSet? Config => _config;

        public ActionCatalog? Catalog => _catalog;

        /// <summary>
        /// Each snapshot that carried the local player: its server tick, the server's action state of the local player (what
        /// the server acknowledged), and whether reconciliation found the predicted action state different.
        /// </summary>
        public event Action<uint, ActionWireState, bool>? OwnActionReceived;

        private void Awake()
        {
            if (!ClientConfig.TryLoad(out var config, out var error))
            {
                Fail(error);
                return;
            }
            // TODO(B4): the world id from the enter-world message instead of the inspector.
            if (!ClientWorld.TryLoadGrid(worldId, out var grid, out error) || grid == null)
            {
                Fail(error);
                return;
            }
            _settings = PredictionSettings.From(config);
            _config = config;
            var actions = ActionCatalog.From(config);
            _catalog = actions;
            _predictor = new LocalPlayerPredictor(_settings, grid, actions);
            _motion = new LocalPlayerMotion(_settings);
            _ticks = new TickClock(_settings);
            _packets = new InputPacketBuilder(_settings.InputRedundancy);
            _packetBuffer = new byte[_packets.MaxPacketSize];
            _registry = new EntityRegistry(_settings);
            // R-2.2-07: the monsters' bodies block the predicted walk and dashes as on the server.
            _predictor.UseBodies(new RegistryBodyBlockers(_registry, BodyRadii.From(config)));
            _projectiles = ProjectileTracker.From(config, _settings.TickRateHz, grid);
            _groundAreas = GroundAreaTracker.From(config);
            _inventory = new ClientInventory(config);
            _interpolation = new InterpolationClock(_settings);
            _targetLock = new TargetLock(config.Single<TargetLockSettings>());
            _input = new GameInput(Hotkeys.Find(config, Hotkeys.TargetLock)?.Binding);

            if (cameraRig == null)
                cameraRig = FindAnyObjectByType<TopDownCameraRig>() ?? new GameObject("Camera", typeof(Camera), typeof(TopDownCameraRig)).GetComponent<TopDownCameraRig>();
            cameraRig.Configure(config.Single<ClientCamera>());
            if (views == null)
                views = gameObject.AddComponent<EntityViews>();
            views.Bind(_registry, config);
            if (projectileViews == null)
                projectileViews = gameObject.AddComponent<ProjectileViews>();
            projectileViews.Bind(_projectiles, config);
            if (groundAreaViews == null)
                groundAreaViews = gameObject.AddComponent<GroundAreaViews>();
            groundAreaViews.Bind(_groundAreas, config);

            // Real action phases drive the animations (D2 -> visuals): the local player from prediction, remote entities
            // from snapshots, presented when the render tick reaches them.
            var shown = views;
            var adapter = new ActionPresentationAdapter(() => shown.LocalPresentation, shown.Presentation);
            var remoteActions = new RemoteActionTracker(actions) { Listener = adapter.Remote };
            _remoteActions = remoteActions;
            _actionViews = adapter;
            _predictor.Listener = adapter.Local;
            _registry.Actions = remoteActions;
            _registry.Despawned += entity =>
            {
                remoteActions.Forget(entity.Id);
                adapter.Forget(entity.Id);
            };
            Feed = new WorldClientFeed(config, actions, _registry, _predictor, views, cameraRig.Camera, _settings.TickMs)
            {
                ChatSender = SendChat,
                Requests = this,
            };
            // The dev stand-in's keys (1, 2) are hotbar keys now; real actions replace it.
            var fakeSource = FindAnyObjectByType<FakeActionPhaseSource>();
            if (fakeSource != null)
                fakeSource.enabled = false;
        }

        private void Start()
        {
            if (_predictor == null)
                return;
            if (WorldHandoff.TryTake(out var entry) && entry != null) // opened from the login scene (B5, R-9.10-05)
            {
                entry.Relay.Target = this; // events waited in LiteNetLib; Update polls from here on
                _connection = entry.Connection;
                _status = "connected";
                return;
            }
            var path = string.IsNullOrEmpty(ticketPath) ? DevTicket.DefaultPath : ticketPath;
            if (!DevTicket.TryLoad(path, out var ticket, out var error) || ticket == null)
            {
                Fail("no dev ticket: " + error + ". See unity/Assets/Game/Dev/README.md.");
                return;
            }
            _connection = new ClientConnection(this, ticket.Limits);
            _connection.Connect(ticket.Host, ticket.Port, ticket.Ticket, ticket.SessionKey);
            Array.Clear(ticket.SessionKey, 0, ticket.SessionKey.Length);
            _status = $"connecting to {ticket.Host}:{ticket.Port}";
        }

        private void Update()
        {
            if (_predictor == null || _motion == null || _ticks == null || _registry == null || _interpolation == null
                || _input == null || cameraRig == null || views == null)
                return;

            float deltaMs = Time.unscaledDeltaTime * 1000f;
            double nowMs = Time.realtimeSinceStartupAsDouble * 1000.0;
            _connection?.PollEvents(); // snapshots arrive here (OnUnreliableReceived)
            _input.Suspended = Feed != null && Feed.TextInputActive; // typing in the chat never moves or attacks (spec 9.12)

            cameraRig.Turn(_input.CameraTurn, deltaMs);

            bool connected = _predictor.HasState && _connection != null && _connection.State == ConnectionState.Connected;
            if (connected)
            {
                if (_input.PickupPressed)
                    RequestPickup();
                if (_input.TargetLockPressed)
                    PressTargetLock(nowMs);
                _presses.Frame(_input.HeldActions, CurrentRenderTick());
                int ticks = _ticks.Advance(deltaMs);
                for (int i = 0; i < ticks; i++)
                {
                    RunTick();
                    Feed?.RaisePredictedTick();
                }
                if (ticks > 0)
                    views.SyncLocalTick(_predictor.ClientTick);
            }
            // Backlog G: the drop the pickup key asks for now, for the HUD's pickup prompt (the same rule as the press).
            Feed?.SetPickupTarget(connected ? PickupTarget() : 0, _input.PickupKeyName);

            _motion.Update(deltaMs);
            if (_predictor.HasState)
            {
                _motion.GetVisualCm(_ticks.Alpha, out float xCm, out float yCm);
                var local = WorldUnits.FromGroundCm(xCm, yCm, _predictor.HeightCm);
                views.PlaceLocal(local, WorldUnits.FacingRotation(_predictor.State.Facing));
                cameraRig.Follow(local);
            }

            _interpolation.Advance(nowMs, deltaMs);
            if (_interpolation.HasTime)
            {
                _registry.Update(_interpolation.RenderTick);
                views.PlaceRemotes(_interpolation.RenderTick);
                _projectiles?.Update(_interpolation.RenderTick);
                projectileViews?.Place(_interpolation.RenderTick);
                _groundAreas?.Update(_interpolation.RenderTick);
                groundAreaViews?.Place(_interpolation.RenderTick);
                _actionViews?.ReleaseRemote(_interpolation.RenderTick);
                Feed?.SetRenderTick(_interpolation.RenderTick);
            }
        }

        /// <summary>
        /// Backlog G: the nearest ground drop in the server's pickup range of both the predicted and the last confirmed own
        /// position (<see cref="ClientInventory.NearestDrop(EntityRegistry, int, int, int, int)"/>), 0 for none.
        /// </summary>
        private uint PickupTarget()
        {
            var position = _predictor!.State.Position;
            return _inventory!.NearestDrop(_registry!, Shared.Simulation.FixedPoint.ToCmRound(position.X),
                Shared.Simulation.FixedPoint.ToCmRound(position.Y), _confirmedXCm, _confirmedYCm);
        }

        /// <summary>
        /// Backlog G: asks for the drop of <see cref="PickupTarget"/> on the reliable channel. The server checks range, the
        /// lock and bag space and answers with a pickup result; the items show once they are saved.
        /// </summary>
        private void RequestPickup()
        {
            uint drop = PickupTarget();
            if (drop == 0)
            {
                Debug.Log("[PredictionDev] nothing to pick up in range");
                return;
            }
            _connection!.SendReliable(ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = drop }));
        }

        /// <summary>
        /// WP16: a bag move from the inventory window, on the reliable channel with the next request id of this connection.
        /// The server applies it through InventoryService and answers; the window shows only what it confirms.
        /// </summary>
        public uint RequestInventoryMove(byte fromSlot, byte toSlot)
        {
            if (_inventory == null || _connection == null || _connection.State != ConnectionState.Connected)
                return 0;
            _connection.SendReliable(_inventory.MoveRequest(fromSlot, toSlot, out uint requestId));
            return requestId;
        }

        /// <summary>The render tick the player sees now (the view clock of R-3.1-03), 0 before the first snapshot.</summary>
        private double CurrentRenderTick() => _interpolation!.HasTime ? Math.Max(0.0, _interpolation.RenderTick) : 0.0;

        /// <summary>One client tick: sample input, predict, send (spec 1.7, 2.2).</summary>
        private void RunTick()
        {
            var keys = ScreenRelativeKeys.ToWorld(_input!.ScreenMoveKeys, cameraRig!.YawStep);
            var character = views!.LocalView != null ? views.LocalView.position : Vector3.zero;
            if (TargetLockFacing(out ushort facing))
                _facing = facing; // R-2.2-06: the locked monster sets the facing, the mouse does not
            else if (!_input.Suspended && GroundAim.TryFacing(cameraRig.Camera, _input.AimScreenPosition, character, out facing))
                _facing = facing;

            // R-3.1-03: the server rewinds this player's hits to the tick it renders the others at, and judges its dodges and
            // parries against monsters' hits on that view clock (tick plus its fraction in 1/64, R-3.3-02).
            // A press keeps the view clock of the frame it went down in, and a tap between two ticks is not lost
            // (InputPressLatch).
            var actions = _presses.Take(_input.HeldActions, CurrentRenderTick(), out double renderTick);
            uint viewTick = (uint)Math.Floor(renderTick);
            byte viewSubTick = (byte)Math.Min(63.0, Math.Floor((renderTick - viewTick) * 64.0));
            if (Feed != null && Feed.UiCapturesPointer) // the pointer is on a UI window: its clicks are not attacks or parries
                actions &= ~(InputActions.Attack | InputActions.Parry);
            var frame = _predictor!.Predict(keys, _facing, actions, _input.HeldSkillSlot, viewTick, viewSubTick);
            _motion!.OnTick(_predictor.State.Position);
            int size = _packets!.Write(frame, _packetBuffer);
            _connection!.SendInput(new ReadOnlySpan<byte>(_packetBuffer, 0, size));
        }

        /// <summary>
        /// R-2.2-06: the lock key locks the nearest monster, cycles, or (a second press within the window) clears the lock.
        /// Candidates are the monsters as drawn at the render time; the player is where prediction has it.
        /// </summary>
        private void PressTargetLock(double nowMs)
        {
            if (_targetLock == null || !_interpolation!.HasTime || _predictor!.Actions.State == ActionState.Dead)
                return;
            var self = _predictor.State.Position;
            TargetCandidates.Collect(_registry!, _interpolation.RenderTick, _lockCandidates);
            if (_targetLock.Press(nowMs, Cm(self.X), Cm(self.Y), _lockCandidates))
                Feed?.RaiseLockedTarget(_targetLock.LockedId);
        }

        /// <summary>
        /// R-2.2-06: while a lock holds, the facing toward the locked monster's drawn position from the predicted own
        /// position (the server rewinds this player's hits to the same render time, R-3.1-03). The lock breaks when the
        /// target dies, leaves interest or goes beyond the break range, and when the player dies. During an action the
        /// action machine keeps the facing of its start anyway (R-2.2-02).
        /// </summary>
        private bool TargetLockFacing(out ushort facing)
        {
            facing = 0;
            if (_targetLock == null || !_targetLock.IsLocked)
                return false;
            var self = _predictor!.State.Position;
            bool cleared;
            if (_predictor.Actions.State == ActionState.Dead || !_interpolation!.HasTime)
                cleared = _targetLock.Clear();
            else
            {
                TargetCandidates.Collect(_registry!, _interpolation.RenderTick, _lockCandidates);
                cleared = _targetLock.Refresh(Cm(self.X), Cm(self.Y), _lockCandidates);
            }
            if (cleared)
                Feed?.RaiseLockedTarget(0);
            return _targetLock.TryFacing(Cm(self.X), Cm(self.Y), out facing);
        }

        private static float Cm(int raw) => raw / (float)Shared.Simulation.FixedPoint.RawPerCm;

        /// <summary>The feed's chat sender: one already checked line as <see cref="ChatSendMessage"/> on the reliable channel.</summary>
        private bool SendChat(string text)
        {
            if (_connection == null || _connection.State != ConnectionState.Connected)
                return false;
            _chatSend.Text = text;
            return _connection.SendReliable(ChatCodec.WriteSend(_chatWriter, _chatSend));
        }

        private void OnDestroy()
        {
            _connection?.Dispose();
            _connection = null;
            _input?.Dispose();
            _input = null;
        }

        /// <summary>
        /// Dev diagnostics only (not player text; the HUD of backlog I shows the game state): one line at the bottom right,
        /// clear of the HUD zones of spec 9.2.
        /// </summary>
        private void OnGUI()
        {
            string line = _predictor == null
                ? _status
                : $"{_status} | tick {_lastHeader.ServerTick} ack {(_lastHeader.HasInputAck ? _lastHeader.LastInputSequence.ToString() : "-")} " +
                  $"pending {_predictor.PendingCount} corr {_predictor.Corrections} err {_lastErrorCm:0.0} cm | " +
                  $"remotes {_registry!.Entities.Count} | areas {_groundAreas?.Areas.Count ?? 0} | cam {cameraRig!.YawDegrees:0} (C/V)";
            GUI.Label(new Rect(Screen.width - 700, Screen.height - 22, 690, 22), line);
        }

        public void OnConnected()
        {
            _status = "connected";
        }

        public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion)
        {
            _status = reason == ClientDisconnectReason.Rejected
                ? $"rejected: {rejectReason} (server protocol {serverProtocolVersion}, ours {ProtocolVersion.Current})"
                : rejectReason != ConnectRejectReason.None ? $"disconnected: {reason} ({rejectReason})" : $"disconnected: {reason}";
            Debug.Log($"[PredictionDev] {_status}");
            _predictor?.Reset();
            _predictor?.SetLoadout(ActionLoadout.None); // the next character's class comes with its own spawn
            if (_predictor != null)
                _predictor.OwnBodyRadiusRaw = 0;
            _classNetId = 0;
            _ticks?.Reset();
            _packets?.Reset();
            _presses.Reset();
            _interpolation?.Reset();
            _registry?.Clear();
            _projectiles?.Clear();
            _groundAreas?.Clear();
            _actionViews?.Clear();
            _inventory?.Reset();
            _targetLock?.Clear(); // the feed drops its locked id with the disconnect below
            views?.ShowLocal(false);
            Feed?.RaiseDisconnected(DisconnectReasonCode.For(reason, rejectReason)); // the HUD shows its ReasonCodeStrings text
        }

        /// <summary>
        /// Hits the server resolved on entities this client sees (backlog D3), for the HUD's damage numbers and boss bar
        /// (backlog I); HP and states come from snapshots.
        /// </summary>
        private void OnHits(ReadOnlySpan<byte> message)
        {
            if (!HitMessage.TryRead(message, out uint serverTick, _hits, out int count))
            {
                Debug.LogWarning($"[PredictionDev] dropped an unreadable hit message ({message.Length} bytes)");
                return;
            }
            Feed?.RaiseHits(serverTick, new ReadOnlySpan<HitEntry>(_hits, 0, count));
        }

        public void OnReliableReceived(ReadOnlySpan<byte> message)
        {
            if (_inventory != null && _inventory.Apply(message, out string log))
            {
                Debug.Log($"[PredictionDev] {log}");
                switch (ReliableMessages.PeekId(message))
                {
                    case ReliableMessageId.PickupResult when _inventory.LastResult is { Reason: not PickupReason.None } refused:
                        Feed?.RaisePickupRefused(refused.Reason); // the HUD toast shows the ReasonCodeStrings text
                        break;
                    case ReliableMessageId.InventoryContents when _inventory.Contents != null:
                        Feed?.RaiseInventory(_inventory.Contents); // the inventory window (WP16)
                        break;
                    case ReliableMessageId.InventoryMoveResult when _inventory.LastMoveResult != null:
                        Feed?.RaiseInventoryMoveAnswered(_inventory.LastMoveResult);
                        break;
                }
                return;
            }
            // TODO(B4): enter-world and session messages.
            if (ActionResult.TryRead(message, out ushort sequence, out ushort actionId, out byte slot, out ActionReason reason))
            {
                // The HUD shows the reason text (toast, slot flash; spec 9.4); the log keeps the input sequence for debugging.
                Debug.Log($"[PredictionDev] action {actionId} (slot {slot}) of input {sequence}: {reason}");
                Feed?.RaiseActionResult(actionId, slot, reason);
                return;
            }
            if (EntityIdentity.TryRead(message, out uint entityId, out string name)) // name plates (spec 9.3, protocol 10)
            {
                Feed?.RaiseIdentity(entityId, name);
                return;
            }
            if (ChatCodec.TryReadLine(message, out var line)) // local chat (spec 9.12, protocol 11)
            {
                Feed?.RaiseChatLine(line.SenderEntityId, line.SenderName, line.Text);
                return;
            }
            if (ChatCodec.TryReadRefused(message, out var chatReason))
            {
                Feed?.RaiseChatRefused(chatReason);
                return;
            }
            Debug.LogWarning($"[PredictionDev] dropped an unknown reliable message ({message.Length} bytes)");
        }

        public void OnUnreliableReceived(ReadOnlySpan<byte> message)
        {
            HotMessage.TryPeekId(message, out var id);
            if (id == HotMessageId.ProjectileSpawn || id == HotMessageId.ProjectileDespawn)
            {
                _projectiles!.Apply(message); // spec 3.2: drawn on their path from the start tick
                return;
            }
            if (id == HotMessageId.GroundAreaSpawn || id == HotMessageId.GroundAreaDespawn)
            {
                _groundAreas!.Apply(message); // spec 3.1: drawn over their span at the render time
                return;
            }
            if (id == HotMessageId.Hit)
            {
                OnHits(message);
                return;
            }
            if (!_registry!.ApplySnapshot(message, out var header, out var own, out bool hasOwn))
                return;
            _interpolation!.OnSnapshot(header.ServerTick, Time.realtimeSinceStartupAsDouble * 1000.0);
            if (!hasOwn)
            {
                Feed?.RaiseSnapshotApplied();
                return;
            }

            _predictor!.EntityId = own.EntityId;
            ApplyOwnClass();
            var serverState = SnapshotAxes.GroundState(own);
            var result = _predictor.Reconcile(header, serverState, ActionWireState.From(own));
            if (result.Outcome != ReconcileOutcome.Stale)
            {
                // Where the server has the player: one end of the pickup range rule (backlog G, PickupTarget).
                _confirmedXCm = Shared.Simulation.FixedPoint.ToCmRound(serverState.Position.X);
                _confirmedYCm = Shared.Simulation.FixedPoint.ToCmRound(serverState.Position.Y);
            }
            switch (result.Outcome)
            {
                case ReconcileOutcome.Initialized:
                    _motion!.Reset(_predictor.State.Position);
                    _facing = _predictor.State.Facing;
                    views!.ShowLocal(true);
                    break;
                case ReconcileOutcome.Corrected:
                    _motion!.OnCorrected(result.Shift);
                    Debug.Log($"[PredictionDev] corrected {result.ErrorCm:0.0} cm at ack {header.LastInputSequence}");
                    break;
                case ReconcileOutcome.Stale:
                    Feed?.RaiseSnapshotApplied(); // remote entities may have changed even if the own state is old
                    return;
            }
            _lastHeader = header;
            if (result.Outcome != ReconcileOutcome.UnknownAck)
                _lastErrorCm = result.ErrorCm;
            OwnActionReceived?.Invoke(header.ServerTick, ActionWireState.From(own), result.ActionMismatch);
            Feed?.RaiseSnapshotApplied();
        }

        /// <summary>
        /// Backlog E: once the own spawn names the class (<see cref="EntityRegistry.OwnDefId"/>, a <c>ClassDef</c> net id),
        /// prediction uses that class's bindings, the same row the server uses, and the local player gets its visual.
        /// </summary>
        private void ApplyOwnClass()
        {
            ushort netId = _registry!.OwnDefId;
            if (netId == 0 || netId == _classNetId || _config == null || _catalog == null)
                return;
            _classNetId = netId;
            ClassDef? classDef = null;
            if (_config.TryGet(typeof(ClassDef), out var classes))
            {
                foreach (var row in classes.Rows)
                {
                    if (((ClassDef)row).NetId == netId)
                        classDef = (ClassDef)row;
                }
            }
            if (classDef == null)
            {
                Debug.LogWarning($"[PredictionDev] no ClassDef with net id {netId}: the tables differ from the server's");
                _predictor!.SetLoadout(ActionLoadout.None);
                _predictor.OwnBodyRadiusRaw = 0;
                return;
            }
            _predictor!.SetLoadout(_catalog.Loadout(classDef.Id));
            _predictor.OwnBodyRadiusRaw = Shared.Simulation.FixedPoint.FromCm(classDef.BodyRadiusCm);
            views?.SetLocalVisual(classDef.VisualId);
        }

        /// <summary>
        /// Dev hot reload (spec 11.7): swaps in tables the server accepted (same hash): prediction through the shared
        /// <see cref="ActionMachine.Retune"/>, the remote action timings, the projectile and ground area rows of new spawns,
        /// then the feed tells the HUD. Main thread, between frames.
        /// </summary>
        public void Retune(Shared.Data.ConfigSet config)
        {
            if (_predictor == null)
                return;
            var actions = ActionCatalog.From(config);
            _predictor.Retune(actions);
            _remoteActions?.Retune(actions);
            _projectiles?.Retune(config);
            _groundAreas?.Retune(actions, config);
            _targetLock?.Retune(config.Single<TargetLockSettings>());
            _config = config;
            _catalog = actions;
            Feed?.RaiseConfigReloaded(config, actions);
        }

        private void Fail(string message)
        {
            _status = message;
            Debug.LogError($"[PredictionDev] {message}");
        }
    }
}
#endif
