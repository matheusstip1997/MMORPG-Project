namespace Shared.Protocol.Auth
{
    /// <summary>
    /// Why the Auth API refused a request (spec 9.10, login and character reason codes). Sent as the enum name in JSON;
    /// the client maps it to text. Numbers are stable: append new codes, never renumber.
    /// </summary>
    public enum AuthReasonCode
    {
        None = 0,

        // Login and access (spec 9.10 LoginReasonCodes)
        /// <summary>The Steam (or dev) login ticket did not verify.</summary>
        InvalidTicket = 1,
        /// <summary>The Auth API or a service behind it (database, Steam) cannot serve the request now.</summary>
        ServerUnavailable = 2,
        /// <summary>The request body or a path value is malformed or missing a required field.</summary>
        RequestInvalid = 3,
        /// <summary>The bearer access token is missing, unreadable or not signed by this Auth API.</summary>
        AuthTokenInvalid = 4,
        /// <summary>The bearer access token expired: log in again.</summary>
        AuthTokenExpired = 5,
        /// <summary>This Auth API does not serve the requested realm.</summary>
        RealmUnknown = 6,

        // Character creation (spec 9.10 creation contract)
        NameTaken = 10,
        NameInvalid = 11,
        NoFreeSlot = 12,
        ClassNotAllowed = 13,
        AppearanceInvalid = 14,

        // Character selection
        /// <summary>No such character on this account and realm.</summary>
        CharacterNotFound = 20,
        /// <summary>The character is soft deleted (R-2.1-05) and cannot enter the world.</summary>
        CharacterPendingDeletion = 21,
    }
}
