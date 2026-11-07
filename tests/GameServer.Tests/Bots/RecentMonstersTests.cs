using BotClient.World;
using Shared.Simulation;

namespace GameServer.Tests.Bots;

public class RecentMonstersTests
{
    [Fact]
    public void A_victim_gone_from_the_registry_is_still_known_with_its_newest_position()
    {
        var recent = new RecentMonsters();
        recent.Note(7, 1, FixVec2.FromCm(100, 100));
        recent.Note(7, 1, FixVec2.FromCm(120, 90));

        Assert.True(recent.TryGet(7, out int defId, out var at));
        Assert.Equal(1, defId);
        Assert.Equal(FixVec2.FromCm(120, 90), at);
        Assert.False(recent.TryGet(8, out _, out _));
    }

    [Fact]
    public void The_first_combat_keeps_its_time_whatever_marks_it_again()
    {
        var counters = new BotCounters();
        Assert.False(counters.InCombat);
        counters.NoteInCombat(1_000);
        counters.NoteInCombat(2_000);
        Assert.True(counters.InCombat);
        Assert.Equal(1_000, counters.FirstCombatAtMs);
    }

    [Fact]
    public void The_record_stays_bounded_and_forgets_the_oldest_monster_first()
    {
        var recent = new RecentMonsters();
        for (uint id = 1; id <= RecentMonsters.Capacity; id++)
            recent.Note(id, 1, default);
        recent.Note(1, 1, default); // seen again: not a new entry
        Assert.True(recent.TryGet(1, out _, out _));

        recent.Note(RecentMonsters.Capacity + 1, 1, default);
        Assert.False(recent.TryGet(1, out _, out _));
        Assert.True(recent.TryGet(2, out _, out _));
        Assert.True(recent.TryGet(RecentMonsters.Capacity + 1, out _, out _));
    }
}
