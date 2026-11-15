using GameServer.Social;
using GameServer.Tests.Support;
using Tests.Common;

namespace GameServer.Tests.Social;

/// <summary>
/// The party paths that run for every character, not per request (CLAUDE.md: no allocations on the world thread's steady
/// paths; fixer backlog M4 wave 0, item 6): a character leaving the world without a party or invite allocates nothing.
/// </summary>
public class PartyServiceAllocationTests
{
    [Fact]
    public void A_character_leaving_the_world_outside_a_party_allocates_nothing()
    {
        var parties = new PartyService(TestWorlds.Party(), events: null);
        parties.Detach(41); // warm

        AllocationMeter.AssertNone(() =>
        {
            for (long character = 1; character <= 100; character++)
                parties.Detach(character);
        });
    }
}
