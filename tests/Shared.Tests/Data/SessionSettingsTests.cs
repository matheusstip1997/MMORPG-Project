using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

public class SessionSettingsTests
{
    private static RowErrors Validate(SessionSettings row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors;
    }

    [Fact]
    public void A_heartbeat_slower_than_half_the_lock_ttl_is_a_broken_rule()
    {
        var errors = Validate(new SessionSettings { OwnershipLockTtlMs = 30_000, HeartbeatIntervalMs = 15_001 });

        Assert.Equal(nameof(SessionSettings.HeartbeatIntervalMs), Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void Two_heartbeats_per_ttl_are_valid()
    {
        Assert.Empty(Validate(new SessionSettings { OwnershipLockTtlMs = 30_000, HeartbeatIntervalMs = 15_000 }).Items);
    }
}
