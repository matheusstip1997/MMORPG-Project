#nullable enable
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// WP-M2-2 on the client: the own-stats message (protocol 19) reads in Unity's build of Game.Shared, values are found by
    /// StatDef id, a message out of step with the tables is dropped, and TOX and SAT decay between messages (R-3.4-03).
    /// </summary>
    public class ClientStatsTests
    {
        private static ConfigSet Config()
        {
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(StatDef), 1, new object[]
            {
                new StatDef { Id = "atk", Min = 0, Max = 1000 },
                new StatDef { Id = "str", Min = 0, Max = 1000 },
                new StatDef { Id = "def", Min = 0, Max = 1000 },
            }));
            set.Add(new ConfigTable(typeof(ConsumptionMeterSettings), 1, new object[]
            {
                new ConsumptionMeterSettings { ToxicityMax = 100, ToxicityDecayPerMinute = 10, SatietyMax = 80, SatietyDecayPerMinute = 5 },
            }));
            return set;
        }

        private static byte[] Message(int[] values, int toxicityMilli = 0, int satietyMilli = 0) =>
            StatsCodec.Write(new ReliableMessageWriter(), new OwnStats
            {
                Version = 3, Values = values, ToxicityMilli = toxicityMilli, SatietyMilli = satietyMilli,
            }).ToArray();

        [Test]
        public void The_own_stats_are_read_by_StatDef_id()
        {
            var stats = new ClientStats(Config());
            Assert.IsFalse(stats.HasValues);
            Assert.AreEqual(0, stats.Get("atk"));

            Assert.IsTrue(stats.Apply(Message(new[] { 20, 10, 15 }), nowMs: 0));

            Assert.IsTrue(stats.HasValues);
            Assert.AreEqual(3u, stats.Version);
            Assert.AreEqual(20, stats.Get("atk"));
            Assert.AreEqual(10, stats.Get("str"));
            Assert.AreEqual(15, stats.Get("def"));
            Assert.AreEqual(0, stats.Get("luck"));
            Assert.AreEqual(100, stats.ToxicityMax);
            Assert.AreEqual(80, stats.SatietyMax);
        }

        [Test]
        public void A_message_out_of_step_with_the_tables_is_dropped_and_other_messages_are_not_taken()
        {
            var stats = new ClientStats(Config());
            Assert.IsTrue(stats.Apply(Message(new[] { 1, 2 }), 0)); // taken, but two stats for a table of three
            Assert.AreEqual(1, stats.Dropped);
            Assert.IsFalse(stats.HasValues);

            var other = ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = 1 });
            Assert.IsFalse(stats.Apply(other, 0));
        }

        [Test]
        public void Toxicity_and_satiety_decay_on_the_display_clock_from_the_message()
        {
            var stats = new ClientStats(Config());
            stats.Apply(Message(new[] { 1, 2, 3 }, toxicityMilli: 50_000, satietyMilli: 20_000), nowMs: 1_000);

            Assert.AreEqual(50, stats.Toxicity(1_000));
            Assert.AreEqual(40, stats.Toxicity(61_000));
            Assert.AreEqual(15, stats.Satiety(61_000));
            Assert.AreEqual(0, stats.Satiety(1_000_000));

            stats.Reset();
            Assert.IsFalse(stats.HasValues);
            Assert.AreEqual(0, stats.Toxicity(61_000));
        }
    }
}
