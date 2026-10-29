#nullable enable
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The body radius of an entity the client knows (R-2.2-07, R-3.1-03): <c>ClassDef.bodyRadiusCm</c> for a player,
    /// <c>MonsterDef.bodyRadiusCm</c> for a monster, looked up by kind and def net id (the two tables' net ids overlap).
    /// Plain C# with no UnityEngine API (ClientCore and GameServer.Tests compile it).
    /// </summary>
    public sealed class BodyRadii
    {
        private readonly Dictionary<int, int> _classes = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _monsters = new Dictionary<int, int>();

        public static BodyRadii From(ConfigSet set)
        {
            var radii = new BodyRadii();
            if (set.TryGet(typeof(ClassDef), out var classes))
            {
                foreach (var row in classes.Rows)
                    radii._classes[((ClassDef)row).NetId] = ((ClassDef)row).BodyRadiusCm;
            }
            if (set.TryGet(typeof(MonsterDef), out var monsters))
            {
                foreach (var row in monsters.Rows)
                    radii._monsters[((MonsterDef)row).NetId] = ((MonsterDef)row).BodyRadiusCm;
            }
            return radii;
        }

        /// <summary>The radius in whole cm; false for other kinds and unknown def ids.</summary>
        public bool TryGetCm(EntityKind kind, int defId, out int radiusCm)
        {
            radiusCm = 0;
            return kind == EntityKind.Player ? _classes.TryGetValue(defId, out radiusCm)
                : kind == EntityKind.Monster && _monsters.TryGetValue(defId, out radiusCm);
        }

        /// <summary>The radius in raw Q24.8 cm; false for other kinds and unknown def ids.</summary>
        public bool TryGetRaw(EntityKind kind, int defId, out int radiusRaw)
        {
            bool found = TryGetCm(kind, defId, out int cm);
            radiusRaw = FixedPoint.FromCm(cm);
            return found;
        }
    }
}
