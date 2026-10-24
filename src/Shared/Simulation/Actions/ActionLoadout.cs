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

        private static int Lookup(Dictionary<string, int> indexById, string? id) =>
            id != null && indexById.TryGetValue(id, out int index) ? index : -1;

        public override string ToString() => ClassId.Length == 0 ? "no loadout" : "loadout " + ClassId;
    }
}
