#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Skills;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of the skill tree (spec 3.5, 9.6; WP-M2-5), engine-free so the bots use it too: the tree rules from
    /// the tables, the committed class, learned nodes and hotbar the server sent (<see cref="SkillTreeState"/>), the skill
    /// requests with their ids (one counter per connection) and their answers. The predictor's loadout comes from
    /// <see cref="Loadout"/>, built by the same <see cref="LoadoutBuilder"/> as the server's, and the weapon mask from the
    /// committed equipped container (R-3.5-07). The server checks everything again.
    /// </summary>
    public sealed class ClientSkillTree
    {
        private readonly Dictionary<ushort, WeaponType> _weaponByItem = new Dictionary<ushort, WeaponType>();
        private readonly int _equippedCode = -1;

        public ClientSkillTree(ConfigSet config)
        {
            Rules = SkillRules.From(config);
            if (config.TryGet(typeof(ItemDef), out var items))
            {
                foreach (var row in items.Rows)
                {
                    var item = (ItemDef)row;
                    if (item.WeaponType != WeaponType.None)
                        _weaponByItem[(ushort)item.NetId] = item.WeaponType;
                }
            }
            if (config.TryGet(typeof(ContainerDef), out var containers))
            {
                foreach (var row in containers.Rows)
                {
                    var container = (ContainerDef)row;
                    if (container.Kind == ContainerKind.Equipped)
                        _equippedCode = container.Code;
                }
            }
        }

        /// <summary>The skill tree tables as rules; null when the tables have none.</summary>
        public SkillRules? Rules { get; }

        /// <summary>The last state the server sent; null before the first.</summary>
        public SkillTreeState? State { get; private set; }

        /// <summary>The committed state as the shared rules read it; null before the first state.</summary>
        public SkillState? Skills { get; private set; }

        /// <summary>Skill requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        public SkillTreeResult? LastResult { get; private set; }

        /// <summary>Counts the states received, so a view can tell it changed.</summary>
        public int Revision { get; private set; }

        /// <summary>Takes the skill-tree messages of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.SkillTreeState:
                    if (ReliableMessages.TryRead(message, out SkillTreeState state) && (State == null || state.Version > State.Version))
                    {
                        State = state;
                        var skills = new SkillState(state.ClassId);
                        foreach (var node in state.Nodes)
                            skills.SetLevel(node.NodeId, node.Level);
                        foreach (var slot in state.Hotbar)
                        {
                            if (slot.Slot >= 1 && slot.Slot <= ActionBindings.MaxSkillSlots)
                                skills.Place(slot.Slot, slot.NodeId);
                        }
                        Skills = skills;
                        Revision++;
                        log = $"skills {state.ClassId}: {state.Nodes.Length} learned, {state.Hotbar.Length} on the bar ({state.Cause})";
                    }
                    return true;
                case ReliableMessageId.SkillTreeResult:
                    if (ReliableMessages.TryRead(message, out SkillTreeResult result))
                    {
                        LastResult = result;
                        log = $"skill {result.RequestId} {result.Op} {result.Target}: {result.Reason}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        public byte[] Request(SkillTreeOp op, string target, byte slot, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.SkillTreeRequest,
                new SkillTreeRequest { RequestId = requestId, Op = op, Target = target, Slot = slot });
        }

        /// <summary>The predicted loadout of the committed skills (null before the first state or without the tables).</summary>
        public ActionLoadout? Loadout(ActionCatalog catalog) =>
            Rules != null && Skills != null ? LoadoutBuilder.Build(catalog, Rules, Skills) : null;

        /// <summary>R-3.5-07: the weapon types of the committed equipped set, as the action machine checks them.</summary>
        public uint EquippedWeapons(InventoryContents? contents)
        {
            if (contents == null || _equippedCode < 0)
                return 0;
            uint mask = 0;
            foreach (var container in contents.Containers)
            {
                if (container.Code != _equippedCode)
                    continue;
                foreach (var slot in container.Slots)
                {
                    if (_weaponByItem.TryGetValue(slot.ItemNetId, out var type))
                        mask |= WeaponMasks.Bit(type);
                }
            }
            return mask;
        }

        /// <summary>Forgets the connection's state and request ids (a reconnect starts again).</summary>
        public void Reset()
        {
            State = null;
            Skills = null;
            LastResult = null;
            RequestsSent = 0;
        }
    }
}
