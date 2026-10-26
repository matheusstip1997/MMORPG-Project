#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's view of its bag, pickups and bag moves (backlog G; the inventory window of WP16 shows it): the last
    /// <see cref="InventoryContents"/> the server sent (the bag as saved, rule 5), the answers to pickup and move requests,
    /// which ground drop a pickup press asks for, and the request ids of bag moves (one counter per connection: the server
    /// scopes them by connection). Text only from the tables (spec 9.1): item names from <c>UiStrings</c>
    /// through <c>ItemDef.nameId</c>, refusals from <c>ReasonCodeStrings</c>. The server checks everything again.
    /// </summary>
    public sealed class ClientInventory
    {
        private readonly Dictionary<ushort, string> _names = new Dictionary<ushort, string>();
        private readonly Dictionary<string, string> _ui = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly long _pickupRangeSq;

        public ClientInventory(ConfigSet config)
        {
            foreach (var row in Rows<UiStrings>(config))
                _ui[row.Id] = row.Text;
            foreach (var row in Rows<ItemDef>(config))
                _names[(ushort)row.NetId] = _ui.TryGetValue(row.NameId, out var name) ? name : row.Id;
            foreach (var row in Rows<ReasonCodeStrings>(config))
                _reasons[row.Code] = row.Text;
            long range = config.TryGet(typeof(LootSettings), out var settings) && settings.Rows.Count == 1
                ? ((LootSettings)settings.Rows[0]).PickupRangeCm
                : 0;
            _pickupRangeSq = range * range;
        }

        /// <summary>The bag as last saved on the server; null before the first message.</summary>
        public InventoryContents? Contents { get; private set; }

        /// <summary>The last pickup answer, for logs and the QA run.</summary>
        public PickupResult? LastResult { get; private set; }

        /// <summary>The last bag move answer (WP16), for logs and the QA run.</summary>
        public InventoryMoveResult? LastMoveResult { get; private set; }

        /// <summary>Bag move requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint MoveRequestsSent { get; private set; }

        /// <summary>
        /// Takes the inventory messages of the reliable channel; false for any other message. <paramref name="log"/> says
        /// what happened in words for the dev log.
        /// </summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.InventoryContents:
                    if (!ReliableMessages.TryRead(message, out InventoryContents contents))
                    {
                        log = "dropped an unreadable inventory message";
                        return true;
                    }
                    Contents = contents;
                    log = Text();
                    return true;
                case ReliableMessageId.PickupResult:
                    if (!ReliableMessages.TryRead(message, out PickupResult result))
                    {
                        log = "dropped an unreadable pickup result";
                        return true;
                    }
                    LastResult = result;
                    log = result.Reason == PickupReason.None
                        ? $"picked up {result.Taken} from drop {result.DropEntityId}"
                        : ReasonText(result.Reason);
                    return true;
                case ReliableMessageId.InventoryMoveResult:
                    if (!ReliableMessages.TryRead(message, out InventoryMoveResult move))
                    {
                        log = "dropped an unreadable bag move result";
                        return true;
                    }
                    LastMoveResult = move;
                    log = move.Reason == InventoryMoveReason.None
                        ? $"bag move {move.RequestId}: {move.Outcome}"
                        : $"bag move {move.RequestId} refused: {Reason(move.Reason.ToString())}";
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A bag move request (WP16): move the stack in <paramref name="fromSlot"/> onto <paramref name="toSlot"/>, with the next
        /// request id of this connection. The server checks the slots and answers with an <see cref="InventoryMoveResult"/>.
        /// </summary>
        public byte[] MoveRequest(byte fromSlot, byte toSlot, out uint requestId)
        {
            requestId = ++MoveRequestsSent;
            return ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
                new InventoryMoveRequest { RequestId = requestId, FromSlot = fromSlot, ToSlot = toSlot });
        }

        /// <summary>The connection ended: the bag shown is stale and the next connection numbers its moves from 1 again.</summary>
        public void Reset()
        {
            Contents = null;
            LastResult = null;
            LastMoveResult = null;
            MoveRequestsSent = 0;
        }

        /// <summary>The nearest ground drop within the pickup range of the own position (ground cm), or 0 for none.</summary>
        public uint NearestDrop(EntityRegistry registry, int ownXCm, int ownYCm) =>
            NearestDrop(registry, ownXCm, ownYCm, ownXCm, ownYCm);

        /// <summary>
        /// The drop a pickup press asks for and the HUD's pickup prompt names (backlog G): the nearest ground drop (from the
        /// predicted position) within <c>LootSettings.pickupRangeCm</c>, the range the server checks, of both the predicted
        /// and the last server-confirmed own position (ground cm); 0 for none. The server checks the request against where
        /// it has the player by then, which lies on the way from the confirmed to the predicted position, and the range is
        /// a disc: in range at both ends means in range on that way, so the press is not refused for range while moving.
        /// Allocates nothing (read every frame).
        /// </summary>
        public uint NearestDrop(EntityRegistry registry, int predictedXCm, int predictedYCm, int confirmedXCm, int confirmedYCm)
        {
            uint best = 0;
            long bestSq = long.MaxValue;
            foreach (var entity in registry.EntityValues)
            {
                if (entity.Kind != EntityKind.LootDrop || !entity.Samples.TryNewest(out var sample))
                    continue;
                // Snapshot axes: X is ground X, Z is ground Y (SnapshotAxes).
                long dx = sample.XCm - predictedXCm, dy = sample.ZCm - predictedYCm;
                long sq = dx * dx + dy * dy;
                long cx = sample.XCm - confirmedXCm, cy = sample.ZCm - confirmedYCm;
                if (sq <= _pickupRangeSq && cx * cx + cy * cy <= _pickupRangeSq && (sq < bestSq || (sq == bestSq && entity.Id < best)))
                {
                    best = entity.Id;
                    bestSq = sq;
                }
            }
            return best;
        }

        /// <summary>The bag as one line: "Bag: Demon Horn x2, ..." (<c>UiStrings</c> dev.inventory).</summary>
        public string Text()
        {
            if (Contents == null || Contents.Slots.Length == 0)
                return Ui("dev.inventory_empty");
            var items = new StringBuilder();
            foreach (var slot in Contents.Slots)
            {
                if (items.Length > 0)
                    items.Append(", ");
                items.Append(_names.TryGetValue(slot.ItemNetId, out var name) ? name : "#" + slot.ItemNetId);
                items.Append(" x").Append(slot.Count);
            }
            return Ui("dev.inventory").Replace("{items}", items.ToString());
        }

        public string ReasonText(PickupReason reason) => Reason(reason.ToString());

        private string Reason(string code)
        {
            if (_reasons.TryGetValue(code, out var text))
                return text;
            return _reasons.TryGetValue(ReasonCodeStrings.UnknownCode, out var unknown) ? unknown.Replace("{code}", code) : code;
        }

        /// <summary>The <c>UiStrings</c> text, or the id itself when the row is missing (a table error, visible on screen).</summary>
        private string Ui(string id) => _ui.TryGetValue(id, out var text) ? text : id;

        private static List<T> Rows<T>(ConfigSet set)
        {
            var rows = new List<T>();
            if (set.TryGet(typeof(T), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((T)row);
            }
            return rows;
        }
    }
}
