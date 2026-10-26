using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Which action each input starts for one class (R-2.2-01, backlog E, spec 9.6): left click the class's own normal
    /// attack (R-3.1-06), Ctrl the dodge, right click the parry, and the hotbar slots its starting kit. M1 has one fixed
    /// layout per class, no per-character hotbar; a character uses its class's row on the server and in client prediction
    /// (<c>ActionCatalog.Loadout</c>). Slot numbers are the input frame's: 1-6 are keys 1-6, 7 Q, 8 E, 9 R, 10 F, 11 Z.
    /// TODO(M2): the hotbar stored per character, checked against the learned skills (spec 9.6).
    /// </summary>
    [Table("ActionBindings", 2)]
    public sealed class ActionBindings : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Hotbar slots an input frame can name (<c>InputPacket.MaxSkillSlot</c>).</summary>
        public const int MaxSkillSlots = 15;

        /// <summary>The class this layout belongs to; every <see cref="ClassDef"/> row has one (ClassDef checks it).</summary>
        [TableId, Ref(typeof(ClassDef))] public string ClassId { get; set; } = string.Empty;

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

            // R-3.1-04, spec 12.7: a skill costs a small amount of MP, sized to the class's pool, so it can always be used.
            var classDef = ClassDef.Find(set, ClassId);
            if (classDef != null)
            {
                foreach (var slot in SkillSlots)
                {
                    var skill = slot.Length != 0 ? ActionDef.Find(set, slot) : null;
                    // (A slot holding another kind is reported above.)
                    if (skill != null && skill.Kind == ActionKind.Skill && (skill.MpCost < 1 || skill.MpCost > classDef.MaxMp))
                        errors.Add(nameof(SkillSlots), $"'{slot}' costs {skill.MpCost} MP, outside 1..{classDef.MaxMp} "
                            + $"(R-3.1-04; ClassDef '{ClassId}' maxMp)");
                }
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
                // R-3.3-02: the dodge is a movement action; its Dash effects cover the dodge length of spec 12.7.
                int dashCm = DashDistanceCm(set, Dodge);
                if (dashCm >= 0 && dashCm != values.DodgeDistanceCm)
                    errors.Add(nameof(Dodge), $"'{Dodge}' dashes {dashCm} cm, StartingValuesM1.DodgeDistanceCm is {values.DodgeDistanceCm}");
                // R-3.3-02 (user decision 2026-10-26): along the movement keys held at the press, backward with none held.
                if (!DashesFollowTheDodgeRule(set, Dodge))
                    errors.Add(nameof(Dodge), $"'{Dodge}' must dash along the move input (dashDirection moveInput) and backward without input (directionOffsetDeg 180), R-3.3-02");
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

        /// <summary>The distance of the action's Dash effects added up, in cm; -1 when ActionEffectDef is not loaded.</summary>
        private static int DashDistanceCm(ConfigSet set, string actionId)
        {
            if (!set.TryGet(typeof(ActionEffectDef), out var effects))
                return -1;
            int total = 0;
            foreach (var row in effects.Rows)
            {
                var effect = (ActionEffectDef)row;
                if (effect.ActionId == actionId && effect.Kind == EffectKind.Dash)
                    total += effect.DistanceCm;
            }
            return total;
        }

        /// <summary>Every Dash effect of the action goes along the move input, backward without input (true when ActionEffectDef is not loaded).</summary>
        private static bool DashesFollowTheDodgeRule(ConfigSet set, string actionId)
        {
            if (!set.TryGet(typeof(ActionEffectDef), out var effects))
                return true;
            foreach (var row in effects.Rows)
            {
                var effect = (ActionEffectDef)row;
                if (effect.ActionId == actionId && effect.Kind == EffectKind.Dash
                    && (effect.DashDirection != DashDirection.MoveInput || effect.DirectionOffsetDeg != 180))
                    return false;
            }
            return true;
        }

        /// <summary>The row of a class, or null.</summary>
        public static ActionBindings? For(ConfigSet set, string classId)
        {
            if (!set.TryGet(typeof(ActionBindings), out var table))
                return null;
            foreach (var row in table.Rows)
            {
                var bindings = (ActionBindings)row;
                if (bindings.ClassId == classId)
                    return bindings;
            }
            return null;
        }
    }
}
