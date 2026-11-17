using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Contracts;
using Shared.Simulation.Crafting;

namespace Shared.Tests.Simulation.Contracts;

/// <summary>
/// Enchant contracts (spec 5.4, 5.3 R-5.3-04; WP-M4-10 part C, user answers 2026-11-16): the shared terms check
/// (<see cref="ContractRules.CheckEnchantTerms"/>: the customer's equipment instance in the Safe bag below +9 and the tier's stones,
/// either side's) with the committed tables, and the protocol 55 readers (an enchant is a contract kind, never a forge session; an
/// enchant request names an instance or a contract).
/// </summary>
public class ContractEnchantTermsTests
{
    private static readonly ConfigSet Tables = Shared.Tests.Simulation.Crafting.ForgeMathTests.LoadRepository();
    private static readonly EnchantRules Rules = EnchantRules.From(Tables)!;
    private static readonly ItemDef Sword = Rules.Item("m3.forgedSword")!;
    private static readonly ItemDef Stone = Rules.Item("m3.enchantStone")!;

    private static ContractSlot ItemSlot() => new()
    {
        Supplier = ContractSupplier.Customer, Container = Rules.SafeCode, Slot = 3, ItemNetId = (ushort)Sword.NetId, Count = 1,
        InstanceId = 42,
    };

    private static ContractSlot StoneSlot(ContractSupplier supplier) => new()
    {
        Supplier = supplier, Container = Rules.NormalCode, Slot = 0, ItemNetId = (ushort)Stone.NetId, Count = 1,
    };

    [Theory]
    [InlineData(ContractSupplier.Customer)]
    [InlineData(ContractSupplier.Crafter)]
    public void The_item_and_the_tier_s_stones_of_either_supplier_are_good_terms(ContractSupplier stones)
    {
        Assert.True(Rules.TryMaterial(Sword, out var stone, out int count));
        Assert.Equal((Stone.NetId, 1), (stone.NetId, count));
        Assert.Equal(ContractReason.None, ContractRules.CheckEnchantTerms(Rules, Sword, 0, [ItemSlot(), StoneSlot(stones)]));
        Assert.Equal(ContractReason.None, ContractRules.CheckEnchantTerms(Rules, Sword, EnchantLimits.MaxLevel - 1, [ItemSlot(), StoneSlot(stones)]));
    }

