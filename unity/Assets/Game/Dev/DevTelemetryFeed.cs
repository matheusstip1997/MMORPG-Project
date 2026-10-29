#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Shared.Data.Tables;
using UnityEngine;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// The dev views' channel to the local GameServer (spec 11.7): a background thread follows the server's telemetry
    /// folder (<see cref="TelemetryTail"/>) every <c>DevTools.telemetryPollMs</c>, and <see cref="Update"/> hands the
    /// records to the main thread: the per-tick timing (<c>TickTiming</c>, <c>TickStats</c>), the strike trace and the
    /// reload results. The folder is <c>MMORPG_TELEMETRY_DIR</c> (absolute, or relative to the repository root like the
    /// server's working directory), else the repository's <c>telemetry</c>. Works only with a server on this machine.
    /// Compiled with the dev client (editor and <c>MMORPG_DEV_LOGIN</c> builds).
    /// </summary>
    public sealed class DevTelemetryFeed : MonoBehaviour
    {
        public const string TelemetryDirectoryVariable = "MMORPG_TELEMETRY_DIR";

        [SerializeField] private PredictionDevClient? client;

        private readonly ConcurrentQueue<DevTelemetryRecord> _queue = new ConcurrentQueue<DevTelemetryRecord>();
        private Thread? _thread;
        private volatile bool _stop;
        private int _pollMs;

        /// <summary>Each record, on the main thread, in file order.</summary>
        public event Action<DevTelemetryRecord>? Received;

        public string Directory { get; private set; } = string.Empty;

        /// <summary>Records handed out since start (QA counter).</summary>
        public long Records { get; private set; }

        /// <summary>The file followed (written by the tail's thread).</summary>
        public string? CurrentFile { get; private set; }

        public static string DefaultDirectory()
        {
            string repository = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string? configured = Environment.GetEnvironmentVariable(TelemetryDirectoryVariable);
            if (string.IsNullOrWhiteSpace(configured))
                return Path.Combine(repository, "telemetry");
            return Path.GetFullPath(Path.IsPathRooted(configured) ? configured.Trim() : Path.Combine(repository, configured.Trim()));
        }

        private void Start()
        {
            if (client == null)
                client = FindAnyObjectByType<PredictionDevClient>();
            var config = client?.Config;
            if (config == null)
            {
                Debug.LogError("[DevTools] telemetry feed: the dev client has no tables");
                enabled = false;
                return;
            }
            _pollMs = config.Single<DevTools>().TelemetryPollMs;
            Directory = DefaultDirectory();
            var tail = new TelemetryTail(Directory);
            _thread = new Thread(() => Run(tail)) { IsBackground = true, Name = "Dev telemetry tail" };
            _thread.Start();
        }

        private void Run(TelemetryTail tail)
        {
            var batch = new List<DevTelemetryRecord>();
            while (!_stop)
            {
                batch.Clear();
                tail.Poll(batch);
                CurrentFile = tail.CurrentFile;
                foreach (var record in batch)
                    _queue.Enqueue(record);
                Thread.Sleep(_pollMs);
            }
        }

        private void Update()
        {
            while (_queue.TryDequeue(out var record))
            {
                Records++;
                Received?.Invoke(record);
            }
        }

        private void OnDestroy()
        {
            _stop = true;
            _thread?.Join(_pollMs * 4);
        }
    }
}
#endif
