using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Simulation.Skills
{
    /// <summary>
    /// One character's skill state (spec 3.5 Implementation, R-3.5-04; WP-M2-5): its class, the learned nodes with their
    /// levels (points spent are derived) and the hotbar layout (slot to node; spec 9.6). The server's
    /// <c>SkillTreeService</c> is its only writer; the client holds the copy the server sent.
    /// </summary>
    public sealed class SkillState
    {
        private readonly SortedDictionary<string, int> _learned = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private readonly string[] _hotbar = new string[ActionBindings.MaxSkillSlots + 1];

        public SkillState(string classId)
        {
            ClassId = classId ?? throw new ArgumentNullException(nameof(classId));
            for (int i = 0; i < _hotbar.Length; i++)
                _hotbar[i] = string.Empty;
        }

        public string ClassId { get; set; }

        /// <summary>Learned nodes and levels, sorted by node id (ordinal).</summary>
        public IEnumerable<KeyValuePair<string, int>> Learned => _learned;

        public int LearnedCount => _learned.Count;

        /// <summary>The node's learned level, 0 when not learned.</summary>
        public int LevelOf(string nodeId) => _learned.TryGetValue(nodeId, out int level) ? level : 0;

        public bool IsLearned(string nodeId) => _learned.ContainsKey(nodeId);

        public void SetLevel(string nodeId, int level)
        {
            if (level <= 0)
                _learned.Remove(nodeId);
            else
                _learned[nodeId] = level;
        }

        public void Forget(string nodeId)
        {
            _learned.Remove(nodeId);
            for (int slot = 1; slot < _hotbar.Length; slot++)
            {
                if (_hotbar[slot] == nodeId)
                    _hotbar[slot] = string.Empty;
            }
        }

        /// <summary>The node on a hotbar slot (1-based), empty when none.</summary>
        public string NodeAt(int slot) => slot > 0 && slot < _hotbar.Length ? _hotbar[slot] : string.Empty;

        /// <summary>The slot holding the node, 0 when it is not on the hotbar.</summary>
        public int SlotOf(string nodeId)
        {
            for (int slot = 1; slot < _hotbar.Length; slot++)
            {
                if (_hotbar[slot] == nodeId)
                    return slot;
            }
            return 0;
        }

        /// <summary>Puts a node on a slot; a node is on one slot at most, so it leaves the one it was on.</summary>
        public void Place(int slot, string nodeId)
        {
            if (slot <= 0 || slot >= _hotbar.Length)
                throw new ArgumentOutOfRangeException(nameof(slot));
            int old = SlotOf(nodeId);
            if (old != 0)
                _hotbar[old] = string.Empty;
            _hotbar[slot] = nodeId;
        }

        public void ClearSlot(int slot)
        {
            if (slot > 0 && slot < _hotbar.Length)
                _hotbar[slot] = string.Empty;
        }

        /// <summary>Forgets every node and empties the hotbar (respec, R-3.5-04).</summary>
        public void ClearAll()
        {
            _learned.Clear();
            for (int slot = 0; slot < _hotbar.Length; slot++)
                _hotbar[slot] = string.Empty;
        }

        public SkillState Clone()
        {
            var copy = new SkillState(ClassId);
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(SkillState other)
        {
            ClassId = other.ClassId;
            _learned.Clear();
            foreach (var pair in other._learned)
                _learned[pair.Key] = pair.Value;
            Array.Copy(other._hotbar, _hotbar, _hotbar.Length);
        }

        public bool SameAs(SkillState other)
        {
            if (ClassId != other.ClassId || _learned.Count != other._learned.Count)
                return false;
            foreach (var pair in _learned)
            {
                if (other.LevelOf(pair.Key) != pair.Value)
                    return false;
            }
            for (int slot = 0; slot < _hotbar.Length; slot++)
            {
                if (_hotbar[slot] != other._hotbar[slot])
                    return false;
            }
            return true;
        }
    }
}
