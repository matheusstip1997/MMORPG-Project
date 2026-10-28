using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// DataValidator rule for the names the HUD shows from <see cref="UiStrings"/> (spec 9.1: no user-facing text in code;
    /// backlog I): every action on an <see cref="ActionBindings"/> row (<c>action.&lt;id&gt;</c>: hotbar and cast bar), every
    /// monster with a spawn point (<c>monster.&lt;id&gt;</c>: name plates, boss bar) and its rank (<c>rank.&lt;Rank&gt;</c>: boss
    /// bar), every status (<c>status.&lt;id&gt;</c>: the buff row while its icon is a placeholder), every rarity
    /// (<c>rarity.&lt;id&gt;</c>) and the category of every item (<c>itemCategory.&lt;category&gt;</c>: the inventory tooltip), every stat
    /// (<c>stat.&lt;id&gt;</c>: the character sheet and item stats, WP-M2-11). The client shows the id
    /// for a missing row, so this only keeps placeholders out of the game.
    /// </summary>
    public static class UiStringCoverage
    {
        public const string ActionPrefix = "action.";
        public const string MonsterPrefix = "monster.";
        public const string RankPrefix = "rank.";
        public const string StatusPrefix = "status.";

        public static string ActionId(string actionId) => ActionPrefix + actionId;

        public static string MonsterId(string monsterId) => MonsterPrefix + monsterId;

        public static string RankId(Simulation.Stats.Rank rank) => RankPrefix + rank;

        public static string StatusId(string statusId) => StatusPrefix + statusId;

        public const string StatPrefix = "stat.";

        /// <summary>The name of a <see cref="StatDef"/> row (the character sheet and item tooltips, WP-M2-11).</summary>
        public static string StatId(string statId) => StatPrefix + statId;

        public const string RarityPrefix = "rarity.";
        public const string ItemCategoryPrefix = "itemCategory.";

        /// <summary>The name of a <see cref="RarityDef"/> row (inventory tooltip, spec 9.13).</summary>
        public static string RarityId(string rarityId) => RarityPrefix + rarityId;

        /// <summary>The name of an item category, by its config name (camelCase, for example <c>itemCategory.sellOnly</c>).</summary>
        public static string ItemCategoryId(ItemCategory category)
        {
            string name = category.ToString();
            return ItemCategoryPrefix + char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        public const string ContainerPrefix = "inv.container.";
        public const string EquipSlotPrefix = "inv.equipSlot.";

        /// <summary>The tab name of a <see cref="ContainerDef"/> row (inventory window, spec 9.13).</summary>
        public static string ContainerId(string containerId) => ContainerPrefix + containerId;

        /// <summary>The name of an <see cref="EquipSlotDef"/> row (the equipment strip, spec 9.13).</summary>
        public static string EquipSlotId(string slotId) => EquipSlotPrefix + slotId;

        public const string ClassPrefix = "class.";
        public const string SkillNodePrefix = "skillNode.";

        /// <summary>The name of a <see cref="ClassDef"/> row (character list, the skill tree window; WP-M2-5).</summary>
        public static string ClassName(string classId) => ClassPrefix + classId;

        /// <summary>The name of a <see cref="SkillTree"/> node (the skill tree window, hotbar tooltips; WP-M2-5).</summary>
        public static string SkillNodeName(string nodeId) => SkillNodePrefix + nodeId;
        /// <summary>The Bystial book window's names (WP-M2-8): a tier, a stat, an effect kind, an element, a family; camelCase.</summary>
        public static string BookTierId(BystialTier tier) => "book.tier." + Camel(tier.ToString());

        public static string BookStatId(string statId) => "book.stat." + statId;

        public static string BookEffectId(BystialEffectKind kind) => "book.effect." + Camel(kind.ToString());

        public static string BookElementId(Element element) => "book.element." + Camel(element.ToString());

        public static string BookFamilyId(MonsterFamily family) => "book.family." + Camel(family.ToString());

        private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);

        /// <summary>One error per missing row. Empty when <see cref="UiStrings"/> is not loaded (the loader reports that).</summary>
        public static List<ValidationError> Check(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            if (!set.TryGet(typeof(UiStrings), out var strings))
                return errors;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in strings.Rows)
                ids.Add(((UiStrings)row).Id);
            var tableName = TableInfo.For(typeof(UiStrings)).Name;
            var reported = new HashSet<string>(StringComparer.Ordinal);

            void Require(string id, string usedBy)
            {
                if (!ids.Contains(id) && reported.Add(id))
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, tableName, null, nameof(UiStrings.Id),
                        $"no row for '{id}' ({usedBy})"));
            }

            if (set.TryGet(typeof(ActionBindings), out var bindings))
            {
                foreach (var row in bindings.Rows)
                {
                    var binding = (ActionBindings)row;
                    var usedBy = "ActionBindings " + binding.ClassId;
                    Require(ActionId(binding.NormalAttack), usedBy);
                    // Every hit of the normal attack chain (R-3.1-10): a refused hit names its action.
                    foreach (var hit in ActionDef.ChainFrom(set, binding.NormalAttack, out _))
                        Require(ActionId(hit), usedBy);
                    Require(ActionId(binding.Dodge), usedBy);
                    Require(ActionId(binding.Parry), usedBy);
                    foreach (var skill in binding.SkillSlots)
                    {
                        if (!string.IsNullOrEmpty(skill))
                            Require(ActionId(skill), usedBy);
                    }
                }
            }

            if (set.TryGet(typeof(SpawnPoints), out var spawns) && set.TryGet(typeof(MonsterDef), out var monsters))
            {
                var spawned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in spawns.Rows)
                    spawned.Add(((SpawnPoints)row).MonsterId);
                foreach (var row in monsters.Rows)
                {
                    var monster = (MonsterDef)row;
                    if (!spawned.Contains(monster.Id))
                        continue;
                    Require(MonsterId(monster.Id), "SpawnPoints");
                    Require(RankId(monster.Rank), "MonsterDef " + monster.Id);
                }
            }

            // The player's rank in the resource panel line and the rank-up toast (spec 3.6, WP-M2-3): every rank.
            if (set.TryGet(typeof(RankLevelRanges), out var ranks))
            {
                foreach (var row in ranks.Rows)
                    Require(RankId(((RankLevelRanges)row).Rank), "RankLevelRanges");
            }

            if (set.TryGet(typeof(StatusEffectDef), out var statuses))
            {
                foreach (var row in statuses.Rows)
                    Require(StatusId(((StatusEffectDef)row).Id), "StatusEffectDef");
            }

            // WP-M2-11: every stat by name (the character sheet and item tooltips).
            if (set.TryGet(typeof(StatDef), out var stats))
            {
                foreach (var row in stats.Rows)
                    Require(StatId(((StatDef)row).Id), "StatDef");
            }

            // The inventory tooltip (spec 9.13): every rarity, and the category of every item.
            if (set.TryGet(typeof(RarityDef), out var rarities))
            {
                foreach (var row in rarities.Rows)
                    Require(RarityId(((RarityDef)row).Id), "RarityDef");
            }
            if (set.TryGet(typeof(ItemDef), out var items))
            {
                foreach (var row in items.Rows)
                    Require(ItemCategoryId(((ItemDef)row).Category), "ItemDef " + ((ItemDef)row).Id);
            }
            // The inventory window's container tabs and equipment slots (spec 9.13, WP-M2-1).
            if (set.TryGet(typeof(ContainerDef), out var containers))
            {
                foreach (var row in containers.Rows)
                    Require(ContainerId(((ContainerDef)row).Id), "ContainerDef");
            }
            if (set.TryGet(typeof(EquipSlotDef), out var slots))
            {
                foreach (var row in slots.Rows)
                    Require(EquipSlotId(((EquipSlotDef)row).Id), "EquipSlotDef");
            }
            // The skill tree window (spec 3.5, WP-M2-5): every class and every node, and every action a skill starts.
            if (set.TryGet(typeof(ClassDef), out var classes))
            {
                foreach (var row in classes.Rows)
                    Require(ClassName(((ClassDef)row).Id), "ClassDef");
            }
            if (set.TryGet(typeof(SkillTree), out var nodes))
            {
                foreach (var row in nodes.Rows)
                    Require(SkillNodeName(((SkillTree)row).Id), "SkillTree");
            }
            if (set.TryGet(typeof(SkillDef), out var skills))
            {
                foreach (var row in skills.Rows)
                {
                    var skill = (SkillDef)row;
                    if (skill.ActionId.Length != 0)
                        Require(ActionId(skill.ActionId), "SkillDef " + skill.Id);
                }
            }
            // The Bystial book window (spec 14.2, 9.13; WP-M2-8): every tier, and every monster, stat, effect, element and family
            // a card tier or a family set bonus names.
            if (set.TryGet(typeof(BystialCardDef), out var cards))
            {
                for (int tier = 1; tier <= BystialTiers.Count; tier++)
                    Require(BookTierId((BystialTier)tier), "BystialCardDef");
                foreach (var row in cards.Rows)
                {
                    var card = (BystialCardDef)row;
                    string usedBy = "BystialCardDef " + card.Id;
                    Require(MonsterId(card.MonsterId), usedBy);
                    RequireBonus(card.StatId, card.EffectKind, card.EffectElement, MonsterDef.Find(set, card.MonsterId)?.Family, usedBy);
                }
            }
            if (set.TryGet(typeof(BystialFamilyBonus), out var families))
            {
                foreach (var row in families.Rows)
                {
                    var bonus = (BystialFamilyBonus)row;
                    string usedBy = "BystialFamilyBonus " + bonus.Id;
                    Require(BookFamilyId(bonus.Family), usedBy);
                    RequireBonus(bonus.StatId, bonus.EffectKind, bonus.EffectElement, bonus.Family, usedBy);
                }
            }

            void RequireBonus(string statId, BystialEffectKind kind, Element element, MonsterFamily? family, string usedBy)
            {
                if (statId.Length > 0)
                    Require(BookStatId(statId), usedBy);
                if (kind == BystialEffectKind.None)
                    return;
                Require(BookEffectId(kind), usedBy);
                if (kind == BystialEffectKind.ElementResistance)
                    Require(BookElementId(element), usedBy);
                if (kind is BystialEffectKind.FamilyDamage or BystialEffectKind.FamilyExp && family is { } named)
                    Require(BookFamilyId(named), usedBy);
            }
            return errors;
        }
    }
}
