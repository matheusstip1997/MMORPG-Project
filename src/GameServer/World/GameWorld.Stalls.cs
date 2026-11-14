using GameServer.Entities;
using GameServer.Events;
using GameServer.Market;
using GameServer.Net;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Items;

namespace GameServer.World;

/// <summary>
/// Player shop stalls in the world (spec 4.5 "M4 stalls", M4 decision 7; WP-M4-8): <see cref="StallService"/> holds the stalls,
/// one <see cref="ShopStall"/> entity each on a <c>stall</c> marker (the places, by ordinal). Phase 1 reads the stall requests
/// through the <c>StallSettings</c> rate limit; phase 8 closes stalls past their time, drops viewers out of reach and writes
/// closes off the tick; phase 10 sends the answers and the states of the stalls that changed (to their viewers and owner); phase
/// 11 hears the listing events. Without <see cref="EnableStalls"/> (worlds built for tests, a config without the tables, the
/// listings or mail) there are no stalls: every request is refused <c>InteractionUnavailable</c>.
/// </summary>
public sealed partial class GameWorld
{
    private StallService? _stalls;
    private readonly List<ShopStall> _stallsToRemove = new();
    private readonly List<ShopStall> _stallScratch = new();

    /// <summary>The stalls (WP-M4-8); null until <see cref="EnableStalls"/>.</summary>
    public StallService? Stalls => _stalls;

