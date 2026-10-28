using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.Skills;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Skills;

namespace GameServer.World;

/// <summary>
/// The skill tree in the world (spec 3.5, 9.6; WP-M2-5): <see cref="SkillTreeService"/> holds every character's skills; the
/// world turns its committed state into the player's loadout (<see cref="LoadoutBuilder"/>, the same one client prediction
/// builds), the level multipliers of its skills, the weapon mask of its equipped set (R-3.5-07) and the usable weapons of
/// its inventory, and announces committed operations on the bus (<c>SkillLearned</c>, <c>SkillEvolved</c>,
/// <c>Respecced</c>, <c>ClassAdvanced</c>) and to the player. Without the skill tables a player keeps its class's
/// <c>ActionBindings</c> kit (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    private SkillTreeService? _skills;
    private SkillTreeSender? _skillSender;
    private string _skillRunId = string.Empty;
    private int _combatStateMs;

    /// <summary>The skill tree of the world's characters (WP-M2-5); null without the skill tables.</summary>
    public SkillTreeService? Skills => _skills;

    private void BuildSkills(SkillRules? rules, EventBus? events, string runId, int combatStateMs)
    {
        _skillSender = new SkillTreeSender(_transport);
        if (rules == null || _progression == null)
            return;
        _skillRunId = runId;
        _combatStateMs = combatStateMs;
        _skills = new SkillTreeService(rules, _progression) { OpCommitted = OnSkillOpCommitted, VisibleChanged = OnSkillVisibleChanged };
        if (_stats != null)
            _stats.Skills = _skills;
        events?.Subscribe<EquipmentChanged>(new EquippedWeapons(this));
    }

    /// <summary>
    /// The session attaches a character's skills as loaded, before the spawn (which builds the loadout from them), and the
    /// weapons its inventory accepts (R-3.5-07: its class line and learned weapon unlocks).
    /// </summary>
    internal void AttachSkills(long characterId, string classId, SkillRecord record)
    {
        if (_skills == null)
            return;
        _skills.Attach(characterId, classId, record);
        if (_skills.UsableWeapons(characterId) is { } usable)
            _inventory?.SetUsableWeapons(characterId, usable);
    }

    /// <summary>The spawn's loadout, multipliers and weapon mask (a player built without the skill tables keeps its class kit).</summary>
    private void ApplySkills(Player player)
    {
        if (_skills?.Visible(player.CharacterId) is not { } state)
            return;
        var rules = _skills.Rules;
        player.Actions.Loadout = LoadoutBuilder.Build(_actions, rules, state);
        if (player.Combatant != null)
        {
            var percents = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in state.Learned)
            {
                if (rules.Skill(pair.Key) is { ActionId.Length: > 0 } skill)
                    percents[skill.ActionId] = skill.LevelPercent(pair.Value);
            }
            player.Combatant.SkillPercent = percents;
        }
        UpdateWeapons(player);
    }

    /// <summary>R-3.5-07: the equipped set's weapon types, as the action machine checks them (committed equipment).</summary>
    private void UpdateWeapons(Player player)
    {
        if (_inventory != null)
            player.Actions.EquippedWeapons = WeaponMasks.Of(_inventory.GetEquippedWeaponTypes(player.CharacterId));
    }

    /// <summary>Phase 1: one skill-tree request of <paramref name="player"/>, answered now or once its save committed.</summary>
    private void HandleSkillTreeRequest(Player player, in Net.ClientRequest request)
    {
        if (_skills == null)
        {
            player.SkillTreeAnswers.Add(new SkillTreeResult
            {
                RequestId = request.RequestId, Op = request.SkillOp, Reason = SkillTreeReason.Unavailable, Target = request.Text ?? string.Empty,
            });
            return;
        }
        // Blocked: in combat (instances and boss fights are combat; TODO(M4) instances) or during an action, dead included.
        bool blocked = CombatState.IsInCombat(player.LastCombatAtMs, _clock.UtcNowMs, _combatStateMs) || player.Actions.IsBusy;
        var ask = new SkillTreeAsk(request.RequestId, request.SkillOp, request.Text ?? string.Empty, request.FromSlot,
            SkillOpKey(player, request.RequestId), _progression?.GetLevel(player.CharacterId) ?? 1, blocked);
        if (_skills.Handle(player.CharacterId, ask, player.Connection) is { } answer)
            player.SkillTreeAnswers.Add(answer);
    }

    /// <summary>The idempotency key of a skill request: one per world run, entity, connection and request id (spec 9.13 pattern).</summary>
    internal string SkillOpKey(Player player, uint requestId) => $"skill:{_skillRunId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>Phase 10: answers and the committed state of connected players.</summary>
    private void SendSkillTrees()
    {
        if (_skillSender == null)
            return;
        foreach (var entity in _entities)
        {
            if (entity is Player { IsConnected: true } player)
                _skillSender.Send(player, _skills);
        }
    }

    /// <summary>A save committed a skill operation (rule 5): the bus, the answer, the points and the usable weapons.</summary>
    private void OnSkillOpCommitted(long characterId, CommittedSkillOp op)
    {
        _playersByCharacter.TryGetValue(characterId, out var player);
        uint entityId = player?.Id ?? 0;
        int region = player?.RegionIndex ?? 0;
        var record = op.Record;
        int spent = Math.Max(0, record.PointsBefore - record.PointsAfter);
        switch (record.Op)
        {
            case SkillTreeOp.Learn:
            case SkillTreeOp.Upgrade:
                _events?.Publish(new SkillLearned(characterId, entityId, record.NodeId, record.ToLevel, spent), characterId, region);
                break;
            case SkillTreeOp.Evolve:
                _events?.Publish(new SkillEvolved(characterId, entityId, record.FromNodeId, record.FromLevel, record.NodeId, spent),
                    characterId, region);
                break;
            case SkillTreeOp.Respec:
                _events?.Publish(new Respecced(characterId, entityId, record.FromLevel, record.PointsAfter - record.PointsBefore), characterId, region);
                break;
            case SkillTreeOp.Advance:
                _events?.Publish(new ClassAdvanced(characterId, entityId, record.ClassFrom, record.ClassTo, record.AdvanceTier ?? 0),
                    characterId, region);
                break;
        }
        if (_skills?.UsableWeapons(characterId) is { } usable)
            _inventory?.SetUsableWeapons(characterId, usable);
        OnProgressionChanged(characterId, ProgressionCause.SkillPoints);
        if (player != null && player.Connection == op.Connection)
            player.SkillTreeAnswers.Add(op.Answer);
    }

    /// <summary>The committed state or the hotbar changed: the loadout again, and the client hears of it in phase 10.</summary>
    private void OnSkillVisibleChanged(long characterId)
    {
        if (!_playersByCharacter.TryGetValue(characterId, out var player))
            return;
        ApplySkills(player);
        player.SkillTreeDirty = true;
        player.SkillTreeCause = SkillTreeCause.Changed;
    }

    /// <summary>Dev hot reload: a player's loadout from its skills on the new catalog (null: the class kit).</summary>
    private ActionLoadout? SkillLoadout(Player player) =>
        _skills?.Visible(player.CharacterId) is { } state ? LoadoutBuilder.Build(_actions, _skills.Rules, state) : null;

    /// <summary>R-3.5-07: a committed equipment change changes the weapon mask the action machine checks (phase 11).</summary>
    private sealed class EquippedWeapons(GameWorld world) : IEventConsumer<EquipmentChanged>
    {
        public void Handle(in EventEnvelope envelope, in EquipmentChanged payload)
        {
            if (world._playersByCharacter.TryGetValue(payload.CharacterId, out var player))
                world.UpdateWeapons(player);
        }
    }
}
