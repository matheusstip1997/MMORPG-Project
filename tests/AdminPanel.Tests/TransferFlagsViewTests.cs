using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminPanel.Tests.Support;
using Dapper;
using GameServer.Admin;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace AdminPanel.Tests;

/// <summary>
/// The transfer flag list (WP-M4-15; spec 4.5 R-4.5-02, 7.4; M4 decision 17): <c>GET /api/transfer-flags</c> lists the flags the
/// game servers' scan wrote, newest day first, with the characters' names; the window in days, defaulted, capped and bounded
/// by <c>transferFlagMaxRows</c> (<c>cut</c>); every read recorded; nothing changes a player.
/// </summary>
public sealed class TransferFlagsViewTests : IAsyncLifetime
{
    private AdminPanelEnvironment _env = null!;
    private long _ann, _bob;

    public async Task InitializeAsync()
    {
        _env = await AdminPanelEnvironment.CreateAsync();
        _ann = (await _env.CreateCharacterAsync(71, "Ann")).Character.CharacterId;
        _bob = (await _env.CreateCharacterAsync(72, "Bob")).Character.CharacterId;
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(AdminPanelEnvironment.Json);

    /// <summary>A committed trade from Ann to Bob of <paramref name="gold"/>, as the game server writes its <c>transfer_ops</c> row.</summary>
    private async Task TradeAsync(long gold, long atMs)
    {
        bool annIsA = _ann < _bob;
        string side = $$"""{"items":[],"gold":{{gold}}}""", none = """{"items":[],"gold":0}""";
        await using var connection = await _env.RealmDb.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO transfer_ops (op_key, kind, character_a, character_b, save_id_a, save_id_b, summary, created_at)
            VALUES (@op, 1, @a, @b, gen_random_uuid(), gen_random_uuid(), @summary::jsonb, @at)
            """, new
        {
            op = "trade:test:" + atMs, a = Math.Min(_ann, _bob), b = Math.Max(_ann, _bob),
            summary = annIsA ? $$"""{"aToB":{{side}},"bToA":{{none}}}""" : $$"""{"aToB":{{none}},"bToA":{{side}}}""",
            at = DateTime.UnixEpoch.AddMilliseconds(atMs),
        });
    }

    private Task<TransferFlagScan> ScanAsync() => new TransferFlagRepository(_env.RealmDb, _env.Clock).ScanAsync(new TransferFlagSettings
    {
        GoldPerPairDay = 1_000, ValuePerPairDay = 1_000_000, OneSidedGoldPercent = 10, ScanIntervalMs = 30_000, ScanBatchMax = 100, LookbackMs = 10 * TransferFlagSettings.DayMs,
    }, null);

    [Fact]
    public async Task The_list_needs_a_signed_in_admin()
    {
        var response = await _env.App.CreateClient().GetAsync("/api/transfer-flags");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_trade_over_the_threshold_is_listed_with_the_names_and_the_read_is_recorded()
    {
        long now = _env.Clock.UtcNowMs;
        await TradeAsync(1_500, now - 60_000);
        await TradeAsync(400, now - 3 * TransferFlagSettings.DayMs); // under the threshold that day: no flag
        await ScanAsync();
        var client = await _env.SignedInClientAsync();

        var body = await JsonOf(await client.GetAsync("/api/transfer-flags"));

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(7, body.GetProperty("windowDays").GetInt32()); // AdminSettings.transferFlagDefaultDays
        Assert.False(body.GetProperty("cut").GetBoolean());
        var flag = Assert.Single(body.GetProperty("flags").EnumerateArray());
        Assert.Equal(("Ann", "Bob", _ann, _bob), (flag.GetProperty("giverName").GetString(), flag.GetProperty("receiverName").GetString(),
            flag.GetProperty("giverCharacterId").GetInt64(), flag.GetProperty("receiverCharacterId").GetInt64()));
        Assert.Equal((1_500L, 1_500L, TransferFlagMath.GoldCrossed, 1), (flag.GetProperty("netGold").GetInt64(), flag.GetProperty("netValue").GetInt64(),
            flag.GetProperty("crossed").GetInt32(), flag.GetProperty("transfers").GetInt32()));
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(now).ToString("yyyy-MM-dd"), flag.GetProperty("day").GetString());

        var view = Assert.Single(await _env.Actions.ListRecentAsync(50), a => a.Action == AdminActionKind.ViewTransferFlags);
        Assert.Equal(("test:ana", "days=7", "done"), (view.AdminName, view.Detail, view.State));
    }

    [Fact]
    public async Task The_window_is_capped_a_bad_one_refused_and_a_longer_list_is_cut_to_the_newest()
    {
        long now = _env.Clock.UtcNowMs;
        for (int day = 0; day < 4; day++)
            await TradeAsync(2_000, now - day * TransferFlagSettings.DayMs - 1_000);
        await ScanAsync();
        var client = await _env.SignedInClientAsync();

        var capped = await JsonOf(await client.GetAsync("/api/transfer-flags?days=100000"));
        Assert.Equal(31, capped.GetProperty("windowDays").GetInt32()); // AdminSettings.transferFlagMaxDays
        var days = capped.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("day").GetString()).ToList();
        Assert.Equal(4, days.Count);
        Assert.Equal(days.OrderByDescending(d => d, StringComparer.Ordinal), days); // newest first
        var two = await JsonOf(await client.GetAsync("/api/transfer-flags?days=2"));
        Assert.Equal(2, two.GetProperty("flags").GetArrayLength());

        foreach (var bad in new[] { "0", "-1", "x" })
        {
            var response = await client.GetAsync($"/api/transfer-flags?days={bad}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // The cut: one flag past the cap says the window held more.
        var repository = new TransferFlagRepository(_env.RealmDb, _env.Clock);
        var newest = await repository.ListAsync(TransferFlagMath.UtcDay(now) - 30, 2);
        Assert.Equal(2, newest.Count);
        Assert.Equal(days.Take(2), newest.Select(f => f.Day));
    }
}
