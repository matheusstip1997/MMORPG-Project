using System.Text.Json;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Skills;

namespace DataValidator;

/// <summary>
/// Prints one skill-tree node as the game resolves it (spec 3.5 Implementation: "a validation tool prints the resolved
/// skill"; R-3.5-05; WP-M2-5): the generated <see cref="SkillTree"/> row, the <see cref="SkillDef"/> chain it inherits from,
/// and the resolved action, level percent and modifiers, as indented JSON.
/// </summary>
public static class SkillPrinter
{
    public static string? Print(ConfigSet set, string nodeId)
    {
        var node = SkillTree.Find(set, nodeId);
        if (node == null)
            return null;
        var rows = SkillDefResolver.RowsOf(set);
        object skill;
        if (!rows.Skills.ContainsKey(nodeId))
            skill = new { learnable = false, reason = "no SkillDef row: the node cannot be learned yet" };
        else if (!SkillDefResolver.TryResolve(rows.Skills, rows.Modifiers, nodeId, out var resolved, out string error))
            skill = new { learnable = false, reason = error };
        else
            skill = new
            {
                learnable = true,
                inherits = resolved.Chain,
                actionId = resolved.ActionId,
                damagePercentPerLevel = resolved.DamagePercentPerLevel,
                levelPercents = Enumerable.Range(1, node.MaxLevel).Select(resolved.LevelPercent),
                armorClass = resolved.ArmorClass == 0 ? null : ConfigTableReader.CamelCase(resolved.ArmorClass.ToString()),
                modifiers = resolved.Modifiers.Select(m => new
                {
                    m.Id, m.Stat, m.FlatPerLevel, m.PercentPerLevel, trigger = ConfigTableReader.CamelCase(m.Trigger.ToString()),
                    weapons = m.Weapons.Select(w => ConfigTableReader.CamelCase(w.ToString())),
                }),
            };
        var printed = new
        {
            node = new
            {
                node.Id, node.Family, node.ClassId, node.MinCharLevel, kind = ConfigTableReader.CamelCase(node.Kind.ToString()),
                node.MaxLevel, node.PointCost, node.EvolvesFrom,
                requires = node.Requires.Select((r, i) => new { node = r, level = i < node.RequiresLevels.Length ? node.RequiresLevels[i] : 0 }),
                weapons = node.Weapons.Select(w => ConfigTableReader.CamelCase(w.ToString())),
                elements = node.Elements.Select(e => ConfigTableReader.CamelCase(e.ToString())),
            },
            skill,
        };
        return JsonSerializer.Serialize(printed, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }
}
