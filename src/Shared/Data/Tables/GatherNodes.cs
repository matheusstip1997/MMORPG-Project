namespace Shared.Data.Tables
{
    /// <summary>
    /// Resource node placement (catalog 8.5, spec 5.2, 11.6; WP-M3-4): one node of type <see cref="NodeId"/> at the world's
    /// <c>resourceNode</c> marker <see cref="MarkerId"/>, as <see cref="SpawnPoints"/> places monsters. The world loader and
    /// DataValidator --world check that every row names a resourceNode marker (<c>MissingNode</c>) and that every
    /// resourceNode marker has a row (<c>UnusedNode</c>).
    /// </summary>
    [Table("GatherNodes", 1)]
    public sealed class GatherNodes : IValidatableRow
    {
        /// <summary>The marker id, for example <c>field1.node.ring1.a</c>; one row per marker.</summary>
        [TableId] public string MarkerId { get; set; } = string.Empty;

        [Ref(typeof(NodeDef))] public string NodeId { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(MarkerId))
                errors.Add(nameof(MarkerId), "must name a resourceNode marker of the world");
        }
    }
}
