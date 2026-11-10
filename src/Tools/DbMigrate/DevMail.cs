using Dapper;
using GameServer.Host;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using Npgsql;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

/// <summary>
/// Dev only (WP-M4-3, the QA smoke step <c>claim-mail</c> and play tests): writes a system mail with an item and gold for a
/// character in the local dev realm database. The mail goes through <see cref="MailWriter"/>, the one send path of the mail
/// tables (the stand-alone send the admin compensation uses), with the draft checked by <see cref="MailRules.Check"/>; never raw
/// SQL. The mail is an admin compensation (its claim is the faucet, reason <c>Admin</c>). Its operation key is fixed per
/// character, item, count, gold and the optional tag (the QA smoke passes its run id), so a second run writes nothing more. The mailbox is not the character row: the character
/// may be online (a world holding it sees the mail at its next mailbox read). Never for a shared or real database:
/// <c>DbMigrate</c> takes it only with <c>--dev</c>, and only a Debug build has the <c>--mail</c> argument.
/// </summary>
internal static class DevMail
{
    public const string Source = "dev-mail";

    public enum Outcome { Sent, AlreadySent, NoCharacter, Invalid }

    /// <summary>The operation key of one seed mail: a second run with the same tag finds it and writes nothing.</summary>
    public static string OpKey(long characterId, string itemId, int count, long gold, string? tag = null) =>
        $"{Source}:{characterId}:{itemId}:{count}:{gold}" + (tag == null ? "" : ":" + tag);

    /// <summary>Writes a mail with <paramref name="count"/> of <paramref name="itemId"/> and <paramref name="gold"/> gold.</summary>
    public static async Task<(Outcome Outcome, string? Error)> SendAsync(NpgsqlDataSource realm, ConfigSet tables, string characterName,
        string itemId, int count, long gold, string? tag = null)
    {
        if (ItemRules.From(tables) is not { } items || MailRules.From(tables, items) is not { } rules)
            return (Outcome.Invalid, "the item or mail tables are missing");
        await using var connection = await realm.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        long? characterId = await connection.QuerySingleOrDefaultAsync<long?>(
            "SELECT character_id FROM characters WHERE name = @characterName AND deleted_at IS NULL", new { characterName }, transaction);
        if (characterId is not { } id)
            return (Outcome.NoCharacter, null);
        string key = OpKey(id, itemId, count, gold, tag);
        var draft = new MailDraft(id, MailKind.AdminCompensation, MailSubjects.AdminCompensation, Array.Empty<MailArg>(),
            new[] { new MailAttachment(itemId, 0, count) }, gold, key, Source);
        if (rules.Check(draft) is { } error)
            return (Outcome.Invalid, error);
        long now = new SystemClock().UtcNowMs;
        var mailId = await MailWriter.SendAsync(connection, transaction, draft, now, rules.ExpiresAt(now), carrierId: null, saveId: null,
            skipDuplicate: true, CancellationToken.None);
        await transaction.CommitAsync();
        return (mailId == null ? Outcome.AlreadySent : Outcome.Sent, null);
    }

    /// <summary>The command line's --mail: every (name, item, count, gold) in order, printed; exit code 0, or 1 on any refusal.</summary>
    public static async Task<int> RunAsync(string realmConnection, IReadOnlyList<(string Name, string Item, int Count, long Gold)> mails,
        string? tag = null)
    {
        var loaded = GameServer.Host.Config.ConfigLoader.LoadAndValidate(Path.Combine("data", "tables"), ConfigTables.All);
        if (!loaded.Ok)
        {
            Console.Error.WriteLine("--mail needs the config tables (run it from the repository root).");
            return 2;
        }
        await using var realm = NpgsqlDataSource.Create(realmConnection);
        int exit = 0;
        foreach (var (name, item, count, gold) in mails)
        {
            var (outcome, error) = await SendAsync(realm, loaded.Set, name, item, count, gold, tag);
            bool ok = outcome is Outcome.Sent or Outcome.AlreadySent;
            (ok ? Console.Out : Console.Error).WriteLine($"--mail {name} {item} {count} {gold}: {outcome}{(error == null ? "" : $" ({error})")}");
            if (!ok)
                exit = 1;
        }
        return exit;
    }
}
