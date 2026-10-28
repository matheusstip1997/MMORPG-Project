using GameServer.Bystial;
using GameServer.Entities;
using GameServer.Progression;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 14.2, 9.4; WP-M2-8): a player's Bystial book. First the answers to its change requests: refusals at
/// once, accepted changes once their save committed (rule 5); then the <see cref="BookState"/> when the book or a counter
/// changed or the client is new, so a confirmation always comes right before the state that shows it. Reliable channel,
/// MemoryPack, never the hot snapshot; at most one state per player per tick.
/// </summary>
internal sealed class BookSender
{
    private readonly ITransport _transport;

    public BookSender(ITransport transport)
    {
        _transport = transport;
    }

    public long Sent { get; private set; }

    public void Send(Player player, BystialService? book, ProgressionStore? progression)
    {
        var answers = player.Book;
        foreach (var refusal in answers.Refusals)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.BookChangeResult, refusal));
        answers.Refusals.Clear();
        if (book == null || progression == null || !progression.TryGetSnapshot(player.CharacterId, out var progress)
            || !book.IsAttached(player.CharacterId))
            return;
        var pending = answers.PendingConfirmations;
        int confirmed = 0;
        while (confirmed < pending.Count && book.IsCommitted(player.CharacterId, pending[confirmed].OpKey))
        {
            _transport.Send(player.PeerId, Channel.ReliableOrdered,
                ReliableMessages.Write(ReliableMessageId.BookChangeResult, pending[confirmed].Result));
            confirmed++;
        }
        pending.RemoveRange(0, confirmed);
        if (!answers.Dirty)
            return;
        answers.Dirty = false;
        var state = book.StateFor(player.CharacterId, progress.Rank, ++answers.Version, answers.Cause);
        answers.Cause = BookCause.Initial;
        _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.BookState, state));
        Sent++;
    }
}
