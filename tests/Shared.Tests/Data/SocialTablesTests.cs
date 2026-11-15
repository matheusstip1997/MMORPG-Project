using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Social;

namespace Shared.Tests.Data;

/// <summary>
/// Friends, mute and block (spec 6.5 "M4 social", 8.5; M4 decision 16; WP-M4-16): <see cref="SocialSettings"/> (a burst covers its
/// rate; the whole lists fit one <see cref="SocialState"/> within <c>NetSecurity.maxReliableMessageBytes</c> at the longest character
/// name) and the pure rules of <see cref="SocialRules"/> (what a mute and a block stop, the request and accept checks, the cap).
/// </summary>
public class SocialTablesTests
{
    private static SocialSettings Row() => new()
    {
        MaxFriends = 50, MaxBlocks = 100, MaxIncomingRequests = 5, RequestTimeoutMs = 60_000, MaxQueuedRequests = 4,
        RequestsPerPeriod = 4, PeriodMs = 2_000, Burst = 8,
    };

    private static List<string> Errors(SocialSettings row, int? nameMaxLength)
    {
        var repository = RepositoryTables();
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(SocialSettings), 1, [row]));
        set.Add(new ConfigTable(typeof(NetSecurity), 1, [repository.Single<NetSecurity>()]));
        if (nameMaxLength is { } length)
        {
            var creation = repository.Single<CharacterCreationConfig>();
            creation.NameMaxLength = length;
            set.Add(new ConfigTable(typeof(CharacterCreationConfig), 1, [creation]));
        }
        return TableValidator.Validate(set).Where(e => e.Table == "SocialSettings").Select(e => e.Field + ": " + e.Message).ToList();
    }

    [Fact]
    public void The_committed_row_passes_and_holds_the_plan_placeholders()
    {
        var row = RepositoryTables().Single<SocialSettings>();
        Assert.Equal((50, 100, 5, 60_000), (row.MaxFriends, row.MaxBlocks, row.MaxIncomingRequests, row.RequestTimeoutMs));
        Assert.Empty(Errors(row, 16));
    }

    [Fact]
    public void A_burst_below_its_rate_is_an_error()
    {
        var row = Row();
        row.Burst = 3;
        Assert.Contains(Errors(row, 16), e => e.StartsWith("Burst", StringComparison.Ordinal));
    }

    [Fact]
    public void The_lists_must_fit_one_state_message_at_the_longest_name()
    {
        Assert.Empty(Errors(Row(), 16)); // 155 entries of 16 characters
        Assert.Contains(Errors(Row(), 64), e => e.StartsWith("MaxFriends", StringComparison.Ordinal)); // the same at 64 characters
        Assert.True(SocialSettings.StateBytes(50, 5, 100, 16) <= RepositoryTables().Single<NetSecurity>().MaxReliableMessageBytes);
    }

    [Fact]
    public void The_largest_state_the_validator_allows_is_what_the_writer_produces()
    {
        int nameChars = 16;
        var state = new SocialState
        {
            Version = uint.MaxValue,
            Friends = Enumerable.Range(0, 3).Select(i => new SocialFriendEntry { Ref = uint.MaxValue, Name = new string('一', nameChars), Online = true }).ToArray(),
            Incoming = Enumerable.Range(0, 2).Select(i => new SocialIncomingEntry { Ref = uint.MaxValue, Name = new string('一', nameChars), ExpiresInMs = uint.MaxValue }).ToArray(),
            Blocks = Enumerable.Range(0, 4).Select(i => new SocialBlockEntry { Ref = uint.MaxValue, Name = new string('一', nameChars), Kind = SocialBlockKind.Block }).ToArray(),
        };
        int written = ReliableMessages.Write(ReliableMessageId.SocialState, state).Length;
        Assert.True(written <= SocialSettings.StateBytes(3, 2, 4, nameChars), $"{written} bytes");
    }

    [Theory]
    [InlineData(SocialBlockKind.None, false, false)]
    [InlineData(SocialBlockKind.Mute, true, false)]
    [InlineData(SocialBlockKind.Block, true, true)]
    public void A_mute_hides_lines_and_a_block_also_refuses_requests(SocialBlockKind kind, bool hides, bool refuses)
    {
        Assert.Equal(hides, SocialRules.HidesChat(kind));
        Assert.Equal(refuses, SocialRules.RefusesRequests(kind));
    }

    [Theory]
    [InlineData(SocialBlockKind.Mute, SocialBlockKind.None, false, false, 0, 0, 0, SocialReason.SocialTargetBlocked)]
    [InlineData(SocialBlockKind.None, SocialBlockKind.Block, false, false, 0, 0, 0, SocialReason.SocialTargetNotFound)]
    [InlineData(SocialBlockKind.None, SocialBlockKind.Mute, false, false, 0, 0, 0, SocialReason.None)] // a mute refuses nothing
    [InlineData(SocialBlockKind.None, SocialBlockKind.None, true, false, 0, 0, 0, SocialReason.SocialAlreadyFriends)]
    [InlineData(SocialBlockKind.None, SocialBlockKind.None, false, true, 0, 0, 0, SocialReason.SocialRequestPending)]
    [InlineData(SocialBlockKind.None, SocialBlockKind.None, false, false, 2, 0, 0, SocialReason.SocialFriendCap)]
    [InlineData(SocialBlockKind.None, SocialBlockKind.None, false, false, 0, 2, 0, SocialReason.SocialTargetFriendCap)]
    [InlineData(SocialBlockKind.None, SocialBlockKind.None, false, false, 0, 0, 3, SocialReason.SocialRequestPending)]
    [InlineData(SocialBlockKind.None, SocialBlockKind.None, false, false, 1, 1, 2, SocialReason.None)]
    public void A_friend_request_is_checked_in_order(SocialBlockKind own, SocialBlockKind theirs, bool friends, bool waiting, int ownFriends,
        int targetFriends, int incoming, SocialReason expected)
    {
        Assert.Equal(expected, SocialRules.CheckRequest(own, theirs, friends, waiting, ownFriends, targetFriends, incoming, maxFriends: 2,
            maxIncoming: 3));
    }

    [Fact]
    public void An_accept_checks_both_caps()
    {
        Assert.Equal(SocialReason.SocialFriendCap, SocialRules.CheckAccept(SocialBlockKind.None, SocialBlockKind.None, 2, 0, 2));
        Assert.Equal(SocialReason.SocialTargetFriendCap, SocialRules.CheckAccept(SocialBlockKind.None, SocialBlockKind.None, 1, 2, 2));
        Assert.Equal(SocialReason.None, SocialRules.CheckAccept(SocialBlockKind.None, SocialBlockKind.None, 1, 1, 2));
    }

    [Theory]
    [InlineData(SocialBlockKind.Mute, SocialBlockKind.None, 2, SocialReason.SocialTargetBlocked)] // the accepting player mutes the requester
    [InlineData(SocialBlockKind.Block, SocialBlockKind.None, 2, SocialReason.SocialTargetBlocked)] // or blocks it (before the full cap)
    [InlineData(SocialBlockKind.None, SocialBlockKind.Block, 2, SocialReason.SocialTargetNotFound)] // the requester blocks it: never revealed
    [InlineData(SocialBlockKind.None, SocialBlockKind.Mute, 0, SocialReason.None)] // a mute by the requester refuses nothing
    public void An_accept_is_refused_by_a_mute_or_block_made_since_the_request(SocialBlockKind own, SocialBlockKind theirs, int friends,
        SocialReason expected)
    {
        // Review of PR #257: the same refusals as an add by name, checked again at the accept, before the caps.
        Assert.Equal(expected, SocialRules.CheckAccept(own, theirs, friends, friends, maxFriends: 2));
    }

    [Fact]
    public void A_new_entry_needs_room_and_a_change_of_kind_does_not()
    {
        Assert.Equal(SocialReason.SocialBlockCap, SocialRules.CheckBlock(SocialBlockKind.None, SocialBlockKind.Mute, 2, 2, out bool changes));
        Assert.False(changes);
        Assert.Equal(SocialReason.None, SocialRules.CheckBlock(SocialBlockKind.Mute, SocialBlockKind.Block, 2, 2, out changes));
        Assert.True(changes);
        Assert.Equal(SocialReason.None, SocialRules.CheckBlock(SocialBlockKind.Block, SocialBlockKind.Block, 2, 2, out changes));
        Assert.False(changes); // already so: nothing to write
    }

    private static ConfigSet RepositoryTables()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var tables = Path.Combine(directory!.FullName, "data", "tables");
        var errors = new List<ValidationError>();
        var set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tables, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        Assert.Empty(errors);
        return set;
    }
}
