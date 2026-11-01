#nullable enable
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
    }

    /// <summary>One line of the chat panel.</summary>
    public readonly struct ChatLine
    {
        public ChatLine(ChatLineKind kind, uint senderEntityId, string name, string text)
        {
            Kind = kind;
            SenderEntityId = senderEntityId;
            Name = name;
            Text = text;
        }

        public ChatLineKind Kind { get; }

        public uint SenderEntityId { get; }

        /// <summary>The sender's display name from the server (<c>DisplayIdentity</c>); empty for a refusal.</summary>
        public string Name { get; }

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

        public void AddLine(uint senderEntityId, string name, string text) =>
            Add(new ChatLine(ChatLineKind.Player, senderEntityId, name, text));

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
            InputOpen = false;
            NotifyChanged();
        }

        private void Add(in ChatLine line)
        {
            if (_lines.Count == HistoryLines)
                _lines.RemoveAt(0);
            _lines.Add(line);
            NotifyChanged();
        }
    }
}
