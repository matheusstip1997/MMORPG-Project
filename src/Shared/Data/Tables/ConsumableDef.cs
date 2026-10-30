using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Potion or food (R-3.4-03): a potion raises toxicity and its status is source <c>potion</c>, food raises satiety and
    /// its status is source <c>food</c> (R-3.7-05). Config enum (camelCase names); append only.
    /// </summary>
    public enum ConsumableKind : byte
    {
        Potion = 1,
        Food = 2,
    }

    /// <summary>
    /// What using a consumable does (spec 3.4 "Consumable use", R-3.4-03, R-3.7-05; M3 plan decision 3, WP-M3-3). Using an
    /// item is an action: the item plays <see cref="ActionId"/> (an <see cref="ActionKind.UseItem"/> action), and at its
    /// <c>consume</c> effect the server removes one unit and applies the effects below: a flat HP heal and MP restore at
    /// once, the status (source <c>potion</c> or <c>food</c>) and the meter raise. Refused with <c>TooToxic</c> when the
    /// toxicity would pass <c>ConsumptionMeterSettings.toxicityMax</c>, <c>TooFull</c> when the satiety would pass its max
    /// ("you cannot overeat"). A cooldown per consumable starts when it is used. Every consumable item has exactly one
    /// row (<see cref="ConsumableCoverage"/>). Every number in data/tables/ConsumableDef.json is a placeholder (balance is
    /// deferred to the end of the project).
    /// </summary>
    [Table("ConsumableDef", 1)]
    public sealed class ConsumableDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The consumable item (<see cref="ItemDef"/> of category <c>consumable</c>).</summary>
        [TableId, Ref(typeof(ItemDef))] public string Id { get; set; } = string.Empty;

        public ConsumableKind Kind { get; set; }

        /// <summary>The action using it plays (kind <c>useItem</c>: drinking, eating).</summary>
        [Ref(typeof(ActionDef))] public string ActionId { get; set; } = string.Empty;

        /// <summary>HP restored at once at the action's effect point (flat; never above the max).</summary>
        [Range(0, 1_000_000)] public int HealHp { get; set; }

        /// <summary>MP restored at once at the action's effect point (flat; never above the max).</summary>
        [Range(0, 1_000_000)] public int RestoreMp { get; set; }

        /// <summary>A status applied to the user (a buff: source <c>potion</c> or <c>food</c>); empty for none.</summary>
        [Optional, Ref(typeof(StatusEffectDef), Optional = true)] public string StatusId { get; set; } = string.Empty;

        /// <summary>Toxicity points added (potions only; R-3.4-03).</summary>
        [Range(0, 1_000_000)] public int Toxicity { get; set; }

        /// <summary>Satiety points added (food only; R-3.4-03).</summary>
        [Range(0, 1_000_000)] public int Satiety { get; set; }

        /// <summary>From one use of this consumable until the next one may start; 0 = none. Volatile (not saved).</summary>
        [Range(0, 3_600_000)] public int CooldownMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Kind != ConsumableKind.Potion && Kind != ConsumableKind.Food)
                errors.Add(nameof(Kind), "must be potion or food");
            if (HealHp == 0 && RestoreMp == 0 && StatusId.Length == 0)
                errors.Add(nameof(HealHp), "a consumable needs an effect: healHp, restoreMp or statusId");
            if (Kind == ConsumableKind.Potion)
            {
                if (Toxicity <= 0)
                    errors.Add(nameof(Toxicity), "a potion adds toxicity (R-3.4-03)");
                if (Satiety != 0)
                    errors.Add(nameof(Satiety), "a potion adds no satiety (food does, R-3.4-03)");
            }
            else if (Kind == ConsumableKind.Food)
            {
                if (Satiety <= 0)
                    errors.Add(nameof(Satiety), "food adds satiety (R-3.4-03)");
                if (Toxicity != 0)
                    errors.Add(nameof(Toxicity), "food adds no toxicity (potions do, R-3.4-03)");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, Id);
            if (item != null && item.Category != ItemCategory.Consumable)
                errors.Add(nameof(Id), $"'{Id}' is {item.Category}; only consumables are used");
            var action = ActionDef.Find(set, ActionId);
            if (action != null && action.Kind != ActionKind.UseItem)
                errors.Add(nameof(ActionId), $"'{ActionId}' is a {action.Kind} action; a consumable plays a useItem action");
            if (StatusId.Length != 0)
            {
                var status = StatusEffectDef.Find(set, StatusId);
                if (status != null)
                {
                    var wanted = Kind == ConsumableKind.Food ? StatusSource.Food : StatusSource.Potion;
                    if (status.Kind != StatusEffectKind.Buff || status.Source != wanted)
                        errors.Add(nameof(StatusId), $"'{StatusId}' must be a buff with source {wanted} (R-3.7-05)");
                }
            }
            var meters = set.TryGet(typeof(ConsumptionMeterSettings), out var table) && table.Rows.Count == 1
                ? (ConsumptionMeterSettings)table.Rows[0]
                : null;
            if (meters != null)
            {
                // Over the cap it could never be used (R-3.4-03: refused when the meter would pass the max).
                if (Toxicity > meters.ToxicityMax)
                    errors.Add(nameof(Toxicity), $"above ConsumptionMeterSettings.toxicityMax ({meters.ToxicityMax})");
                if (Satiety > meters.SatietyMax)
                    errors.Add(nameof(Satiety), $"above ConsumptionMeterSettings.satietyMax ({meters.SatietyMax})");
            }
        }

        public static ConsumableDef? Find(ConfigSet set, string itemId) => NetIds.Find<ConsumableDef>(set, itemId, row => row.Id);
    }

    /// <summary>
    /// DataValidator rule across the item tables (spec 3.4 "Consumable use", WP-M3-3): every consumable item has a
    /// <see cref="ConsumableDef"/> row (one per item by its id), so every consumable in a bag can be used. Skipped quietly
    /// when the table is not loaded (the loader reports that).
    /// </summary>
    public static class ConsumableCoverage
    {
        public static List<ValidationError> Check(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            if (!set.TryGet(typeof(ConsumableDef), out var table))
                return errors;
            var covered = new HashSet<string>();
            foreach (var row in table.Rows)
                covered.Add(((ConsumableDef)row).Id);
            foreach (var item in NetIds.Rows<ItemDef>(set))
            {
                if (item.Category == ItemCategory.Consumable && !covered.Contains(item.Id))
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, TableInfo.For(typeof(ConsumableDef)).Name, null,
                        nameof(ConsumableDef.Id), $"no row for consumable item '{item.Id}'"));
            }
            return errors;
        }
    }
}