    public static IEnumerable<object[]> Refusals()
    {
        ContractSlot With(Action<ContractSlot> change)
        {
            var slot = ItemSlot();
            change(slot);
            return slot;
        }
        ContractSlot Stones(Action<ContractSlot> change)
        {
            var slot = StoneSlot(ContractSupplier.Customer);
            change(slot);
            return slot;
        }
        yield return ["one slot", 0, new[] { ItemSlot() }];
        yield return ["three slots", 0, new[] { ItemSlot(), StoneSlot(ContractSupplier.Customer), StoneSlot(ContractSupplier.Crafter) }];
        yield return ["the crafter's item", 0, new[] { With(s => s.Supplier = ContractSupplier.Crafter), StoneSlot(ContractSupplier.Customer) }];
        yield return ["two units", 0, new[] { With(s => s.Count = 2), StoneSlot(ContractSupplier.Customer) }];
        yield return ["no instance", 0, new[] { With(s => s.InstanceId = 0), StoneSlot(ContractSupplier.Customer) }];
        yield return ["equipped", 0, new[] { With(s => s.Container = Rules.EquippedCode), StoneSlot(ContractSupplier.Customer) }];
        yield return ["the Normal bag", 0, new[] { With(s => s.Container = Rules.NormalCode), StoneSlot(ContractSupplier.Customer) }];
        yield return ["another item", 0, new[] { With(s => s.ItemNetId = (ushort)Stone.NetId), StoneSlot(ContractSupplier.Customer) }];
        yield return ["at +9", EnchantLimits.MaxLevel, new[] { ItemSlot(), StoneSlot(ContractSupplier.Customer) }];
        yield return ["a negative level", -1, new[] { ItemSlot(), StoneSlot(ContractSupplier.Customer) }];
        yield return ["another stone", 0, new[] { ItemSlot(), Stones(s => s.ItemNetId = (ushort)Sword.NetId) }];
        yield return ["too few stones", 0, new[] { ItemSlot(), Stones(s => s.Count = 0) }];
        yield return ["too many stones", 0, new[] { ItemSlot(), Stones(s => s.Count = 2) }];
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Every_malformed_term_is_refused(string because, int level, ContractSlot[] slots)
    {
        Assert.True(ContractReason.ContractTermsInvalid == ContractRules.CheckEnchantTerms(Rules, Sword, level, slots), because);
    }

    [Fact]
    public void An_item_that_is_not_enchantable_equipment_is_refused()
    {
        var ore = Rules.Item("m1.demonAsh") ?? Stone; // a material: not equipment
        var slots = new[] { ItemSlot(), StoneSlot(ContractSupplier.Customer) };
        slots[0].ItemNetId = (ushort)ore.NetId;
        Assert.Equal(ContractReason.ContractTermsInvalid, ContractRules.CheckEnchantTerms(Rules, ore, 0, slots));
        Assert.Equal(ContractReason.ContractTermsInvalid, ContractRules.CheckEnchantTerms(Rules, null, 0, slots));
        // Equipment without a job (no ItemDef.jobId and no mold makes it) has no station to enchant at.
        var jobless = NetIds.Rows<ItemDef>(Tables).FirstOrDefault(i => i.Category == ItemCategory.Equipment && Rules.JobOf(i).Length == 0);
        if (jobless != null)
        {
            slots[0].ItemNetId = (ushort)jobless.NetId;
            Assert.Equal(ContractReason.ContractTermsInvalid, ContractRules.CheckEnchantTerms(Rules, jobless, 0, slots));
        }
    }

    // ---- The protocol 55 readers ----

    [Fact]
    public void An_enchant_is_a_contract_kind_and_never_a_forge_session()
    {
        var start = ReliableMessages.Write(ReliableMessageId.ForgeStart, new ForgeStart
        {
            RequestId = 1, StationEntityId = 2, Kind = ForgeKind.Enchant, DefNetId = (ushort)Sword.NetId, Inputs = [], ContractId = 7,
        });
        Assert.False(ReliableMessages.TryRead(start, out ForgeStart _));
        var recipe = ReliableMessages.Write(ReliableMessageId.ForgeStart, new ForgeStart
        {
            RequestId = 1, StationEntityId = 2, Kind = ForgeKind.Recipe, DefNetId = 1, Inputs = [],
        });
        Assert.True(ReliableMessages.TryRead(recipe, out ForgeStart _));

        var propose = ReliableMessages.Write(ReliableMessageId.ContractRequest, new ContractRequest
        {
            RequestId = 3, Action = ContractAction.Propose, Kind = ForgeKind.Enchant, DefNetId = (ushort)Sword.NetId, Gold = 100,
            DeadlineHours = 1, Slots = [ItemSlot(), StoneSlot(ContractSupplier.Crafter)],
        });
        Assert.True(ReliableMessages.TryRead(propose, out ContractRequest read));
        Assert.Equal((ForgeKind.Enchant, 42L, ContractSupplier.Crafter), (read.Kind, read.Slots[0].InstanceId, read.Slots[1].Supplier));

        var entry = new ContractEntry
        {
            ContractId = 5, Status = ContractStatus.Accepted, Role = ContractRole.Crafter, Kind = ForgeKind.Enchant,
            DefNetId = (ushort)Sword.NetId, Gold = 100, DeadlineHours = 1, Slots = [ItemSlot(), StoneSlot(ContractSupplier.Customer)],
        };
        entry.Slots[0].EnchantLevel = 4;
        var list = ReliableMessages.Write(ReliableMessageId.ContractList, new ContractList { RequestId = 1, Badge = 1, Rows = [entry] });
        Assert.True(ReliableMessages.TryRead(list, out ContractList readList));
        var row = Assert.Single(readList.Rows);
        Assert.Equal((ForgeKind.Enchant, 42L, (byte)4), (row.Kind, row.Slots[0].InstanceId, row.Slots[0].EnchantLevel));
        var board = ReliableMessages.Write(ReliableMessageId.ContractBoard, new ContractBoard { RequestId = 1, Rows = [entry] });
        Assert.True(ReliableMessages.TryRead(board, out ContractBoard _));
    }

    [Theory]
    [InlineData(5L, 0L, true)] // an own item
    [InlineData(0L, 9L, true)] // a contract's item (WP-M4-10 part C)
    [InlineData(5L, 9L, true)]
    [InlineData(0L, 0L, false)] // neither
    [InlineData(5L, -1L, false)]
    public void An_enchant_request_names_an_instance_or_a_contract(long instanceId, long contractId, bool reads)
    {
        var message = ReliableMessages.Write(ReliableMessageId.EnchantRequest, new EnchantRequest
        {
            RequestId = 1, StationEntityId = 2, Container = 2, Slot = 0, InstanceId = instanceId, ContractId = contractId,
        });
        Assert.Equal(reads, ReliableMessages.TryRead(message, out EnchantRequest read));
        if (reads)
            Assert.Equal(contractId, read.ContractId);
    }

    [Fact]
    public void The_contract_enchant_reasons_read()
    {
        foreach (var reason in new[] { EnchantReason.ContractNotFound, EnchantReason.ContractStateInvalid, EnchantReason.ContractExpired,
                     EnchantReason.GoldCapReached })
        {
            var message = ReliableMessages.Write(ReliableMessageId.EnchantResult, new EnchantResult { RequestId = 1, Reason = reason });
            Assert.True(ReliableMessages.TryRead(message, out EnchantResult read));
            Assert.Equal(reason, read.Reason);
        }
        var beyond = ReliableMessages.Write(ReliableMessageId.EnchantResult,
            new EnchantResult { RequestId = 1, Reason = ReliableMessages.LastEnchantReason + 1 });
        Assert.False(ReliableMessages.TryRead(beyond, out EnchantResult _));
    }
}
