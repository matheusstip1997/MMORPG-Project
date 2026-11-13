using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.Progression;
using GameServer.Social;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.World;

/// <summary>
/// Parties in the world (spec 6.5 "M4 party", M4 decision 1; WP-M4-2): <see cref="PartyService"/> keeps them in memory; the
/// world reads the party requests in phase 1 (after sweeping expired invites), removes a character from its party when the
/// character leaves the world (the end of its linger, R-2.1-08), and sends the party frame in phase 10. The parties are read by
/// visibility (<see cref="PartyVisibility"/>, built first), damage (<c>PvpRules</c> through the hit resolver), exp
/// (<see cref="ExpService"/> with the <see cref="KillContributions"/> ledger), runs (<c>QuestService</c>) and chat. Without
/// <see cref="PartySettings"/> nobody can form a party (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    private PartyService? _party;
    private PartySender? _partySender;
    private KillContributions? _contributions;

    /// <summary>The world's parties (WP-M4-2); null without <see cref="PartySettings"/>.</summary>
    public PartyService? Parties => _party;

    /// <summary>What other systems read of the parties: none without <see cref="PartySettings"/>.</summary>
    public IPartyQuery PartyQuery => (IPartyQuery?)_party ?? NoParties.Instance;

    /// <summary>The damage each character dealt to a victim (WP-M4-2; WP-M4-6 reads "a hit on the monster"); null without parties or events.</summary>
    public IKillContributions? KillContributions => _contributions;

    private void BuildParty(PartySettings? settings, EventBus? events)
    {
        if (settings == null)
            return;
        _party = new PartyService(settings, events)
        {
            PlayerOf = id => _playersByCharacter.TryGetValue(id, out var player) ? player : null,
            FindByName = FindPlayerByName,
            FindVisible = FindVisiblePlayer,
        };
    }

    /// <summary>The end of the constructor: what the parties change in exp, runs, damage and chat.</summary>
    private void WireParty(EventBus? events, PartyLootParams? partyLoot)
    {
        if (_chat != null)
            _chat.FindByName = FindPlayerByName; // whispers work without parties too
        if (_party == null)
            return;
        var settings = _party.Settings;
        if (_loot != null && partyLoot != null)
        {
            // Spec 4.3 "M4 party loot" (WP-M4-6): drops lock to the killer's party and members' pickups roll. The loot service
            // reads the kill's hits before KillContributions (subscribed below, last) drops them; the debts live in the parties.
            _party.LootDebtHalfLifeMs = partyLoot.DebtHalfLifeMs;
            _loot.PartyLootRules = partyLoot;
            _loot.Parties = _party;
            _loot.Debts = _party;
            _loot.PlayerOf = id => _playersByCharacter.TryGetValue(id, out var player) ? player : null;
            _loot.InterestRadiusCm = _settings.InterestRadiusCm;
            _loot.UnitValue = item => Economy != null && Economy.TryGetBasePrice(item.Id, out int gold) ? gold : 0;
            _loot.Mail = _mail;
            if (_inventory != null)
                _inventory.OpCommitted += _loot.OnOpCommitted; // a roll part is announced once the winner's save holds it
        }
        _partySender = new PartySender(_transport, _party, id => _playersByCharacter.TryGetValue(id, out var player) ? player : null,
            FillMember, settings.StateIntervalMs);
        if (_hits != null)
            _hits.Parties = _party;
        if (_chat != null)
            _chat.Parties = _party;
        _chatSender.Parties = _party;
        _chatSender.PlayerOf = id => _playersByCharacter.TryGetValue(id, out var player) ? player : null;
        if (_quests != null)
            _quests.Parties = _party;
        if (_exp != null)
        {
            _exp.Parties = _party;
            _exp.PartyRules = settings;
            _exp.Presence = PresenceOf;
            if (events != null)
            {
                // Subscribed last, so every Killed consumer reads a victim's ledger before it is dropped.
                _contributions = new KillContributions(settings.MaxContributors, settings.ContributionForgetMs, () => _clock.UtcNowMs);
                _contributions.Subscribe(events);
                _exp.Contributions = _contributions;
            }
        }
        if (_loot != null && partyLoot != null)
            _loot.Contributions = _contributions;
    }

    /// <summary>Phase 1: one party request of <paramref name="player"/>, answered at once.</summary>
    private void HandlePartyRequest(Player player, in ClientRequest request)
    {
        var action = (PartyAction)request.FromSlot;
        if (_party == null)
        {
            player.Party.Results.Add(new PartyResult { RequestId = request.RequestId, Action = action, Reason = PartyReason.PartyNotFound });
            return;
        }
        _party.Handle(player, request.RequestId, action, request.BoardEpoch, request.DropEntityId,
            request.Text ?? string.Empty, _clock.UtcNowMs);
    }

    /// <summary>
    /// The connected player whose display name, as <paramref name="observer"/> sees it, is <paramref name="name"/> (ignoring
    /// case), the lowest entity id first (rule 6); null for none. Through <see cref="DisplayIdentity"/> only (rule 8): a
    /// disguised player is not found by its real name. Walks the connected players only (the peer map, not every entity); the
    /// lowest entity id among the matches wins, so the map's order does not matter.
    /// TODO(M5, open): this lookup has no visibility check, so once stealth exists an invite or a whisper by name confirms that a
    /// hidden player is online (rule 9). The rule is the user's to decide with M5 stealth (spec 6.5); do not invent it here.
    /// </summary>
    internal Player? FindPlayerByName(Player observer, string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        Player? found = null;
        foreach (var player in _playersByPeer.Values)
        {
            if ((found == null || player.Id < found.Id)
                && string.Equals(DisplayIdentity.For(player, observer), name, StringComparison.OrdinalIgnoreCase))
                found = player;
        }
        return found;
    }

    /// <summary>A connected player <paramref name="observer"/> has in interest (rule 9), or itself; null otherwise.</summary>
    private Player? FindVisiblePlayer(Player observer, uint entityId)
    {
        if (FindEntity(entityId) is not Player { IsConnected: true } player)
            return null;
        return player == observer || observer.Interest.Contains(player) ? player : null;
    }

    private MemberPresence PresenceOf(long characterId)
    {
        if (!_playersByCharacter.TryGetValue(characterId, out var player))
            return default;
        return new MemberPresence(true, player.Combatant is { IsAlive: true }, player.XCm, player.YCm, player.Id);
    }

    /// <summary>A member's class, level, online state, HP percent and position for a party frame (phase 10).</summary>
    private bool FillMember(Player member, PartyMemberState entry)
    {
        entry.ClassNetId = 0;
        if (member.Combatant is { } combatant && _combat != null && _combat.TryGetClass(combatant.TemplateId, out var classDef))
            entry.ClassNetId = (ushort)classDef.NetId;
        entry.Level = (ushort)Math.Clamp(_progression != null && _progression.TryGetSnapshot(member.CharacterId, out var progress)
            ? progress.Level : 1, 1, ushort.MaxValue);
        entry.Online = member.IsConnected;
        entry.HpPercent = 0;
        if (member.Combatant is { IsAlive: true } alive)
        {
            long now = NowMs;
            int max = alive.Meters.Max(ResourceKind.Hp, now);
            int hp = alive.Meters.Current(ResourceKind.Hp, now);
            entry.HpPercent = (byte)(max <= 0 ? 0 : Math.Clamp((long)hp * 100 / max, hp > 0 ? 1 : 0, 100));
        }
        entry.XCm = member.XCm;
        entry.YCm = member.YCm;
        return true;
    }

    /// <summary>Phase 10: party answers, invites and the party frame of a connected player.</summary>
    private void SendParty(Player player) => _partySender?.Send(player, _clock.UtcNowMs);
}
