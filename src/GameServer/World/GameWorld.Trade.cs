using GameServer.Combat;
using GameServer.Entities;
using GameServer.Items;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.Social;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.World;

/// <summary>
/// Trades and escrow in the world (spec 4.5, M4 decision 5; WP-M4-4): <see cref="TradeService"/> reads the trade requests in
/// phase 1 (after the other requests, in entity id order) and checks the open trades every tick; <see cref="EscrowService"/>
/// holds assets outside characters for listings and contracts. Both answer through the outbox in phase 10. The save gate
/// (<see cref="CanLinkSaves"/>, <see cref="RequestTransferSave"/>) is the session manager's scheduler. Without the item tables
/// or <c>TradeSettings</c> there is no trade service (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    private TradeService? _trades;
    private EscrowService? _escrow;
    private TradeSender? _tradeSender;
    private int _tradeCombatStateMs;

    /// <summary>The trade window of the world's characters (WP-M4-4); null without the item tables or <c>TradeSettings</c>.</summary>
    public TradeService? Trades => _trades;

    /// <summary>Escrow (WP-M4-4): the only holder of assets outside a character; null without the item tables.</summary>
    public EscrowService? Escrow => _escrow;

    /// <summary>Both characters can be saved together now (set by the session manager: tracked, not faulted, not linked).</summary>
    public Func<long, long, bool>? CanLinkSaves { get; set; }

    /// <summary>Saves two characters in one transaction (set by the session manager: <c>SaveScheduler.RequestTransferSave</c>).</summary>
    public Func<TransferRecord, bool>? RequestTransferSave { get; set; }

    private void BuildTrade(TradeSettings? settings, Events.EventBus? events, string runId, int combatStateMs)
    {
        _tradeSender = new TradeSender(_transport);
        if (_inventory == null)
            return;
        _escrow = new EscrowService(_inventory, _clock, events);
        if (settings == null)
            return;
        _tradeCombatStateMs = combatStateMs;
        _trades = new TradeService(_inventory, new TradeWorld(this), settings, events, runId);
    }

    /// <summary>Phase 1: one trade request of <paramref name="player"/> (WP-M4-4).</summary>
    private void HandleTradeRequest(Player player, in ClientRequest request)
    {
        var action = (TradeAction)request.FromSlot;
        if (_trades == null)
        {
            _tradeSender?.Refuse(player, request.RequestId, action, TradeReason.TradeUnavailable);
            return;
        }
        _trades.Handle(player.CharacterId, request.RequestId, action, request.DropEntityId, request.FromContainer, request.ItemNetId,
            request.Count, request.ToSlot, request.QuotedTotal, request.BoardEpoch);
    }

    /// <summary>
    /// Phase 1, the dev-line hook (<c>/trade &lt;name&gt;</c>, WP-M4-4): true when the line was a trade command. The name is matched
    /// against the players the sender may see, as <c>DisplayIdentity</c> names them for the sender (rule 8, rule 9).
    /// </summary>
    private bool TryTradeLine(Player sender, string text)
    {
        const string command = "/trade";
        if (!text.StartsWith(command, StringComparison.OrdinalIgnoreCase)
            || (text.Length > command.Length && !char.IsWhiteSpace(text[command.Length])))
            return false;
        if (_trades == null)
        {
            _tradeSender?.Refuse(sender, 0, TradeAction.Invite, TradeReason.TradeUnavailable);
            return true;
        }
        string name = text.Substring(command.Length).Trim();
        uint target = 0;
        if (name.Length > 0)
        {
            foreach (var entity in _entities) // entity id order: the first match wins (rule 6)
            {
                if (entity is Player other && other != sender && sender.Interest.Contains(other) // only players it sees (rule 9)
                    && string.Equals(DisplayIdentity.For(other, sender), name, StringComparison.OrdinalIgnoreCase))
                {
                    target = other.Id;
                    break;
                }
            }
        }
        _trades.InviteByName(sender.CharacterId, target);
        return true;
    }

    /// <summary>Phase 1, after the requests: escrow releases to mail that finished, then the open trades (WP-M4-4).</summary>
    private void TickTrades()
    {
        _escrow?.Tick();
        _trades?.Tick();
    }

    /// <summary>Phase 10: trade answers and states to connected players, in outbox order (WP-M4-4).</summary>
    private void SendTrades()
    {
        if (_trades == null || _tradeSender == null)
            return;
        _tradeSender.Send(_trades.Outbox, characterId => PlayerOf(characterId));
        _trades.Outbox.Clear();
    }

    /// <summary>The world as the trade sees it: players by character or entity, names through DisplayIdentity, the save gate.</summary>
    private sealed class TradeWorld(GameWorld world) : ITradeWorld
    {
        public long NowMs => world._clock.UtcNowMs;

        public bool TryGetCharacter(long characterId, out TradeParticipant participant)
        {
            participant = default;
            if (world.PlayerOf(characterId) is not { } player)
                return false;
            participant = Of(player);
            return true;
        }

        public bool TryGetEntity(uint entityId, out TradeParticipant participant)
        {
            participant = default;
            if (world._findEntity(entityId) is not Player player || !world._playersByCharacter.ContainsKey(player.CharacterId))
                return false;
            participant = Of(player);
            return true;
        }

        public string NameFor(long characterId, long observerCharacterId) =>
            world.PlayerOf(characterId) is { } player && world.PlayerOf(observerCharacterId) is { } observer
                ? DisplayIdentity.For(player, observer)
                : string.Empty;

        public bool CanLink(long characterA, long characterB) => world.CanLinkSaves?.Invoke(characterA, characterB) ?? false;

        public bool RequestTransferSave(TransferRecord transfer) => world.RequestTransferSave?.Invoke(transfer) ?? false;

        private TradeParticipant Of(Player player) => new(player.CharacterId, player.Id, player.XCm, player.YCm,
            player.Combatant is not { IsAlive: false },
            CombatState.IsInCombat(player.LastCombatAtMs, world._clock.UtcNowMs, world._tradeCombatStateMs), player.IsConnected);
    }
}
