using Dapper;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Protocol;

namespace GameServer.Tests.Social;

/// <summary>
/// Friends and the block list against a real PostgreSQL (WP-M4-16; spec 6.5 "M4 social", 7.1; rule 5): a social operation rides
/// the character save that carries it, so it is written with the save or not at all; a double submit and a retry after an unknown
/// outcome write the same end state once; a crash in the middle writes nothing; both sides accepting at once leave one row; and a
/// login reads the lists back through the character source, each side with the name it saw.
/// </summary>
public sealed class SocialPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private SocialRepository _social = null!;
    private long _nextAccount = 1;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _social = new SocialRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<CharacterRecord> CreateAsync(string name)
    {
        var result = await _characters.CreateAsync(new NewCharacter(_nextAccount++, 0, name, "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return (await _characters.LoadAsync(result.Character!.CharacterId))!;
    }

    private Task<SaveResult> SaveAsync(CharacterRecord character, Guid saveId, params SocialOpRow[] ops) =>
        _characters.SaveJobAsync(new SaveJob(character.CharacterId, character.State, character.Version, saveId, SaveReason.Social,
            Social: new SocialBatch(ops)));

    private static SocialOpRow Friend(CharacterRecord accepter, CharacterRecord asker) =>
        new(SocialOpKind.FriendAdd, accepter.CharacterId, asker.CharacterId, OtherName: asker.Name, OwnName: accepter.Name);

    private async Task<int> FriendRowsAsync()
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM friends");
    }

    [Fact]
    public async Task A_friendship_and_a_block_are_read_back_at_login_each_side_with_the_name_it_saw()
    {
        var ann = await CreateAsync("Ann");
        var bob = await CreateAsync("Bob");
        var cid = await CreateAsync("Cid");

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(bob, Guid.NewGuid(), Friend(bob, ann),
            new SocialOpRow(SocialOpKind.BlockSet, bob.CharacterId, cid.CharacterId, OtherName: "Cid", BlockKind: SocialBlockKind.Mute))).Status);

        var source = new RepositoryCharacterSource(_characters, social: _social);
        var annLists = await source.LoadSocialAsync(ann.CharacterId);
        var bobLists = await source.LoadSocialAsync(bob.CharacterId);
        Assert.Equal([new FriendRow(bob.CharacterId, "Bob")], annLists.Friends);
        Assert.Empty(annLists.Blocks);
        Assert.Equal([new FriendRow(ann.CharacterId, "Ann")], bobLists.Friends);
        Assert.Equal([new BlockRow(cid.CharacterId, SocialBlockKind.Mute, "Cid")], bobLists.Blocks);

        // A mute made a block, then lifted; a friendship ended from the other side.
        bob = (await _characters.LoadAsync(bob.CharacterId))!;
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(bob, Guid.NewGuid(),
            new SocialOpRow(SocialOpKind.BlockSet, bob.CharacterId, cid.CharacterId, OtherName: "Cid", BlockKind: SocialBlockKind.Block))).Status);
        Assert.Equal(SocialBlockKind.Block, Assert.Single((await _social.LoadAsync(bob.CharacterId)).Blocks).Kind);
        bob = (await _characters.LoadAsync(bob.CharacterId))!;
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(bob, Guid.NewGuid(),
            new SocialOpRow(SocialOpKind.BlockRemove, bob.CharacterId, cid.CharacterId))).Status);
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(ann, Guid.NewGuid(),
            new SocialOpRow(SocialOpKind.FriendRemove, ann.CharacterId, bob.CharacterId))).Status);
        Assert.Equal(SocialRecord.Empty.Friends, (await _social.LoadAsync(bob.CharacterId)).Friends);
        Assert.Empty((await _social.LoadAsync(bob.CharacterId)).Blocks);
    }

    [Fact]
    public async Task A_double_submit_and_a_retry_after_an_unknown_outcome_write_one_row()
    {
        var ann = await CreateAsync("Ann");
        var bob = await CreateAsync("Bob");
        var saveId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SaveAsync(bob, saveId, Friend(bob, ann)))));
        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        var retry = await SaveAsync(bob, saveId, Friend(bob, ann)); // the worker never heard the answer

        Assert.Equal(new SaveResult(SaveStatus.Saved, bob.Version + 1), retry);
        Assert.Equal(1, await FriendRowsAsync());
        Assert.Equal(bob.Version + 1, (await _characters.LoadAsync(bob.CharacterId))!.Version);
    }

    [Fact]
    public async Task A_crash_in_the_middle_writes_nothing_and_the_retry_writes_it()
    {
        var ann = await CreateAsync("Ann");
        var bob = await CreateAsync("Bob");
        var saveId = Guid.NewGuid();
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT ON blocks FOR EACH STATEMENT EXECUTE FUNCTION test_crash();
                """);
        }
        var ops = new[]
        {
            Friend(bob, ann),
            new SocialOpRow(SocialOpKind.BlockSet, bob.CharacterId, ann.CharacterId, OtherName: "Ann", BlockKind: SocialBlockKind.Mute),
        };

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(bob, saveId, ops));
        Assert.Equal(0, await FriendRowsAsync()); // the friendship before the failing block rolled back with it
        Assert.Equal(bob.Version, (await _characters.LoadAsync(bob.CharacterId))!.Version);

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON blocks");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(bob, saveId, ops)).Status);
        Assert.Equal(1, await FriendRowsAsync());
        Assert.Single((await _social.LoadAsync(bob.CharacterId)).Blocks);
    }

    [Fact]
    public async Task Both_sides_writing_the_friendship_at_once_leave_one_row()
    {
        var ann = await CreateAsync("Ann");
        var bob = await CreateAsync("Bob");

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(ann, Guid.NewGuid(), Friend(ann, bob))),
            Task.Run(() => SaveAsync(bob, Guid.NewGuid(), Friend(bob, ann))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(1, await FriendRowsAsync());
        Assert.Equal("Bob", Assert.Single((await _social.LoadAsync(ann.CharacterId)).Friends).Name);
        Assert.Equal("Ann", Assert.Single((await _social.LoadAsync(bob.CharacterId)).Friends).Name);
    }
}
