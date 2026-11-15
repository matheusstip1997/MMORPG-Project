using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Social;

namespace GameServer.Social;

/// <summary>What one social request asks for, as the world reads it in phase 1.</summary>
public readonly record struct SocialAsk(uint RequestId, SocialAction Action, uint Ref, string Name, string OpKey);

/// <summary>
/// The only writer of friends and the block list (spec 6.5 "M4 social", R-6.5-01; M4 decision 16; WP-M4-16; architecture rule 4),
/// for every character this world holds, in memory on the world thread.
/// <para>
/// The rules (<see cref="SocialRules"/>; answers to the WP-M4-16 plan, 2026-11-15): a friendship is mutual, asked by display
/// name of an online player (<c>GameWorld.FindPlayerByName</c>, rule 8) and accepted by that player; the request lives in memory
/// (<c>SocialSettings.requestTimeoutMs</c>, and ends when either side leaves the world). A mute hides the target's chat lines; a
/// block also refuses the target's whispers, party, trade and friend requests (read through <see cref="ISocialQuery"/>) and ends
/// a friendship; both persist. The names in the lists are the names <see cref="DisplayIdentity"/> gave the owner at the time, and
/// the wire knows an entry only by a per-session ref: no character id, entity, region or position of a friend reaches a client.
/// </para>
/// <para>
/// Persistence (rule 5): the memory holds committed state only. An accepted change becomes an operation in the requester's
/// character save (<see cref="ISocialBatchSource"/>; a friendship in the save of the one who accepted), saved at once; only once
/// that save committed is the change applied to both sides' lists and the requester answered. Its operation key (one per world
/// run, entity, connection and request id) answers a repeated request with the first answer (double submit). While a pair has an
/// operation in flight, a new one on the same pair waits (refused <see cref="SocialReason.SocialRequestPending"/>), so two saves
/// of different characters never race on one row.
/// </para>
/// </summary>
public sealed class SocialService : ISocialQuery, ISocialBatchSource
{
    /// <summary>Answered operation keys kept per character for double submits (the newest ones).</summary>
    internal const int SeenCapacity = 64;

    private readonly SocialSettings _settings;
    private readonly EventBus? _events;
    private readonly Dictionary<long, CharacterSocial> _characters = new();
    private readonly Dictionary<long, List<PendingOp>> _pending = new();
    private readonly Dictionary<long, InFlightBatch> _inFlight = new();
    private readonly HashSet<(long, long)> _busyPairs = new();
    private readonly HashSet<(long, long)> _busyBlocks = new();
    private readonly List<FriendRequest> _requests = new();

    public SocialService(SocialSettings settings, EventBus? events)
    {
        _settings = settings;
        _events = events;
    }

    public SocialSettings Settings => _settings;

    /// <summary>The world's player of a character; null when it is not in the world (set by the world).</summary>
    public Func<long, Player?> PlayerOf { get; set; } = _ => null;

    /// <summary>The connected player whose display name, as the requester sees it, is the name; null for none (set by the world).</summary>
    public Func<Player, string, Player?> FindByName { get; set; } = (_, _) => null;

    /// <summary>Asks for a save at the next tick (set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>Friend requests waiting for an answer (tests, telemetry).</summary>
    public int RequestCount => _requests.Count;

