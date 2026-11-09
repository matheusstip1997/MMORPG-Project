using GameServer.Admin;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using GameServer.World;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Admin;

/// <summary>
/// "Send compensation by mail" (spec 7.4; WP-M4-3) against a real PostgreSQL: the panel records a <c>send_mail</c> action with
/// its payload and no target server; any game server's worker claims it, checks it against its tables and writes the system
/// mail and the outcome in one transaction (the stand-alone send path), then asks an online recipient's world to reload. A
/// retry writes no second mail; a bad item or a full mailbox is refused with its outcome.
/// </summary>
public sealed class AdminMailTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly MailRules _rules;
    private NpgsqlDataSource _realm = null!;
    private AdminActionRepository _actions = null!;
    private MailRepository _mail = null!;
    private long _characterId;

    public AdminMailTests()
    {
        var tables = TestCombat.Tables();
        _rules = MailRules.From(tables, ItemRules.From(tables))!;
    }

    public async Task InitializeAsync()
    {
        _realm = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _actions = new AdminActionRepository(_realm, 1, _clock);
        _mail = new MailRepository(_realm, _clock);
        var characters = new CharacterRepository(_realm, 1, _clock);
        _characterId = (await characters.CreateAsync(new NewCharacter(9, 0, "Compensated", "warrior", Spawn, Guid.NewGuid()))).Character!.CharacterId;
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private AdminCommandWorker Worker(WorldInbox inbox) =>
        new(_actions, inbox, new AdminSettings { CommandPollMs = 100, CommandExpiryMs = 30_000, MaxClaimPerPoll = 10 }, "server-b",
            mail: new AdminMailSender(_realm, _rules, _clock));

    private Task<(AdminSubmitStatus Status, AdminActionRecord Record)> SubmitAsync(string payload) =>
        _actions.SubmitAsync(new AdminActionRequest(Guid.NewGuid(), "dev:ana", AdminActionKind.SendMail, _characterId, 9,
            TargetServer: null, Detail: "lost sword", Payload: payload), null);

    [Fact]
    public async Task Any_server_writes_the_compensation_mail_and_the_outcome_in_one_transaction()
    {
        var inbox = new WorldInbox();
        await using var worker = Worker(inbox);
        var submitted = await SubmitAsync(new AdminMailPayload
        {
            Gold = 500, Items = [new AdminMailItem { Id = "m1.demonAsh", Count = 3 }, new AdminMailItem { Id = "m2.trainingSword", Count = 1 }],
        }.ToJson());

        await worker.PollAsync();

        var row = (await _actions.GetAsync(submitted.Record.RequestId))!;
        Assert.Equal((AdminActionState.Done, nameof(AdminOutcome.MailSent), "server-b"), (row.State, row.Outcome, row.ClaimedBy));
        var mail = Assert.Single((await _mail.LoadOpenAsync(_characterId, 10)).Open);
        Assert.Equal((MailKind.AdminCompensation, MailSubjects.AdminCompensation, 500L, 2),
            (mail.Kind, mail.SubjectId, mail.Gold, mail.Items.Count));
        Assert.True(inbox.TryTake(out var command));
        Assert.Equal((WorldCommandKind.MailArrived, _characterId), (command.Kind, command.CharacterId));

        // The same command again after it completed (a claim run twice) is no longer this server's: nothing is written.
        var again = await new AdminMailSender(_realm, _rules, _clock).SendAsync(
            new AdminCommand(row.ActionId, AdminActionKind.SendMail, _characterId, new AdminMailPayload
            {
                Gold = 500, Items = [new AdminMailItem { Id = "m1.demonAsh", Count = 3 }, new AdminMailItem { Id = "m2.trainingSword", Count = 1 }],
            }.ToJson()), "server-b");
        Assert.Null(again);
        Assert.Equal(1, (await _mail.LoadOpenAsync(_characterId, 10)).OpenCount);
        await worker.PollAsync();
        Assert.False(inbox.TryTake(out _)); // claimed once
    }

    [Fact]
    public async Task A_compensation_the_tables_refuse_is_closed_invalid_and_writes_nothing()
    {
        var inbox = new WorldInbox();
        await using var worker = Worker(inbox);
        var unknown = await SubmitAsync(new AdminMailPayload { Items = [new AdminMailItem { Id = "nope", Count = 1 }] }.ToJson());
        var tooMany = await SubmitAsync(new AdminMailPayload { Items = [new AdminMailItem { Id = "m1.demonAsh", Count = 5_000 }] }.ToJson());

        await worker.PollAsync();

        Assert.Equal(nameof(AdminOutcome.Invalid), (await _actions.GetAsync(unknown.Record.RequestId))!.Outcome);
        Assert.Equal(nameof(AdminOutcome.Invalid), (await _actions.GetAsync(tooMany.Record.RequestId))!.Outcome);
        Assert.Equal(0, (await _mail.LoadOpenAsync(_characterId, 10)).OpenCount);
        Assert.False(inbox.TryTake(out _));
    }

    [Fact]
    public async Task A_send_for_an_action_another_server_finished_writes_nothing()
    {
        var submitted = await SubmitAsync(new AdminMailPayload { Gold = 5 }.ToJson());
        var claimed = Assert.Single(await _actions.ClaimAsync("server-a", 10));
        Assert.True(await _actions.CompleteAsync(claimed.ActionId, "server-a", AdminOutcome.Invalid));

        var outcome = await new AdminMailSender(_realm, _rules, _clock).SendAsync(claimed, "server-b");

        Assert.Null(outcome); // not ours: rolled back, no mail and no reload
        Assert.Equal(0, (await _mail.LoadOpenAsync(_characterId, 10)).OpenCount);
        Assert.Equal(nameof(AdminOutcome.Invalid), (await _actions.GetAsync(submitted.Record.RequestId))!.Outcome);
    }

    [Fact]
    public async Task A_compensation_above_the_gold_cap_is_invalid()
    {
        var inbox = new WorldInbox();
        await using var worker = Worker(inbox);
        var rich = await SubmitAsync(new AdminMailPayload { Gold = _rules.MaxGold + 1 }.ToJson());

        await worker.PollAsync();

        Assert.Equal(nameof(AdminOutcome.Invalid), (await _actions.GetAsync(rich.Record.RequestId))!.Outcome);
        Assert.Equal(0, (await _mail.LoadOpenAsync(_characterId, 10)).OpenCount);
    }

    [Fact]
    public async Task A_full_mailbox_refuses_a_compensation()
    {
        var inbox = new WorldInbox();
        await using var worker = Worker(inbox);
        for (int i = 0; i < _rules.Settings.MailboxCap; i++)
            await SubmitAsync(new AdminMailPayload { Gold = 1 + i }.ToJson());
        for (int poll = 0; poll * 10 < _rules.Settings.MailboxCap; poll++) // 10 claims per poll
            await worker.PollAsync();
        Assert.Equal(_rules.Settings.MailboxCap, (await _mail.LoadOpenAsync(_characterId, 1)).OpenCount);

        var full = await SubmitAsync(new AdminMailPayload { Gold = 1 }.ToJson());
        await worker.PollAsync();

        Assert.Equal(nameof(AdminOutcome.MailboxFull), (await _actions.GetAsync(full.Record.RequestId))!.Outcome);
        Assert.Equal(_rules.Settings.MailboxCap, (await _mail.LoadOpenAsync(_characterId, 1)).OpenCount);
    }
}
