using System;

namespace Shared.Protocol.Auth
{
    // Auth API contract (spec 2.1, 2.5, 9.10): JSON over HTTPS, camelCase names, enums as names, byte arrays as base64.
    // Every response carries ok and reasonCode; the HTTP status is a hint, the reason code drives the UI.
    // Requests after login send "Authorization: Bearer <accessToken>".

    /// <summary>Common part of every Auth API response.</summary>
    public class AuthResponse
    {
        public bool Ok { get; set; }

        public AuthReasonCode ReasonCode { get; set; }
    }

    /// <summary><c>POST /v1/login</c>. <see cref="Ticket"/> is the Steam session ticket as hex (R-2.1-01).</summary>
    public sealed class LoginRequest
    {
        public string Ticket { get; set; } = "";
    }

    /// <summary>The access token for later calls and the character list of the Auth API's realm.</summary>
    public sealed class LoginResponse : AuthResponse
    {
        public string AccessToken { get; set; } = "";

        public long AccessTokenExpiresAtUtcMs { get; set; }

        public long AccountId { get; set; }

        public CharacterListResponse? Characters { get; set; }
    }

    /// <summary><c>GET /v1/realms/{realmId}/characters</c>: the cards of the character selection screen (R-9.10-04).</summary>
    public sealed class CharacterListResponse : AuthResponse
    {
        public int RealmId { get; set; }

        /// <summary>Slot count for the slot counter (R-2.1-05).</summary>
        public int SlotsPerRealm { get; set; }

        /// <summary>Server time, so the client counts down the grace period without trusting its own clock.</summary>
        public long ServerTimeUtcMs { get; set; }

        public CharacterCard[] Characters { get; set; } = Array.Empty<CharacterCard>();
    }

    /// <summary>One character slot card. Rank, jail state and location come with their systems.</summary>
    public sealed class CharacterCard
    {
        public long CharacterId { get; set; }

        public int Slot { get; set; }

        public string Name { get; set; } = "";

        public string ClassId { get; set; } = "";

        public int Level { get; set; }

        /// <summary>Soft deleted and inside the grace period (R-2.1-05).</summary>
        public bool PendingDeletion { get; set; }

        /// <summary>End of the grace period when <see cref="PendingDeletion"/>, otherwise 0.</summary>
        public long PurgeAfterUtcMs { get; set; }
    }

    /// <summary>
    /// <c>POST /v1/realms/{realmId}/characters</c> (spec 9.10). <see cref="RequestId"/> is a client-generated idempotency
    /// key (architecture rule 5): sending the same request again returns the same character.
    /// </summary>
    public sealed class CreateCharacterRequest
    {
        public Guid RequestId { get; set; }

        public string Name { get; set; } = "";

        public string InitialClass { get; set; } = "";

        public CharacterAppearance Appearance { get; set; } = new CharacterAppearance();
    }

    /// <summary>Opaque, versioned appearance blob until the creation screen is designed (spec 9.10).</summary>
    public sealed class CharacterAppearance
    {
        public int Version { get; set; }

        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    /// <summary><c>CreateCharacterResult { ok | reasonCode, characterId }</c> (spec 9.10).</summary>
    public sealed class CreateCharacterResult : AuthResponse
    {
        public long CharacterId { get; set; }

        /// <summary>The new card, so the selection screen can show it without listing again.</summary>
        public CharacterCard? Character { get; set; }
    }

    /// <summary><c>DELETE /v1/realms/{realmId}/characters/{characterId}</c>: soft delete with a grace period (R-2.1-05).</summary>
    public sealed class DeleteCharacterResult : AuthResponse
    {
        public long PurgeAfterUtcMs { get; set; }
    }

    /// <summary>
    /// <c>POST /v1/realms/{realmId}/characters/{characterId}/enter</c>: the single-use session ticket for the game server
    /// (R-2.5-01) and the session key, which only ever travels to the client over HTTPS (R-2.5-02).
    /// </summary>
    public sealed class EnterWorldResult : AuthResponse
    {
        /// <summary>Opaque; the client sends it in the connection request.</summary>
        public byte[] Ticket { get; set; } = Array.Empty<byte>();

        public byte[] SessionKey { get; set; } = Array.Empty<byte>();

        public long TicketExpiresAtUtcMs { get; set; }
    }
}
