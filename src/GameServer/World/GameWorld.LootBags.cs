using GameServer.Entities;
using GameServer.Items.LootBags;
using GameServer.Net;
using GameServer.Persistence;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;

namespace GameServer.World;

/// <summary>
/// Death loot bags in the world (spec 4.3 R-4.3-03, 4.4 R-4.4-01; WP-M4-9): <see cref="DeathLootService"/> drops a dead player's
/// Normal bag where the region says so, answers the bag requests in phase 1, closes expired bags in phase 8 (off the tick) and
/// applies the results in phase 1, and phase 10 sends the answers, the open bag's contents and the own bags. Without
/// <see cref="EnableLootBags"/> (worlds built for tests, a config without the bag tables) nothing drops.
/// </summary>
public sealed partial class GameWorld
{
    private DeathLootService? _lootBags;
    private IVisibilityQuery _visibilityQuery = EveryoneVisible.Instance;

    /// <summary>The death loot bags of this world (WP-M4-9); null until <see cref="EnableLootBags"/>.</summary>
    public DeathLootService? LootBags => _lootBags;

    /// <summary>
    /// Turns death loot bags on (before the <c>SessionManager</c> is built, which wires their saves) with the bags still open in
    /// the database (each spawned again; empty in tests) and the expiry's store (null: expired bags close in memory). Needs the
    /// item tables.
    /// </summary>
    public void EnableLootBags(LootBagConfig config, IEnumerable<LootBagRecord>? open = null, ILootBagExpirer? expirer = null)
    {
        if (_inventory == null || _lootBags != null)
            return;
        _lootBags = new DeathLootService(config, _inventory.Rules, _inventory, new LootBagWorld(this), _events, _runId,
            () => _clock.UtcNowMs)
        {
            Mail = new LootBagMail(this),
            Expirer = expirer,
        };
        if (_events != null)
            _lootBags.Subscribe(_events);
        if (open != null)
            _lootBags.Load(open);
    }

    /// <summary>Phase 1: one loot bag request (open, take one, take all, close).</summary>
    private void HandleLootBagRequest(Player player, in ClientRequest request)
    {
        var op = (LootBagOp)request.FromSlot;
        if (_lootBags == null)
        {
            player.LootBags.Messages.Add(ReliableMessages.Write(ReliableMessageId.LootBagResult, new LootBagResult
            {
                RequestId = request.RequestId, BagEntityId = request.DropEntityId, Op = op, Reason = LootBagReason.BagNotFound,
            }));
            return;
        }
        _lootBags.Request(player, request.RequestId, request.DropEntityId, op, request.ToSlot);
    }

    /// <summary>Phase 10: answers in order, then the open bag's contents after a change, then the own bags when they changed.</summary>
    private void SendLootBags(Player player)
    {
        var answers = player.LootBags;
        foreach (var message in answers.Messages)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, message);
        answers.Messages.Clear();
        if (_lootBags == null)
            return;
        if (_lootBags.ContentFor(player) is { } content)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.LootBagContent, content));
        if (answers.OwnBagsDirty)
        {
            answers.OwnBagsDirty = false;
            _transport.Send(player.PeerId, Channel.ReliableOrdered,
                ReliableMessages.Write(ReliableMessageId.OwnLootBags, _lootBags.OwnBags(player)));
        }
    }

    /// <summary>Adds and removes bag entities like ground drops (entity list and grid) and answers the service's questions.</summary>
    private sealed class LootBagWorld(GameWorld world) : ILootBagWorld
    {
        public Entity? FindEntity(uint id) => world.FindEntity(id);

        public Player? PlayerOf(long characterId) => world._playersByCharacter.TryGetValue(characterId, out var player) ? player : null;

        public (string RegionId, string AreaType, RegionFlags Flags)? RegionOf(Entity entity) =>
            world._map.RegionByIndex(entity.RegionIndex) is { } region ? (region.Id, region.AreaType, region.Flags) : null;

        public LootBag AddBag(string bagId, int xCm, int yCm, int heightCm)
        {
            var position = FixVec2.FromCm(xCm, yCm);
            var bag = new LootBag(world._nextEntityId++, bagId, new MovementState(position, 0), heightCm)
            {
                RegionIndex = world._map.Grid.RegionIndexAt(position),
                SpeedCheckedPosition = position,
            };
            world._entities.Add(bag); // ids only grow, so the list stays in id order
            world._grid.Add(bag);
            return bag;
        }

        public void RemoveBag(LootBag bag)
        {
            world._grid.Remove(bag);
            world._entities.Remove(bag);
        }

        public bool CanSee(Player observer, Entity target) => world._visibilityQuery.CanSee(observer, target);

        public bool SameParty(long a, long b) => world.PartyQuery.SameParty(a, b);
    }

    /// <summary>The world's mail service for a take (null mail: nothing can be sent).</summary>
    private sealed class LootBagMail(GameWorld world) : ILootBagMail
    {
        public int AttachmentsPerMail => world._mail?.Rules.Settings.AttachmentsPerMail ?? ReliableMessages.MaxMailAttachments;

        public bool CanSend(long carrier, MailDraft draft) => world._mail?.CanSend(carrier, draft) ?? false;

        public bool Send(long carrier, MailDraft draft) => world._mail?.Send(carrier, draft).Accepted ?? false;
    }
}
