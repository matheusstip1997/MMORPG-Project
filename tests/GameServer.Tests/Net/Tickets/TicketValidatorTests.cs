using GameServer.Net.Tickets;
using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Net.Tickets;

public class TicketValidatorTests
{
    private const int Realm = 1;
    private const int LifetimeMs = 60_000;
    private static readonly byte[] Key1 = Enumerable.Repeat((byte)0x11, 32).ToArray();
    private static readonly byte[] Key2 = Enumerable.Repeat((byte)0x22, 32).ToArray();

    private readonly FakeClock _clock = new();
    private readonly TicketValidator _validator;

    public TicketValidatorTests()
    {
        var keys = new TicketKeyRing().Add(1, Key1).Add(2, Key2);
        _validator = new TicketValidator(keys, new InMemoryNonceCache(_clock), _clock, Realm, LifetimeMs);
    }

    private byte[] Issue(byte keyId = 1, byte[]? key = null, int realm = Realm, int lifetimeMs = LifetimeMs) =>
        new DevTicketIssuer(keyId, key ?? Key1, _clock, lifetimeMs).Issue(accountId: 42, realm, slot: 2);

    [Fact]
    public void Valid_ticket_is_accepted_with_its_fields()
    {
        var result = _validator.Validate(Issue());

        Assert.True(result.Ok);
        Assert.Equal(42, result.Ticket!.AccountId);
        Assert.Equal(2, result.Ticket.Slot);
        Assert.Equal(Realm, result.Ticket.RealmId);
    }

    [Fact]
    public void Any_key_in_the_ring_is_accepted_so_keys_can_rotate()
    {
        Assert.True(_validator.Validate(Issue(keyId: 2, key: Key2)).Ok);
    }

    [Theory]
    [InlineData(1)]   // account id
    [InlineData(13)]  // slot
    [InlineData(30)]  // nonce
    [InlineData(60)]  // signature
    public void Tampered_ticket_is_refused(int byteIndex)
    {
        var ticket = Issue();
        ticket[byteIndex] ^= 0x01;

        Assert.Equal(ConnectRejectReason.InvalidTicket, _validator.Validate(ticket).Reason);
    }

    [Fact]
    public void Ticket_signed_with_an_unknown_key_is_refused()
    {
        var foreignKey = Enumerable.Repeat((byte)0x33, 32).ToArray();

        Assert.Equal(ConnectRejectReason.InvalidTicket, _validator.Validate(Issue(key: foreignKey)).Reason);
        Assert.Equal(ConnectRejectReason.InvalidTicket, _validator.Validate(Issue(keyId: 9, key: foreignKey)).Reason);
    }

    [Fact]
    public void Ticket_for_another_realm_is_refused()
    {
        Assert.Equal(ConnectRejectReason.InvalidTicket, _validator.Validate(Issue(realm: 2)).Reason);
    }

    [Fact]
    public void Expired_ticket_is_refused_as_expired()
    {
        var ticket = Issue();
        _clock.Advance(LifetimeMs);

        Assert.Equal(ConnectRejectReason.TicketExpired, _validator.Validate(ticket).Reason);
    }

    [Fact]
    public void Ticket_claiming_a_longer_life_than_allowed_is_refused()
    {
        Assert.Equal(ConnectRejectReason.InvalidTicket, _validator.Validate(Issue(lifetimeMs: LifetimeMs + 1)).Reason);
    }

    [Fact]
    public void Ticket_works_only_once()
    {
        var ticket = Issue();

        Assert.True(_validator.Validate(ticket).Ok);
        Assert.Equal(ConnectRejectReason.InvalidTicket, _validator.Validate(ticket).Reason);
    }

    [Fact]
    public void Refused_ticket_does_not_burn_its_nonce()
    {
        var ticket = Issue();
        var keys = new TicketKeyRing().Add(1, Key1);
        var sharedCache = new InMemoryNonceCache(_clock);
        var otherRealm = new TicketValidator(keys, sharedCache, _clock, realmId: 2, LifetimeMs);
        var thisRealm = new TicketValidator(keys, sharedCache, _clock, Realm, LifetimeMs);

        Assert.False(otherRealm.Validate(ticket).Ok);
        Assert.True(thisRealm.Validate(ticket).Ok);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SessionTicketCodec.Size - 1)]
    [InlineData(SessionTicketCodec.Size + 1)]
    public void Ticket_of_the_wrong_size_is_refused(int size)
    {
        Assert.Equal(ConnectRejectReason.InvalidTicket, _validator.Validate(new byte[size]).Reason);
    }

    [Fact]
    public void Short_keys_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new TicketKeyRing().Add(1, new byte[16]));
        Assert.Throws<ArgumentException>(() => new DevTicketIssuer(1, new byte[16], _clock, LifetimeMs));
    }
}
