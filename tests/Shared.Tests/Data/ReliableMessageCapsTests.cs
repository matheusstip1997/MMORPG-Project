using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Tests.Data;

/// <summary>R-2.5-07: one cap row per reliable message a client sends, within the protocol bound and NetSecurity.</summary>
public class ReliableMessageCapsTests
{
    private static RowErrors Validate(ReliableMessageCaps row, ConfigSet? set = null)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        if (set != null)
            row.Validate(set, errors);
        return errors;
    }

    private static ConfigSet Set(int maxReliableBytes = 16_384, int chatMaxChars = 200, params ReliableMessageCaps[] rows)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(NetSecurity), 2, new object[] { new NetSecurity { MaxReliableMessageBytes = maxReliableBytes } }));
        set.Add(new ConfigTable(typeof(ChatSettings), 1, new object[] { new ChatSettings { MaxChars = chatMaxChars } }));
        set.Add(new ConfigTable(typeof(ReliableMessageCaps), 1, rows));
        return set;
    }

    private static ReliableMessageCaps[] AllClientRows() => ReliableMessageReader.All
        .Where(spec => spec.Direction == ReliableDirection.ClientToServer)
        .Select(spec => new ReliableMessageCaps { Id = ConfigTableReader.CamelCase(spec.Id.ToString()), MaxBytes = spec.MaxBytes })
        .ToArray();

    [Fact]
    public void A_client_message_within_its_bound_is_valid()
    {
        Assert.Empty(Validate(new ReliableMessageCaps { Id = "pickupRequest", MaxBytes = 6 }, Set()).Items);
    }

    [Theory]
    [InlineData("chatSnd")]
    [InlineData("ChatSend")]
    [InlineData("none")]
    [InlineData("")]
    public void An_id_that_is_not_a_reliable_message_is_a_broken_rule(string id)
    {
        Assert.Equal(nameof(ReliableMessageCaps.Id), Assert.Single(Validate(new ReliableMessageCaps { Id = id, MaxBytes = 6 }).Items).Field);
    }

    [Fact]
    public void The_bag_move_request_needs_its_own_cap_row()
    {
        var withoutMove = AllClientRows().Where(row => row.Id != "inventoryMoveRequest").ToArray();

        var errors = ReliableMessageCaps.CheckCoverage(Set(rows: withoutMove));

        Assert.Contains(errors, e => e.Message.Contains("'inventoryMoveRequest'"));
        Assert.Throws<InvalidOperationException>(() => ReliableMessageCaps.ById(Set(rows: withoutMove)));
        Assert.Empty(ReliableMessageCaps.CheckCoverage(Set(rows: AllClientRows())));
        Assert.Equal(8, ReliableMessageCaps.ById(Set(rows: AllClientRows()))[(byte)ReliableMessageId.InventoryMoveRequest]);
    }

    [Fact]
    public void A_message_the_server_sends_has_no_cap()
    {
        Assert.Equal(nameof(ReliableMessageCaps.Id),
            Assert.Single(Validate(new ReliableMessageCaps { Id = "inventoryContents", MaxBytes = 100 }).Items).Field);
    }

    [Fact]
    public void A_cap_above_the_protocol_bound_is_a_broken_rule()
    {
        Assert.Equal(nameof(ReliableMessageCaps.MaxBytes),
            Assert.Single(Validate(new ReliableMessageCaps { Id = "pickupRequest", MaxBytes = 7 }).Items).Field);
    }

    [Fact]
    public void A_cap_above_net_security_is_a_broken_rule()
    {
        var row = new ReliableMessageCaps { Id = "chatSend", MaxBytes = 810 };

        Assert.Equal(nameof(ReliableMessageCaps.MaxBytes), Assert.Single(Validate(row, Set(maxReliableBytes: 809)).Items).Field);
    }

    [Fact]
    public void A_chat_cap_below_the_longest_allowed_line_is_a_broken_rule()
    {
        var row = new ReliableMessageCaps { Id = "chatSend", MaxBytes = ReliableMessageCaps.ChatSendBytes(200) };

        Assert.Empty(Validate(row, Set(chatMaxChars: 200)).Items);
        Assert.Equal(nameof(ReliableMessageCaps.MaxBytes), Assert.Single(Validate(row, Set(chatMaxChars: 201)).Items).Field);
    }

    [Fact]
    public void Coverage_names_every_client_message_without_a_row()
    {
        var rows = AllClientRows();
        Assert.Empty(ReliableMessageCaps.CheckCoverage(Set(rows: rows)));

        var errors = ReliableMessageCaps.CheckCoverage(Set(rows: rows.Where(r => r.Id != "chatSend").ToArray()));

        Assert.Contains("'chatSend'", Assert.Single(errors).Message);
        Assert.Throws<InvalidOperationException>(() => ReliableMessageCaps.ById(Set(rows: Array.Empty<ReliableMessageCaps>())));
    }

    [Fact]
    public void The_router_caps_are_the_rows_and_zero_for_every_other_id()
    {
        var rows = AllClientRows();
        rows.Single(r => r.Id == "chatSend").MaxBytes = 500;

        var caps = ReliableMessageCaps.ById(Set(rows: rows));
        var bounds = ReliableMessageCaps.ProtocolBounds();

        Assert.Equal(256, caps.Length);
        for (int id = 0; id < 256; id++)
        {
            bool client = ReliableMessageReader.TryGetSpec((ReliableMessageId)id, out var spec) && spec.Direction == ReliableDirection.ClientToServer;
            Assert.Equal(client ? spec.MaxBytes : 0, bounds[id]);
            Assert.Equal(!client ? 0 : id == (int)ReliableMessageId.ChatSend ? 500 : spec.MaxBytes, caps[id]);
        }
    }
}
