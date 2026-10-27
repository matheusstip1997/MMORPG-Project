using GameServer.Entities;
using GameServer.Progression;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 3.6, 9.4; WP-M2-3): a player's own level, exp, rank and skill points (<see cref="ProgressionState"/>)
/// when they changed or the client is new, with the band multiplier of R-3.6-04 for the exp note. Reliable channel,
/// MemoryPack, never the hot snapshot. A rank-up is marked changed only once its save committed (rule 5). At most one
/// message per player per tick, so encoding allocates only on a change.
/// </summary>
internal sealed class ProgressionSender
{
    private readonly ITransport _transport;

    public ProgressionSender(ITransport transport)
    {
        _transport = transport;
    }

    public long Sent { get; private set; }

    public void Send(Player player, ProgressionStore? store)
    {
        if (!player.ProgressionDirty || store == null || !store.TryGetSnapshot(player.CharacterId, out var progress))
            return;
        player.ProgressionDirty = false;
        var curves = store.Rules.Curves;
        var state = new ProgressionState
        {
            Level = progress.Level,
            Exp = progress.Exp,
            ExpToNext = curves.ExpToNext(progress.Level),
            Rank = progress.Rank,
            SkillPoints = progress.SkillPoints,
            BandPercent = (int)(curves.BandPpm(progress.Level, progress.Rank) / (Shared.Simulation.Progression.ExpMath.PpmOne / 100)),
            Cause = player.ProgressionCause,
        };
        player.ProgressionCause = ProgressionCause.Initial;
        _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.ProgressionState, state));
        Sent++;
    }
}
