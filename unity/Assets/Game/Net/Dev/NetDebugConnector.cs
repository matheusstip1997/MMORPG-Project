#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System;
using System.Text;
using Shared.Protocol;
using UnityEngine;

namespace Game.Net.Dev
{
    /// <summary>
    /// Dev-only check that the client reaches a server (backlog B): connects with the dev ticket, sends a reliable
    /// hello and a stream of empty input packets, and logs what comes back (reliable messages, snapshot acks, reject
    /// reasons). Drop it on any GameObject in an empty scene. No gameplay, no UI beyond a status line.
    /// Start the test host first: <c>dotnet run --project src/Tools/DevConnect -- serve</c>.
    /// </summary>
    public sealed class NetDebugConnector : MonoBehaviour, IClientConnectionListener
    {
        [Tooltip("Dev ticket file; empty means unity/UserSettings/DevTicket.json.")]
        [SerializeField] private string ticketPath = string.Empty;

        [Tooltip("Debug input packets per second while connected (dev tool setting, not the game's input rate).")]
        [SerializeField] private float inputPacketsPerSecond = 2f;

        private readonly InputFrame[] _frame = new InputFrame[1];
        private readonly byte[] _inputBuffer = new byte[InputPacket.SizeFor(1)];
        private ClientConnection? _connection;
        private float _nextInputAt;
        private string _status = "idle";
        private int _snapshots;

        private void Start()
        {
            var path = string.IsNullOrEmpty(ticketPath) ? DevTicket.DefaultPath : ticketPath;
            if (!DevTicket.TryLoad(path, out var ticket, out var error) || ticket == null)
            {
                _status = "no ticket: " + error;
                Debug.LogWarning($"[NetDebug] {_status}");
                return;
            }

            _connection = new ClientConnection(this, ticket.Limits);
            _connection.Connect(ticket.Host, ticket.Port, ticket.Ticket, ticket.SessionKey);
            Array.Clear(ticket.SessionKey, 0, ticket.SessionKey.Length);
            _status = $"connecting to {ticket.Host}:{ticket.Port} as account {ticket.AccountId}";
            Debug.Log($"[NetDebug] {_status}");
        }

        private void Update()
        {
            if (_connection == null)
                return;
            _connection.PollEvents();

            if (_connection.State != ConnectionState.Connected || inputPacketsPerSecond <= 0f || Time.unscaledTime < _nextInputAt)
                return;
            _nextInputAt = Time.unscaledTime + 1f / inputPacketsPerSecond;
            _frame[0].Sequence++;
            _frame[0].ClientTick++;
            var size = InputPacket.Write(_inputBuffer, _frame);
            _connection.SendInput(new ReadOnlySpan<byte>(_inputBuffer, 0, size));
        }

        private void OnDestroy()
        {
            _connection?.Dispose();
            _connection = null;
        }

        private void OnGUI() => GUI.Label(new Rect(10, 10, 900, 22), $"[NetDebug] {_status} | snapshots {_snapshots}");

        public void OnConnected()
        {
            _status = "connected";
            Debug.Log("[NetDebug] connected; sending hello");
            _connection!.SendReliable(Encoding.UTF8.GetBytes("hello from unity"));
        }

        public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion)
        {
            _status = reason == ClientDisconnectReason.Rejected
                ? $"rejected: {rejectReason} (server protocol {serverProtocolVersion}, ours {ProtocolVersion.Current})"
                : $"disconnected: {reason}";
            Debug.Log($"[NetDebug] {_status}");
        }

        public void OnReliableReceived(ReadOnlySpan<byte> message) =>
            Debug.Log($"[NetDebug] reliable {message.Length} bytes: \"{Encoding.UTF8.GetString(message.ToArray())}\"");

        public void OnUnreliableReceived(ReadOnlySpan<byte> message)
        {
            if (!SnapshotReader.TryBegin(message, out var snapshot))
            {
                Debug.LogWarning($"[NetDebug] dropped an unreadable hot message ({message.Length} bytes)");
                return;
            }
            _snapshots++;
            var header = snapshot.Header;
            _status = $"connected, server tick {header.ServerTick}, input ack {header.LastInputSequence}";
        }
    }
}
#endif
