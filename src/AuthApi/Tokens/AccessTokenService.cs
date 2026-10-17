using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Shared.Protocol.Auth;
using Shared.Util;

namespace AuthApi.Tokens;

public readonly record struct IssuedAccessToken(string Token, long ExpiresAtUtcMs);

public readonly record struct AccessTokenCheck(AuthReasonCode Reason, long AccountId)
{
    public bool Ok => Reason == AuthReasonCode.None;
}

/// <summary>
/// Stateless bearer token for Auth API calls after login (spec 2.1): the server keeps no session table.
/// Layout, then base64url: byte version, byte keyId, long accountId, long expiresAtUtcMs (little endian), then
/// HMAC-SHA256 of those bytes. The MAC key is derived with HKDF from its own secret, never the ticket secret.
/// A token cannot be revoked before it expires, so its lifetime (<c>AuthApiLimits.AccessTokenLifetimeMs</c>) is short.
/// </summary>
public sealed class AccessTokenService
{
    public const byte FormatVersion = 1;

    private const int PayloadSize = 1 + 1 + 8 + 8;
    private const int MacSize = 32;
    private const int Size = PayloadSize + MacSize;

    private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("mmorpg/auth/v1/access-token");

    private readonly byte _keyId;
    private readonly byte[] _macKey;
    private readonly IClock _clock;
    private readonly int _lifetimeMs;

    public AccessTokenService(byte keyId, byte[] secret, IClock clock, int lifetimeMs)
    {
        _keyId = keyId;
        _macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, MacSize, salt: [], info: KeyInfo);
        _clock = clock;
        _lifetimeMs = lifetimeMs;
    }

    public IssuedAccessToken Issue(long accountId)
    {
        var expiresAt = _clock.UtcNowMs + _lifetimeMs;
        Span<byte> bytes = stackalloc byte[Size];
        bytes[0] = FormatVersion;
        bytes[1] = _keyId;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[2..], accountId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[10..], expiresAt);
        HMACSHA256.HashData(_macKey, bytes[..PayloadSize], bytes[PayloadSize..]);
        return new IssuedAccessToken(Base64Url.EncodeToString(bytes), expiresAt);
    }

    public AccessTokenCheck Validate(string? token)
    {
        // A valid token is exactly 64 base64url characters; reject anything else before decoding.
        if (token == null || token.Length != Base64Url.GetEncodedLength(Size))
            return Invalid();

        Span<byte> bytes = stackalloc byte[Size];
        if (!Base64Url.TryDecodeFromChars(token, bytes, out var written) || written != Size)
            return Invalid();
        if (bytes[0] != FormatVersion || bytes[1] != _keyId)
            return Invalid();

        Span<byte> expected = stackalloc byte[MacSize];
        HMACSHA256.HashData(_macKey, bytes[..PayloadSize], expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes[PayloadSize..]))
            return Invalid();

        var accountId = BinaryPrimitives.ReadInt64LittleEndian(bytes[2..]);
        var expiresAt = BinaryPrimitives.ReadInt64LittleEndian(bytes[10..]);
        var now = _clock.UtcNowMs;
        if (expiresAt <= now)
            return new AccessTokenCheck(AuthReasonCode.AuthTokenExpired, 0);
        // A token that claims to live longer than this issuer grants is not trusted (same rule as TicketValidator).
        if (expiresAt - now > _lifetimeMs)
            return Invalid();

        return new AccessTokenCheck(AuthReasonCode.None, accountId);
    }

    private static AccessTokenCheck Invalid() => new(AuthReasonCode.AuthTokenInvalid, 0);
}
