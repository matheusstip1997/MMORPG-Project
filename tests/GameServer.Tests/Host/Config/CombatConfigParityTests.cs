using GameServer.Host.Config;
using Shared.Data;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Host.Config;

/// <summary>
/// The client estimates damage (spec 3.4) from the tables it reads with <see cref="ConfigTableReader"/>; the server
/// reads them with <see cref="ConfigLoader"/>. The <see cref="CombatConfig"/> built from either must be the same, so a
/// tooltip and the authoritative hit use the same numbers (backlog D5). Row-by-row parity of the tables themselves is
/// covered by <see cref="ConfigReaderParityTests"/>.
/// </summary>
public class CombatConfigParityTests
{
    private static string TablesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "data", "tables");
    }

    [Fact]
    public void Server_and_client_readers_build_the_same_combat_config()
    {
        var directory = TablesDirectory();
        var serverLoad = ConfigLoader.LoadAndValidate(directory, ConfigTables.All);
        Assert.True(serverLoad.Ok, string.Join("\n", serverLoad.Errors));
        var clientErrors = new List<ValidationError>();
        var clientSet = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(directory, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, clientErrors);
        Assert.Empty(clientErrors);

        var errors = new List<ValidationError>();
        var server = CombatConfig.TryBuild(serverLoad.Set, errors);
        var client = CombatConfig.TryBuild(clientSet, errors);
        Assert.Empty(errors);
        Assert.NotNull(server);
        Assert.NotNull(client);

        Assert.Equal(server.Stats.Count, client.Stats.Count);
        for (int stat = 0; stat < server.Stats.Count; stat++)
        {
            Assert.Equal(server.Stats.IdOf(stat), client.Stats.IdOf(stat));
            Assert.Equal(server.Stats.Min(stat), client.Stats.Min(stat));
            Assert.Equal(server.Stats.Max(stat), client.Stats.Max(stat));
        }
        for (int a = 0; a < Ranks.Count; a++)
        {
            Assert.Equal(server.MitigationConstant((Rank)a), client.MitigationConstant((Rank)a));
            for (int d = 0; d < Ranks.Count; d++)
                Assert.Equal(server.RankFactorPercent((Rank)a, (Rank)d), client.RankFactorPercent((Rank)a, (Rank)d));
        }
        Assert.Equal(server.ElementCount, client.ElementCount);
        for (int e = 0; e < server.ElementCount; e++)
        {
            Assert.Equal(server.ElementId(e), client.ElementId(e));
            Assert.Equal(server.ElementResistStat(e), client.ElementResistStat(e));
            Assert.Equal(server.ElementResistMinPercent(e), client.ElementResistMinPercent(e));
            Assert.Equal(server.ElementResistMaxPercent(e), client.ElementResistMaxPercent(e));
        }
        Assert.Equal(
            (server.BaseCritChancePercent, server.BaseCritDamagePercent, server.CritChanceCapPercent, server.CritDamageBonusCapPercent,
             server.HpRegenDelayMs, server.MpRegenDelayMs, server.StaminaRegenDelayMs),
            (client.BaseCritChancePercent, client.BaseCritDamagePercent, client.CritChanceCapPercent, client.CritDamageBonusCapPercent,
             client.HpRegenDelayMs, client.MpRegenDelayMs, client.StaminaRegenDelayMs));

        // Same hit, same seed-free roll: same damage on both sides.
        var input = new DamageInput
        {
            Power = 250, SkillMultiplierPercent = 130, SkillFlat = 12, Defense = 180, PenetrationPercent = 15, PenetrationFlat = 5,
            AttackerRank = Rank.D, DefenderRank = Rank.C, Element = 0, ResistPercent = 30, CritRatePercent = 10,
            CritDamageBonusPercent = 40, ExtraPercent = 10, ParryReductionPercent = 50,
        };
        for (int roll = 0; roll < 100; roll++)
            Assert.Equal(DamageCalc.ComputeWithRoll(input, server, roll).Damage, DamageCalc.ComputeWithRoll(input, client, roll).Damage);
    }
}
