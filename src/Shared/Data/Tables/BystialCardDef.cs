using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One tier of one monster type's Bystial card (spec 14.2, R-14.2-02/05; user decision 2026-10-27, placeholders): the
    /// kills it needs (<c>monster_kill_counts</c>, R-14.2-01), one stat bonus and one themed effect from the fixed list
    /// (<see cref="BystialEffectKind"/>). A placed card gives its current tier's row only. Flat rows, one per monster and
    /// tier, because config tables hold no object arrays. The card's family is its monster's <see cref="MonsterDef.Family"/>.
    /// <list type="bullet">
    /// <item>A monster's tiers start at normal with no gap, each once, and need strictly more kills each.</item>
    /// <item>Bosses have much lower thresholds (placeholder ladder 1, 5, 20, 50 against 100, 500, 2,000, 5,000).</item>
    /// <item>Every stat named has a <see cref="BystialStatCap"/> row and every effect kind a <see cref="BystialEffectCap"/> row
    /// (architecture rule 2: all bonuses capped).</item>
    /// </list>
    /// </summary>
    [Table("BystialCardDef", 1)]
    public sealed class BystialCardDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Stable id, by convention <c>&lt;monsterId&gt;.&lt;tier&gt;</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(MonsterDef))] public string MonsterId { get; set; } = string.Empty;

        public BystialTier Tier { get; set; }

        /// <summary>The monster type's kill count at which the card reaches this tier.</summary>
        [Range(1, 1_000_000_000)] public int KillsRequired { get; set; }

        /// <summary>The stat bonus: a <see cref="StatDef"/> row.</summary>
        [Ref(typeof(StatDef))] public string StatId { get; set; } = string.Empty;

        [Range(-1_000_000, 1_000_000)] public int Flat { get; set; }

        [Range(-1_000, 1_000)] public int Percent { get; set; }

        public BystialEffectKind EffectKind { get; set; }

        /// <summary>The element of an <see cref="BystialEffectKind.ElementResistance"/> effect; none otherwise.</summary>
        [Optional] public Element EffectElement { get; set; }

        /// <summary>The effect's size in whole percents (0 with no effect).</summary>
        [Range(0, 1_000)] public int EffectValue { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Tier == BystialTier.None || !Enum.IsDefined(typeof(BystialTier), Tier))
                errors.Add(nameof(Tier), $"{(int)Tier} is not a card tier (normal, rare, epic, legendary)");
            if (Flat == 0 && Percent == 0)
                errors.Add(nameof(Flat), "flat and percent are both 0: a card tier gives a stat bonus");
            BystialBonusRules.CheckEffect(EffectKind, EffectElement, EffectValue, errors);
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<BystialCardDef>(set);
            var monster = MonsterDef.Find(set, MonsterId);
            if (monster != null && monster.Family == MonsterFamily.None)
                errors.Add(nameof(MonsterId), $"'{MonsterId}' has no family (MonsterDef.family); a monster with cards needs one");

            // The ladder of this monster: tiers from normal with no gap, each once, kills strictly rising (R-14.2-02).
            var ladder = rows.FindAll(row => row.MonsterId == MonsterId);
            if (ladder.FindAll(row => row.Tier == Tier).Count > 1)
                errors.Add(nameof(Tier), $"'{MonsterId}' has more than one {Tier} row");
            if (Tier > BystialTier.Normal && !ladder.Exists(row => row.Tier == Tier - 1))
                errors.Add(nameof(Tier), $"'{MonsterId}' has no {Tier - 1} row below {Tier}");
            foreach (var other in ladder)
            {
                if (other.Tier < Tier && other.KillsRequired >= KillsRequired)
                    errors.Add(nameof(KillsRequired), $"must be greater than the {other.Tier} tier's {other.KillsRequired}");
            }

            BystialBonusRules.CheckCaps(set, StatId, EffectKind, errors);

            // Once per table, on its first row: the protocol cap and the client art.
            if (rows.Count == 0 || !ReferenceEquals(rows[0], this))
                return;
            var monsters = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
                monsters.Add(row.MonsterId);
            if (monsters.Count > BookLimits.MaxCards)
                errors.Add(nameof(MonsterId), $"{monsters.Count} monster types have cards; BookState carries at most {BookLimits.MaxCards}");
            if (set.TryGet(typeof(BystialCardArt), out var art))
            {
                var drawn = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in art.Rows)
                    drawn.Add(((BystialCardArt)row).Id);
                foreach (var id in monsters)
                {
                    if (!drawn.Contains(id))
                        errors.Add(nameof(MonsterId), $"'{id}' has cards but no BystialCardArt row");
                }
            }
        }

        /// <summary>The card rows in table order (tests and tools).</summary>
        public static List<BystialCardDef> Rows(ConfigSet set) => NetIds.Rows<BystialCardDef>(set);
    }

    /// <summary>The checks a card tier and a family set bonus share: the effect's fields and the caps.</summary>
    internal static class BystialBonusRules
    {
        public static void CheckEffect(BystialEffectKind kind, Element element, int value, RowErrors errors)
        {
            if (!Enum.IsDefined(typeof(BystialEffectKind), kind))
            {
                errors.Add("EffectKind", $"{(int)kind} is not an effect kind");
                return;
            }
            if (kind == BystialEffectKind.None && value != 0)
                errors.Add("EffectValue", "must be 0 without an effect");
            if (kind != BystialEffectKind.None && value <= 0)
                errors.Add("EffectValue", "must be greater than 0 for an effect");
            if (kind == BystialEffectKind.ElementResistance && (element == Element.None || !Enum.IsDefined(typeof(Element), element)))
                errors.Add("EffectElement", "an element resistance names its element");
            if (kind != BystialEffectKind.ElementResistance && element != Element.None)
                errors.Add("EffectElement", "only an element resistance names an element");
        }

        /// <summary>Rule 2: a stat bonus needs its <see cref="BystialStatCap"/> row and an effect its <see cref="BystialEffectCap"/> row.</summary>
        public static void CheckCaps(ConfigSet set, string statId, BystialEffectKind kind, RowErrors errors)
        {
            if (statId.Length > 0 && !NetIds.Rows<BystialStatCap>(set).Exists(row => row.StatId == statId))
                errors.Add("StatId", $"'{statId}' has no BystialStatCap row (every bonus is capped)");
            if (kind != BystialEffectKind.None && !NetIds.Rows<BystialEffectCap>(set).Exists(row => row.Kind == kind))
                errors.Add("EffectKind", $"{kind} has no BystialEffectCap row (every bonus is capped)");
        }
    }
}