    public int Count => _characters.Count;

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    /// <summary>
    /// Starts holding a character's lists as loaded at login (committed). A friend online here whose own list no longer holds this
    /// character (it ended the friendship while this one was loading) is dropped from the loaded list.
    /// </summary>
    public void Attach(long characterId, SocialRecord record)
    {
        if (_characters.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already attached");
        DropOps(characterId); // a faulted save of an earlier session left nothing for this one
        var c = new CharacterSocial();
        foreach (var row in record.Friends)
        {
            if (row.OtherId == characterId)
                continue;
            if (_characters.TryGetValue(row.OtherId, out var other) && !other.Friends.ContainsKey(characterId)
                && !_busyPairs.Contains(Pair(characterId, row.OtherId)))
                continue;
            c.Friends[row.OtherId] = new Friend(++c.NextRef, row.Name);
        }
        foreach (var row in record.Blocks)
        {
            if (row.TargetId != characterId && row.Kind is SocialBlockKind.Mute or SocialBlockKind.Block)
                c.Blocks[row.TargetId] = new Block(++c.NextRef, row.Name, row.Kind);
        }
        _characters.Add(characterId, c);
        PresenceChanged(characterId);
    }

    /// <summary>The character left the world (its last save is in, or it faulted): its requests end, its friends see it offline.</summary>
    public void Detach(long characterId)
    {
        if (!_characters.TryGetValue(characterId, out var c))
            return;
        for (int i = _requests.Count - 1; i >= 0; i--)
        {
            var request = _requests[i];
            if (request.From != characterId && request.To != characterId)
                continue;
            _requests.RemoveAt(i);
            if (request.To == characterId)
                Notice(request.From, SocialNotice.RequestExpired, request.ToName); // the asker hears that it ended
            else
                MarkDirty(request.To);
        }
        DropOps(characterId);
        _characters.Remove(characterId);
        foreach (var friendId in c.Friends.Keys)
            MarkDirty(friendId);
    }

    /// <summary>The character connected or disconnected: its online friends' lists show it at once.</summary>
    public void PresenceChanged(long characterId)
    {
        if (!_characters.TryGetValue(characterId, out var c))
            return;
        foreach (var friendId in c.Friends.Keys)
            MarkDirty(friendId);
    }

    public bool HidesChatFrom(long recipientId, long senderId) =>
        _characters.TryGetValue(recipientId, out var c) && c.Blocks.TryGetValue(senderId, out var b) && SocialRules.HidesChat(b.Kind);

    public bool Blocks(long blockerId, long targetId) =>
        _characters.TryGetValue(blockerId, out var c) && c.Blocks.TryGetValue(targetId, out var b) && SocialRules.RefusesRequests(b.Kind);

    /// <summary>Phase 1, before the requests: friend requests past their time end; the asker hears it.</summary>
    public void Sweep(long nowMs)
    {
        for (int i = _requests.Count - 1; i >= 0; i--)
        {
            var request = _requests[i];
            if (request.ExpiresAtMs > nowMs)
                continue;
            _requests.RemoveAt(i);
            Notice(request.From, SocialNotice.RequestExpired, request.ToName);
            MarkDirty(request.To);
        }
    }

    /// <summary>
    /// One request of <paramref name="player"/> (phase 1). Returns the answer to send now (a refusal, a request sent, a decline,
    /// a change that changes nothing, or a repeat of an answered key), or null when an operation waits for its save (answered once
    /// it committed) or the key repeats such a waiting one.
    /// </summary>
    public SocialResult? Handle(Player player, in SocialAsk ask, long nowMs)
    {
        if (!_characters.TryGetValue(player.CharacterId, out var c))
            return Result(ask, SocialReason.SocialUnavailable);
        if (c.TryGetSeen(ask.OpKey, out var seen))
            return seen;
        if (c.Waiting.Contains(ask.OpKey))
            return null;
        var answer = ask.Action switch
        {
            SocialAction.AddFriend => AddFriend(player, c, ask, nowMs),
            SocialAction.AcceptFriend => Accept(player, c, ask, FindIncoming(player.CharacterId, ask.Ref)),
            SocialAction.DeclineFriend => Decline(player, ask),
            SocialAction.RemoveFriend => RemoveFriend(player, c, ask),
            SocialAction.Block => SetBlock(player, c, ask, SocialBlockKind.Block),
            SocialAction.Mute => SetBlock(player, c, ask, SocialBlockKind.Mute),
            SocialAction.Unblock => Unblock(player, c, ask),
            _ => Result(ask, SocialReason.SocialEntryNotFound),
        };
        if (answer != null)
            c.Remember(ask.OpKey, answer);
        return answer;
    }

    /// <summary>The recipient's lists as its client shows them: the friends with their online state, the requests, the blocks.</summary>
    public SocialState BuildState(long characterId, long nowMs)
    {
        var state = new SocialState();
        if (!_characters.TryGetValue(characterId, out var c))
            return state;
        var friends = new SocialFriendEntry[Math.Min(c.Friends.Count, ReliableMessages.MaxSocialFriends)];
        int n = 0;
        foreach (var pair in c.Friends.OrderBy(p => p.Value.Ref)) // the order they were added (rule 6)
        {
            if (n == friends.Length)
                break;
            friends[n++] = new SocialFriendEntry { Ref = pair.Value.Ref, Name = pair.Value.Name, Online = IsShownOnline(pair.Key, characterId) };
        }
        var incoming = new List<SocialIncomingEntry>();
        foreach (var request in _requests)
        {
            if (request.To != characterId || incoming.Count == ReliableMessages.MaxSocialIncoming)
                continue;
            incoming.Add(new SocialIncomingEntry
            {
                Ref = request.Ref, Name = request.FromName, ExpiresInMs = (uint)Math.Clamp(request.ExpiresAtMs - nowMs, 0, uint.MaxValue),
            });
        }
        var blocks = new SocialBlockEntry[Math.Min(c.Blocks.Count, ReliableMessages.MaxSocialBlocks)];
        n = 0;
        foreach (var block in c.Blocks.Values.OrderBy(b => b.Ref))
        {
            if (n == blocks.Length)
                break;
            blocks[n++] = new SocialBlockEntry { Ref = block.Ref, Name = block.Name, Kind = block.Kind };
        }
        state.Friends = friends;
        state.Incoming = incoming.ToArray();
        state.Blocks = blocks;
        return state;
    }

    /// <summary>
    /// What a friend's online state shows to <paramref name="observerId"/>: connected to this world, nothing else (no region, no
    /// position; a lingering, disconnected character is offline). TODO(open) M5: whether a disguised friend shows online (spec
    /// 6.2 R-6.2-04 names party and guild members only); decide it with the disguise robe, here and nowhere else.
    /// <paramref name="observerId"/> is unused until then: it is that M5 hook (the answer may depend on who looks).
    /// </summary>
    internal bool IsShownOnline(long friendId, long observerId) =>
        _characters.ContainsKey(friendId) && PlayerOf(friendId) is { IsConnected: true };

    public SocialBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_pending.TryGetValue(characterId, out var ops) || ops.Count == 0)
            return null;
        if (_inFlight.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} already has a social batch in flight");
        _pending.Remove(characterId);
        _inFlight[characterId] = new InFlightBatch(saveId, ops);
        return new SocialBatch(ops.Select(op => op.Row).ToList());
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_inFlight.TryGetValue(characterId, out var inFlight) || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        _inFlight.Remove(characterId);
        foreach (var op in inFlight.Ops)
        {
            Apply(op.Row);
            Release(op.Row);
            if (_characters.TryGetValue(characterId, out var c))
            {
                c.Waiting.Remove(op.OpKey);
                if (op.Answer != null)
                    c.Remember(op.OpKey, op.Answer);
            }
            if (op.Answer != null && PlayerOf(characterId) is { } player && player.Connection == op.Connection)
                player.Social.Results.Add(op.Answer);
            if (op.NoticeToOther != null)
                Notice(op.Row.OtherId, op.NoticeToOther.Notice, op.NoticeToOther.Name);
            _events?.Publish(new SocialChanged(op.Row.CharacterId, op.Row.OtherId, op.Row.Kind, op.Row.BlockKind), characterId,
                PlayerOf(characterId)?.RegionIndex ?? 0);
        }
    }

    private SocialResult? AddFriend(Player player, CharacterSocial c, in SocialAsk ask, long nowMs)
    {
        if (FindTarget(player, ask.Name, out var reason) is not { } target)
            return Result(ask, reason);
        long me = player.CharacterId, them = target.CharacterId;
        var tc = _characters[them];
        // The target asked first: this add accepts its request (a friendship is mutual), unless the requester mutes or blocks it.
        if (OwnEntry(c, them) == SocialBlockKind.None)
        {
            foreach (var request in _requests)
            {
                if (request.From == them && request.To == me)
                    return Accept(player, c, ask, request);
            }
        }
        bool waiting = _busyPairs.Contains(Pair(me, them)) || HasRequest(me, them);
        reason = SocialRules.CheckRequest(OwnEntry(c, them), OwnEntry(tc, me), c.Friends.ContainsKey(them), waiting,
            FriendCount(c), FriendCount(tc), IncomingCount(them), _settings.MaxFriends, _settings.MaxIncomingRequests);
        if (reason != SocialReason.None)
            return Result(ask, reason);
        string toName = DisplayIdentity.For(target, player);
        string fromName = DisplayIdentity.For(player, target);
        _requests.Add(new FriendRequest(++tc.NextRef, me, them, fromName, toName, nowMs + _settings.RequestTimeoutMs));
        Notice(them, SocialNotice.RequestReceived, fromName);
        MarkDirty(them);
        return Result(ask, SocialReason.None, SocialNotice.RequestSent, toName);
    }

    private SocialResult? Accept(Player player, CharacterSocial c, in SocialAsk ask, FriendRequest? request)
    {
        if (request == null)
            return Result(ask, SocialReason.SocialEntryNotFound);
        long me = player.CharacterId, them = request.From;
        if (!_characters.TryGetValue(them, out var rc) || PlayerOf(them) is not { } requester)
        {
            _requests.Remove(request);
            MarkDirty(me);
            return Result(ask, SocialReason.SocialRequestExpired);
        }
        // A friendship, or a mute or block of either side by the other, still saving: the accept waits (rule 5, committed state).
        if (_busyPairs.Contains(Pair(me, them)) || _busyBlocks.Contains((me, them)) || _busyBlocks.Contains((them, me)))
            return Result(ask, SocialReason.SocialRequestPending);
        // Checked again (review of PR #257): a mute or block committed since the request was made forbids it as an add would.
        var reason = SocialRules.CheckAccept(OwnEntry(c, them), OwnEntry(rc, me), FriendCount(c), FriendCount(rc), _settings.MaxFriends);
        if (reason != SocialReason.None)
            return Result(ask, reason);
        _requests.Remove(request);
        MarkDirty(me);
        string otherName = DisplayIdentity.For(requester, player);
        string ownName = DisplayIdentity.For(player, requester);
        c.PendingFriends++;
        rc.PendingFriends++;
        Enqueue(player, ask, new SocialOpRow(SocialOpKind.FriendAdd, me, them, otherName, ownName),
            Result(ask, SocialReason.None, SocialNotice.FriendAdded, otherName), new NoticeTo(SocialNotice.FriendAdded, ownName));
        return null;
    }

    private SocialResult Decline(Player player, in SocialAsk ask)
    {
        if (FindIncoming(player.CharacterId, ask.Ref) is not { } request)
            return Result(ask, SocialReason.SocialEntryNotFound);
        _requests.Remove(request);
        MarkDirty(player.CharacterId);
        if (PlayerOf(request.From) is { } requester)
            Notice(request.From, SocialNotice.RequestDeclined, DisplayIdentity.For(player, requester));
        return Result(ask, SocialReason.None);
    }

    private SocialResult? RemoveFriend(Player player, CharacterSocial c, in SocialAsk ask)
    {
        long them = FindFriend(c, ask);
        if (them == 0)
            return Result(ask, SocialReason.SocialEntryNotFound);
        if (_busyPairs.Contains(Pair(player.CharacterId, them)))
            return Result(ask, SocialReason.SocialRequestPending);
        Enqueue(player, ask, new SocialOpRow(SocialOpKind.FriendRemove, player.CharacterId, them), Result(ask, SocialReason.None), null);
        return null;
    }

    private SocialResult? SetBlock(Player player, CharacterSocial c, in SocialAsk ask, SocialBlockKind wanted)
    {
        if (FindTarget(player, ask.Name, out var reason) is not { } target)
            return Result(ask, reason);
        long me = player.CharacterId, them = target.CharacterId;
        bool endsFriendship = wanted == SocialBlockKind.Block && c.Friends.ContainsKey(them);
        if (_busyBlocks.Contains((me, them)) || (wanted == SocialBlockKind.Block && _busyPairs.Contains(Pair(me, them))))
            return Result(ask, SocialReason.SocialRequestPending);
        var current = OwnEntry(c, them);
        reason = SocialRules.CheckBlock(current, wanted, c.Blocks.Count + c.PendingBlocks, _settings.MaxBlocks, out bool changes);
        if (reason != SocialReason.None)
            return Result(ask, reason);
        if (!changes)
            return Result(ask, SocialReason.None); // already so: nothing to write
        if (wanted == SocialBlockKind.Block)
            DropRequestsBetween(me, them); // a block ends what is waiting between the two (again at its commit, in Apply)
        if (current == SocialBlockKind.None)
            c.PendingBlocks++;
        var answer = Result(ask, SocialReason.None);
        if (endsFriendship)
            Enqueue(player, ask, new SocialOpRow(SocialOpKind.FriendRemove, me, them), null, null);
        Enqueue(player, ask, new SocialOpRow(SocialOpKind.BlockSet, me, them, DisplayIdentity.For(target, player), BlockKind: wanted),
            answer, null);
        return null;
    }

    private SocialResult? Unblock(Player player, CharacterSocial c, in SocialAsk ask)
    {
        long them = FindBlock(c, ask);
        if (them == 0)
            return Result(ask, SocialReason.SocialEntryNotFound);
        if (_busyBlocks.Contains((player.CharacterId, them)))
            return Result(ask, SocialReason.SocialRequestPending);
        Enqueue(player, ask, new SocialOpRow(SocialOpKind.BlockRemove, player.CharacterId, them), Result(ask, SocialReason.None), null);
        return null;
    }

    /// <summary>The online target of an add, a mute or a block, by display name (the one name path, rule 8); null with the reason.</summary>
    private Player? FindTarget(Player player, string name, out SocialReason reason)
    {
        var target = string.IsNullOrWhiteSpace(name) ? null : FindByName(player, name.Trim());
        if (target == null || !_characters.ContainsKey(target.CharacterId))
        {
            reason = SocialReason.SocialTargetNotFound;
            return null;
        }
        if (target.CharacterId == player.CharacterId)
        {
            reason = SocialReason.SocialSelf;
            return null;
        }
        reason = SocialReason.None;
        return target;
    }

    private void Enqueue(Player player, in SocialAsk ask, SocialOpRow row, SocialResult? answer, NoticeTo? notice)
    {
        if (!_pending.TryGetValue(player.CharacterId, out var ops))
            _pending[player.CharacterId] = ops = new List<PendingOp>();
        ops.Add(new PendingOp(row, answer, notice, ask.OpKey, player.Connection));
        if (row.Kind is SocialOpKind.FriendAdd or SocialOpKind.FriendRemove)
            _busyPairs.Add(Pair(row.CharacterId, row.OtherId));
        else
            _busyBlocks.Add((row.CharacterId, row.OtherId));
        if (answer != null)
        {
            _characters[player.CharacterId].Waiting.Add(ask.OpKey);
            RequestSave?.Invoke(player.CharacterId, SaveReason.Social);
        }
    }

    /// <summary>A committed operation: both sides' lists change (whichever are in the world) and their clients hear of it.</summary>
    private void Apply(SocialOpRow row)
    {
        _characters.TryGetValue(row.CharacterId, out var own);
        _characters.TryGetValue(row.OtherId, out var other);
        switch (row.Kind)
        {
            case SocialOpKind.FriendAdd:
                if (own != null)
                {
                    own.PendingFriends = Math.Max(0, own.PendingFriends - 1);
                    own.Friends[row.OtherId] = new Friend(++own.NextRef, row.OtherName);
                }
                if (other != null)
                {
                    other.PendingFriends = Math.Max(0, other.PendingFriends - 1);
                    other.Friends[row.CharacterId] = new Friend(++other.NextRef, row.OwnName);
                }
                MarkDirty(row.OtherId);
                break;
            case SocialOpKind.FriendRemove:
                own?.Friends.Remove(row.OtherId);
                other?.Friends.Remove(row.CharacterId);
                MarkDirty(row.OtherId);
                break;
            case SocialOpKind.BlockSet:
                if (own != null)
                {
                    if (own.Blocks.TryGetValue(row.OtherId, out var existing))
                        own.Blocks[row.OtherId] = existing with { Kind = row.BlockKind, Name = row.OtherName };
                    else
                    {
                        own.PendingBlocks = Math.Max(0, own.PendingBlocks - 1);
                        own.Blocks[row.OtherId] = new Block(++own.NextRef, row.OtherName, row.BlockKind);
                    }
                }
                // A committed block ends every request between the two, also one made while it was saving (review of PR #257).
                if (row.BlockKind == SocialBlockKind.Block)
                    DropRequestsBetween(row.CharacterId, row.OtherId);
                break;
            case SocialOpKind.BlockRemove:
                own?.Blocks.Remove(row.OtherId);
                break;
        }
        MarkDirty(row.CharacterId);
    }

    /// <summary>The pair or block an operation held is free again.</summary>
    private void Release(SocialOpRow row)
    {
        if (row.Kind is SocialOpKind.FriendAdd or SocialOpKind.FriendRemove)
            _busyPairs.Remove(Pair(row.CharacterId, row.OtherId));
        else
            _busyBlocks.Remove((row.CharacterId, row.OtherId));
    }

    /// <summary>Operations of the character no save will commit any more (it left or faulted): their pairs are free again.</summary>
    private void DropOps(long characterId)
    {
        if (_pending.Remove(characterId, out var pending))
            Undo(pending);
        if (_inFlight.Remove(characterId, out var inFlight))
            Undo(inFlight.Ops);
    }

    private void Undo(List<PendingOp> ops)
    {
        foreach (var op in ops)
        {
            Release(op.Row);
            if (op.Row.Kind == SocialOpKind.FriendAdd)
            {
                if (_characters.TryGetValue(op.Row.CharacterId, out var own))
                    own.PendingFriends = Math.Max(0, own.PendingFriends - 1);
                if (_characters.TryGetValue(op.Row.OtherId, out var other))
                    other.PendingFriends = Math.Max(0, other.PendingFriends - 1);
            }
            else if (op.Row.Kind == SocialOpKind.BlockSet && _characters.TryGetValue(op.Row.CharacterId, out var own)
                     && !own.Blocks.ContainsKey(op.Row.OtherId))
                own.PendingBlocks = Math.Max(0, own.PendingBlocks - 1);
        }
    }

    private void Notice(long characterId, SocialNotice notice, string name)
    {
        if (PlayerOf(characterId) is { IsConnected: true } player)
            player.Social.Results.Add(new SocialResult { RequestId = 0, Action = SocialAction.None, Notice = notice, Name = name });
    }

    private void MarkDirty(long characterId)
    {
        if (PlayerOf(characterId) is { } player)
            player.Social.Dirty = true;
    }

    /// <summary>Ends the friend requests either way between two characters (in memory only); the target's list shows it.</summary>
    private void DropRequestsBetween(long a, long b)
    {
        for (int i = _requests.Count - 1; i >= 0; i--)
        {
            var request = _requests[i];
            if ((request.From == a && request.To == b) || (request.From == b && request.To == a))
            {
                _requests.RemoveAt(i);
                MarkDirty(request.To);
            }
        }
    }

    private FriendRequest? FindIncoming(long characterId, uint reference)
    {
        foreach (var request in _requests)
        {
            if (request.To == characterId && request.Ref == reference)
                return request;
        }
        return null;
    }

    private bool HasRequest(long a, long b)
    {
        foreach (var request in _requests)
        {
            if ((request.From == a && request.To == b) || (request.From == b && request.To == a))
                return true;
        }
        return false;
    }

    private int IncomingCount(long characterId)
    {
        int count = 0;
        foreach (var request in _requests)
        {
            if (request.To == characterId)
                count++;
        }
        return count;
    }

    private static long FindFriend(CharacterSocial c, in SocialAsk ask)
    {
        foreach (var pair in c.Friends)
        {
            if (ask.Ref != 0 ? pair.Value.Ref == ask.Ref : string.Equals(pair.Value.Name, ask.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                return pair.Key;
        }
        return 0;
    }

    private static long FindBlock(CharacterSocial c, in SocialAsk ask)
    {
        foreach (var pair in c.Blocks)
        {
            if (ask.Ref != 0 ? pair.Value.Ref == ask.Ref : string.Equals(pair.Value.Name, ask.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                return pair.Key;
        }
        return 0;
    }

    private static SocialBlockKind OwnEntry(CharacterSocial c, long target) =>
        c.Blocks.TryGetValue(target, out var block) ? block.Kind : SocialBlockKind.None;

    private static int FriendCount(CharacterSocial c) => c.Friends.Count + c.PendingFriends;

    private static (long, long) Pair(long a, long b) => a < b ? (a, b) : (b, a);

    private static SocialResult Result(in SocialAsk ask, SocialReason reason, SocialNotice notice = SocialNotice.None, string name = "") =>
        new() { RequestId = ask.RequestId, Action = ask.Action, Reason = reason, Notice = notice, Name = name };

    private sealed record Friend(uint Ref, string Name);

    private sealed record Block(uint Ref, string Name, SocialBlockKind Kind);

    /// <summary>A friend request in memory: <see cref="Ref"/> is in the target's ref space; the names as each side sees the other.</summary>
    private sealed record FriendRequest(uint Ref, long From, long To, string FromName, string ToName, long ExpiresAtMs);

    private sealed record NoticeTo(SocialNotice Notice, string Name);

    private sealed record PendingOp(SocialOpRow Row, SocialResult? Answer, NoticeTo? NoticeToOther, string OpKey, int Connection);

    private sealed record InFlightBatch(Guid SaveId, List<PendingOp> Ops);

    private sealed class CharacterSocial
    {
        public Dictionary<long, Friend> Friends { get; } = new();
        public Dictionary<long, Block> Blocks { get; } = new();
        public HashSet<string> Waiting { get; } = new(StringComparer.Ordinal);
        public uint NextRef;
        public int PendingFriends;
        public int PendingBlocks;
        private readonly Dictionary<string, SocialResult> _seen = new(StringComparer.Ordinal);
        private readonly Queue<string> _seenOrder = new();

        public bool TryGetSeen(string key, out SocialResult result) => _seen.TryGetValue(key, out result!);

        public void Remember(string key, SocialResult result)
        {
            if (_seen.ContainsKey(key))
                return;
            if (_seenOrder.Count == SeenCapacity)
                _seen.Remove(_seenOrder.Dequeue());
            _seen[key] = result;
            _seenOrder.Enqueue(key);
        }
    }
}
