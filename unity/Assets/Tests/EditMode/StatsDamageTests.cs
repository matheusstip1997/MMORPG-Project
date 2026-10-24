using System;
using System.Collections.Generic;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;
using Shared.Util;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// Runs the shared StatBlock, DamageCalc and ResourceMeters inside Unity (Mono) and checks them against the golden
    /// result that tests/Shared.Tests/Simulation/Stats/StatsGoldenScenarioTests.cs asserts on .NET: tooltip estimates on
    /// the client and hits on the server must match exactly (architecture rules 6 and 7, spec 3.4, backlog D5).
    /// </summary>
    public class StatsDamageTests
    {
        // Keep in sync with StatsGoldenScenarioTests in the .NET tests.
        private const ulong Seed = 20261018;
        private const ulong Stream = 5;
        private const int Hits = 5000;
        private const ulong ExpectedChecksum = 13662351330715622811UL;

        [Test]
        public void Scripted_run_matches_the_dotnet_golden_result()
        {
            Assert.AreEqual(ExpectedChecksum, Run());
        }

        [Test]
        public void Hand_computed_hit_matches()
        {
            var config = BuildConfig();
            var input = new DamageInput
            {
                Power = 100, SkillMultiplierPercent = 150, Defense = 100, AttackerRank = Rank.F, DefenderRank = Rank.F,
                Element = DamageCalc.NoElement, NeutralRankFactor = true,
            };

            Assert.AreEqual(75, DamageCalc.ComputeWithRoll(input, config, 99).Damage);   // 150 x 100 / 200
            Assert.AreEqual(113, DamageCalc.ComputeWithRoll(input, config, 0).Damage);   // x1.5, 112.5 rounds up
        }

        private static ulong Run()
        {
            var config = BuildConfig();
            var ids = config.Ids;
            var rng = new Pcg32Rng(Seed, Stream);
            var attacker = new StatBlock(config.Stats);
            var defender = new StatBlock(config.Stats);
            defender.SetBase(ids.MaxHp, 1_000_000);
            defender.SetBase(ids.HpRegen, 37);
            var meters = new ResourceMeters(defender, config, 0);
            int fireRes = config.Stats.IdFor("fireRes");
            ulong checksum = 14695981039346656037UL;
            long now = 0;

            for (int hit = 0; hit < Hits; hit++)
            {
                now += rng.NextInt(1, 400);
                attacker.SetBase(ids.Atk, rng.NextInt(0, 100_000));
                attacker.SetBase(ids.CritRate, rng.NextInt(0, 100));
                attacker.SetBase(ids.CritDamage, rng.NextInt(0, 200));
                attacker.SetBase(ids.PhysPenPercent, rng.NextInt(0, 120));
                attacker.SetBase(ids.PhysPenFlat, rng.NextInt(0, 1_000));
                attacker.Add(new StatModifier(ids.Atk, rng.NextInt(-50, 50), rng.NextInt(-30, 60), StatSourceType.Buff, hit,
                    now + rng.NextInt(1, 2_000)));
                attacker.ExpireModifiers(now);
                defender.SetBase(ids.Def, rng.NextInt(0, 100_000));
                defender.SetBase(fireRes, rng.NextInt(-100, 100));

                int element = rng.NextInt(-1, 1);
                var input = DamageCalc.Gather(config, attacker, (Rank)rng.NextInt(8), defender, (Rank)rng.NextInt(8),
                    DamageKind.Physical, rng.NextInt(0, 500), rng.NextInt(-100, 100), element);
                input.ExtraPercent = rng.NextInt(-100, 200);
                input.ParryReductionPercent = rng.NextInt(0, 100);
                var result = DamageCalc.Compute(input, config, rng);

                int drained = meters.Drain(ResourceKind.Hp, result.Damage / 100, now);
                checksum = Mix(checksum, (uint)result.Damage);
                checksum = Mix(checksum, (uint)result.CritRoll);
                checksum = Mix(checksum, (uint)attacker.Get(ids.Atk));
                checksum = Mix(checksum, (uint)drained);
                checksum = Mix(checksum, (uint)meters.Current(ResourceKind.Hp, now + 5_500));
            }
            return checksum;
        }

        private static ulong Mix(ulong hash, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= 1099511628211UL;
            }
            return hash;
        }

        private static CombatConfig BuildConfig()
        {
            var stats = new List<object>();
            foreach (var id in StatIds.Required)
                stats.Add(new StatDef { Id = id, Min = 0, Max = 1_000_000_000 });
            // The 10 elements of R-3.4-07, fire first (index 0), each with its resistance stat.
            string[] elements = { "fire", "ice", "water", "wind", "earth", "lightning", "holy", "dark", "blood", "poison" };
            foreach (var element in elements)
                stats.Add(new StatDef { Id = element + "Res", Min = -900, Max = 100 });
            var ranks = new List<object>();
            for (int a = 0; a < Ranks.Count; a++)
            for (int d = 0; d < Ranks.Count; d++)
                ranks.Add(new RankFactor { Attacker = (Rank)a, Defender = (Rank)d, FactorPercent = 80 + 5 * (a - d + 7) });

            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(StatDef), 1, stats));
            set.Add(new ConfigTable(typeof(DamageConstants), 1, new object[]
            {
                new DamageConstants
                {
                    MitigationConstantByRank = new[] { 100, 150, 200, 300, 400, 550, 700, 900 },
                    CritChanceCapPercent = 75, CritDamageBonusCapPercent = 150, RankFactorMinPercent = 50, RankFactorMaxPercent = 200,
                },
            }));
            set.Add(new ConfigTable(typeof(RankFactor), 1, ranks));
            var caps = new List<object>();
            foreach (var element in elements)
                caps.Add(new ElementCaps { Id = element, ResistStat = element + "Res", DamageResistMinPercent = -50, DamageResistMaxPercent = 75 });
            set.Add(new ConfigTable(typeof(ElementCaps), 1, caps));
            set.Add(new ConfigTable(typeof(StartingValuesM1), 1, new object[]
            {
                new StartingValuesM1 { CritChancePercent = 5, CritDamagePercent = 150, StaminaRegenDelayMs = 1000 },
            }));
            set.Add(new ConfigTable(typeof(ResourceRegen), 1, new object[] { new ResourceRegen { HpRegenDelayMs = 5000, MpRegenDelayMs = 3000 } }));

            var errors = new List<ValidationError>();
            var config = CombatConfig.TryBuild(set, errors);
            if (config == null)
                throw new InvalidOperationException(string.Join("\n", errors));
            return config;
        }
    }
}
