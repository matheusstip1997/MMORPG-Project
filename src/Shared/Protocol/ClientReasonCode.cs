namespace Shared.Protocol
{
    /// <summary>
    /// Refusals and failures the client detects itself, with no server reason code to show (spec 9.1: every failure has
    /// a reason). Shown through <c>ReasonCodeStrings</c> like the server codes, so the names must not collide with
    /// <c>AuthReasonCode</c> or <see cref="ConnectRejectReason"/> members of a different meaning.
    /// </summary>
    public enum ClientReasonCode
    {
        None = 0,
        /// <summary>The Auth API did not answer (network error or <c>LoginSettings.AuthRequestTimeoutMs</c>).</summary>
        AuthApiUnreachable = 1,
        /// <summary>The Auth API answered with something the client cannot read.</summary>
        AuthResponseInvalid = 2,
        /// <summary>The configured Auth API address is not HTTPS or loopback HTTP (R-2.5-02).</summary>
        AuthEndpointInsecure = 3,
        /// <summary>The Steam client is not running or gave no session ticket (R-2.1-01).</summary>
        SteamUnavailable = 4,
        /// <summary>The game server did not answer the connection request.</summary>
        GameServerUnreachable = 5,
        /// <summary>The game connection timed out.</summary>
        ConnectionTimeout = 6,
        /// <summary>The game server closed the connection or the network failed.</summary>
        ConnectionLost = 7,
        /// <summary>A server message failed to decrypt, so the client dropped the connection (R-2.5-02).</summary>
        ConnectionSecurityViolation = 8,
        /// <summary>The game server refused the connection without a readable reason (R-2.5-04).</summary>
        ConnectionRejected = 9,
        /// <summary>The selected server is in maintenance (R-9.10-02).</summary>
        ServerInMaintenance = 10,
        /// <summary>An item was dragged out of the inventory window: items cannot be dropped on the ground in M1 (loot bags, M4).</summary>
        ItemDropUnavailable = 11,
    }
}
