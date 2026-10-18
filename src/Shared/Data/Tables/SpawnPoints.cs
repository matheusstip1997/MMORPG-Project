namespace Shared.Data.Tables
{
    /// <summary>
    /// Fixed monster spawn points (catalog 8.5, backlog F): one monster of <see cref="MonsterId"/> at the world's
    /// <c>monsterSpawn</c> marker <see cref="MarkerId"/> (exported by UnityExporter into markers.json, spec 11.6). The
    /// world loader and DataValidator --world check that each marker exists and is a monsterSpawn. The game server's
    /// <c>SpawnController</c> spawns it when the world starts and again after the respawn delay of its type.
    /// TODO(F, spec 4.2): population caps, spawns only near players (activation by cell), the demand multiplier.
    /// </summary>
    [Table("SpawnPoints", 1)]
    public sealed class SpawnPoints : IValidatableRow
    {
        /// <summary>The marker id, for example <c>m1.spawn.combat.a</c>; one row per marker.</summary>
        [TableId] public string MarkerId { get; set; } = string.Empty;

        [Ref(typeof(MonsterDef))] public string MonsterId { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(MarkerId))
                errors.Add(nameof(MarkerId), "must name a monsterSpawn marker of the world");
        }
    }
}
