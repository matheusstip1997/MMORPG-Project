#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Game.UI.ViewModels.Character
{
    /// <summary>
    /// The character sheet (spec 9.13 M2, WP-M2-11): the own class, level, rank, exp and unspent skill points from
    /// <see cref="ProgressionState"/>, and every final stat of <c>OwnStats</c> by <c>StatDef</c> id; WP-M2-13 (R-3.4-08): the
    /// unspent and placed free attribute points and the one placement waiting for its answer. Display only: the server
    /// computes every number (spec 9.1). Raises <see cref="ViewModel.Changed"/> only on a real change.
    /// </summary>
    public sealed class CharacterSheetViewModel : ViewModel
    {
        private readonly Dictionary<string, int> _stats = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly int[] _placed = new int[Attributes.Count];

        public bool Open { get; private set; }

        /// <summary>The <c>ClassDef</c> id from the committed skill tree; empty until it arrived.</summary>
        public string ClassId { get; private set; } = "";

        public bool HasProgression { get; private set; }
        public int Level { get; private set; }
        public Rank Rank { get; private set; }
        public long Exp { get; private set; }

        /// <summary>Exp the level needs; 0 at the level cap.</summary>
        public long ExpToNext { get; private set; }

        public int SkillPoints { get; private set; }

        /// <summary>Unspent free attribute points (R-3.4-08, WP-M2-13; committed, from <see cref="ProgressionState"/>).</summary>
        public int AttributePoints { get; private set; }

        /// <summary>The request id of the attribute point the sheet waits an answer for; 0 for none (its plus buttons are off meanwhile).</summary>
        public uint Pending { get; private set; }

        /// <summary>Points placed on attribute <paramref name="attribute"/> (<c>Attributes.Str</c> ... <c>Attributes.Int</c>).</summary>
        public int Placed(int attribute) => attribute >= 0 && attribute < _placed.Length ? _placed[attribute] : 0;

        /// <summary>A plus button shows while there are unspent points (spec 9.13 M2).</summary>
        public bool ShowsPlus => HasProgression && AttributePoints > 0;

        /// <summary>A plus button works while it shows and no other placement waits for its answer.</summary>
        public bool CanPlace => ShowsPlus && Pending == 0;

        /// <summary>The server sent the own stats on this connection.</summary>
        public bool HasStats { get; private set; }

        /// <summary>A final stat's value; 0 for an unknown id or before the first message.</summary>
        public int Stat(string statId) => _stats.TryGetValue(statId, out int value) ? value : 0;

        public void Toggle() => SetOpen(!Open);

        public void SetOpen(bool open)
        {
            if (open == Open)
                return;
            Open = open;
            NotifyChanged();
        }

        public void SetProgression(ProgressionState state)
        {
            if (HasProgression && state.Level == Level && state.Rank == Rank && state.Exp == Exp && state.ExpToNext == ExpToNext
                && state.SkillPoints == SkillPoints && state.AttributePoints == AttributePoints && SamePlaced(state))
                return;
            HasProgression = true;
            Level = state.Level;
            Rank = state.Rank;
            Exp = state.Exp;
            ExpToNext = state.ExpToNext;
            SkillPoints = state.SkillPoints;
            AttributePoints = state.AttributePoints;
            for (int a = 0; a < _placed.Length; a++)
                _placed[a] = state.Placed(a);
            NotifyChanged();
        }

        /// <summary>A plus button sent request <paramref name="requestId"/> (0: nothing was sent).</summary>
        public void SetPending(uint requestId)
        {
            if (requestId == 0 || requestId == Pending)
                return;
            Pending = requestId;
            NotifyChanged();
        }

        /// <summary>The server answered request <paramref name="requestId"/>: the plus buttons work again.</summary>
        public void OnAnswer(uint requestId)
        {
            if (requestId != Pending)
                return;
            Pending = 0;
            NotifyChanged();
        }

        private bool SamePlaced(ProgressionState state)
        {
            for (int a = 0; a < _placed.Length; a++)
            {
                if (_placed[a] != state.Placed(a))
                    return false;
            }
            return true;
        }

        public void SetClass(string classId)
        {
            if (classId == ClassId)
                return;
            ClassId = classId;
            NotifyChanged();
        }

        /// <summary>The final stats, read through <paramref name="value"/> for each id of <paramref name="statIds"/>.</summary>
        public void SetStats(IEnumerable<string> statIds, Func<string, int> value)
        {
            bool changed = !HasStats;
            foreach (string id in statIds)
            {
                int v = value(id);
                if (!_stats.TryGetValue(id, out int old) || old != v)
                {
                    _stats[id] = v;
                    changed = true;
                }
            }
            HasStats = true;
            if (changed)
                NotifyChanged();
        }

        /// <summary>The connection ended: nothing shown is current any more.</summary>
        public void Clear()
        {
            _stats.Clear();
            HasStats = false;
            HasProgression = false;
            ClassId = "";
            Pending = 0;
            NotifyChanged();
        }
    }
}
