using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminPanel.Queries;
using AdminPanel.Tests.Support;
using Dapper;
using GameServer.Admin;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Persistence;

namespace AdminPanel.Tests;

/// <summary>
/// The economy view (WP-M4-5; spec 7.4 "economy dashboards", R-7.2-02; M4 decision 13's data side): <c>GET /api/economy</c>
/// serves faucet and sink totals per UTC hour and by reason from <c>gold_ledger</c> (moves never count), the snapshot rows
/// and the newest <c>EconomyTotals</c> telemetry line; the window in hours; every read recorded, nothing written elsewhere.
/// </summary>
public sealed class EconomyViewTests : IAsyncLifetime
{
    private const long H0 = 1_800_000_000_000; // the environment's clock starts on this UTC hour
    private const long Hour = EconomyRepository.HourMs;

    private AdminPanelEnvironment _env = null!;
    private long _character;
    private int _nextOp;

    public async Task InitializeAsync()
    {
        _env = await AdminPanelEnvironment.CreateAsync();
        _character = (await _env.CreateCharacterAsync(7, "Trader")).Character.CharacterId;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(AdminPanelEnvironment.Json);

    private async Task LedgerAsync(long atMs, long delta, short flow, string reason)
    {
        string op = $"seed:{_nextOp++}";
        var at = DateTime.UnixEpoch.AddMilliseconds(atMs);
        await using var connection = await _env.RealmDb.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO inventory_ops (op_key, character_id, reason, source, save_id, created_at)
            VALUES (@op, @character, @reason, 'test', @save, @at);
            INSERT INTO gold_ledger (op_key, character_id, delta, balance_after, flow, reason, source, save_id, created_at)
            VALUES (@op, @character, @delta, 1000, @flow, @reason, 'test', @save, @at);
            """, new { op, character = _character, reason, save = Guid.NewGuid(), at, delta, flow });
    }

    private static Dictionary<string, long> Reasons(JsonElement byReason) =>
        byReason.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());

    [Fact]
    public async Task The_economy_view_needs_a_signed_in_admin()
    {
        var response = await _env.App.CreateClient().GetAsync("/api/economy");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(nameof(AdminReasonCode.NotSignedIn), (await JsonOf(response)).GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task It_serves_hourly_faucets_and_sinks_by_reason_the_snapshots_and_the_live_line_and_records_the_read()
    {
        await LedgerAsync(H0 - 1, 999, 0, nameof(InventoryReason.OutpostSale)); // before the window
        await LedgerAsync(H0 + 1_000, 100, 0, nameof(InventoryReason.OutpostSale));
        await LedgerAsync(H0 + 2_000, -25, 1, nameof(InventoryReason.VendorPurchase));
        await LedgerAsync(H0 + 3_000, 500, 2, "Trade"); // a move between players: neither a faucet nor a sink
        await LedgerAsync(H0 + 3_000, -500, 2, "Trade");
        await LedgerAsync(H0 + 2 * Hour + 10, 40, 0, nameof(InventoryReason.OutpostSale));
        var economy = new EconomyRepository(_env.RealmDb);
        await economy.InsertSnapshotAsync(1, new EconomySnapshot(H0 + 60_000, H0, 100, 25, 75, 2,
            [new EconomyReasonDelta(InventoryReason.OutpostSale, 100, 0, 10), new EconomyReasonDelta(InventoryReason.VendorPurchase, 0, 25, 0)]));
        await File.WriteAllLinesAsync(Path.Combine(_env.TelemetryDirectory, "run-0000.jsonl"),
        [
            """{"schema":"mmorpg.telemetry","v":1,"run":"run","startedMs":1,"file":0}""",
            """{"k":"EconomyTotals","t":10,"w":1,"goldCreated":5,"goldDestroyed":1,"goldOnline":4,"activePlayers":1}""",
            """{"k":"EconomyTotals","t":20,"w":1,"goldCreated":140,"goldDestroyed":25,"goldOnline":115,"activePlayers":2}""",
            """{"k":"EconomyByReason","t":20,"w":1,"reason":5,"goldCreated":140}""",
        ]);
        _env.Clock.Advance(2 * Hour + 30 * 60_000); // now: the third hour of the window
        var client = await _env.SignedInClientAsync();

        var body = await JsonOf(await client.GetAsync("/api/economy?hours=3"));

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal((3, H0), (body.GetProperty("windowHours").GetInt32(), body.GetProperty("fromMs").GetInt64()));
        var hours = body.GetProperty("hours").EnumerateArray().ToList();
        Assert.Equal([H0, H0 + 2 * Hour], hours.Select(h => h.GetProperty("hourStartMs").GetInt64()));
        Assert.Equal((100L, 25L), (hours[0].GetProperty("goldCreated").GetInt64(), hours[0].GetProperty("goldDestroyed").GetInt64()));
        Assert.Equal(new Dictionary<string, long> { ["outpostSale"] = 100 }, Reasons(hours[0].GetProperty("createdByReason")));
        Assert.Equal(new Dictionary<string, long> { ["vendorPurchase"] = 25 }, Reasons(hours[0].GetProperty("destroyedByReason")));
        Assert.Equal((140L, 25L), (body.GetProperty("goldCreated").GetInt64(), body.GetProperty("goldDestroyed").GetInt64()));
        Assert.Equal(new Dictionary<string, long> { ["outpostSale"] = 140 }, Reasons(body.GetProperty("createdByReason")));

        // Reasons come from the enum, in value order, so a new InventoryReason needs no page change.
        Assert.Equal(EconomyReasonNames.All, body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));

        var snapshot = Assert.Single(body.GetProperty("snapshots").EnumerateArray());
        Assert.Equal((H0 + 60_000, 75L, 2), (snapshot.GetProperty("takenAtMs").GetInt64(), snapshot.GetProperty("goldOnline").GetInt64(),
            snapshot.GetProperty("activePlayers").GetInt32()));
        Assert.Equal(10, snapshot.GetProperty("byReason").GetProperty("outpostSale").GetProperty("itemsDestroyed").GetInt64());

        var live = body.GetProperty("live");
        Assert.Equal((20L, 140L, 115L, 2L), (live.GetProperty("t").GetInt64(), live.GetProperty("goldCreated").GetInt64(),
            live.GetProperty("goldOnline").GetInt64(), live.GetProperty("activePlayers").GetInt64()));

        var recorded = await _env.Actions.ListRecentAsync(50);
        var view = Assert.Single(recorded, a => a.Action == AdminActionKind.ViewEconomy);
        Assert.Equal(("test:ana", "hours=3", "done"), (view.AdminName, view.Detail, view.State));
    }

    [Fact]
    public async Task The_window_defaults_and_is_capped_by_the_admin_settings_and_a_bad_window_is_refused()
    {
        var client = await _env.SignedInClientAsync();

        var byDefault = await JsonOf(await client.GetAsync("/api/economy"));
        Assert.Equal(24, byDefault.GetProperty("windowHours").GetInt32()); // AdminSettings.economyDefaultHours
        Assert.Equal(JsonValueKind.Null, byDefault.GetProperty("live").ValueKind);
        Assert.Empty(byDefault.GetProperty("hours").EnumerateArray());
        var capped = await JsonOf(await client.GetAsync("/api/economy?hours=100000"));
        Assert.Equal(168, capped.GetProperty("windowHours").GetInt32()); // AdminSettings.economyMaxHours

        foreach (var bad in new[] { "0", "-2", "abc" })
        {
            var response = await client.GetAsync($"/api/economy?hours={bad}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(nameof(AdminReasonCode.RequestInvalid), (await JsonOf(response)).GetProperty("reasonCode").GetString());
        }
        Assert.Equal(2, (await _env.Actions.ListRecentAsync(50)).Count(a => a.Action == AdminActionKind.ViewEconomy));
    }

    [Fact]
    public void The_window_starts_on_a_UTC_hour()
    {
        Assert.Equal(H0, EconomyView.Window(H0, 1));
        Assert.Equal(H0, EconomyView.Window(H0 + Hour - 1, 1));
        Assert.Equal(H0 - 23 * Hour, EconomyView.Window(H0 + 5, 24));
    }
}
