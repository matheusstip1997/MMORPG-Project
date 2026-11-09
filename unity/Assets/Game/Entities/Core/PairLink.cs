#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// One side of a pair's board (WP-M4-12, spec 11.5): this side posts keys with values, the partner's side reads them;
    /// the newest value of a key wins. Keys and values carry no tab, carriage return or newline. Tools only (bots in pairs,
    /// the QA partner bot): never game state, which comes from the server.
    /// </summary>
    public interface IPairBoard
    {
        /// <summary>Posts <paramref name="value"/> under <paramref name="key"/> on this side (the partner reads it).</summary>
        void Post(string key, string value);

        /// <summary>The newest value the partner posted under <paramref name="key"/>.</summary>
        bool TryReadPartner(string key, out string value);
    }

    /// <summary>How a meeting of the pair stands (<see cref="PairLink.Check"/>).</summary>
    public enum MeetState : byte
    {
        /// <summary>This side arrived; the partner has not (yet).</summary>
        Waiting,
        /// <summary>Both sides arrived at the same occurrence of the point.</summary>
        Met,
        /// <summary>The partner gave up this occurrence (its step ended without meeting: failed or timed out).</summary>
        PartnerGaveUp,
        /// <summary>This side gave this occurrence up (its step ended before meeting): never met afterwards.</summary>
        GivenUp,
        /// <summary>The partner is gone (its script finished, it disconnected) before it arrived.</summary>
        PartnerGone,
    }

    /// <summary>
    /// The pair plumbing of bots in pairs and the QA partner bot (WP-M4-12, spec 11.5; contract in
    /// <c>src/Tools/BotClient/Loop/Steps/README.md</c>, "Pairs"). Over an <see cref="IPairBoard"/> it gives:
    /// <list type="bullet">
    /// <item>the partner's character name (<c>name</c>), looked up by the caller in the <see cref="EntityIdentities"/> the
    /// server sent through <c>DisplayIdentity</c> (rule 8), and its last posted position (<c>at</c>);</item>
    /// <item>meetings: a step that meets <see cref="Open"/>s its n-th occurrence of a named point when it begins,
    /// <see cref="Arrive(string, int)"/>s once there and <see cref="Check"/>s until the partner arrived at the same occurrence; a
    /// step that ends without meeting gives its occurrence up (<see cref="GiveUpOpen"/>), so the partner stops waiting and both
    /// sides still advance once per step entry;</item>
    /// <item>data posts (<c>data.&lt;key&gt;</c>) for what the server does not tell;</item>
    /// <item>script requests (the QA smoke asks the partner bot to play a named script: <c>script.&lt;n&gt;</c>, answered
    /// with <c>result.&lt;n&gt;</c>).</item>
    /// </list>
    /// Game outcomes (a party formed, a trade committed) are read from the server's messages, never from the board: the
    /// board only says "I am here" and "I am ready". Engine free (ClientCore compiles it); C# 9.
    /// </summary>
    public sealed class PairLink
    {
        public const string NameKey = "name";
        public const string PositionKey = "at";
        public const string GoneKey = "gone";

        private readonly Dictionary<string, int> _opened = new Dictionary<string, int>(StringComparer.Ordinal); // the last occurrence per point
        private readonly Dictionary<string, bool> _arrived = new Dictionary<string, bool>(StringComparer.Ordinal); // "<point>.<n>"
        private readonly HashSet<string> _open = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _met = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _gaveUp = new HashSet<string>(StringComparer.Ordinal);
        private int _requests;
        private int _lastXCm = int.MinValue, _lastZCm = int.MinValue;

        public PairLink(IPairBoard board, bool isFirst)
        {
            Board = board ?? throw new ArgumentNullException(nameof(board));
            IsFirst = isFirst;
        }

        public IPairBoard Board { get; }

        /// <summary>The first of the pair (the even bot of a <c>BotScenario.pairs</c> run; the player in the QA smoke).</summary>
        public bool IsFirst { get; }

        /// <summary>The partner's character name once it posted it (as the server's <c>EntityIdentity</c> shows it), else null.</summary>
        public string? PartnerName => Board.TryReadPartner(NameKey, out var name) && name.Length > 0 ? name : null;

        /// <summary>The partner posted <c>gone</c>: its script finished, or it left the world.</summary>
        public bool PartnerGone => Board.TryReadPartner(GoneKey, out _);

        /// <summary>Posts this side's character name (once in the world).</summary>
        public void SayName(string name) => Board.Post(NameKey, name);

        /// <summary>Posts that this side is gone (script finished, disconnected): meetings it never reached end for the partner.</summary>
        public void SayGone() => Board.Post(GoneKey, "1");

        /// <summary>
        /// Posts this side's position (cm, ground plane) when it moved at least <paramref name="minMoveCm"/> since the last post,
        /// so a partner whose entity is out of interest knows where to walk.
        /// </summary>
        public void PostPosition(int xCm, int zCm, int minMoveCm = 0)
        {
            if (_lastXCm != int.MinValue)
            {
                long dx = (long)xCm - _lastXCm, dz = (long)zCm - _lastZCm;
                if (dx * dx + dz * dz < (long)minMoveCm * minMoveCm)
                    return;
            }
            _lastXCm = xCm;
            _lastZCm = zCm;
            Board.Post(PositionKey, xCm.ToString(CultureInfo.InvariantCulture) + "," + zCm.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The partner's last posted position (cm).</summary>
        public bool TryPartnerPosition(out int xCm, out int zCm)
        {
            xCm = zCm = 0;
            if (!Board.TryReadPartner(PositionKey, out var at))
                return false;
            int comma = at.IndexOf(',');
            return comma > 0
                && int.TryParse(at.Substring(0, comma), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out xCm)
                && int.TryParse(at.Substring(comma + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out zCm);
        }

        /// <summary>
        /// Reserves this side's next occurrence of <paramref name="point"/> (1, 2, ... per point: a loop meets at the same point
        /// every round) when a step that meets there begins, before it walks: both sides advance once per step entry, whether
        /// the step arrives or ends first (a step that ends without arriving gives the occurrence up, <see cref="GiveUpOpen"/>).
        /// </summary>
        public int Open(string point)
        {
            CheckPoint(point);
            _opened.TryGetValue(point, out int n);
            n++;
            _opened[point] = n;
            _open.Add(Key(point, n));
            return n;
        }

        /// <summary>
        /// This side arrived at occurrence <paramref name="occurrence"/> of <paramref name="point"/> (one it <see cref="Open"/>ed)
        /// and posts it (<c>meet.&lt;point&gt;</c>, the highest occurrence reached).
        /// </summary>
        public void Arrive(string point, int occurrence)
        {
            CheckPoint(point);
            _arrived[Key(point, occurrence)] = true;
            Board.Post("meet." + point, occurrence.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary><see cref="Open"/> and <see cref="Arrive(string, int)"/> at once (a caller already where it meets); returns the occurrence.</summary>
        public int Arrive(string point)
        {
            int n = Open(point);
            Arrive(point, n);
            return n;
        }

        /// <summary>
        /// The meeting at <paramref name="point"/>, occurrence <paramref name="occurrence"/>: <see cref="MeetState.Met"/> once both
        /// sides arrived at it (the partner's highest occurrence reached is at least it, and it did not give this one up);
        /// <see cref="MeetState.GivenUp"/> when this side gave it up; <see cref="MeetState.PartnerGaveUp"/> when the partner did
        /// (<c>gaveup.&lt;point&gt;.&lt;n&gt;</c>, one key per occurrence); <see cref="MeetState.PartnerGone"/> when the partner left
        /// before arriving; else <see cref="MeetState.Waiting"/>.
        /// </summary>
        public MeetState Check(string point, int occurrence)
        {
            string key = Key(point, occurrence);
            if (_met.Contains(key))
                return MeetState.Met;
            if (_gaveUp.Contains(key))
                return MeetState.GivenUp;
            if (Board.TryReadPartner("gaveup." + key, out _))
                return MeetState.PartnerGaveUp;
            if (_arrived.ContainsKey(key) && PartnerNumber("meet." + point) >= occurrence)
            {
                _met.Add(key);
                _open.Remove(key);
                return MeetState.Met;
            }
            return PartnerGone ? MeetState.PartnerGone : MeetState.Waiting;
        }

        /// <summary>
        /// Ends every occurrence this side opened and has not seen met (a step ended: failed, timed out, or moved on). One this side
        /// arrived at and the partner reached too counts as met (the partner saw it met: no give-up for it); any other is given
        /// up (<c>gaveup.&lt;point&gt;.&lt;n&gt;</c>), so the partner's <see cref="Check"/> answers
        /// <see cref="MeetState.PartnerGaveUp"/> instead of waiting. A given-up occurrence stays <see cref="MeetState.GivenUp"/>.
        /// </summary>
        public void GiveUpOpen()
        {
            if (_open.Count == 0)
                return;
            foreach (var key in _open)
            {
                int dot = key.LastIndexOf('.');
                string point = key.Substring(0, dot);
                int n = int.Parse(key.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture);
                if (_arrived.ContainsKey(key) && PartnerNumber("meet." + point) >= n && !Board.TryReadPartner("gaveup." + key, out _))
                {
                    _met.Add(key);
                    continue;
                }
                _gaveUp.Add(key);
                Board.Post("gaveup." + key, "1");
            }
            _open.Clear();
        }

        private static string Key(string point, int occurrence) => point + "." + occurrence.ToString(CultureInfo.InvariantCulture);

        /// <summary>A fact for the partner that the server does not tell (<c>data.&lt;key&gt;</c>; keys <c>&lt;step&gt;.&lt;what&gt;</c>).</summary>
        public void Post(string key, string value) => Board.Post("data." + key, value);

        /// <summary>The partner's newest <see cref="Post"/> under <paramref name="key"/>.</summary>
        public bool TryRead(string key, out string value) => Board.TryReadPartner("data." + key, out value);

        /// <summary>The QA smoke's side: asks the partner bot to play its script <paramref name="scriptId"/>; returns the request number.</summary>
        public int Request(string scriptId)
        {
            _requests++;
            Board.Post("script." + _requests.ToString(CultureInfo.InvariantCulture), scriptId);
            return _requests;
        }

        /// <summary>The partner bot's side: the script of request <paramref name="number"/>, once posted.</summary>
        public bool TryReadRequest(int number, out string scriptId) =>
            Board.TryReadPartner("script." + number.ToString(CultureInfo.InvariantCulture), out scriptId);

        /// <summary>The partner bot's side: answers request <paramref name="number"/> (<see cref="PairScriptResult"/>'s text).</summary>
        public void Answer(int number, string result) =>
            Board.Post("result." + number.ToString(CultureInfo.InvariantCulture), result);

        /// <summary>The QA smoke's side: the partner bot's answer to request <paramref name="number"/>, once posted.</summary>
        public bool TryReadResult(int number, out string result) =>
            Board.TryReadPartner("result." + number.ToString(CultureInfo.InvariantCulture), out result);

        private int PartnerNumber(string key) =>
            Board.TryReadPartner(key, out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

        private static void CheckPoint(string point)
        {
            if (string.IsNullOrEmpty(point))
                throw new ArgumentException("A meeting point is required.", nameof(point));
        }

        /// <summary>Throws for a key or value a board line cannot carry (empty key, tab, carriage return, newline).</summary>
        public static void CheckEntry(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || HasBreak(key))
                throw new ArgumentException($"Pair board key '{key}' is empty or holds a tab or line break.", nameof(key));
            if (value == null || HasBreak(value))
                throw new ArgumentException($"Pair board value of '{key}' holds a tab or line break.", nameof(value));
        }

        /// <summary>A text made safe for a board value (tabs and line breaks become spaces).</summary>
        public static string Clean(string text) =>
            (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        private static bool HasBreak(string text) => text.IndexOf('\t') >= 0 || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
    }

    /// <summary>
    /// The text of a partner script's answer (<c>result.&lt;n&gt;</c>, WP-M4-12): <c>done</c>, or the first entry that was not
    /// done as <c>&lt;outcome&gt;; &lt;entry&gt;: &lt;reason&gt;</c> (outcome <c>not yet</c>, <c>failed</c>, <c>timed out</c>),
    /// followed by every entry as <c>; &lt;entry&gt; &lt;outcome&gt;</c>.
    /// </summary>
    public static class PairScriptResult
    {
        public const string Done = "done";
        public const string NotYet = "not yet";
        public const string Failed = "failed";
        public const string TimedOut = "timed out";

        /// <summary>The outcome word of an answer (<see cref="Done"/>, <see cref="NotYet"/>, <see cref="Failed"/>, <see cref="TimedOut"/>).</summary>
        public static string Outcome(string result)
        {
            int semicolon = (result ?? string.Empty).IndexOf(';');
            return (semicolon < 0 ? result ?? string.Empty : result!.Substring(0, semicolon)).Trim();
        }
    }

    /// <summary>
    /// The pair board inside one process (WP-M4-12: <c>loadtest</c> pairs, tests): two sides over two dictionaries. Bots
    /// tick on one thread in index order, so it takes no lock.
    /// </summary>
    public sealed class MemoryPairBoard : IPairBoard
    {
        private readonly Dictionary<string, string> _own;
        private readonly Dictionary<string, string> _partner;

        private MemoryPairBoard(Dictionary<string, string> own, Dictionary<string, string> partner)
        {
            _own = own;
            _partner = partner;
        }

        /// <summary>The two sides of a new pair's board.</summary>
        public static MemoryPairBoard[] NewPair()
        {
            var a = new Dictionary<string, string>(StringComparer.Ordinal);
            var b = new Dictionary<string, string>(StringComparer.Ordinal);
            return new[] { new MemoryPairBoard(a, b), new MemoryPairBoard(b, a) };
        }

        public void Post(string key, string value)
        {
            PairLink.CheckEntry(key, value);
            _own[key] = value;
        }

        public bool TryReadPartner(string key, out string value)
        {
            if (_partner.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }
            value = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// The pair board across two processes (WP-M4-12: the QA smoke and its partner bot): each side appends
    /// <c>key&lt;TAB&gt;value</c> lines to its own log, <c>&lt;directory&gt;/&lt;side&gt;.log</c>, and reads the other side's log
    /// incrementally, whole lines only (a line still being written waits for the next read). Append only: no overwrite is
    /// needed, and the two logs stay in the run folder as evidence.
    /// </summary>
    public sealed class FilePairBoard : IPairBoard
    {
        /// <summary>The Unity side of the QA smoke (the first of the pair).</summary>
        public const string PlayerSide = "player";

        /// <summary>The partner bot's side.</summary>
        public const string BotSide = "bot";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private readonly string _ownPath;
        private readonly string _partnerPath;
        private readonly Dictionary<string, string> _partner = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>
        /// The partner's log is read at most this often (WP-M4-12 review: a step polls the board every tick or frame; one read per
        /// interval opens the file once instead of several times per frame). Tool plumbing, not a game rate.
        /// </summary>
        public const int DefaultRefreshIntervalMs = 50;

        private readonly int _refreshIntervalMs;
        private readonly System.Diagnostics.Stopwatch _sinceRefresh = new System.Diagnostics.Stopwatch();
        private long _offset;

        /// <param name="refreshIntervalMs">How often the partner's log is read at most; 0 reads it on every look (tests).</param>
        public FilePairBoard(string directory, string ownSide, string partnerSide, int refreshIntervalMs = DefaultRefreshIntervalMs)
        {
            _refreshIntervalMs = refreshIntervalMs;
            Directory.CreateDirectory(directory);
            _ownPath = Path.Combine(directory, ownSide + ".log");
            _partnerPath = Path.Combine(directory, partnerSide + ".log");
        }

        /// <summary>The log this side writes.</summary>
        public string OwnPath => _ownPath;

        public void Post(string key, string value)
        {
            PairLink.CheckEntry(key, value);
            byte[] line = Utf8.GetBytes(key + "\t" + value + "\n");
            using (var stream = new FileStream(_ownPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Write(line, 0, line.Length);
                stream.Flush();
            }
        }

        public bool TryReadPartner(string key, out string value)
        {
            if (!_sinceRefresh.IsRunning || _sinceRefresh.ElapsedMilliseconds >= _refreshIntervalMs)
            {
                Refresh();
                _sinceRefresh.Restart();
            }
            if (_partner.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }
            value = string.Empty;
            return false;
        }

        /// <summary>Reads the partner's lines appended since the last read.</summary>
        public void Refresh()
        {
            if (!File.Exists(_partnerPath))
                return;
            byte[] bytes;
            try
            {
                using (var stream = new FileStream(_partnerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= _offset)
                        return;
                    stream.Seek(_offset, SeekOrigin.Begin);
                    bytes = new byte[stream.Length - _offset];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0)
                            break;
                        read += n;
                    }
                    if (read < bytes.Length)
                        Array.Resize(ref bytes, read);
                }
            }
            catch (IOException)
            {
                return; // the writer holds it this instant: the next read takes it
            }
            int end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0)
                return; // a line still being written
            foreach (var line in Utf8.GetString(bytes, 0, end).Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0)
                    _partner[line.Substring(0, tab)] = line.Substring(tab + 1);
            }
            _offset += end + 1;
        }
    }
}
