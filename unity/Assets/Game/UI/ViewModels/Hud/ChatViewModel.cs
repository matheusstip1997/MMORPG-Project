#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.UI.ViewModels.Hud
{
    public enum ChatLineKind : byte
    {
        /// <summary>A player's line (another player's, or the echo of the own).</summary>
        Player,
        /// <summary>A refused line: <see cref="ChatLine.Text"/> holds the <c>ReasonCodeStrings</c> code.</summary>
        Refusal,
        /// <summary>A dev console answer line (spec 11.7, WP-M3-13): <see cref="ChatLine.Text"/> is the server's detail line, shown as sent.</summary>
        System,
        /// <summary>
        /// A party loot roll line (WP-M4-6, spec 4.3 R-4.3-02): <see cref="ChatLine.Text"/> is already the <c>UiStrings</c> text, shown on
        /// the Party tab only.
        /// </summary>
        Loot,
    }

    /// <summary>One line of the chat panel.</summary>
    public readonly struct ChatLine
    {
        public ChatLine(ChatLineKind kind, uint senderEntityId, string name, string text, ChatChannel channel = ChatChannel.Local,
            string targetName = "")
        {
            Kind = kind;
            SenderEntityId = senderEntityId;
            Name = name;
            Text = text;
            Channel = channel;
            TargetName = targetName ?? "";
        }

        public ChatLineKind Kind { get; }

        public uint SenderEntityId { get; }

        /// <summary>The sender's display name from the server (<c>DisplayIdentity</c>); empty for a refusal.</summary>
        public string Name { get; }

        /// <summary>The line's channel (WP-M4-2); refusals and system lines show on every tab.</summary>
        public ChatChannel Channel { get; }

        /// <summary>On the echo of an own whisper, the recipient's name ("To X"); empty otherwise.</summary>
        public string TargetName { get; }

        /// <summary>Whether the line shows on the tab of <paramref name="tab"/>.</summary>
        public bool ShowsOn(ChatChannel tab) => (Kind != ChatLineKind.Player && Kind != ChatLineKind.Loot) || Channel == tab;

        /// <summary>The line as the server sent it, or the reason code of a refusal.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// Spec 9.3 <c>chat</c>, Local tab (R-6.5-01; spec 9.12): the last <c>ChatSettings.historyLines</c> lines, oldest first,
    /// and whether the input is open. Lines come only from the server (the own line shows when its echo arrives, so the
    /// panel never shows a line the server refused). <see cref="Submit"/> runs the shared <see cref="ChatText"/> check
    /// before sending, so the player sees the reason at once; the server checks again and decides.
    /// </summary>
    public sealed class ChatViewModel : ViewModel
    {
        private readonly List<ChatLine> _lines = new List<ChatLine>();

        public ChatViewModel(int historyLines, int maxChars)
        {
            HistoryLines = historyLines;
            MaxChars = maxChars;
        }

        public int HistoryLines { get; }

        public int MaxChars { get; }

        public IReadOnlyList<ChatLine> Lines => _lines;

        /// <summary>The text field is open and has focus: gameplay keys are suspended.</summary>
        public bool InputOpen { get; private set; }

        /// <summary>
        /// The tab the panel shows and lines are sent on (WP-M4-2: Local, Party, Whisper). A line of another channel marks its tab
        /// unread; refusals and system lines show on every tab.
        /// </summary>
        public ChatChannel ActiveTab { get; private set; } = ChatChannel.Local;

        /// <summary>
        /// The player a plain line on the Whisper tab goes to: the one this player chose, by its last <c>/w &lt;name&gt;</c> (or
        /// <see cref="SetWhisperPartner"/>); empty until then. An incoming whisper never changes it, so a draft cannot be
        /// retargeted while it is typed (PR #176 review). The panel shows it next to the input.
        /// </summary>
        public string WhisperPartner { get; private set; } = "";

        private readonly bool[] _unread = new bool[(int)ChatCodec.LastChannel + 1];
        private readonly List<ChatLine> _tab = new List<ChatLine>();
        private bool _tabStale = true;

        /// <summary>Whether <paramref name="channel"/>'s tab got a line from another player since it was last shown.</summary>
        public bool HasUnread(ChatChannel channel) => (int)channel < _unread.Length && _unread[(int)channel];

        public void SelectTab(ChatChannel channel)
        {
            if ((int)channel >= _unread.Length || (channel == ActiveTab && !_unread[(int)channel]))
                return;
            ActiveTab = channel;
            _unread[(int)channel] = false;
            _tabStale = true;
            NotifyChanged();
        }

        /// <summary>The player chose whom the Whisper tab's plain lines go to (its <c>/w &lt;name&gt;</c>).</summary>
        public void SetWhisperPartner(string name)
        {
            if (string.IsNullOrEmpty(name) || name == WhisperPartner)
                return;
            WhisperPartner = name;
            NotifyChanged();
        }

        /// <summary>
        /// The lines of the active tab, oldest first: its channel's player lines and the refusals and system lines. Every channel
        /// (and the refusals with the system lines together) keeps its own last <see cref="HistoryLines"/> lines, so local
        /// traffic never evicts a party or whisper line. Rebuilt only after a change.
        /// </summary>
        public IReadOnlyList<ChatLine> TabLines()
        {
            if (!_tabStale)
                return _tab;
            _tab.Clear();
            foreach (var line in _lines)
            {
                if (line.ShowsOn(ActiveTab))
                    _tab.Add(line);
            }
            _tabStale = false;
            return _tab;
        }

        public void AddLine(uint senderEntityId, string name, string text) =>
            AddLine(senderEntityId, name, text, ChatChannel.Local, "", own: false);

        /// <summary>
        /// A line from the server on its channel. A line from another player on another tab marks that tab unread; the echo of an
        /// own line (<paramref name="own"/>) never does, whatever tab it was typed on.
        /// </summary>
        public void AddLine(uint senderEntityId, string name, string text, ChatChannel channel, string targetName, bool own = false)
        {
            if (!own && channel != ActiveTab && (int)channel < _unread.Length)
                _unread[(int)channel] = true;
            Add(new ChatLine(ChatLineKind.Player, senderEntityId, name, text, channel, targetName));
        }

        /// <summary>
        /// A party loot roll line (WP-M4-6): the text from <c>UiStrings</c>, on the Party tab, which it marks unread when another tab is
        /// open; it shares the party lines' history.
        /// </summary>
        public void AddLootLine(string text)
        {
            if (ActiveTab != ChatChannel.Party && (int)ChatChannel.Party < _unread.Length)
                _unread[(int)ChatChannel.Party] = true;
            Add(new ChatLine(ChatLineKind.Loot, 0, "", text, ChatChannel.Party));
        }

        /// <summary>A refusal by the server (<see cref="ChatReason"/>) or the client (a <c>ClientReasonCode</c>), by code.</summary>
        public void AddRefusal(string reasonCode) => Add(new ChatLine(ChatLineKind.Refusal, 0, "", reasonCode));

        /// <summary>
        /// A dev console answer (<see cref="DevCommandResult"/>, WP-M3-13): a refusal line for its reason (none when the command
        /// ran), then one system line per detail line.
        /// </summary>
        public void AddDevAnswer(DevReason reason, IReadOnlyList<string> lines)
        {
            if (reason != DevReason.None)
                Add(new ChatLine(ChatLineKind.Refusal, 0, "", reason.ToString()));
            foreach (var line in lines)
                Add(new ChatLine(ChatLineKind.System, 0, "", line));
        }

        public void OpenInput()
        {
            if (InputOpen)
                return;
            InputOpen = true;
            NotifyChanged();
        }

        public void CloseInput()
        {
            if (!InputOpen)
                return;
            InputOpen = false;
            NotifyChanged();
        }

        /// <summary>
        /// Enter in the open input: checks the draft and closes the input. Returns the trimmed line to send, or null when
        /// there is nothing to send: an empty draft just closes, any other failing check adds its refusal line.
        /// </summary>
        public string? Submit(string draft)
        {
            CloseInput();
            var reason = ChatText.Check(draft, MaxChars, out var clean);
            if (reason == ChatReason.None)
                return clean;
            if (reason != ChatReason.ChatEmpty)
                AddRefusal(reason.ToString());
            return null;
        }

        public void Clear()
        {
            if (_lines.Count == 0 && !InputOpen)
                return;
            _lines.Clear();
            Array.Clear(_unread, 0, _unread.Length);
            _tabStale = true;
            InputOpen = false;
            NotifyChanged();
        }

        /// <summary>Adds a line; when its bucket (its channel, or the refusals and system lines) is full, its oldest line goes.</summary>
        private void Add(in ChatLine line)
        {
            int bucket = Bucket(line);
            int count = 0, oldest = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (Bucket(_lines[i]) != bucket)
                    continue;
                if (oldest < 0)
                    oldest = i;
                count++;
            }
            if (count >= HistoryLines && oldest >= 0)
                _lines.RemoveAt(oldest);
            _lines.Add(line);
            _tabStale = true;
            NotifyChanged();
        }

        private static int Bucket(in ChatLine line) => line.Kind is ChatLineKind.Player or ChatLineKind.Loot ? (int)line.Channel : -1;
    }
}
