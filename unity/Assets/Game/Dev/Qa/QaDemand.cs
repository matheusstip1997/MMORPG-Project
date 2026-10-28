#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Game.Dev.Qa
{
    /// <summary>
    /// The demand loop's spawn delay percent (R-4.2-03, WP-M2-4) of a monster type in a region, as the server had it when a
    /// monster died: the last <c>DemandUpdated</c> record of the run's telemetry before that monster's <c>Died</c>, else the
    /// state the server loaded at start (<see cref="StartFile"/>, read from the run's realm database by Run-QaSmoke.ps1),
    /// else 100. QA wave 1: a second run on the same databases starts with the percent an earlier run left.
    /// </summary>
    public static class QaDemand
    {
        /// <summary>In the run folder: <c>{"rows":[{"defId","regionId","spawnPercent"}]}</c>, the kill counters of world 1.</summary>
        public const string StartFile = "demand-start.json";

        [Serializable]
        private sealed class Row
        {
            public string defId = "";
            public string regionId = "";
            public int spawnPercent;
        }

        [Serializable]
        private sealed class File
        {
            public Row[] rows = Array.Empty<Row>();
        }

        /// <summary>The percent, and where it came from (for the step log).</summary>
        public static int SpawnPercent(string runDir, IEnumerable<string> telemetryLines, string defId, int defNetId,
            string regionId, int regionIndex, uint victimEntityId, out string source)
        {
            int percent = 100;
            source = "no demand state: the base delay";
            string start = Path.Combine(runDir, StartFile);
            if (System.IO.File.Exists(start))
            {
                foreach (var row in JsonUtility.FromJson<File>(System.IO.File.ReadAllText(start)).rows)
                {
                    if (row.defId == defId && row.regionId == regionId)
                    {
                        percent = row.spawnPercent;
                        source = $"{StartFile} (the state the server loaded)";
                    }
                }
            }
            foreach (var line in telemetryLines)
            {
                if (line.Contains("\"k\":\"Died\"") && Field(line, "entity") == victimEntityId)
                    break;
                if (line.Contains("\"k\":\"DemandUpdated\"") && Field(line, "monsterDef") == defNetId && Field(line, "r") == regionIndex)
                {
                    percent = (int)Field(line, "spawnDelayPercent");
                    source = "DemandUpdated at t " + Field(line, "t").ToString(CultureInfo.InvariantCulture);
                }
            }
            return percent;
        }

        /// <summary>The run's telemetry lines in write order (JSON lines, files by name; read while the server writes).</summary>
        public static IEnumerable<string> TelemetryLines(string directory)
        {
            if (!Directory.Exists(directory))
                yield break;
            var files = Directory.GetFiles(directory, "*.jsonl");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var path in files)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    yield return line;
            }
        }

        /// <summary>An integer field of a flat JSON line; -1 when it is missing.</summary>
        public static long Field(string line, string name)
        {
            string key = "\"" + name + "\":";
            int at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
                return -1;
            int start = at + key.Length, end = start;
            while (end < line.Length && (char.IsDigit(line[end]) || (end == start && line[end] == '-')))
                end++;
            return long.TryParse(line.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : -1;
        }
    }
}
