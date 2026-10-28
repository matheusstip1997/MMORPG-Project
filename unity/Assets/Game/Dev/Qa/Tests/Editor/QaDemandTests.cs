#nullable enable
using System.IO;
using NUnit.Framework;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>The wait-respawn step's demand spawn percent (R-4.2-03): start state, then the run's telemetry up to the death.</summary>
    public sealed class QaDemandTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "qa-demand-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        private const string Updated86 =
            "{\"k\":\"DemandUpdated\",\"t\":1793207373328,\"e\":55,\"w\":1,\"r\":6,\"monsterDef\":1,\"decayedMilli\":2802,\"ratioPermille\":311,\"spawnDelayPercent\":86,\"rewardPercent\":120}";
        private const string Died90 = "{\"k\":\"Died\",\"t\":1793207361128,\"e\":47,\"w\":1,\"entity\":90,\"killer\":95}";

        private int Percent(params string[] lines) =>
            QaDemand.SpawnPercent(_dir, lines, "m1.demon", 1, "m1.combat", 6, 90, out _);

        [Test]
        public void Without_demand_state_the_base_delay_applies()
        {
            Assert.That(Percent(), Is.EqualTo(100));
        }

        [Test]
        public void The_state_the_server_loaded_applies_until_a_step_before_the_death()
        {
            File.WriteAllText(Path.Combine(_dir, QaDemand.StartFile),
                "{\"rows\":[{\"defId\":\"m1.demon\",\"regionId\":\"m1.combat\",\"spawnPercent\":90},{\"defId\":\"m1.alien\",\"regionId\":\"m1.combat\",\"spawnPercent\":70}]}");
            // QA wave 1: the second run's demon respawned after 27 s, 90 % of monsterRespawnMs, loaded from the database.
            Assert.That(Percent(Died90, Updated86), Is.EqualTo(90));
            Assert.That(Percent(Updated86, Died90), Is.EqualTo(86));
            Assert.That(Percent(Updated86.Replace("\"r\":6", "\"r\":7"), Died90), Is.EqualTo(90), "another region");
        }

        [Test]
        public void Integer_fields_are_read_from_a_flat_json_line()
        {
            Assert.That(QaDemand.Field(Updated86, "r"), Is.EqualTo(6));
            Assert.That(QaDemand.Field(Updated86, "spawnDelayPercent"), Is.EqualTo(86));
            Assert.That(QaDemand.Field(Died90, "entity"), Is.EqualTo(90));
            Assert.That(QaDemand.Field(Died90, "monsterDef"), Is.EqualTo(-1));
        }
    }
}
