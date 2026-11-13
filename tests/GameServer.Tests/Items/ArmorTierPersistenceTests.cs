using Dapper;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Progression;
using GameServer.Skills;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;
using Shared.Simulation.Skills;

namespace GameServer.Tests.Items;

/// <summary>
/// A tier change against a real PostgreSQL (spec 3.5 R-3.5-08, 4.1 R-4.1-05, M4 decision 9b, 8.2; WP-M4-13; CLAUDE.md rule 5:
/// double submit, retry, crash in the middle). A Knight learning Combat Training (medium) with a heavy chest worn and a full
/// bag: the skill operation, the piece leaving the Equipped container and the <c>ArmorReturn</c> mail that carries it are one
/// character save: all or nothing.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class ArmorTierPersistenceTests : IAsyncLifetime
{
    private const string CombatTraining = "knight.combatTraning";
    private static readonly CharacterState Spawn = new(35, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100, SkillPoints: 35);
    private static readonly Shared.Data.ConfigSet Tables = ServerTestEnvironment.LoadTables();

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ItemRules _items = ItemRules.From(Tables)!;
    private readonly MailRules _mailRules;
    private readonly ProgressionRules _progressionRules = ProgressionRules.From(Tables)!;
    private readonly SkillRules _skillRules = SkillRules.From(Tables)!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private ProgressionRepository _progression = null!;
    private SkillRepository _skills = null!;
    private MailRepository _mail = null!;
    private long _nextAccount = 1;

    public ArmorTierPersistenceTests() => _mailRules = MailRules.From(Tables, _items)!;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
        _progression = new ProgressionRepository(_realmDb);
        _skills = new SkillRepository(_realmDb);
        _mail = new MailRepository(_realmDb, _clock);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private sealed record World(InventoryService Items, MailService Mail, ProgressionStore Progression, SkillTreeService Skills);

    private async Task<World> LoginAsync(CharacterRecord character)
    {
        long id = character.CharacterId;
        var items = new InventoryService(_items, events: null);
        Assert.True(items.Attach(id, await _inventory.LoadInventoryAsync(id), character.ClassId, character.State.Level));
        items.AttachGold(id, character.Gold);
        var mail = new MailService(_mailRules, items, () => _clock.UtcNowMs);
        mail.Attach(id, await _mail.LoadOpenAsync(id, _mailRules.Settings.MaxListed));
        var progression = new ProgressionStore(_progressionRules);
        progression.Attach(id, character.State, await _progression.LoadAsync(id));
        var skills = new SkillTreeService(_skillRules, progression) { ArmorTiers = new ArmorTierSweep(items, () => mail) };
        skills.Attach(id, character.ClassId, await _skills.LoadAsync(id));
        items.SetArmorTier(id, skills.EffectiveArmor(id)!.Value);
        return new World(items, mail, progression, skills);
    }

    /// <summary>A Knight wearing an iron cuirass with a Safe bag full of swords, saved.</summary>
    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var created = await _characters.CreateAsync(new NewCharacter(account, 0, $"Knight{account}", "knight", Spawn, Guid.NewGuid()));
        var character = created.Character!;
        long id = character.CharacterId;
        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("UPDATE characters SET level = 35, skill_points = 35 WHERE character_id = @id", new { id });
        character = (await _characters.LoadAsync(id))!;
        var world = await LoginAsync(character);
        Assert.True(_items.TryGetItem("m2.ironCuirass", out var cuirass));
        Assert.True(_items.TryGetItem("m2.trainingSword", out var sword));
        Assert.Equal(1, world.Items.TryAdd(id, cuirass, 0, 1, "seed:cuirass", InventoryReason.Admin, "test").Added);
        byte safe = (byte)_items.ReceiverOf(ItemCategory.Equipment)!.Code, equipped = (byte)_items.Equipped!.Code;
        Assert.True(world.Items.TryMove(id, new InventoryLocation(safe, 0), new InventoryLocation(equipped, 3), "seed:equip",
            InventoryReason.PlayerMove).Applied);
        for (int i = 0; i < _items.ReceiverOf(ItemCategory.Equipment)!.Slots; i++)
            Assert.Equal(1, world.Items.TryAdd(id, sword, 0, 1, $"seed:sword:{i}", InventoryReason.Admin, "test").Added);
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved,
            (await _characters.SaveAsync(id, character.State, character.Version, saveId, world.Items.TakeBatch(id, saveId))).Status);
        return (await _characters.LoadAsync(id))!;
    }

    /// <summary>Learns Combat Training on a fresh login and takes the save it would issue.</summary>
    private async Task<(World World, SaveJob Job)> LearnAsync(CharacterRecord character, string key)
    {
        var world = await LoginAsync(character);
        long id = character.CharacterId;
        Assert.Null(world.Skills.Handle(id, new SkillTreeAsk(1, SkillTreeOp.Learn, CombatTraining, 0, key, 35, false), connection: 1));
        var saveId = Guid.NewGuid();
        var progress = world.Progression.Snapshot(id);
        var state = character.State with { SkillPoints = progress.SkillPoints };
        var job = new SaveJob(id, state, character.Version, saveId, SaveReason.SkillTree, world.Items.TakeBatch(id, saveId),
            world.Progression.TakeBatch(id, saveId), world.Skills.TakeBatch(id, saveId), Mail: world.Mail.TakeBatch(id, saveId));
        return (world, job);
    }

    private Task<SaveResult> SaveAsync(SaveJob job) =>
        _characters.SaveAsync(job.CharacterId, job.State, job.ExpectedVersion, job.SaveId, job.Inventory, progression: job.Progression,
            skills: job.Skills, mail: job.Mail);

    private async Task<(bool CuirassWorn, bool Learned, int Mails)> StoredAsync(long id)
    {
        var load = await _inventory.LoadInventoryAsync(id);
        bool worn = load.Instances.Any(i => i.DefId == "m2.ironCuirass" && i.Container == _items.Equipped!.Code);
        bool learned = (await _skills.LoadAsync(id)).Learned.Any(s => s.NodeId == CombatTraining);
        return (worn, learned, (await _mail.LoadOpenAsync(id, 10)).OpenCount);
    }

    [Fact]
    public async Task The_skill_op_the_unequip_and_the_armor_return_mail_commit_together()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        Assert.Equal((true, false, 0), await StoredAsync(id));

        var (world, job) = await LearnAsync(character, "skill:learn:1");
        var send = Assert.Single(job.Mail!.Sends);
        Assert.Equal(MailKind.ArmorReturn, send.Draft.Kind);

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(job)).Status);

        Assert.Equal((false, true, 1), await StoredAsync(id));
        var mail = Assert.Single((await _mail.LoadOpenAsync(id, 10)).Open);
        var item = Assert.Single(mail.Items);
        Assert.Equal((MailKind.ArmorReturn, "m2.ironCuirass", 1, send.Draft.Items[0].InstanceId),
            (mail.Kind, item.DefId, item.Count, item.InstanceId));
        Assert.Contains("originalOwner", item.PropertiesJson);
        // The commit answers the op, after which the tier the equip check reads is medium (set at apply).
        world.Items.Committed(id, job.SaveId);
        world.Skills.Committed(id, job.SaveId);
        Assert.Equal(ArmorClass.Medium, world.Skills.EffectiveArmor(id));
    }

    [Fact]
    public async Task A_retry_of_the_committed_save_writes_no_second_mail()
    {
        var character = await CreateAsync();
        var (_, job) = await LearnAsync(character, "skill:learn:retry");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(job)).Status);

        var retry = await SaveAsync(job); // the worker never heard the answer and retries the identical job

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal((false, true, 1), await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_crash_in_the_middle_writes_nothing_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        var (_, job) = await LearnAsync(character, "skill:learn:crash");
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_armor_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_armor_crash BEFORE INSERT ON mail_items FOR EACH ROW EXECUTE FUNCTION test_armor_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(job));
        Assert.Equal((true, false, 0), await StoredAsync(character.CharacterId)); // the skill and the unequip rolled back with the mail

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_armor_crash ON mail_items; DROP FUNCTION test_armor_crash();");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(job)).Status);
        Assert.Equal((false, true, 1), await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task The_mail_kind_check_takes_the_armor_return()
    {
        // V0019 widened mail.kind to 1..7 (MailKind.ArmorReturn); 8 is still refused.
        await using var connection = await _realmDb.OpenConnectionAsync();
        string check = await connection.ExecuteScalarAsync<string>(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'mail_kind_check'") ?? "";
        Assert.Contains("7", check);
        Assert.Equal((int)ReliableMessages.LastMailKind, 7);
    }
}
