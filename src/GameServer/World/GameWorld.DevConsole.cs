using GameServer.Dev.Commands;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.World;

/// <summary>
/// The dev console in the world (spec 11.7, WP-M3-13): chat lines starting with <c>/</c> go to <see cref="DevConsole"/>
/// (phase 1) instead of local chat, its answers go out in phase 10, and the commands that are world state, not a writer's
/// (a teleport, a heal, a death, spawned monsters, a resource node), are applied here the way play applies them. A world
/// without the console (every Release build, and a DevTools build without <c>MMORPG_DEV_TOOLS</c>) refuses every such line
/// with <see cref="DevReason.DevToolsOff"/>.
/// </summary>
public sealed partial class GameWorld
{
    private DevConsole? _devConsole;
    private readonly DevConsoleSender _devSender;
    private readonly List<Monster> _devMonsters = new();

    /// <summary>The dev console; null unless the host turned it on (<see cref="EnableDevConsole"/>).</summary>
    public DevConsole? DevConsole => _devConsole;

    /// <summary>
    /// Turns the dev console on (the host, in a DevTools build with <c>MMORPG_DEV_TOOLS</c> on; tests). Before the world
    /// starts. <paramref name="accounts"/> says which accounts the dev login made.
    /// </summary>
    public DevConsole EnableDevConsole(IDevAccounts accounts, DevTools settings)
    {
        if (_devConsole != null)
            throw new InvalidOperationException("The dev console is already on.");
        _devConsole = new DevConsole(this, accounts, settings, _runId);
        _events?.Subscribe<SessionStarted>(_devConsole);
        _sessions?.AttachDevConsole(_devConsole);
        return _devConsole;
    }

    /// <summary>Phase 1, after local chat: the dev lines <see cref="ChatService"/> set aside, in its order.</summary>
    private void HandleDevLines()
    {
        if (_chat == null || _chat.Commands.Count == 0)
            return;
        foreach (var line in _chat.Commands)
        {
            if (_devConsole != null)
                _devConsole.Submit(line.Sender, line.Text);
            else
                line.Sender.DevAnswers.Add(new DevCommandResult { Reason = DevReason.DevToolsOff });
        }
        _chat.Commands.Clear();
    }

    /// <summary>The player entity of a held character, or null.</summary>
    internal Player? PlayerOf(long characterId) => _playersByCharacter.TryGetValue(characterId, out var player) ? player : null;

    internal EventBus? DevEvents => _events;

    internal Bystial.BystialService? DevBystial => _bystial;

    internal string DevJobOpKey(Player player, uint requestId) => JobOpKey(player, requestId);

    /// <summary>
    /// <c>/tp</c>: moves the player as a respawn does (position, region, the speed check's start, the grid, the lag
    /// compensation history). Refused outside the walkable world or into a region the character's level may not enter
    /// (R-2.3-06), so the console reaches no place play could not.
    /// </summary>
    internal DevReason DevTeleport(Player player, FixVec2 to)
    {
        if (!_map.Grid.IsWalkable(to))
            return DevReason.TargetBlocked;
        byte region = _map.Grid.RegionIndexAt(to);
        if (_progression != null && _map.Gates.Any && _map.Gates.MinLevelOf(region) > _progression.GetLevel(player.CharacterId))
            return DevReason.TargetBlocked;
        player.Movement = new MovementState(to, player.Movement.Facing);
        player.HeightCm = _map.HeightCmAt(to, _map.Grid.BaseHeightCm);
        player.RegionIndex = region;
        player.SpeedCheckedPosition = to;
        _grid.Update(player);
        if (player.Combatant is { } combatant)
        {
            combatant.History.Clear();
            combatant.History.Record(ServerTick, to);
        }
        return DevReason.None;
    }

    /// <summary><c>/heal</c>: full HP, MP, stamina and poise, as a respawn gives (statuses are kept).</summary>
    internal void DevHeal(Player player) => player.Combatant?.RestoreFull(NowMs);

    /// <summary><c>/die</c>: the player dies now; phase 8 respawns it after <c>playerRespawnDelayMs</c> as after any death.</summary>
    internal void DevDie(Player player)
    {
        if (player.Combatant is not { IsAlive: true } combatant)
            return;
        combatant.Meters.Set(ResourceKind.Hp, 0, NowMs);
        combatant.Die(ServerTick);
        ActionMachine.Kill(player.Actions);
        _events?.Publish(new Died(player.Id, 0), player.CharacterId, player.RegionIndex);
    }

    /// <summary>
    /// <c>/spawn</c>: <paramref name="count"/> monsters of <paramref name="def"/> at the player, with full meters, like a
    /// spawn point's. They belong to no spawn point: phase 8 removes them once dead and nothing respawns them. Their kills
    /// pay exp, loot and kill counts as any other.
    /// </summary>
    internal int DevSpawn(Player player, MonsterDef def, int count)
    {
        if (_combat == null)
            return 0;
        var marker = new WorldMarker("dev:" + player.Id, Shared.World.MarkerKind.MonsterSpawn, player.Movement.Position,
            player.Movement.Facing, def.Id);
        for (int i = 0; i < count; i++)
            _devMonsters.Add(SpawnMonster(def, marker, marker.Id));
        return count;
    }

    /// <summary>Phase 8: dead dev monsters leave the world.</summary>
    private void RemoveDeadDevMonsters()
    {
        for (int i = _devMonsters.Count - 1; i >= 0; i--)
        {
            if (_devMonsters[i].Combatant is { IsAlive: false })
            {
                RemoveMonster(_devMonsters[i]);
                _devMonsters.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// The resource node nearest to the player within its interest radius (what it can see), or null. Ties go to the lower
    /// entity id (rule 6).
    /// </summary>
    internal ResourceNode? DevNearestNode(Player player)
    {
        if (_nodes == null)
            return null;
        ResourceNode? best = null;
        long bestDistance = (long)_settings.InterestRadiusCm * _settings.InterestRadiusCm;
        foreach (var node in _nodes.Nodes)
        {
            long dx = node.XCm - player.XCm, dy = node.YCm - player.YCm;
            long distance = dx * dx + dy * dy;
            if (distance <= bestDistance && (best == null || distance < bestDistance))
            {
                best = node;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary><c>/node deplete</c>: as a harvest would deplete it (its respawn scheduled). False when it is depleted already.</summary>
    internal bool DevDepleteNode(ResourceNode node) => _nodes != null && _nodes.TryDeplete(node.Id, NowMs);

    /// <summary><c>/node respawn</c>: a depleted node is available again now. False when it is available already.</summary>
    internal bool DevRespawnNode(ResourceNode node) => _nodes != null && _nodes.RespawnNow(node);

    /// <summary>The marker nearest to <paramref name="player"/>, for <c>/where</c>.</summary>
    internal WorldMarker? DevNearestMarker(Player player)
    {
        WorldMarker? best = null;
        long bestDistance = long.MaxValue;
        foreach (var marker in _map.Markers)
        {
            long dx = FixedPoint.ToCmRound(marker.Position.X) - player.XCm, dy = FixedPoint.ToCmRound(marker.Position.Y) - player.YCm;
            long distance = dx * dx + dy * dy;
            if (distance < bestDistance || (distance == bestDistance && best != null && string.CompareOrdinal(marker.Id, best.Id) < 0))
            {
                best = marker;
                bestDistance = distance;
            }
        }
        return best;
    }
}
