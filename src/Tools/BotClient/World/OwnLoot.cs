using BotClient.Brain;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;

namespace BotClient.World;

/// <summary>
/// Which drops are the bot's own (QA wave 6 O3, 50-bot run: 42 pickups against 260 refusals, 134 LootLocked). A drop is
/// locked to the killer for <c>StartingValuesM1.partyLootLockMs</c> (LootService, spec 4.3) and lies within
/// <c>LootSettings.dropScatterCm</c> of where the monster died; snapshots do not say whose a drop is. So a drop counts as
/// the bot's own while one of its own kills (its <c>Killed</c> hit) lies within that scatter of it, plus the farthest any
/// monster moves in one tick (the newest sample of the dead monster can be a tick old), and is younger than the lock.
/// </summary>
public sealed class OwnLoot
{
    private readonly List<(FixVec2 At, long AtMs)> _kills = [];
    private readonly long _lockMs;
    private readonly double _nearCm;

    public OwnLoot(int lockMs, int nearCm)
    {
        _lockMs = lockMs;
        _nearCm = nearCm;
    }

    public static OwnLoot From(ConfigSet tables)
    {
        var starting = tables.Single<StartingValuesM1>();
        int fastest = tables.TryGet(typeof(MonsterDef), out var monsters)
            ? monsters.Rows.Cast<MonsterDef>().Select(m => m.MoveSpeedCmPerSec).DefaultIfEmpty(0).Max()
            : 0;
        int stepCm = (fastest + starting.TickRateHz - 1) / Math.Max(1, starting.TickRateHz);
        return new OwnLoot(starting.PartyLootLockMs, tables.Single<LootSettings>().DropScatterCm + stepCm);
    }

    /// <summary>The bot killed a monster last seen at <paramref name="at"/>.</summary>
    public void NoteKill(FixVec2 at, long nowMs)
    {
        _kills.RemoveAll(kill => nowMs - kill.AtMs > _lockMs);
        _kills.Add((at, nowMs));
    }

    /// <summary>A drop at <paramref name="at"/> is locked to this bot now.</summary>
    public bool IsOwn(FixVec2 at, long nowMs)
    {
        foreach (var (killAt, killMs) in _kills)
        {
            if (nowMs - killMs <= _lockMs && GridPath.DistanceCm(killAt, at) <= _nearCm)
                return true;
        }
        return false;
    }
}
