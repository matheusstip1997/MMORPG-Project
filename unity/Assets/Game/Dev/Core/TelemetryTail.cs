#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shared.Data;

namespace Game.Gameplay.Dev
{
    /// <summary>The telemetry kinds the dev views read (server <c>TelemetryKind</c>, spec 7.2).</summary>
    public enum DevTelemetryKind : byte
    {
        TickTiming,
        TickStats,
        ConfigReload,
        StrikePlaced,
        StrikeTested,
    }

    /// <summary>
    /// One record of the local server's telemetry the dev views read (spec 11.7), with the server record's generic fields
    /// (<c>TelemetryRecord</c>: subject, target, v0 to v3). What they mean per kind is <see cref="DevTelemetryLayout"/>.
    /// </summary>
    public readonly struct DevTelemetryRecord
    {
        public DevTelemetryRecord(DevTelemetryKind kind, long timeMs, long subject, long target, long v0, long v1, long v2, long v3)
        {
            Kind = kind;
            TimeMs = timeMs;
            Subject = subject;
            Target = target;
            V0 = v0;
            V1 = v1;
            V2 = v2;
            V3 = v3;
        }

        public DevTelemetryKind Kind { get; }
        public long TimeMs { get; }
        public long Subject { get; }
        public long Target { get; }
        public long V0 { get; }
        public long V1 { get; }
        public long V2 { get; }
        public long V3 { get; }
    }

    /// <summary>
    /// The JSON field names of the kinds in <see cref="DevTelemetryKind"/>, as the server's <c>TelemetrySchema</c> v1 writes
    /// them (a test in GameServer.Tests keeps the two the same). Null: the field is not used.
    /// </summary>
    public static class DevTelemetryLayout
    {
        public const string KindKey = "k";
        public const string TimeKey = "t";

        public sealed class Layout
        {
            public Layout(string name, string? subject, string? target, string? v0, string? v1, string? v2, string? v3)
            {
                Name = name;
                Subject = subject;
                Target = target;
                V0 = v0;
                V1 = v1;
                V2 = v2;
                V3 = v3;
            }

            public string Name { get; }
            public string? Subject { get; }
            public string? Target { get; }
            public string? V0 { get; }
            public string? V1 { get; }
            public string? V2 { get; }
            public string? V3 { get; }
        }

        /// <summary>By <see cref="DevTelemetryKind"/>.</summary>
        public static readonly Layout[] Kinds =
        {
            new Layout("TickTiming", null, null, "tick", "tickUs", "lateUs", null),
            new Layout("TickStats", null, null, "lastUs", "maxUs", "overruns", "maxLateUs"),
            new Layout("ConfigReload", "tables", null, "tick", "result", "hash", "errors"),
            new Layout("StrikePlaced", "attacker", "actionEffect", "tick", "x", "y", "facing"),
            new Layout("StrikeTested", "attacker", "target", "tick", "x", "y", "radius"),
        };

        /// <summary><c>ConfigReloadResult</c> values of the server.</summary>
        public const long ReloadAccepted = 1;
        public const long ReloadRejected = 2;
        public const long ReloadNotServer = 3;

        /// <summary>The <c>actionEffect</c> of a <c>StrikePlaced</c> record: action net id * 256 + effect index.</summary>
        public static int ActionNetIdOf(long actionEffect) => (int)(actionEffect >> 8);

        /// <summary>255: the strike did not come from a running action.</summary>
        public static int EffectIndexOf(long actionEffect) => (int)(actionEffect & 0xFF);

        public static bool TryKind(string name, out DevTelemetryKind kind)
        {
            for (int i = 0; i < Kinds.Length; i++)
            {
                if (Kinds[i].Name == name)
                {
                    kind = (DevTelemetryKind)i;
                    return true;
                }
            }
            kind = default;
            return false;
        }
    }

    /// <summary>
    /// Follows the telemetry the GameServer on this machine writes (spec 11.7: the dev views' only channel to the server's
    /// tick timing, strike trace and reload results; no protocol message). Picks the newest <c>*.jsonl</c> file of the
    /// folder (run ids start with the UTC time, so the newest run sorts last, and its rotated files after it), reads what
    /// was appended since the last <see cref="Poll"/>, keeps a torn last line for the next poll, and parses the kinds of
    /// <see cref="DevTelemetryKind"/>. The file that was newest when the tail was made is read from its end (the views
    /// show what happens from now on); every other file (a rotation, or a server started afterwards) from its start. Not thread-safe: call it from one
    /// thread (the dev feed's background thread). Allocates; dev only.
    /// </summary>
    public sealed class TelemetryTail
    {
        private readonly string _directory;
        private readonly StringBuilder _partial = new StringBuilder();
        private byte[] _buffer = new byte[64 * 1024];
        private string? _file;
        private long _offset;
        private bool _skipToNewline;
        private readonly string? _history;
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private char[] _chars = new char[64 * 1024];

