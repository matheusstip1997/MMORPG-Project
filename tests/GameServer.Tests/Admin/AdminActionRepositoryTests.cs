using Dapper;
using GameServer.Admin;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using GameServer.World;
using Npgsql;
using Shared.Data.Tables;

namespace GameServer.Tests.Admin;

/// <summary>
/// The admin action log (realm migration V0003, spec 7.4) against a real PostgreSQL: one row per request id (double
/// submit, retry), one claim per command, one completion, expiry.
/// </summary>
public sealed class AdminActionRepositoryTests : IAsyncLifetime
{
    private const int RealmId = 1;
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realm = null!;
    private AdminActionRepository _actions = null!;

    public async Task InitializeAsync()
    {
        _realm = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _actions = new AdminActionRepository(_realm, RealmId, _clock);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static AdminActionRequest Kick(Guid requestId, long characterId = 42, string server = "server-a", string admin = "dev:ana") =>
        new(requestId, admin, AdminActionKind.Kick, characterId, TargetAccountId: 7, TargetServer: server, Detail: "spam");

    [Fact]
    public async Task A_double_submit_records_one_row_and_returns_it_both_times()
    {
        var id = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _actions.SubmitAsync(Kick(id), outcome: null)));

        Assert.Single(results, r => r.Status == AdminSubmitStatus.Created);
        Assert.All(results, r => Assert.Equal(results[0].Record.ActionId, r.Record.ActionId));
        Assert.Equal(1, await CountAsync());
        var row = results[0].Record;
        Assert.Equal((AdminActionState.Pending, "dev:ana", "server-a", 42L, _clock.UtcNowMs),
            (row.State, row.AdminName, row.TargetServer, row.TargetCharacterId, row.RequestedAtUtcMs));
    }

    [Fact]
    public async Task Reusing_a_request_id_for_another_target_or_admin_is_a_conflict_and_changes_nothing()
    {
        var id = Guid.NewGuid();
        await _actions.SubmitAsync(Kick(id), null);

        var otherTarget = await _actions.SubmitAsync(Kick(id, characterId: 43), null);
        var otherAdmin = await _actions.SubmitAsync(Kick(id, admin: "dev:bob"), null);

        Assert.Equal(AdminSubmitStatus.Conflict, otherTarget.Status);
        Assert.Equal(AdminSubmitStatus.Conflict, otherAdmin.Status);
        Assert.Equal(42, (await _actions.GetAsync(id))!.TargetCharacterId);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task A_read_or_refused_action_is_recorded_finished_with_its_outcome()
    {
        var (status, row) = await _actions.SubmitAsync(
            new AdminActionRequest(Guid.NewGuid(), "dev:ana", AdminActionKind.Search, Detail: "name=Qa"), AdminOutcome.Done);

        Assert.Equal(AdminSubmitStatus.Created, status);
        Assert.Equal((AdminActionState.Done, "Done", _clock.UtcNowMs), (row.State, row.Outcome, row.CompletedAtUtcMs));
        Assert.Empty(await _actions.ClaimAsync("server-a", 10));
    }

    [Fact]
    public async Task A_command_is_claimed_once_only_by_its_target_server_even_by_concurrent_claimers()
    {
        var mine = await _actions.SubmitAsync(Kick(Guid.NewGuid()), null);
        await _actions.SubmitAsync(Kick(Guid.NewGuid(), server: "server-b"), null);

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _actions.ClaimAsync("server-a", 10)));

        var claimed = Assert.Single(claims.SelectMany(c => c));
        Assert.Equal(new AdminCommand(mine.Record.ActionId, AdminActionKind.Kick, 42), claimed);
        var row = (await _actions.GetAsync(mine.Record.RequestId))!;
        Assert.Equal((AdminActionState.Claimed, "server-a"), (row.State, row.ClaimedBy));
    }

    [Fact]
    public async Task An_outcome_is_written_once_and_only_by_the_claiming_server()
    {
        var submitted = await _actions.SubmitAsync(Kick(Guid.NewGuid()), null);
        var command = Assert.Single(await _actions.ClaimAsync("server-a", 10));

        Assert.False(await _actions.CompleteAsync(command.ActionId, "server-b", AdminOutcome.Kicked));
        Assert.True(await _actions.CompleteAsync(command.ActionId, "server-a", AdminOutcome.Kicked));
        Assert.False(await _actions.CompleteAsync(command.ActionId, "server-a", AdminOutcome.NotConnected));

        var row = (await _actions.GetAsync(submitted.Record.RequestId))!;
        Assert.Equal((AdminActionState.Done, "Kicked"), (row.State, row.Outcome));
    }

    [Fact]
    public async Task Commands_nobody_finished_in_time_expire_and_a_late_real_outcome_replaces_Expired()
    {
        var pending = await _actions.SubmitAsync(Kick(Guid.NewGuid(), server: "gone"), null);
        var running = await _actions.SubmitAsync(Kick(Guid.NewGuid()), null);
        var command = Assert.Single(await _actions.ClaimAsync("server-a", 10));
        _clock.Advance(5_000);
        var fresh = await _actions.SubmitAsync(Kick(Guid.NewGuid(), server: "gone"), null);

        Assert.Equal(2, await _actions.ExpireAsync(expiryMs: 4_000));

        Assert.Equal("Expired", (await _actions.GetAsync(pending.Record.RequestId))!.Outcome);
        Assert.Equal(AdminActionState.Pending, (await _actions.GetAsync(fresh.Record.RequestId))!.State);
        Assert.True(await _actions.CompleteAsync(command.ActionId, "server-a", AdminOutcome.Kicked));
        Assert.Equal("Kicked", (await _actions.GetAsync(running.Record.RequestId))!.Outcome);
    }

    [Fact]
    public async Task The_worker_posts_claimed_kicks_to_the_world_and_writes_back_the_outcomes()
    {
        var inbox = new WorldInbox();
        var settings = new AdminSettings { CommandPollMs = 100, CommandExpiryMs = 30_000, MaxClaimPerPoll = 10 };
        await using var worker = new AdminCommandWorker(_actions, inbox, settings, "server-a");
        var submitted = await _actions.SubmitAsync(Kick(Guid.NewGuid()), null);

        await worker.PollAsync();
        Assert.True(inbox.TryTake(out var command));
        Assert.Equal((WorldCommandKind.AdminKick, submitted.Record.ActionId, 42L),
            (command.Kind, command.ActionId, command.CharacterId));
        Assert.False(inbox.TryTake(out _));

        command.AdminResults!.Post(command.ActionId, AdminOutcome.Kicked); // as the world does
        await worker.PollAsync();

        var row = (await _actions.GetAsync(submitted.Record.RequestId))!;
        Assert.Equal((AdminActionState.Done, "Kicked", "server-a"), (row.State, row.Outcome, row.ClaimedBy));
        Assert.False(inbox.TryTake(out _)); // nothing is posted twice
    }

    private async Task<long> CountAsync()
    {
        await using var connection = await _realm.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM admin_actions");
    }
}
