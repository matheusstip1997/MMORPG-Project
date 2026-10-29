using GameServer.Entities;
using GameServer.Skills;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 3.5, 9.6; WP-M2-5): a player's skill-tree answers, then its <see cref="SkillTreeState"/> (class, learned
/// nodes and hotbar as committed) when it changed or the client is new. Reliable channel, MemoryPack, never the hot
/// snapshot; an accepted operation is answered only once its save committed (rule 5), right before the state that shows it.
/// </summary>
internal sealed class SkillTreeSender
{
    private readonly ITransport _transport;

    public SkillTreeSender(ITransport transport)
    {
        _transport = transport;
    }

    public long StatesSent { get; private set; }

    public void Send(Player player, SkillTreeService? skills)
    {
        foreach (var answer in player.SkillTreeAnswers)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.SkillTreeResult, answer));
        player.SkillTreeAnswers.Clear();
        if (!player.SkillTreeDirty || skills?.Visible(player.CharacterId) is not { } state)
            return;
        player.SkillTreeDirty = false;
        var nodes = new List<SkillNodeEntry>(state.LearnedCount);
        foreach (var pair in state.Learned)
            nodes.Add(new SkillNodeEntry { NodeId = pair.Key, Level = (byte)Math.Min(pair.Value, byte.MaxValue) });
        var hotbar = new List<HotbarEntry>();
        for (int slot = 1; slot <= Shared.Data.Tables.ActionBindings.MaxSkillSlots; slot++)
        {
            string node = state.NodeAt(slot);
            if (node.Length != 0)
                hotbar.Add(new HotbarEntry { Slot = (byte)slot, NodeId = node });
        }
        var message = new SkillTreeState
        {
            Version = ++player.SkillTreeVersion,
            ClassId = state.ClassId,
            Nodes = nodes.ToArray(),
            Hotbar = hotbar.ToArray(),
            Cause = player.SkillTreeCause,
        };
        player.SkillTreeCause = SkillTreeCause.Initial;
        _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.SkillTreeState, message));
        StatesSent++;
    }
}
