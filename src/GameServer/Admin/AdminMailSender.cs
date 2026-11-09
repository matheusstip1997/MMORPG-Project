using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using GameServer.Items.Mail;
using GameServer.Persistence;
using Npgsql;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.Admin;

/// <summary>
/// The compensation an admin asked for (<c>admin_actions.payload</c>, spec 7.4; WP-M4-3): items by id with a rarity and a
/// count, and gold. The panel writes it; only the game server checks it against the tables.
/// </summary>
public sealed class AdminMailPayload
{
    [JsonPropertyName("items")] public List<AdminMailItem> Items { get; set; } = new();
    [JsonPropertyName("gold")] public long Gold { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static AdminMailPayload? TryParse(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AdminMailPayload>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One item of a compensation.</summary>
public sealed class AdminMailItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("rarity")] public int Rarity { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

/// <summary>
/// Executes a claimed compensation command (spec 7.4 "send compensation by mail"; WP-M4-3) on the admin command worker's
/// thread, never the world's: the draft is checked against the server's tables (<see cref="MailRules.Check"/>), then <b>one
/// transaction</b> writes the system mail through <see cref="MailWriter"/> (the stand-alone send path, key
/// <c>admin:&lt;action id&gt;</c>, so a retry writes nothing twice) and the command's outcome. The panel never writes items, gold
/// or mail; the claim of the mail is the faucet (reason <c>Admin</c>, R-4.2-01: the only gold a mail creates). A full mailbox
/// refuses it (a compensation is a new mail source, user answer 2026-11-09).
/// </summary>
public sealed class AdminMailSender(NpgsqlDataSource realmDb, MailRules rules, IClock clock)
{
    /// <summary>The operation key of a compensation (one mail per admin command).</summary>
    public static string OpKey(long actionId) => "admin:" + actionId;

    /// <summary>
    /// Writes the mail of <paramref name="command"/> and completes it as claimed by <paramref name="serverId"/>, in one
    /// transaction. Returns the outcome written; <see cref="AdminOutcome.MailSent"/> when the mail exists (written now or by
    /// an earlier attempt); null when the action is no longer this server's to finish (nothing written).
    /// </summary>
    public async Task<AdminOutcome?> SendAsync(AdminCommand command, string serverId, CancellationToken ct = default)
    {
        var draft = Draft(command);
        long now = clock.UtcNowMs;
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var outcome = AdminOutcome.Invalid;
        if (draft != null && rules.Check(draft) == null && await IsLiveAsync(connection, transaction, command.CharacterId, ct))
        {
            // Two compensations for one character at once (two servers claiming two actions) count the open mails one after the
            // other, so the cap holds. A transaction lock on the recipient, not the character row: the world's saves of that
            // row never wait for it.
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended('mail-cap:' || @id, 0))",
                new { id = command.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, transaction,
                cancellationToken: ct));
            if (await MailWriter.CountOpenAsync(connection, transaction, command.CharacterId, ct) >= rules.Settings.MailboxCap)
                outcome = AdminOutcome.MailboxFull;
            else
            {
                await MailWriter.SendAsync(connection, transaction, draft, now, rules.ExpiresAt(now), carrierId: null, saveId: null,
                    skipDuplicate: true, ct);
                outcome = AdminOutcome.MailSent;
            }
        }
        int completed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE admin_actions SET state = 'done', outcome = @outcome, completed_at = @now
            WHERE action_id = @actionId AND claimed_by = @serverId
              AND (state = 'claimed' OR (state = 'done' AND outcome = 'Expired'))
            """, new { actionId = command.ActionId, serverId, outcome = outcome.ToString(), now = DbTime.ToUtc(now) }, transaction,
            cancellationToken: ct));
        if (completed != 1)
        {
            // The action is not this server's to finish any more (completed elsewhere, or not claimed by it): write nothing.
            await transaction.RollbackAsync(ct);
            return null;
        }
        await transaction.CommitAsync(ct);
        return outcome;
    }

    /// <summary>The system mail of a compensation command; null when its payload is not one.</summary>
    public static MailDraft? Draft(AdminCommand command)
    {
        var payload = AdminMailPayload.TryParse(command.Payload);
        if (payload == null || command.CharacterId <= 0 || payload.Items.Any(i => i.Rarity is < 0 or > byte.MaxValue))
            return null;
        string key = OpKey(command.ActionId);
        return new MailDraft(command.CharacterId, MailKind.AdminCompensation, MailSubjects.AdminCompensation, Array.Empty<MailArg>(),
            payload.Items.Select(i => new MailAttachment(i.Id, (byte)i.Rarity, i.Count)).ToList(), payload.Gold, key, key);
    }

    private static Task<bool> IsLiveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId,
        CancellationToken ct) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM characters WHERE character_id = @characterId AND deleted_at IS NULL)",
            new { characterId }, transaction, cancellationToken: ct));
}
