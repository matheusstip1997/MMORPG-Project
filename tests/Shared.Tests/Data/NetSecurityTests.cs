using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

public class NetSecurityTests
{
    [Fact]
    public void Unreliable_cap_above_reliable_cap_is_a_broken_rule()
    {
        var row = new NetSecurity { MaxReliableMessageBytes = 1_000, MaxUnreliableMessageBytes = 1_001 };
        var errors = new RowErrors();

        row.Validate(errors);

        Assert.Equal(nameof(NetSecurity.MaxUnreliableMessageBytes), Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void Equal_caps_are_allowed()
    {
        var row = new NetSecurity { MaxReliableMessageBytes = 1_000, MaxUnreliableMessageBytes = 1_000 };
        var errors = new RowErrors();

        row.Validate(errors);

        Assert.Empty(errors.Items);
    }
}
