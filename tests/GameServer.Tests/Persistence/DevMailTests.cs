using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data;
using Shared.Protocol;

namespace GameServer.Tests.Persistence;

/// <summary>
/// DbMigrate's dev mail seed (WP-M4-3, the QA smoke step <c>claim-mail</c>) against a real PostgreSQL: the mail goes through
/// <see cref="MailWriter"/>, the send path, with its checked draft; a second run of the same seed writes nothing more, and a
/// draft the tables refuse writes nothing.
/// </summary>
public sealed class DevMailTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(5, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly ConfigSet _tables = TestCombat.Tables();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, new FakeClock());
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Fact]
    public async Task The_seed_writes_one_mail_with_the_item_and_the_gold_once()
    {
        long id = (await _characters.CreateAsync(new NewCharacter(1, 0, "Mailed", "warrior", Spawn, Guid.NewGuid()))).Character!.CharacterId;

        Assert.Equal(DevMail.Outcome.Sent, (await DevMail.SendAsync(_realmDb, _tables, "Mailed", "m1.demonAsh", 3, 120)).Outcome);
        Assert.Equal(DevMail.Outcome.AlreadySent, (await DevMail.SendAsync(_realmDb, _tables, "Mailed", "m1.demonAsh", 3, 120)).Outcome);
        Assert.Equal(DevMail.Outcome.NoCharacter, (await DevMail.SendAsync(_realmDb, _tables, "Nobody", "m1.demonAsh", 3, 120)).Outcome);
        Assert.Equal(DevMail.Outcome.Invalid, (await DevMail.SendAsync(_realmDb, _tables, "Mailed", "nope", 1, 0)).Outcome);

        var record = await new MailRepository(_realmDb, new FakeClock()).LoadOpenAsync(id, 10);
        var mail = Assert.Single(record.Open);
        Assert.Equal((MailKind.AdminCompensation, 120L, "m1.demonAsh", 3), (mail.Kind, mail.Gold, mail.Items.Single().DefId, mail.Items.Single().Count));
    }
    [Fact]
    public async Task A_tag_writes_the_mail_again_once_per_tag()
    {
        long id = (await _characters.CreateAsync(new NewCharacter(1, 0, "Tagged", "warrior", Spawn, Guid.NewGuid()))).Character!.CharacterId;

        Assert.Equal(DevMail.Outcome.Sent, (await DevMail.SendAsync(_realmDb, _tables, "Tagged", "m1.demonAsh", 3, 50, "run1")).Outcome);
        Assert.Equal(DevMail.Outcome.AlreadySent, (await DevMail.SendAsync(_realmDb, _tables, "Tagged", "m1.demonAsh", 3, 50, "run1")).Outcome);
        Assert.Equal(DevMail.Outcome.Sent, (await DevMail.SendAsync(_realmDb, _tables, "Tagged", "m1.demonAsh", 3, 50, "run2")).Outcome);

        Assert.Equal(2, (await new MailRepository(_realmDb, new FakeClock()).LoadOpenAsync(id, 10)).Open.Count);
        Assert.Equal("dev-mail:7:m1.demonAsh:3:50:run1", DevMail.OpKey(7, "m1.demonAsh", 3, 50, "run1"));
    }
}
