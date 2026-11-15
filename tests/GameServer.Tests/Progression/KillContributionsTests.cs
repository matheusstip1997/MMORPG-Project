using GameServer.Events;
using GameServer.Progression;
using GameServer.Tests.Support;

namespace GameServer.Tests.Progression;

/// <summary>
/// The damage ledger the party exp split and the party loot roll read (spec 6.5 "M4 party", R-3.6-03; WP-M4-2): a monster's
/// leash reset drops it, so damage dealt before the reset never pays a later kill of the same monster (fixer backlog M4
/// wave 0, item 5), also when the reset and the hits are delivered in the same tick.
/// </summary>
public class KillContributionsTests
{
    private const uint Demon = 50;
    private const long Ana = 7, Bo = 8;

    private static (EventBus Bus, KillContributions Ledger) Fixture()
    {
        var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var ledger = new KillContributions(maxContributors: 16, forgetMs: 300_000, () => 1_000);
        ledger.Subscribe(bus);
        return (bus, ledger);
    }

    private static void Hit(EventBus bus, long character, int amount) =>
        bus.Publish(new Damaged((uint)character, Demon, 0, amount, 100, false), character);

    [Fact]
    public void A_leash_reset_drops_the_ledger_and_only_later_damage_counts()
    {
        var (bus, ledger) = Fixture();
        Hit(bus, Ana, 100);
        bus.DispatchQueued();
        Assert.Equal(100, ledger.DamageOf(Demon, Ana));

        bus.Publish(new MonsterReset(Demon));
        bus.DispatchQueued();
        Assert.Equal((0L, false, 0), (ledger.DamageOf(Demon, Ana), ledger.HasHit(Demon, Ana), ledger.Count));

        Hit(bus, Bo, 40);
        bus.DispatchQueued();
        Assert.Equal((0L, 40L), (ledger.DamageOf(Demon, Ana), ledger.DamageOf(Demon, Bo)));
    }

    [Fact]
    public void Hits_of_the_reset_tick_go_with_the_reset_and_hits_after_it_count()
    {
        // Hits are published in phases 3 to 5, the reset in phase 7, all delivered in phase 11 in that order.
        var (bus, ledger) = Fixture();
        Hit(bus, Ana, 100);
        bus.Publish(new MonsterReset(Demon));
        Hit(bus, Bo, 25);
        bus.DispatchQueued();

        Assert.False(ledger.HasHit(Demon, Ana));
        Assert.Equal(25, ledger.DamageOf(Demon, Bo));
    }
}
