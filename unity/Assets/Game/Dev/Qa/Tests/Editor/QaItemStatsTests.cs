#nullable enable
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>
    /// The split-stack step's expected tooltip stats (wave 1 QA F4): a crafted weapon lists its stats snapshot, any other
    /// weapon its <c>ItemStatBonusDef</c> rows, as the inventory tooltip does (spec 9.13, 4.1 M3 item model).
    /// </summary>
    public sealed class QaItemStatsTests
    {
        private static readonly StatDef[] Stats = { new StatDef { Id = "hp" }, new StatDef { Id = "atk" }, new StatDef { Id = "def" } };

        private static readonly ItemStatBonusDef[] Bonuses =
        {
            new ItemStatBonusDef { ItemId = "m2.trainingSword", StatId = "atk" },
            new ItemStatBonusDef { ItemId = "m2.trainingSword", StatId = "str" },
            new ItemStatBonusDef { ItemId = "m2.otherItem", StatId = "def" },
        };

        [Test]
        public void A_plain_weapon_expects_its_item_stat_rows()
        {
            var saved = new InventorySlotEntry { Slot = 0, ItemNetId = 1, Count = 1, InstanceId = 7 };

            Assert.That(QaItemStats.TooltipStatIds("m2.trainingSword", saved, Stats, Bonuses), Is.EqualTo(new[] { "atk", "str" }));
            Assert.That(QaItemStats.TooltipStatIds("m2.trainingSword", null, Stats, Bonuses), Is.EqualTo(new[] { "atk", "str" }));
        }

        [Test]
        public void A_crafted_weapon_expects_its_stats_snapshot_and_no_item_rows()
        {
            var saved = new InventorySlotEntry
            {
                Slot = 0, ItemNetId = 2, Count = 1, InstanceId = 33554437, CrafterId = 2,
                CraftedStats = new[]
                {
                    new CraftedStatEntry { Stat = 1, Flat = 12 }, new CraftedStatEntry { Stat = 2, Flat = 6 },
                    new CraftedStatEntry { Stat = 0 }, // no value: the tooltip shows no line
                    new CraftedStatEntry { Stat = 9, Flat = 1 }, // an unknown stat index: left out, as the tooltip does
                },
            };

            Assert.That(QaItemStats.TooltipStatIds("m3.forgedSword", saved, Stats, Bonuses), Is.EqualTo(new[] { "atk", "def" }));
        }

        [Test]
        public void Swapping_a_crafted_sword_for_one_with_the_same_stats_and_enchant_leaves_the_stats()
        {
            // The wave 1 fix's second smoke run: equip-crafted put a new forged sword over the first run's, both Atk +12 / Def +6.
            InventorySlotEntry Sword(long id, int atk, byte enchant = 0) => new InventorySlotEntry
            {
                Slot = 0, ItemNetId = 2, Count = 1, InstanceId = id, CrafterId = 2, Enchant = enchant,
                CraftedStats = new[] { new CraftedStatEntry { Stat = 1, Flat = atk }, new CraftedStatEntry { Stat = 2, Flat = 6 } },
            };

            Assert.That(QaItemStats.SameCraftedBonuses(Sword(1, 12), Sword(2, 12)), Is.True);
            Assert.That(QaItemStats.SameCraftedBonuses(Sword(1, 13), Sword(2, 12)), Is.False);
            Assert.That(QaItemStats.SameCraftedBonuses(Sword(1, 12, enchant: 1), Sword(2, 12)), Is.False); // the enchant-sword step's +1
            Assert.That(QaItemStats.SameCraftedBonuses(new InventorySlotEntry { Slot = 0, ItemNetId = 1, Count = 1, InstanceId = 3 }, Sword(2, 12)),
                Is.False); // the starter weapon
            Assert.That(QaItemStats.SameCraftedBonuses(null, Sword(2, 12)), Is.False);
        }
    }
}