        public TelemetryTail(string directory)
        {
            _directory = directory;
            _history = NewestFile();
        }

        /// <summary>The file being followed, or null before the folder has one.</summary>
        public string? CurrentFile => _file;

        /// <summary>Lines that looked like records but did not parse.</summary>
        public long BadLines { get; private set; }

        /// <summary>Adds the records appended since the last call to <paramref name="records"/>; returns how many.</summary>
        public int Poll(List<DevTelemetryRecord> records)
        {
            int before = records.Count;
            if (_file == null)
            {
                var newest = NewestFile();
                if (newest == null)
                    return 0;
                Open(newest);
            }
            ReadRest(_file!, records);
            // A rotation or a newer run since the last poll: the rest of the old file is read above, then every later file
            // from its start, in order, so nothing between two polls is lost.
            foreach (var file in FilesAfter(_file!))
            {
                Open(file);
                ReadRest(file, records);
            }
            return records.Count - before;
        }

        private void Open(string file)
        {
            bool history = string.Equals(file, _history, StringComparison.Ordinal);
            _file = file;
            _partial.Clear();
            _decoder.Reset();
            _offset = history ? LengthOf(file) : 0;
            _skipToNewline = history && _offset > 0;
        }

        private void ReadRest(string file, List<DevTelemetryRecord> records)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _offset)
                    _offset = 0; // the file was replaced
                stream.Seek(_offset, SeekOrigin.Begin);
                int read;
                while ((read = stream.Read(_buffer, 0, _buffer.Length)) > 0)
                {
                    _offset += read;
                    if (_chars.Length < _decoder.GetCharCount(_buffer, 0, read))
                        _chars = new char[_decoder.GetCharCount(_buffer, 0, read) * 2];
                    int count = _decoder.GetChars(_buffer, 0, read, _chars, 0);
                    Split(_chars, count, records);
                }
            }
            catch (IOException)
            {
                // The server is rotating or the file went away: try again next poll.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>The <c>*.jsonl</c> files that sort after <paramref name="file"/>, oldest first.</summary>
        private List<string> FilesAfter(string file)
        {
            var later = new List<string>();
            if (!Directory.Exists(_directory))
                return later;
            string name = Path.GetFileName(file);
            foreach (var candidate in Directory.EnumerateFiles(_directory, "*.jsonl"))
            {
                if (string.CompareOrdinal(Path.GetFileName(candidate), name) > 0)
                    later.Add(candidate);
            }
            later.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            return later;
        }

        private void Split(char[] chars, int count, List<DevTelemetryRecord> records)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                if (chars[i] != '\n')
                    continue;
                if (_skipToNewline)
                {
                    _skipToNewline = false;
                    _partial.Clear();
                }
                else
                {
                    _partial.Append(chars, start, i - start);
                    ParseLine(_partial.ToString(), records);
                    _partial.Clear();
                }
                start = i + 1;
            }
            if (start < count && !_skipToNewline)
                _partial.Append(chars, start, count - start);
        }

        /// <summary>Parses one JSON line; lines of other kinds and the file header are skipped.</summary>
        public static bool TryParse(string line, out DevTelemetryRecord record)
        {
            record = default;
            line = line.Trim();
            if (line.Length == 0 || line[0] != '{')
                return false;
            if (!ConfigJson.TryParse(line, out var root, out _) || root.Kind != ConfigJsonKind.Object)
                return false;
            if (!root.TryGetMember(DevTelemetryLayout.KindKey, out var kindValue) || kindValue.Kind != ConfigJsonKind.String
                || !DevTelemetryLayout.TryKind(kindValue.Text, out var kind))
                return false;
            var layout = DevTelemetryLayout.Kinds[(int)kind];
            record = new DevTelemetryRecord(kind, Long(root, DevTelemetryLayout.TimeKey), Long(root, layout.Subject),
                Long(root, layout.Target), Long(root, layout.V0), Long(root, layout.V1), Long(root, layout.V2), Long(root, layout.V3));
            return true;
        }

        private void ParseLine(string line, List<DevTelemetryRecord> records)
        {
            if (TryParse(line, out var record))
                records.Add(record);
            else if (line.Contains("\"" + DevTelemetryLayout.KindKey + "\"") && !ConfigJson.TryParse(line.Trim(), out _, out _))
                BadLines++;
        }

        private static long Long(ConfigJsonValue root, string? name) =>
            name != null && root.TryGetMember(name, out var value) && value.TryGetInt64(out long number) ? number : 0;

        private string? NewestFile()
        {
            if (!Directory.Exists(_directory))
                return null;
            string? newest = null;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
            {
                if (newest == null || string.CompareOrdinal(Path.GetFileName(file), Path.GetFileName(newest)) > 0)
                    newest = file;
            }
            return newest;
        }

        private static long LengthOf(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }
}
