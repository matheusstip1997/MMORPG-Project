using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminPanel.Tests.Support;
using GameServer.Admin;

namespace AdminPanel.Tests;

/// <summary>
/// The admin panel API end to end against a real PostgreSQL and Redis (spec 7.4, admin panel v0): sign-in and the
/// request header, search by name and account, kick recording (not online, double submit, conflicts), the action log,
/// logs and telemetry. Who did what is recorded for every action.
/// </summary>
public sealed class AdminPanelIntegrationTests : IAsyncLifetime
{
    private AdminPanelEnvironment _env = null!;

    public async Task InitializeAsync() => _env = await AdminPanelEnvironment.CreateAsync();

    public async Task DisposeAsync() => await _env.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(AdminPanelEnvironment.Json));

    private static async Task AssertRefused(HttpResponseMessage response, HttpStatusCode status, AdminReasonCode reason)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await JsonOf(response);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(reason.ToString(), body.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Everything_but_signing_in_needs_a_signed_in_admin()
    {
        var client = _env.App.CreateClient();

        await AssertRefused(await client.GetAsync("/api/characters?name=a"), HttpStatusCode.Unauthorized, AdminReasonCode.NotSignedIn);
        await AssertRefused(await client.GetAsync("/api/logs"), HttpStatusCode.Unauthorized, AdminReasonCode.NotSignedIn);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Contains("Admin Panel", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Changes_without_the_request_header_are_refused()
    {
        var client = _env.App.CreateClient();

        await AssertRefused(await client.PostAsJsonAsync("/api/session", new { name = "ana" }), HttpStatusCode.BadRequest,
            AdminReasonCode.RequestInvalid);
    }

    [Fact]
    public async Task Without_an_admin_identity_nobody_can_sign_in()
    {
        _env.UseFakeIdentity = false; // MMORPG_ADMIN_IDENTITY unset: none (production admin auth is TODO(open))
        var client = _env.App.CreateClient();
        client.DefaultRequestHeaders.Add(AdminEndpoints.RequestHeader, "1");

        await AssertRefused(await client.PostAsJsonAsync("/api/session", new { name = "ana" }), HttpStatusCode.Forbidden,
            AdminReasonCode.SignInUnavailable);
    }

    [Fact]
    public async Task Search_finds_characters_by_name_prefix_and_by_account_with_who_holds_them_and_is_recorded()
    {
        var (account, hero) = await _env.CreateCharacterAsync(steamId: 7, "Heroine");
        await _env.CreateCharacterAsync(steamId: 8, "Herald");
        await _env.CreateCharacterAsync(steamId: 9, "Other");
        await _env.HoldAsync(hero.CharacterId, "server-a");
        var client = await _env.SignedInClientAsync();

        var byName = await JsonOf(await client.GetAsync("/api/characters?name=her"));
        var names = byName.GetProperty("characters").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Herald", "Heroine"], names);

        var byAccount = (await JsonOf(await client.GetAsync("/api/characters?account=dev:7"))).GetProperty("characters");
        var found = Assert.Single(byAccount.EnumerateArray());
        Assert.Equal((hero.CharacterId, account.AccountId, 7L, true, "server-a"),
            (found.GetProperty("characterId").GetInt64(), found.GetProperty("accountId").GetInt64(),
                found.GetProperty("steamId").GetInt64(), found.GetProperty("loaded").GetBoolean(),
                found.GetProperty("server").GetString()));
        Assert.Single((await JsonOf(await client.GetAsync($"/api/characters?account=id:{account.AccountId}")))
            .GetProperty("characters").EnumerateArray());
        Assert.Empty((await JsonOf(await client.GetAsync("/api/characters?name=her%25"))).GetProperty("characters").EnumerateArray());

        await AssertRefused(await client.GetAsync("/api/characters"), HttpStatusCode.BadRequest, AdminReasonCode.RequestInvalid);
        await AssertRefused(await client.GetAsync("/api/characters?account=steam"), HttpStatusCode.BadRequest, AdminReasonCode.RequestInvalid);

        var recorded = await _env.Actions.ListRecentAsync(50);
        Assert.Equal(4, recorded.Count(a => a.Action == AdminActionKind.Search && a.AdminName == "test:ana"));
        Assert.Single(recorded, a => a.Action == AdminActionKind.SignIn);
    }

    [Fact]
    public async Task A_kick_of_a_character_nobody_holds_is_recorded_finished_as_NotOnline()
    {
        var (_, hero) = await _env.CreateCharacterAsync(7, "Heroine");
        var client = await _env.SignedInClientAsync();
        var requestId = Guid.NewGuid();

        var body = await JsonOf(await client.PostAsJsonAsync($"/api/characters/{hero.CharacterId}/kick",
            new { requestId, note = "afk" }));

        var action = body.GetProperty("action");
        Assert.Equal(("done", "NotOnline", "afk"),
            (action.GetProperty("state").GetString(), action.GetProperty("outcome").GetString(), action.GetProperty("detail").GetString()));
        Assert.Empty(await _env.Actions.ClaimAsync("server-a", 10));
    }

    [Fact]
    public async Task A_double_submitted_kick_is_one_pending_command_for_the_holding_server()
    {
        var (account, hero) = await _env.CreateCharacterAsync(7, "Heroine");
        await _env.HoldAsync(hero.CharacterId, "server-a");
        var client = await _env.SignedInClientAsync();
        var kick = new { requestId = Guid.NewGuid(), note = (string?)null };

        var first = await JsonOf(await client.PostAsJsonAsync($"/api/characters/{hero.CharacterId}/kick", kick));
        var second = await JsonOf(await client.PostAsJsonAsync($"/api/characters/{hero.CharacterId}/kick", kick));

        Assert.Equal("Created", first.GetProperty("submit").GetString());
        Assert.Equal("AlreadyRecorded", second.GetProperty("submit").GetString());
        Assert.Equal(first.GetProperty("action").GetProperty("actionId").GetInt64(), second.GetProperty("action").GetProperty("actionId").GetInt64());
        var command = Assert.Single(await _env.Actions.ClaimAsync("server-a", 10));
        Assert.Equal(hero.CharacterId, command.CharacterId);
        var row = (await _env.Actions.GetAsync(kick.requestId))!;
        Assert.Equal(("test:ana", account.AccountId, "server-a"), (row.AdminName, row.TargetAccountId, row.TargetServer));

        // The panel follows the action by its request id.
        var followed = await JsonOf(await client.GetAsync($"/api/actions/{kick.requestId}"));
        Assert.Equal("claimed", followed.GetProperty("action").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Reusing_a_kick_request_id_for_another_character_is_a_conflict_and_malformed_ids_are_refused()
    {
        var (_, hero) = await _env.CreateCharacterAsync(7, "Heroine");
        var (_, other) = await _env.CreateCharacterAsync(8, "Other");
        var client = await _env.SignedInClientAsync();
        var requestId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/characters/{hero.CharacterId}/kick", new { requestId });

        await AssertRefused(await client.PostAsJsonAsync($"/api/characters/{other.CharacterId}/kick", new { requestId }),
            HttpStatusCode.Conflict, AdminReasonCode.RequestConflict);
        await AssertRefused(await client.PostAsJsonAsync($"/api/characters/{hero.CharacterId}/kick", new { requestId = "nope" }),
            HttpStatusCode.BadRequest, AdminReasonCode.RequestInvalid);
        await AssertRefused(await client.PostAsJsonAsync("/api/characters/999999/kick", new { requestId = Guid.NewGuid() }),
            HttpStatusCode.NotFound, AdminReasonCode.CharacterNotFound);
        await AssertRefused(await client.GetAsync($"/api/actions/{Guid.NewGuid()}"), HttpStatusCode.NotFound, AdminReasonCode.ActionNotFound);
    }

    [Fact]
    public async Task A_pending_kick_no_server_answers_shows_as_expired()
    {
        var (_, hero) = await _env.CreateCharacterAsync(7, "Heroine");
        await _env.HoldAsync(hero.CharacterId, "crashed-server");
        var client = await _env.SignedInClientAsync();
        var requestId = Guid.NewGuid();
        await client.PostAsJsonAsync($"/api/characters/{hero.CharacterId}/kick", new { requestId });

        _env.Clock.Advance(60_000); // past AdminSettings.commandExpiryMs

        var action = (await JsonOf(await client.GetAsync($"/api/actions/{requestId}"))).GetProperty("action");
        Assert.Equal(("done", "Expired"), (action.GetProperty("state").GetString(), action.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task The_character_page_shows_its_saved_state_and_its_admin_actions()
    {
        var (_, hero) = await _env.CreateCharacterAsync(7, "Heroine");
        var client = await _env.SignedInClientAsync();
        await client.PostAsJsonAsync($"/api/characters/{hero.CharacterId}/kick", new { requestId = Guid.NewGuid() });

        var body = await JsonOf(await client.GetAsync($"/api/characters/{hero.CharacterId}"));

        Assert.Equal(100, body.GetProperty("character").GetProperty("posXCm").GetInt32());
        var actions = body.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Equal([AdminActionKind.ViewCharacter, AdminActionKind.Kick], actions);
        await AssertRefused(await client.GetAsync("/api/characters/999999"), HttpStatusCode.NotFound, AdminReasonCode.CharacterNotFound);
    }

    [Fact]
    public async Task Logs_and_telemetry_are_read_from_their_folders_and_the_views_are_recorded()
    {
        await File.WriteAllLinesAsync(Path.Combine(_env.LogDirectory, "gameserver.log"),
            ["GameServer running", "login Heroine", "ERROR something about Heroine"]);
        await File.WriteAllLinesAsync(Path.Combine(_env.TelemetryDirectory, "run-0000.jsonl"),
        [
            """{"schema":"mmorpg-telemetry","v":1,"run":"run","startedMs":1,"file":0}""",
            """{"k":"SessionStarted","t":10,"c":42,"a":7,"entity":3}""",
            """{"k":"Moved","t":11,"c":43}""",
            """{"k":"SessionEnded","t":12,"c":42,"a":7,"entity":3,"reason":6}""",
        ]);
        var client = await _env.SignedInClientAsync();

        var logs = await JsonOf(await client.GetAsync("/api/logs?contains=heroine"));
        Assert.Equal("gameserver.log", logs.GetProperty("file").GetString());
        Assert.Equal(["login Heroine", "ERROR something about Heroine"],
            logs.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
        Assert.Empty((await JsonOf(await client.GetAsync("/api/logs?file=..%2F..%2Fsecret.log"))).GetProperty("lines").EnumerateArray());

        var telemetry = (await JsonOf(await client.GetAsync("/api/telemetry?characterId=42"))).GetProperty("records");
        Assert.Equal(["SessionEnded", "SessionStarted"], telemetry.EnumerateArray().Select(r => r.GetProperty("k").GetString()));

        var recorded = await _env.Actions.ListRecentAsync(50);
        Assert.Equal(2, recorded.Count(a => a.Action == AdminActionKind.ViewLogs));
        Assert.Single(recorded, a => a.Action == AdminActionKind.ViewTelemetry && a.TargetCharacterId == 42);
    }
}
