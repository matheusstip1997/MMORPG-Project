using GameServer.Net.Tickets;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol.Auth;
using Shared.Util;
using CreateCharacterResult = Shared.Protocol.Auth.CreateCharacterResult;

namespace AuthApi.Characters;

/// <summary>
/// Character selection and creation for one account in this API's realm (R-2.1-05, spec 9.10), and the ticket for
/// "enter world" (R-2.5-01). The caller has already authenticated the account.
/// </summary>
public sealed class CharacterService(
    CharacterRepository characters,
    AuthConfig config,
    DevTicketIssuer tickets,
    IClock clock)
{
    private readonly CharacterNameRules _names = new(config.Creation);
    private readonly HashSet<string> _classes = new(config.Creation.AllowedInitialClasses, StringComparer.Ordinal);

    public int RealmId => characters.RealmId;

    public async Task<CharacterListResponse> ListAsync(long accountId, CancellationToken ct)
    {
        var records = await characters.ListByAccountAsync(accountId, ct);
        return new CharacterListResponse
        {
            Ok = true,
            RealmId = characters.RealmId,
            SlotsPerRealm = config.Slots.SlotsPerRealm,
            ServerTimeUtcMs = clock.UtcNowMs,
            Characters = records.Select(ToCard).ToArray(),
        };
    }

    /// <summary>
    /// Validates in the order of spec 9.10 (name, class, appearance) and then takes the lowest free slot. A double submit
    /// with the same request ID returns the character it created (architecture rule 5).
    /// </summary>
    public async Task<CreateCharacterResult> CreateAsync(long accountId, CreateCharacterRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.Name == null || request.InitialClass == null)
            return CreateRefused(AuthReasonCode.RequestInvalid);
        if (!_names.IsValid(request.Name))
            return CreateRefused(AuthReasonCode.NameInvalid);
        if (!_classes.Contains(request.InitialClass))
            return CreateRefused(AuthReasonCode.ClassNotAllowed);
        if (!IsAppearanceValid(request.Appearance))
            return CreateRefused(AuthReasonCode.AppearanceInvalid);

        // Another create on the same account can take the slot between the list and the insert: list again and retry.
        for (var attempt = 0; attempt <= config.Slots.SlotsPerRealm; attempt++)
        {
            var used = (await characters.ListByAccountAsync(accountId, ct)).Select(c => c.Slot).ToHashSet();
            var slot = Enumerable.Range(0, config.Slots.SlotsPerRealm).FirstOrDefault(s => !used.Contains(s), -1);

            // With no free slot, insert into slot 0 anyway: the repository checks the request ID before reporting the
            // slot as taken, so a retry of a create that filled the last slot still gets its character back.
            var result = await characters.CreateAsync(NewCharacter(accountId, Math.Max(slot, 0), request), ct);
            switch (result.Status)
            {
                case CreateCharacterStatus.Created:
                    var card = ToCard(result.Character!);
                    return new CreateCharacterResult { Ok = true, CharacterId = card.CharacterId, Character = card };
                case CreateCharacterStatus.NameTaken:
                    return CreateRefused(AuthReasonCode.NameTaken);
                case CreateCharacterStatus.RequestIdReused:
                    return CreateRefused(AuthReasonCode.RequestInvalid);
                case CreateCharacterStatus.SlotTaken when slot < 0:
                    return CreateRefused(AuthReasonCode.NoFreeSlot);
                case CreateCharacterStatus.SlotTaken:
                    continue;
            }
        }

        return CreateRefused(AuthReasonCode.NoFreeSlot);
    }

    /// <summary>Soft delete with the grace period from <see cref="CharacterSlots"/> (R-2.1-05). Deleting twice is not an error.</summary>
    public async Task<DeleteCharacterResult> DeleteAsync(long accountId, long characterId, CancellationToken ct)
    {
        // TODO(B4): refuse while the character is in the world (ownership lock, R-2.1-06).
        var purgeAfter = clock.UtcNowMs + config.Slots.DeleteGraceMs;
        var status = await characters.SoftDeleteAsync(characterId, accountId, purgeAfter, ct);
        switch (status)
        {
            case DeleteStatus.Deleted:
                return new DeleteCharacterResult { Ok = true, PurgeAfterUtcMs = purgeAfter };
            case DeleteStatus.AlreadyDeleted:
                var existing = await characters.LoadAsync(characterId, ct);
                return new DeleteCharacterResult { Ok = true, PurgeAfterUtcMs = existing?.PurgeAfterUtcMs ?? 0 };
            default:
                return new DeleteCharacterResult { ReasonCode = AuthReasonCode.CharacterNotFound };
        }
    }

    /// <summary>
    /// Issues the single-use session ticket for the character's slot (R-2.5-01) and the session key (R-2.5-02).
    /// The game server's session start loads the character by account, realm and slot (R-9.10-05).
    /// </summary>
    public async Task<EnterWorldResult> EnterWorldAsync(long accountId, long characterId, CancellationToken ct)
    {
        var character = await characters.LoadAsync(characterId, ct);
        if (character == null || character.AccountId != accountId)
            return new EnterWorldResult { ReasonCode = AuthReasonCode.CharacterNotFound };
        if (character.IsPendingDeletion)
            return new EnterWorldResult { ReasonCode = AuthReasonCode.CharacterPendingDeletion };

        var issued = tickets.Issue(accountId, characters.RealmId, (byte)character.Slot);
        SessionTicketCodec.TryReadPayload(issued.Ticket, out var ticket);
        return new EnterWorldResult
        {
            Ok = true,
            Ticket = issued.Ticket,
            SessionKey = issued.SessionKey,
            TicketExpiresAtUtcMs = ticket.ExpiresAtMs,
        };
    }

    private bool IsAppearanceValid(CharacterAppearance? appearance) =>
        appearance != null
        && appearance.Version == config.Creation.AppearanceVersion
        && (appearance.Data ?? []).Length <= config.Creation.AppearanceMaxBytes;

    private NewCharacter NewCharacter(long accountId, int slot, CreateCharacterRequest request)
    {
        var c = config.Creation;
        // TODO(open): the appearance is validated but not stored until the creation screen defines it (spec 9.10).
        var initial = new CharacterState(
            c.StartLevel, Exp: 0, c.StartWorldId, c.StartPosXCm, c.StartPosYCm, c.StartPosZCm, c.StartFacing,
            c.StartHp, c.StartMp, config.StartingValues.StaminaPool);
        return new NewCharacter(accountId, slot, request.Name, request.InitialClass, initial, request.RequestId);
    }

    private static CreateCharacterResult CreateRefused(AuthReasonCode reason) => new() { ReasonCode = reason };

    private static CharacterCard ToCard(CharacterRecord record) => new()
    {
        CharacterId = record.CharacterId,
        Slot = record.Slot,
        Name = record.Name,
        ClassId = record.ClassId,
        Level = record.State.Level,
        PendingDeletion = record.IsPendingDeletion,
        PurgeAfterUtcMs = record.PurgeAfterUtcMs ?? 0,
    };
}
