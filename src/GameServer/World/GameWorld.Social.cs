using GameServer.Entities;
using GameServer.Net;
using GameServer.Social;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.World;

/// <summary>
/// Friends, mute and block in the world (spec 6.5 "M4 social", M4 decision 16; WP-M4-16): <see cref="SocialService"/> holds every
/// character's lists; the world reads the social requests in phase 1 (after sweeping expired friend requests), tells the service
/// when a character connects or disconnects (its friends see it online or offline), and sends the lists in phase 10. Chat, party
/// invites and trade invites read the mutes and blocks through <see cref="SocialQuery"/>. Without <see cref="SocialSettings"/>
/// (<see cref="EnableSocial"/> not called) nobody has friends or blocks (worlds built for tests).
/// </summary>
public sealed partial class GameWorld
{
    private SocialService? _social;
    private SocialSender? _socialSender;

    /// <summary>The world's friends and block lists (WP-M4-16); null until <see cref="EnableSocial"/>.</summary>
    public SocialService? Social => _social;

    /// <summary>What chat, parties and trades read of the mutes and blocks: none without <see cref="SocialSettings"/>.</summary>
    public ISocialQuery SocialQuery => (ISocialQuery?)_social ?? NoSocial.Instance;

    /// <summary>Social requests refused <c>SocialRateLimited</c> since the world started (telemetry <c>RequestCounters</c>).</summary>
    public long SocialRateLimited { get; private set; }

    /// <summary>
    /// Turns friends, mute and block on (WP-M4-16), before the sessions are built (they wire its saves and its login loads). Chat,
    /// the party and the trade window read its mutes and blocks from then on.
    /// </summary>
    public void EnableSocial(SocialSettings settings)
    {
        if (_social != null)
            throw new InvalidOperationException("social is enabled already");
        _social = new SocialService(settings, _events)
        {
            PlayerOf = id => _playersByCharacter.TryGetValue(id, out var player) ? player : null,
            FindByName = FindPlayerByName,
        };
        _socialSender = new SocialSender(_transport);
        if (_chat != null)
            _chat.Social = _social;
        _chatSender.Social = _social;
        if (_party != null)
            _party.Social = _social;
        if (_trades != null)
            _trades.Social = _social;
    }

    /// <summary>Phase 1: one social request of <paramref name="player"/>, answered now or once its save committed.</summary>
    private void HandleSocialRequest(Player player, in ClientRequest request)
    {
        var action = (SocialAction)request.FromSlot;
        if (_social == null)
        {
            player.Social.Results.Add(new SocialResult { RequestId = request.RequestId, Action = action, Reason = SocialReason.SocialUnavailable });
            return;
        }
        var settings = _social.Settings;
        if (!player.SocialBucket.TryTake(_clock.UtcNowMs, settings.Burst, settings.RequestsPerPeriod, settings.PeriodMs))
        {
            // Spec 7.5 rate limits: refused before anything is looked up.
            SocialRateLimited++; // RequestCounters telemetry
            player.Social.Results.Add(new SocialResult { RequestId = request.RequestId, Action = action, Reason = SocialReason.SocialRateLimited });
            return;
        }
        var ask = new SocialAsk(request.RequestId, action, request.DropEntityId, request.Text ?? string.Empty,
            SocialOpKey(player, request.RequestId));
        if (_social.Handle(player, ask, _clock.UtcNowMs) is { } answer)
            player.Social.Results.Add(answer);
    }

    /// <summary>The idempotency key of a social request: one per world run, entity, connection and request id (spec 9.13 pattern).</summary>
    internal string SocialOpKey(Player player, uint requestId) => $"social:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>Phase 10: social answers, notices and the lists of a connected player.</summary>
    private void SendSocial(Player player)
    {
        if (_social != null)
            _socialSender?.Send(player, _social, _clock.UtcNowMs);
    }
}