    /// <summary>
    /// Turns the stalls on (after <see cref="EnableListings"/>, before the <c>SessionManager</c> is built, which wires their saves).
    /// The places are this world's <c>stall</c> markers in ordinal order (<c>Shared.World.StallPlaces</c>). The caller loads the
    /// open stalls (<see cref="StallService.Load"/>) after the listings, before the world starts.
    /// </summary>
    public void EnableStalls(StallSettings settings, IShopStore? store)
    {
        if (_listings == null || _inventory == null || _escrow == null)
            return;
        var ids = Shared.World.StallPlaces.Ordered(_map.Markers.Select(m => (m.Id, m.Kind)));
        var places = new List<StallPlace>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            _map.TryGetMarker(ids[i], out var marker);
            places.Add(new StallPlace(i, marker!.Id, marker.Position, marker.Facing));
        }
        _stalls = new StallService(settings, _listings, _clock, _events, places)
        {
            Store = store,
            Spawn = SpawnStall,
            Despawn = stall => _stallsToRemove.Add(stall), // removed between phases, never inside an entity loop
            Answer = OnStallAnswer,
            Closed = OnStallClosed,
        };
        _listings.Answer = (characterId, connection, answer) =>
        {
            if (!_stalls.TryTakeListingAnswer(characterId, connection, answer))
                OnListingAnswer(characterId, connection, answer);
        };
        if (_events != null)
        {
            _events.Subscribe<ListingCreated>(_stalls);
            _events.Subscribe<ListingSold>(_stalls);
            _events.Subscribe<ListingCancelled>(_stalls);
            _events.Subscribe<ListingExpired>(_stalls);
        }
    }

    /// <summary>Phase 1: a stall request of <paramref name="player"/>; false when the request is not one.</summary>
    private bool HandleStallRequest(Player player, in ClientRequest request)
    {
        if (request.Kind != ReliableMessageId.StallRequest || request.Body is not StallRequest stall)
            return false;
        long now = _clock.UtcNowMs;
        if (_stalls == null)
        {
            AnswerStall(player, new StallResult { RequestId = stall.RequestId, Action = stall.Action, Reason = StallReason.InteractionUnavailable });
            return true;
        }
        var settings = _stalls.Settings;
        if (stall.Action != StallAction.Leave
            && !player.StallBucket.TryTake(now, settings.RequestBurst, settings.RequestsPerPeriod, settings.RequestPeriodMs))
        {
            AnswerStall(player, new StallResult { RequestId = stall.RequestId, Action = stall.Action, Reason = StallReason.StallRateLimited });
            return true;
        }
        var at = new InventoryLocation(stall.Container, stall.Slot);
        int x = player.XCm, y = player.YCm;
        switch (stall.Action)
        {
            case StallAction.Open:
                AnswerStall(player, _stalls.Open(AskOf(player, stall.RequestId, "stallOpen", now), player.Name, x, y, stall.Place, at,
                    stall.Count, stall.UnitPrice));
                break;
            case StallAction.Add:
                AnswerStall(player, _stalls.Add(AskOf(player, stall.RequestId, "stallAdd", now), x, y, stall.StallEntityId, at, stall.Count,
                    stall.UnitPrice));
                break;
            case StallAction.Cancel:
                AnswerStall(player, _stalls.Cancel(AskOf(player, stall.RequestId, "stallCancel", now), x, y, stall.StallEntityId, stall.ListingId));
                break;
            case StallAction.Close:
                AnswerStall(player, _stalls.Close(AskOf(player, stall.RequestId, "stallClose", now), x, y, stall.StallEntityId));
                break;
            case StallAction.Buy:
                AnswerStall(player, _stalls.Buy(AskOf(player, stall.RequestId, "stallBuy", now), x, y, stall.StallEntityId, stall.ListingId,
                    stall.Count, stall.UnitPrice));
                break;
            case StallAction.Browse:
                var refusal = _stalls.Browse(AskOf(player, stall.RequestId, "stallBrowse", now), x, y, stall.StallEntityId, out var open);
                if (refusal != null)
                    AnswerStall(player, refusal);
                else
                    player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.StallState, StallStateOf(open!, player.CharacterId)));
                break;
            case StallAction.Leave:
                _stalls.Leave(player.CharacterId, stall.StallEntityId);
                break;
        }
        return true;
    }

    /// <summary>Phase 8: stalls past their time close, viewers out of reach are dropped, closes are written off the tick.</summary>
    private void TickStalls()
    {
        if (_stalls == null)
            return;
        _stalls.Tick(_clock.UtcNowMs);
        DropFarViewers();
        RemoveClosedStalls();
    }

    /// <summary>Phase 10: the stalls that changed send their state to their viewers and to their owner when online.</summary>
    private void SendStalls()
    {
        if (_stalls == null)
            return;
        foreach (var stall in _stalls.TakeDirty(_stallScratch))
        {
            var viewers = _stalls.ViewersOf(stall);
            for (int i = 0; i < viewers.Count; i++)
            {
                if (_playersByCharacter.TryGetValue(viewers[i].CharacterId, out var viewer) && viewer.Connection == viewers[i].Connection)
                    viewer.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.StallState, StallStateOf(stall, viewer.CharacterId)));
            }
            if (_playersByCharacter.TryGetValue(stall.OwnerCharacterId, out var owner) && !IsViewer(viewers, owner))
                owner.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.StallState, StallStateOf(stall, owner.CharacterId)));
        }
    }

    /// <summary>Removes the entities of stalls that closed (queued by the service: never inside an entity loop).</summary>
    private void RemoveClosedStalls()
    {
        foreach (var stall in _stallsToRemove)
        {
            _grid.Remove(stall);
            _entities.Remove(stall);
        }
        _stallsToRemove.Clear();
    }

    private static bool IsViewer(IReadOnlyList<StallService.Viewer> viewers, Player player)
    {
        for (int i = 0; i < viewers.Count; i++)
            if (viewers[i].CharacterId == player.CharacterId && viewers[i].Connection == player.Connection)
                return true;
        return false;
    }

    private void DropFarViewers()
    {
        _stalls!.CopyOpen(_stallScratch);
        long range = _stalls!.Settings.InteractRangeCm;
        foreach (var stall in _stallScratch)
        {
            var viewers = _stalls.ViewersOf(stall);
            for (int i = viewers.Count - 1; i >= 0; i--)
            {
                var viewer = viewers[i];
                bool keep = _playersByCharacter.TryGetValue(viewer.CharacterId, out var player) && player.Connection == viewer.Connection
                            && Square(stall.XCm - (long)player.XCm) + Square(stall.YCm - (long)player.YCm) <= range * range;
                if (!keep)
                    _stalls.DropViewer(stall, viewer);
            }
        }
        _stallScratch.Clear();
    }

    private static long Square(long value) => value * value;

    private ShopStall SpawnStall(ShopRow row, StallPlace place)
    {
        var stall = new ShopStall(_nextEntityId++, row.ShopId, row.OwnerId, row.OwnerName, place.Ordinal, place.MarkerId, row.OpenedUtcMs,
            row.ClosesUtcMs, new MovementState(place.Position, place.Facing), _map.HeightCmAt(place.Position, _map.Grid.BaseHeightCm))
        {
            RegionIndex = _map.Grid.RegionIndexAt(place.Position),
            SpeedCheckedPosition = place.Position,
        };
        _entities.Add(stall); // ids only grow, so the list stays in id order
        _grid.Add(stall);
        return stall;
    }

    /// <summary>The stall as <paramref name="viewerId"/> sees it: its listings on sale, oldest first, never the seller.</summary>
    private StallState StallStateOf(ShopStall stall, long viewerId)
    {
        var rows = _listings!.ActiveForStall(stall.ShopId);
        var entries = new List<ListingEntry>(rows.Count);
        foreach (var row in rows)
        {
            if (ListingViews.Entry(_inventory!.Rules, row, _escrow!.Find(row.EscrowId)) is { } entry)
                entries.Add(entry);
        }
        return new StallState
        {
            StallEntityId = stall.Id,
            Place = (byte)stall.PlaceOrdinal,
            Own = stall.OwnerCharacterId == viewerId,
            Open = !_stalls!.IsClosing(stall),
            ClosesUtcMs = stall.ClosesUtcMs,
            Cap = (ushort)_stalls.Settings.ListingsPerStall,
            Rows = entries.Take(ReliableMessages.MaxStallRows).ToArray(),
        };
    }

    private void OnStallClosed(ShopStall stall, IReadOnlyList<StallService.Viewer> viewers)
    {
        var last = new StallState
        {
            StallEntityId = stall.Id, Place = (byte)stall.PlaceOrdinal, Open = false, ClosesUtcMs = stall.ClosesUtcMs,
            Cap = (ushort)_stalls!.Settings.ListingsPerStall,
        };
        foreach (var viewer in viewers)
        {
            if (_playersByCharacter.TryGetValue(viewer.CharacterId, out var player) && player.Connection == viewer.Connection)
            {
                last.Own = player.CharacterId == stall.OwnerCharacterId;
                player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.StallState, last));
            }
        }
        if (_playersByCharacter.TryGetValue(stall.OwnerCharacterId, out var owner) && !IsViewer(viewers, owner))
        {
            last.Own = true;
            owner.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.StallState, last));
        }
    }

    private void OnStallAnswer(long characterId, int connection, StallResult answer)
    {
        if (_playersByCharacter.TryGetValue(characterId, out var player) && player.Connection == connection)
            player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.StallResult, answer));
    }

    private static void AnswerStall(Player player, StallResult? answer)
    {
        if (answer != null)
            player.ListingMessages.Add(ReliableMessages.Write(ReliableMessageId.StallResult, answer));
    }
}
