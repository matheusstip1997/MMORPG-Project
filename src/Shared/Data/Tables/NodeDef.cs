using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A resource node type (spec 5.2 R-5.2-02, R-5.2-03; WP-M3-4): ore of tiers 1 to 3 and the gem vein in M3. Nodes are
    /// placed by <see cref="GatherNodes"/> on the world's resourceNode markers and are entities in the snapshot (drawn with
    /// <see cref="VisualId"/>, or <see cref="DepletedVisualId"/> while depleted), visible to everyone, never combatants and
    /// never targetable. One harvest depletes a node; it respawns after <see cref="RespawnMs"/> scaled by the demand loop.
    /// The job, tool, channel and yield fields are read by harvesting (WP-M3-5). Values in data/tables/NodeDef.json are
    /// placeholders.
    /// </summary>
    [Table("NodeDef", 1)]
    public sealed class NodeDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Largest <see cref="NetId"/>: it travels as 16 bits in spawn events.</summary>
        public const int MaxNetId = ushort.MaxValue;

        /// <summary>Stable id, for example <c>node.ore.t1</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire (the spawn event's definition id); unique, never reused.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        /// <summary>
        /// The job whose players may harvest it (R-5.2-01), for example <c>blacksmith</c>.
        /// TODO(WP-M3-1): <c>[Ref(typeof(JobDef))]</c> once the job table is on main.
        /// </summary>
        public string JobId { get; set; } = string.Empty;

        /// <summary>The tool a harvest needs in the Safe bag's equipment area (R-5.2-02, spec 4.1 M3 item model).</summary>
        public ToolType ToolType { get; set; }

        /// <summary>
        /// The lowest tool tier that may harvest it (R-5.2-02 "tool tier gates node tier"): a <see cref="MaterialTierDef"/>
        /// tier that a <see cref="ToolDef"/> row of <see cref="ToolType"/> reaches.
        /// </summary>
        [Range(1, MaterialTierDef.MaxTier)] public int MinToolTier { get; set; }

        /// <summary>The node's tier, a <see cref="MaterialTierDef"/> tier (spec 4.1: T1 in rings 1-2, T2 in ring 3, T3 in ring 4).</summary>
        [Range(1, MaterialTierDef.MaxTier)] public int NodeTier { get; set; }

        /// <summary>The yield table harvesting rolls (WP-M3-5); empty until it lands.</summary>
        [Optional] public string YieldTableId { get; set; } = string.Empty;

        /// <summary>The base respawn delay after a harvest, before the demand scale (R-5.2-03).</summary>
        [Range(1_000, 86_400_000)] public int RespawnMs { get; set; }

        /// <summary>The harvest channel's length (WP-M3-5; spec 5.2 "harvest is a channeled action").</summary>
        [Range(0, 60_000)] public int ChannelMs { get; set; }

        /// <summary>A player at most this far from the node gets the prompt and may harvest it (server check by WP-M3-5).</summary>
        [Range(1, 10_000)] public int ReachCm { get; set; }

        /// <summary>Look of the available node (greybox placeholder in M3).</summary>
        [Ref(typeof(EntityVisualDef))] public string VisualId { get; set; } = string.Empty;

        /// <summary>Look of the depleted node.</summary>
        [Ref(typeof(EntityVisualDef))] public string DepletedVisualId { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (string.IsNullOrWhiteSpace(JobId))
                errors.Add(nameof(JobId), "must name the job that harvests it");
            if (ToolType == ToolType.None || !Enum.IsDefined(typeof(ToolType), ToolType))
                errors.Add(nameof(ToolType), $"{(int)ToolType} is not a tool type");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<NodeDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            // R-5.2-02: both tiers on the material tier ladder, and a tool of the type that reaches the minimum tier exists
            // (a node no tool can harvest is a forgotten row).
            if (MaterialTierDef.ByTier(set, NodeTier) == null)
                errors.Add(nameof(NodeTier), $"{NodeTier} is not a MaterialTierDef tier");
            if (MaterialTierDef.ByTier(set, MinToolTier) == null)
                errors.Add(nameof(MinToolTier), $"{MinToolTier} is not a MaterialTierDef tier");
            else if (set.TryGet(typeof(ToolDef), out _) && !HasTool(set))
                errors.Add(nameof(MinToolTier), $"no ToolDef row of type {ToolType} reaches tier {MinToolTier}");
        }

        private bool HasTool(ConfigSet set)
        {
            foreach (var tool in NetIds.Rows<ToolDef>(set))
            {
                if (tool.ToolType == ToolType && tool.Tier >= MinToolTier)
                    return true;
            }
            return false;
        }

        public static NodeDef? Find(ConfigSet set, string id) => NetIds.Find<NodeDef>(set, id, row => row.Id);
    }
}
