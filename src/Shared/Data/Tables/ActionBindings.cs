using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Which action each input starts (R-2.2-01): left click the normal attack, Ctrl the dodge, right click the parry,
    /// and the hotbar slots their skills. One row for everyone in M1 until the class kits and the per-character hotbar
    /// stored on the server replace it. TODO(E): per-class normal attack (R-3.1-06) and kit; the server then checks
    /// that the skill is learned and on the bar (spec 2.2).
    /// </summary>
    [Table("ActionBindings", 1)]
    [Singleton]
    public sealed class ActionBindings : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Hotbar slots an input frame can name (<c>InputPacket.MaxSkillSlot</c>).</summary>
        public const int MaxSkillSlots = 15;

        [Ref(typeof(ActionDef))] public string NormalAttack { get; set; } = string.Empty;
        [Ref(typeof(ActionDef))] public string Dodge { get; set; } = string.Empty;
        [Ref(typeof(ActionDef))] public string Parry { get; set; } = string.Empty;

        /// <summary>Slot 1 is the first item; an empty string leaves the slot unassigned (its press is refused).</summary>
        [Ref(typeof(ActionDef), Optional = true)] public string[] SkillSlots { get; set; } = Array.Empty<string>();

        public void Validate(RowErrors errors)
        {
            if (SkillSlots.Length > MaxSkillSlots)
                errors.Add(nameof(SkillSlots), $"at most {MaxSkillSlots} slots");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            CheckKind(set, errors, nameof(NormalAttack), NormalAttack, ActionKind.Attack);
            CheckKind(set, errors, nameof(Dodge), Dodge, ActionKind.Dodge);
            CheckKind(set, errors, nameof(Parry), Parry, ActionKind.Parry);
            foreach (var slot in SkillSlots)
            {
                if (slot.Length != 0)
                    CheckKind(set, errors, nameof(SkillSlots), slot, ActionKind.Skill);
            }

            // Spec 12.7 gives the dodge and parry timings and the stamina costs; the actions must match them.
            if (!set.TryGet(typeof(StartingValuesM1), out var valuesTable) || valuesTable.Rows.Count != 1)
                return;
            var values = (StartingValuesM1)valuesTable.Rows[0];
            var dodge = ActionDef.Find(set, Dodge);
            if (dodge != null)
            {
                if (dodge.TotalMs() != values.DodgeDurationMs)
                    errors.Add(nameof(Dodge), $"'{Dodge}' lasts {dodge.TotalMs()} ms, StartingValuesM1.DodgeDurationMs is {values.DodgeDurationMs}");
                if (dodge.IFramesMs != values.DodgeIFramesMs)
                    errors.Add(nameof(Dodge), $"'{Dodge}' has {dodge.IFramesMs} ms of i-frames, StartingValuesM1.DodgeIFramesMs is {values.DodgeIFramesMs}");
                if (dodge.StaminaCost != values.DodgeStaminaCost)
                    errors.Add(nameof(Dodge), $"'{Dodge}' costs {dodge.StaminaCost} stamina, StartingValuesM1.DodgeStaminaCost is {values.DodgeStaminaCost}");
            }
            var parry = ActionDef.Find(set, Parry);
            if (parry != null && parry.ActiveMs != values.ParryWindowMs)
                errors.Add(nameof(Parry), $"'{Parry}' is active {parry.ActiveMs} ms, StartingValuesM1.ParryWindowMs is {values.ParryWindowMs}");
            var attack = ActionDef.Find(set, NormalAttack);
            if (attack != null && attack.StaminaCost != values.NormalAttackStaminaCost)
                errors.Add(nameof(NormalAttack),
                    $"'{NormalAttack}' costs {attack.StaminaCost} stamina, StartingValuesM1.NormalAttackStaminaCost is {values.NormalAttackStaminaCost}");
            foreach (var slot in SkillSlots)
            {
                var skill = slot.Length != 0 ? ActionDef.Find(set, slot) : null;
                if (skill != null && (skill.StaminaCost < values.SkillStaminaCostMin || skill.StaminaCost > values.SkillStaminaCostMax))
                    errors.Add(nameof(SkillSlots), $"'{slot}' costs {skill.StaminaCost} stamina, outside StartingValuesM1 "
                        + $"SkillStaminaCostMin..Max ({values.SkillStaminaCostMin}..{values.SkillStaminaCostMax})");
            }
        }

        private static void CheckKind(ConfigSet set, RowErrors errors, string field, string id, ActionKind kind)
        {
            var action = ActionDef.Find(set, id);
            if (action != null && action.Kind != kind)
                errors.Add(field, $"'{id}' is a {action.Kind} action, expected {kind}");
        }
    }
}
