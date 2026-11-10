using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Protocol;
using Shared.Simulation.Items;
using static GameServer.Tests.Items.TransferTestKit;

namespace GameServer.Tests.Items;

/// <summary>
/// The escrow release to mail wired to the real mail writer (WP-M4-3 with WP-M4-4's <see cref="IEscrowMailSink"/>; spec 4.5,
/// 8.2) against a real PostgreSQL: a held escrow released to mail becomes one claimable mail with its items (the instance with
/// its id) and gold, in the escrow's own transaction; a repeated release sends nothing more, another key is refused; the mail
/// is claimed into the recipient's bag once.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class EscrowMailTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly MailRules _rules = MailRules.From(TestCombat.Tables(), TransferTestKit.Items)!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private EscrowRepository _escrows = null!;
    private MailRepository _mail = null!;
    private TransferTestKit _kit = null!;
    private CharacterRecord _seller = null!, _buyer = null!;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _escrows = new EscrowRepository(_realmDb, new EscrowMailSink(_rules));
        _mail = new MailRepository(_realmDb, _clock);
        _kit = new TransferTestKit(_realmDb, _characters, _clock);
        _seller = await _kit.CreateAsync(ash: 10, swords: 1, gold: 100);
        _buyer = await _kit.CreateAsync();
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>The seller holds 4 ash, its sword and 30 gold in escrow (a listing will), committed.</summary>
    private async Task<(string EscrowId, long SwordId)> HeldAsync()
    {
        var world = await _kit.AttachAsync(_seller.CharacterId);
        var escrow = new EscrowService(world, _clock, null);
        var swordAt = SwordAt(world, _seller.CharacterId);
        long swordId = world.CurrentInventory(_seller.CharacterId).At(swordAt).InstanceId;
        var result = escrow.Hold(_seller.CharacterId, [new ReserveItem(new InventoryLocation(Normal, 0), 4), new ReserveItem(swordAt, 1)],
            30, EscrowHolderKind.Test, "listing:1", "escrow:mail:1");
        Assert.Equal(EscrowRefusal.None, result.Refusal);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(Job(world, _seller, SaveReason.Escrow, escrow))).Status);
        return ("escrow:mail:1", swordId);
    }

    [Fact]
    public async Task A_released_escrow_becomes_one_claimable_mail_and_a_repeated_release_sends_nothing()
    {
        var (escrowId, swordId) = await HeldAsync();

        Assert.Equal(EscrowReleaseStatus.Released,
            await _escrows.ReleaseToMailAsync(escrowId, _buyer.CharacterId, (short)MailKind.ListingReturn, "escrow:mail:1:release", _clock.UtcNowMs));
        Assert.Equal(EscrowReleaseStatus.Released, // the same release again (an unknown outcome retried): nothing more
            await _escrows.ReleaseToMailAsync(escrowId, _buyer.CharacterId, (short)MailKind.ListingReturn, "escrow:mail:1:release", _clock.UtcNowMs));
        Assert.Equal(EscrowReleaseStatus.Conflict, // another release of the same escrow: refused
            await _escrows.ReleaseToMailAsync(escrowId, _buyer.CharacterId, (short)MailKind.ListingReturn, "escrow:mail:1:other", _clock.UtcNowMs));

        var record = await _mail.LoadOpenAsync(_buyer.CharacterId, 10);
        var mail = Assert.Single(record.Open);
        Assert.Equal((MailKind.ListingReturn, MailSubjects.ListingReturn, 30L), (mail.Kind, mail.SubjectId, mail.Gold));
        Assert.Equal(4, mail.Items.Single(i => i.DefId == Ash).Count);
        Assert.Equal(swordId, mail.Items.Single(i => i.DefId == Sword).InstanceId); // the instance keeps its id

        // The buyer claims it into the bag, once.
        var bag = await _kit.AttachAsync(_buyer.CharacterId);
        var mailbox = new MailService(_rules, bag, () => _clock.UtcNowMs);
        mailbox.Attach(_buyer.CharacterId, record);
        Assert.Null(mailbox.Claim(_buyer.CharacterId, new MailClaimAsk(1, mail.MailId, false, 1, "mail:test:1:1:1")));
        var buyer = (await _characters.LoadAsync(_buyer.CharacterId))!;
        var saveId = Guid.NewGuid();
        var job = new SaveJob(buyer.CharacterId, buyer.State, buyer.Version, saveId, SaveReason.MailClaim,
            bag.TakeBatch(buyer.CharacterId, saveId), Mail: mailbox.TakeBatch(buyer.CharacterId, saveId));
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);

        Assert.Equal(0, (await _mail.LoadOpenAsync(_buyer.CharacterId, 10)).OpenCount);
        Assert.Equal(30, (await _characters.LoadAsync(_buyer.CharacterId))!.Gold);
        Assert.Contains((await new InventoryRepository(_realmDb).LoadInventoryAsync(_buyer.CharacterId)).Instances,
            i => i.InstanceId == swordId);
    }

    [Fact]
    public async Task A_release_whose_mail_the_tables_refuse_leaves_the_escrow_held()
    {
        var (escrowId, _) = await HeldAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _escrows.ReleaseToMailAsync(escrowId, _buyer.CharacterId, 99, "escrow:mail:1:bad", _clock.UtcNowMs)); // no such mail kind

        Assert.Equal(EscrowState.Held, await _escrows.StateAsync(escrowId));
        Assert.Equal(0, (await _mail.LoadOpenAsync(_buyer.CharacterId, 10)).OpenCount);
    }
}
