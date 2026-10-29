using Dapper;
using GameServer.Admin;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Admin;

/// <summary>
/// Admin kick over localhost (spec 7.4, admin panel v0): a real Bootstrap server with its admin command worker, a
/// LiteNetLib client logged in with a dev ticket, Redis locks and the realm database. The kick is written the way the
/// panel writes it (twice: a double submit), claimed by the server holding the character, run on its tick, and its
/// outcome written back to the one audit row.
/// </summary>
public sealed class AdminKickEndToEndTests
{
    [Fact]
    public async Task A_double_submitted_kick_disconnects_the_client_once_with_Kicked_and_leaves_one_audit_row()
    {
        await using var env = await ServerTestEnvironment.CreateAsync();
        var config = ServerTestEnvironment.LoadConfig();
        config.StartingValues.LingerOutOfCombatMs = 300;
        config.StartingValues.LingerInCombatMs = 300;
        var server = env.StartServer(config);
        var character = await env.CreateCharacterAsync(accountId: 9);
        using var client = env.Connect(server, config, accountId: 9);
        env.WaitFor(() => client.OwnEntityId != null, client);

        // What the panel does: the holder from the lock, then the row, keyed by the request id.
        var holder = OwnershipLockToken.ServerOf(env.LockHolder(character.CharacterId));
        Assert.Equal(server.ServerInstanceId, holder);
        var actions = new AdminActionRepository(env.RealmDatabase, ServerTestEnvironment.Realm, env.Clock);
        var request = new AdminActionRequest(Guid.NewGuid(), "dev:test", AdminActionKind.Kick, character.CharacterId,
            TargetAccountId: 9, TargetServer: holder);
        var first = await actions.SubmitAsync(request, outcome: null);
        var second = await actions.SubmitAsync(request, outcome: null);
        Assert.Equal((AdminSubmitStatus.Created, AdminSubmitStatus.AlreadyRecorded), (first.Status, second.Status));

        env.WaitFor(() => client.Disconnected, client);
        Assert.Equal(ConnectRejectReason.Kicked, client.ServerReason);

        AdminActionRecord? row = null;
        env.WaitFor(() => (row = actions.GetAsync(request.RequestId).GetAwaiter().GetResult())!.State == AdminActionState.Done);
        Assert.Equal(("Kicked", server.ServerInstanceId, first.Record.ActionId), (row!.Outcome, row.ClaimedBy, row.ActionId));
        await using (var connection = await env.RealmDatabase.OpenConnectionAsync())
            Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM admin_actions"));

        // Like a disconnect: the character lingers, then is saved and released.
        env.WaitFor(() => env.LockHolder(character.CharacterId) == null);
    }
}
