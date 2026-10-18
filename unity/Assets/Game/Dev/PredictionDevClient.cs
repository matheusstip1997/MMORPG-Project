#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.Gameplay.Input;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.Net;
using Game.Net.Dev;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// Dev client for backlog D1 (scene <c>Assets/Game/Dev/PredictionDev.unity</c>): connects to a locally running
    /// GameServer with the dev ticket, predicts the local player with reconciliation, interpolates everyone else, and
    /// draws placeholder capsules under the 45-degree camera. WASD moves (screen-relative), the mouse sets the facing,
    /// C and V turn the camera. A status line shows the ack, pending inputs and corrections.
    /// Compiled only in the editor and in builds with <c>MMORPG_DEV_LOGIN</c>, like <see cref="DevTicket"/>.
    /// </summary>
    public sealed class PredictionDevClient : MonoBehaviour, IClientConnectionListener
    {
        [Tooltip("Dev ticket file; empty means unity/UserSettings/DevTicket.json.")]
        [SerializeField] private string ticketPath = string.Empty;

        [Tooltip("Exported world under data/world to predict collision with; the server must run the same world (MMORPG_WORLD_DIR).")]
        [SerializeField] private string worldId = "m1";

        [SerializeField] private TopDownCameraRig? cameraRig;
        [SerializeField] private EntityViews? views;

        private PredictionSettings _settings;
        private LocalPlayerPredictor? _predictor;
        private LocalPlayerMotion? _motion;
        private TickClock? _ticks;
        private InputPacketBuilder? _packets;
        private EntityRegistry? _registry;
        private InterpolationClock? _interpolation;
        private GameInput? _input;
        private ClientConnection? _connection;
        private byte[] _packetBuffer = Array.Empty<byte>();
        private ushort _facing;
        private string _status = "starting";
        private double _lastErrorCm;
        private SnapshotHeader _lastHeader;

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
            _predictor = new LocalPlayerPredictor(_settings, grid);
            _motion = new LocalPlayerMotion(_settings);
            _ticks = new TickClock(_settings);
            _packets = new InputPacketBuilder(_settings.InputRedundancy);
            _packetBuffer = new byte[_packets.MaxPacketSize];
            _registry = new EntityRegistry(_settings);
            _interpolation = new InterpolationClock(_settings);
            _input = new GameInput();

            if (cameraRig == null)
                cameraRig = FindAnyObjectByType<TopDownCameraRig>() ?? new GameObject("Camera", typeof(Camera), typeof(TopDownCameraRig)).GetComponent<TopDownCameraRig>();
            cameraRig.Configure(config.Single<ClientCamera>());
            if (views == null)
                views = gameObject.AddComponent<EntityViews>();
            views.Bind(_registry);
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

            cameraRig.Turn(_input.CameraTurn, deltaMs);

            if (_predictor.HasState && _connection != null && _connection.State == ConnectionState.Connected)
            {
                int ticks = _ticks.Advance(deltaMs);
                for (int i = 0; i < ticks; i++)
                    RunTick();
            }

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
            }
        }

        /// <summary>One client tick: sample input, predict, send (spec 1.7, 2.2).</summary>
        private void RunTick()
        {
            var keys = ScreenRelativeKeys.ToWorld(_input!.ScreenMoveKeys, cameraRig!.YawStep);
            var character = views!.LocalView != null ? views.LocalView.position : Vector3.zero;
            if (GroundAim.TryFacing(cameraRig.Camera, _input.AimScreenPosition, character, out ushort facing))
                _facing = facing;

            var frame = _predictor!.Predict(keys, _facing);
            _motion!.OnTick(_predictor.State.Position);
            int size = _packets!.Write(frame, _packetBuffer);
            _connection!.SendInput(new ReadOnlySpan<byte>(_packetBuffer, 0, size));
        }

        private void OnDestroy()
        {
            _connection?.Dispose();
            _connection = null;
            _input?.Dispose();
            _input = null;
        }

        private void OnGUI()
        {
            string line = _predictor == null
                ? _status
                : $"{_status} | tick {_lastHeader.ServerTick} ack {(_lastHeader.HasInputAck ? _lastHeader.LastInputSequence.ToString() : "-")} " +
                  $"sent {_predictor.LastSequence} pending {_predictor.PendingCount} corrections {_predictor.Corrections} " +
                  $"last error {_lastErrorCm:0.0} cm | remotes {_registry!.Entities.Count} | camera {cameraRig!.YawDegrees:0} deg (C/V)";
            GUI.Label(new Rect(10, 10, 1400, 22), line);
        }

        public void OnConnected()
        {
            _status = "connected";
        }

        public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion)
        {
            _status = reason == ClientDisconnectReason.Rejected
                ? $"rejected: {rejectReason} (server protocol {serverProtocolVersion}, ours {ProtocolVersion.Current})"
                : $"disconnected: {reason}";
            Debug.Log($"[PredictionDev] {_status}");
            _predictor?.Reset();
            _ticks?.Reset();
            _packets?.Reset();
            _interpolation?.Reset();
            _registry?.Clear();
            views?.ShowLocal(false);
        }

        public void OnReliableReceived(ReadOnlySpan<byte> message)
        {
            // TODO(B4): enter-world and session messages.
        }

        public void OnUnreliableReceived(ReadOnlySpan<byte> message)
        {
            if (!_registry!.ApplySnapshot(message, out var header, out var own, out bool hasOwn))
                return;
            _interpolation!.OnSnapshot(header.ServerTick, Time.realtimeSinceStartupAsDouble * 1000.0);
            if (!hasOwn)
                return;

            var result = _predictor!.Reconcile(header, SnapshotAxes.GroundState(own));
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
                    return;
            }
            _lastHeader = header;
            if (result.Outcome != ReconcileOutcome.UnknownAck)
                _lastErrorCm = result.ErrorCm;
        }

        private void Fail(string message)
        {
            _status = message;
            Debug.LogError($"[PredictionDev] {message}");
        }
    }
}
#endif
