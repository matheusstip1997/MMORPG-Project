#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shared.Data;
using Shared.Simulation.Stats;

namespace Game.Gameplay.Dev
{
    /// <summary>What one <see cref="ClientTableReload.TryLoad"/> found.</summary>
    public enum ClientReloadStatus
    {
        /// <summary>The files hash as the server's reload and pass the checks: <see cref="ClientReloadResult.Set"/> is ready.</summary>
        Ready,
        /// <summary>The client already runs these tables.</summary>
        AlreadyApplied,
        /// <summary>The files changed again since the server read them; the server's next reload brings them.</summary>
        HashMismatch,
        /// <summary>The files hash the same but fail the client's checks (a bug: the server accepted them).</summary>
        Invalid,
    }

    public sealed class ClientReloadResult
    {
        public ClientReloadResult(ClientReloadStatus status, ulong hash, ConfigSet? set, IReadOnlyList<string> errors)
        {
            Status = status;
            Hash = hash;
            Set = set;
            Errors = errors;
        }

        public ClientReloadStatus Status { get; }

        /// <summary>The hash of the Hot table files read.</summary>
        public ulong Hash { get; }

        public ConfigSet? Set { get; }

        public IReadOnlyList<string> Errors { get; }
    }

    /// <summary>
    /// The client side of the dev hot reload (spec 11.7): the client applies only what the server accepted. When the
    /// server's <c>ConfigReload</c> telemetry says it swapped tables with a hash, this reads every table file once, hashes the
    /// Hot ones the way the server does (<see cref="TableHash"/>) and, when the hashes agree, parses the same bytes with
    /// <see cref="ConfigTableReader"/> and the shared cross-table checks. The caller swaps the result in on the main thread.
    /// Any thread; reads files and allocates (dev only).
    /// </summary>
    public sealed class ClientTableReload
    {
        private readonly string _directory;

        /// <param name="appliedHash">The hash of the tables the client runs now (<see cref="HashOf"/> at start).</param>
        public ClientTableReload(string directory, ulong appliedHash)
        {
            _directory = directory;
            AppliedHash = appliedHash;
        }

        /// <summary>The hash of the tables the client runs.</summary>
        public ulong AppliedHash { get; private set; }

        /// <summary>The Hot tables' hash of the files in <paramref name="directory"/> now.</summary>
        public static ulong HashOf(string directory)
        {
            var files = ReadAll(directory);
            return TableHash.Compute(TableReloadPolicy.HotTables(), name => files.TryGetValue(name, out var bytes) ? bytes : null);
        }

        /// <summary>Loads the tables the server accepted with <paramref name="serverHash"/>, if the files still hash the same.</summary>
        public ClientReloadResult TryLoad(ulong serverHash)
        {
            if (serverHash == AppliedHash)
                return new ClientReloadResult(ClientReloadStatus.AlreadyApplied, serverHash, null, Array.Empty<string>());
            var files = ReadAll(_directory);
            ulong hash = TableHash.Compute(TableReloadPolicy.HotTables(), name => files.TryGetValue(name, out var bytes) ? bytes : null);
            if (hash != serverHash)
                return new ClientReloadResult(ClientReloadStatus.HashMismatch, hash, null, Array.Empty<string>());

            var errors = new List<ValidationError>();
            var set = ConfigTableReader.LoadAndValidate(ConfigTables.All,
                name => files.TryGetValue(name, out var bytes) ? Text(bytes) : null, errors);
            if (errors.Count == 0)
                CombatConfig.TryBuild(set, errors);
            if (errors.Count > 0)
            {
                var messages = new List<string>();
                foreach (var error in errors)
                    messages.Add(error.ToString());
                return new ClientReloadResult(ClientReloadStatus.Invalid, hash, null, messages);
            }
            return new ClientReloadResult(ClientReloadStatus.Ready, hash, set, Array.Empty<string>());
        }

        /// <summary>The caller swapped <paramref name="result"/>'s tables in.</summary>
        public void MarkApplied(ClientReloadResult result) => AppliedHash = result.Hash;

        private static Dictionary<string, byte[]> ReadAll(string directory)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var type in ConfigTables.All)
            {
                string name = TableInfo.For(type).Name;
                var path = Path.Combine(directory, name + ".json");
                if (File.Exists(path))
                    files[name] = File.ReadAllBytes(path);
            }
            return files;
        }

        /// <summary>The file text as <c>File.ReadAllText</c> reads it (UTF-8, a byte order mark dropped).</summary>
        private static string Text(byte[] bytes)
        {
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }
    }
}
