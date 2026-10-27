using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Simulation.Actions
{
    /// <summary>
    /// One class's <see cref="ActionBindings"/> row as catalog indices (backlog E, spec 9.6): which action left click, Ctrl,
    /// right click and each hotbar slot start. Built once by the <see cref="ActionCatalog"/>, shared by every entity of
    /// that class; the server and client prediction pick the same one from the character's class. Read-only.
    /// </summary>
    public sealed class ActionLoadout
    {
        /// <summary>Binds nothing: every press does nothing (monsters, which start actions through the AI's request).</summary>
        public static readonly ActionLoadout None = new ActionLoadout();

        private readonly int[] _slotIndex = new int[ActionBindings.MaxSkillSlots + 1];
        private readonly uint[] _slotWeapons = new uint[ActionBindings.MaxSkillSlots + 1];

        private ActionLoadout()
        {
            ClassId = string.Empty;
            AttackIndex = DodgeIndex = ParryIndex = -1;
            for (int slot = 0; slot < _slotIndex.Length; slot++)
                _slotIndex[slot] = -1;
        }

        internal ActionLoadout(ActionBindings bindings, Dictionary<string, int> indexById)
            : this()
        {
            ClassId = bindings.ClassId;
            AttackIndex = Lookup(indexById, bindings.NormalAttack);
            DodgeIndex = Lookup(indexById, bindings.Dodge);
            ParryIndex = Lookup(indexById, bindings.Parry);
            for (int i = 0; i < bindings.SkillSlots.Length && i < ActionBindings.MaxSkillSlots; i++)
                _slotIndex[i + 1] = Lookup(indexById, bindings.SkillSlots[i]);
        }

        /// <summary>
        /// A character's own loadout (WP-M2-5, spec 3.5 and 9.6): the class line's attack, dodge and parry and the learned
        /// skills the player placed on the hotbar. <paramref name="slotIndex"/> and <paramref name="slotWeapons"/> are indexed
        /// by slot (index 0 unused): the catalog index or -1, and the weapon types the slot's skill needs (a <c>WeaponMasks</c>
        /// mask, 0 for any). Built by <c>Skills.LoadoutBuilder</c> on the server, in client prediction and in the bots.
        /// </summary>
        public ActionLoadout(string classId, int attackIndex, int dodgeIndex, int parryIndex, int[] slotIndex, uint[] slotWeapons)
            : this()
        {
            ClassId = classId ?? throw new ArgumentNullException(nameof(classId));
            AttackIndex = attackIndex;
            DodgeIndex = dodgeIndex;
            ParryIndex = parryIndex;
            for (int slot = 1; slot < _slotIndex.Length; slot++)
            {
                _slotIndex[slot] = slot < slotIndex.Length ? slotIndex[slot] : -1;
                _slotWeapons[slot] = slot < slotWeapons.Length ? slotWeapons[slot] : 0;
            }
        }

        /// <summary>The <see cref="ClassDef"/> id of the row; empty for <see cref="None"/>.</summary>
        public string ClassId { get; }

        /// <summary>Catalog index of the class's normal attack (left click, R-3.1-06), or -1.</summary>
        public int AttackIndex { get; }

        /// <summary>Catalog index of the dodge (Ctrl), or -1.</summary>
        public int DodgeIndex { get; }

        /// <summary>Catalog index of the parry (right click), or -1.</summary>
        public int ParryIndex { get; }

        /// <summary>Catalog index of the action on a hotbar slot (1-based), or -1 when the slot is unassigned.</summary>
        public int SlotIndex(int slot) => slot > 0 && slot < _slotIndex.Length ? _slotIndex[slot] : -1;

        /// <summary>R-3.5-07: the weapon types the skill on a slot needs (any of them), as a <c>WeaponMasks</c> mask; 0 for any.</summary>
        public uint SlotWeapons(int slot) => slot > 0 && slot < _slotWeapons.Length ? _slotWeapons[slot] : 0;

        /// <summary>True when both bind the same actions with the same weapon needs.</summary>
        public bool SameBindings(ActionLoadout other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (ClassId != other.ClassId || AttackIndex != other.AttackIndex || DodgeIndex != other.DodgeIndex || ParryIndex != other.ParryIndex)
                return false;
            for (int slot = 0; slot < _slotIndex.Length; slot++)
            {
                if (_slotIndex[slot] != other._slotIndex[slot] || _slotWeapons[slot] != other._slotWeapons[slot])
                    return false;
            }
            return true;
        }

        private static int Lookup(Dictionary<string, int> indexById, string? id) =>
            id != null && indexById.TryGetValue(id, out int index) ? index : -1;

        public override string ToString() => ClassId.Length == 0 ? "no loadout" : "loadout " + ClassId;
    }
}
