using GameServer.Combat;
using GameServer.Entities;
using GameServer.Net;
using GameServer.Progression;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.Bystial;

/// <summary>
/// Book changes asked for by the book window (spec 14.2 R-14.2-04/06, 9.13; WP-M2-8), in phase 1 on the world thread. The
/// client names a card and a slot; the place, swap or take-out is <see cref="BystialService.TryChange"/> (the only writer of
/// the book, rule 4). The book is the player's own, so it is save class 2 (R-7.1-01).
/// <list type="bullet">
/// <item>Operation key <c>book:{runId}:{entityId}:{connection}:{requestId}</c>: unique for ever (the database keeps every
/// key), and the same for a repeated request id on one connection, which gets the first answer and changes nothing
/// (rule 5, double submit).</item>
/// <item>Free out of combat only (R-14.2-06 with the blocks of R-3.5-04): refused with <c>InCombat</c> in the combat
/// state of spec 12.7 (a boss fight is combat) and during an action, the gear change rule. A dead player's action state is
/// not idle. TODO(M4): instances block it too once they exist.</item>
/// <item>A refusal is answered in the same tick's phase 10. An accepted change is answered only once the save that holds
/// it committed (rule 5), right before the <see cref="BookState"/> that shows it.</item>
/// </list>
/// </summary>
public sealed class BookChangeHandler
{
    private readonly BystialService _book;
    private readonly ProgressionStore _progression;
    private readonly string _runId;
    private readonly Func<long> _utcNowMs;

    /// <param name="runId">Unique per world run, so operation keys never repeat across restarts.</param>
    /// <param name="utcNowMs">The world clock (<c>IClock.UtcNowMs</c>), the one <c>Player.LastCombatAtMs</c> is in.</param>
    public BookChangeHandler(BystialService book, ProgressionStore progression, string runId, Func<long> utcNowMs)
    {
        _book = book;
        _progression = progression;
        _runId = runId;
        _utcNowMs = utcNowMs;
    }

    /// <summary>Applies one change request of <paramref name="player"/> and queues its answer.</summary>
    public void Handle(Player player, in ClientRequest request)
    {
        if (!_book.IsAttached(player.CharacterId) || !_progression.TryGetSnapshot(player.CharacterId, out var progress))
        {
            Refuse(player, request.RequestId, BookReason.BookUnavailable);
            return;
        }
        string opKey = OpKey(player, request.RequestId);
        // A repeat of an applied request gets its first answer below, whatever the combat state is now (rule 5).
        if (!_book.WasApplied(player.CharacterId, opKey)
            && (CombatState.IsInCombat(player.LastCombatAtMs, _utcNowMs(), _book.Rules.CombatStateMs) || player.Actions.IsBusy))
        {
            Refuse(player, request.RequestId, BookReason.InCombat);
            return;
        }
        var outcome = _book.TryChange(player.CharacterId, request.ItemNetId, request.ToSlot, progress.Rank, opKey);
        if (!outcome.Applied)
        {
            Refuse(player, request.RequestId, outcome.Reason);
            return;
        }
        player.Book.PendingConfirmations.Add(new PendingBookConfirmation(opKey,
            new BookChangeResult { RequestId = request.RequestId, Reason = BookReason.None }));
    }

    public string OpKey(Player player, uint requestId) => $"book:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    private static void Refuse(Player player, uint requestId, BookReason reason) =>
        player.Book.Refusals.Add(new BookChangeResult { RequestId = requestId, Reason = reason });
}
