using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.Outposts;

/// <summary>
/// A system's answer to one NPC interaction kind (spec 4.2; WP-M2-4 registers sell and buy, WP-M2-3 rankUp, WP-M2-6
/// quests). Called in phase 1 on the world thread after <see cref="OutpostInteractions.CheckReach"/> passed.
/// </summary>
public interface IOutpostInteractionHandler
{
    /// <summary>
    /// Handles one interaction. Return the reason to answer now (<see cref="OutpostReason.None"/>: go ahead, the client
    /// opens its window); return null when the handler answers later itself (for example after a rank-up committed).
    /// </summary>
    OutpostReason? Interact(Player player, Npc npc, uint requestId);
}

/// <summary>
/// The NPC interaction registry and its common gate (spec 4.2, WP-M2-4). A reliable <see cref="OutpostInteractRequest"/>
/// names an NPC and an interaction kind; the gate checks that the NPC exists and the player sees it
/// (<see cref="IVisibilityQuery"/>, rule 9), that its <c>NpcDef.interactions</c> offer the kind and a handler is
/// registered, and that the player stands within <c>NpcDef.interactRangeCm</c> (reason <c>OutOfRange</c>); then the kind's
/// handler runs. The first successful interaction with an outpost NPC in a visit publishes <see cref="OutpostVisited"/>.
/// Sale and purchase requests use the same gate. Simulation thread only.
/// </summary>
public sealed class OutpostInteractions
{
    private readonly Func<uint, Entity?> _findEntity;
    private readonly IVisibilityQuery _visibility;
    private readonly EventBus? _events;
    private readonly IClock _clock;
    private readonly int _visitGapMs;
    private readonly Dictionary<NpcInteraction, IOutpostInteractionHandler> _handlers = new();

    public OutpostInteractions(Func<uint, Entity?> findEntity, IVisibilityQuery visibility, EventBus? events, IClock clock,
        EconomySettings settings)
    {
        _findEntity = findEntity;
        _visibility = visibility;
        _events = events;
        _clock = clock;
        _visitGapMs = settings.VisitGapMs;
    }

    /// <summary>Registers the handler of an interaction kind, once, when the world is built.</summary>
    public void Register(NpcInteraction kind, IOutpostInteractionHandler handler)
    {
        if (kind == NpcInteraction.None || !Enum.IsDefined(typeof(NpcInteraction), kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!_handlers.TryAdd(kind, handler))
            throw new InvalidOperationException($"interaction {kind} already has a handler");
    }

    public bool IsRegistered(NpcInteraction kind) => _handlers.ContainsKey(kind);

    /// <summary>
    /// The common gate: None when <paramref name="player"/> may use interaction <paramref name="kind"/> of the NPC
    /// <paramref name="npcEntityId"/> now.
    /// </summary>
    public OutpostReason CheckReach(Player player, uint npcEntityId, NpcInteraction kind, out Npc? npc)
    {
        npc = _findEntity(npcEntityId) as Npc;
        if (npc == null || !_visibility.CanSee(player, npc))
        {
            npc = null;
            return OutpostReason.NpcNotFound;
        }
        if (!npc.Def.Offers(kind) || !_handlers.ContainsKey(kind))
            return OutpostReason.InteractionUnavailable;
        long dx = npc.XCm - player.XCm, dy = npc.YCm - player.YCm;
        long range = npc.Def.InteractRangeCm;
        return dx * dx + dy * dy > range * range ? OutpostReason.OutOfRange : OutpostReason.None;
    }

    /// <summary>Phase 1: one <see cref="OutpostInteractRequest"/> of <paramref name="player"/>, answered in phase 10.</summary>
    internal void Handle(Player player, in ClientRequest request)
    {
        var kind = request.Interaction;
        var reason = CheckReach(player, request.DropEntityId, kind, out var npc);
        if (reason == OutpostReason.None)
        {
            NoteVisit(player, npc!);
            var answer = _handlers[kind].Interact(player, npc!, request.RequestId);
            if (answer == null)
                return; // the handler answers later
            reason = answer.Value;
        }
        player.Outposts.Interactions.Add(new OutpostInteractResult
        {
            RequestId = request.RequestId,
            NpcEntityId = request.DropEntityId,
            Kind = kind,
            Reason = reason,
        });
    }

    /// <summary>OutpostVisited on the first interaction with an outpost NPC after <c>visitGapMs</c> without one.</summary>
    private void NoteVisit(Player player, Npc npc)
    {
        if (npc.Def.Kind != NpcKind.Outpost)
            return;
        long now = _clock.UtcNowMs;
        var visits = player.Outposts.LastInteractionMs;
        bool newVisit = !visits.TryGetValue(npc.Id, out long last) || now - last >= _visitGapMs;
        visits[npc.Id] = now;
        if (newVisit)
            _events?.Publish(new OutpostVisited(player.CharacterId, npc.Id, npc.Def.NetId), player.CharacterId, player.RegionIndex);
    }
}
