#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System.Threading.Tasks;
using Game.Gameplay.Entities;
using UnityEngine;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// The client side of the dev hot reload (spec 11.7): when the local server's telemetry says it swapped in reloaded
    /// tables (<c>ConfigReload</c>, accepted, with their hash), this reads the same files off the main thread
    /// (<see cref="ClientTableReload"/>) and, when they hash the same, swaps them into prediction and the views between
    /// frames (<see cref="PredictionDevClient.Retune"/>). A rejected reload changes nothing on either side. Compiled with
    /// the dev client.
    /// </summary>
    public sealed class DevTableReload : MonoBehaviour
    {
        [SerializeField] private PredictionDevClient? client;
        [SerializeField] private DevTelemetryFeed? telemetry;

        private ClientTableReload? _reload;
        private Task<ClientReloadResult>? _loading;
        private ulong _pendingHash;

        /// <summary>Reloads applied on the client (QA counter).</summary>
        public int Applied { get; private set; }

        /// <summary>The last server reload record: <c>ConfigReloadResult</c> (0 before any), its tick and its hash.</summary>
        public long LastServerResult { get; private set; }
        public long LastServerTick { get; private set; }
        public ulong LastServerHash { get; private set; }

        /// <summary>What happened on the client to the last accepted reload, for the tick panel.</summary>
        public string Status { get; private set; } = "no reload yet";

        public ulong AppliedHash => _reload?.AppliedHash ?? 0;

        private void Start()
        {
            if (client == null)
                client = FindAnyObjectByType<PredictionDevClient>();
            if (telemetry == null)
                telemetry = FindAnyObjectByType<DevTelemetryFeed>();
            if (client == null || telemetry == null)
            {
                Debug.LogError("[DevTools] table reload: no dev client or telemetry feed in the scene");
                enabled = false;
                return;
            }
            var directory = ClientConfig.TablesDirectory;
            _reload = new ClientTableReload(directory, ClientTableReload.HashOf(directory));
            telemetry.Received += OnTelemetry;
        }

        private void OnTelemetry(DevTelemetryRecord record)
        {
            if (record.Kind != DevTelemetryKind.ConfigReload)
                return;
            LastServerResult = record.V1;
            LastServerTick = record.V0;
            LastServerHash = unchecked((ulong)record.V2);
            if (record.V1 == DevTelemetryLayout.ReloadRejected)
                Status = $"server rejected a reload ({record.V3} errors, see its console): both keep their tables";
            if (record.V1 != DevTelemetryLayout.ReloadAccepted || _reload == null)
                return;
            _pendingHash = LastServerHash;
            StartLoad();
        }

        private void StartLoad()
        {
            if (_loading != null || _reload == null || _pendingHash == 0 || _pendingHash == _reload.AppliedHash)
                return;
            ulong hash = _pendingHash;
            var reload = _reload;
            _loading = Task.Run(() => reload.TryLoad(hash));
        }

        private void Update()
        {
            if (_loading == null || !_loading.IsCompleted || _reload == null || client == null)
                return;
            var task = _loading;
            _loading = null;
            if (task.IsFaulted)
            {
                Status = "client reload failed: " + task.Exception?.GetBaseException().Message;
                Debug.LogError("[DevTools] " + Status);
                return;
            }
            var result = task.Result;
            switch (result.Status)
            {
                case ClientReloadStatus.Ready:
                    client.Retune(result.Set!);
                    _reload.MarkApplied(result);
                    Applied++;
                    Status = $"applied the server's tables of tick {LastServerTick} ({result.Hash:x16})";
                    Debug.Log("[DevTools] " + Status);
                    break;
                case ClientReloadStatus.HashMismatch:
                    Status = "the tables changed again after the server read them: waiting for its next reload";
                    break;
                case ClientReloadStatus.Invalid:
                    Status = "the server's tables fail the client's checks: " + string.Join("; ", result.Errors);
                    Debug.LogError("[DevTools] " + Status);
                    break;
            }
            StartLoad(); // a newer accepted reload may have come in meanwhile
        }

        private void OnDestroy()
        {
            if (telemetry != null)
                telemetry.Received -= OnTelemetry;
        }
    }
}
#endif
