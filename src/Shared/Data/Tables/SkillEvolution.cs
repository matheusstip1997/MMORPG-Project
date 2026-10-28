namespace Shared.Data.Tables
{
    /// <summary>
    /// The tree branches (spec 3.5 Implementation, R-3.5-04; catalog 8.5): for a skill family and a class, the node that
    /// evolves it on that class's tree. Several rows for one family on one line are a choice: the player takes one of
    /// them. **Generated** with <see cref="SkillTree"/> from <c>docs/design/skill-trees.json</c>; it repeats what the nodes'
    /// <c>evolvesFrom</c> say, so the window and the bots can list a base skill's evolutions without walking the tree.
    /// </summary>
    [Table("SkillEvolution", 1)]
    public sealed class SkillEvolution : ISetValidatableRow
    {
        /// <summary>The evolved node (one row per evolved node).</summary>
        [TableId, Ref(typeof(SkillTree))] public string NodeId { get; set; } = string.Empty;

        /// <summary>The family's root node (<see cref="SkillTree.Family"/>).</summary>
        [Ref(typeof(SkillTree))] public string Family { get; set; } = string.Empty;

        /// <summary>The class whose tree holds <see cref="NodeId"/>.</summary>
        [Ref(typeof(ClassDef))] public string ClassId { get; set; } = string.Empty;

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var node = SkillTree.Find(set, NodeId);
            if (node == null)
                return;
            if (node.EvolvesFrom.Length == 0)
                errors.Add(nameof(NodeId), $"'{NodeId}' evolves from nothing");
            if (node.Family != Family)
                errors.Add(nameof(Family), $"is '{Family}', the node's family is '{node.Family}'");
            if (node.ClassId != ClassId)
                errors.Add(nameof(ClassId), $"is '{ClassId}', the node is in the tree of '{node.ClassId}'");

            // Every evolved node has its row (checked on the first row only).
            if (!set.TryGet(typeof(SkillEvolution), out var table) || table.Rows.Count == 0 || !ReferenceEquals(table.Rows[0], this))
                return;
            foreach (var other in SkillTree.Rows(set))
            {
                if (other.EvolvesFrom.Length != 0 && NetIds.Find<SkillEvolution>(set, other.Id, row => row.NodeId) == null)
                    errors.Add(nameof(NodeId), $"'{other.Id}' evolves from '{other.EvolvesFrom}' but has no SkillEvolution row");
            }
        }
    }
}
