namespace Shared.Data.Tables
{
    /// <summary>Auth API limits (spec 2.1, 2.5). Values in data/tables/AuthApiLimits.json are placeholders.</summary>
    [Table("AuthApiLimits", 1)]
    [Singleton]
    public sealed class AuthApiLimits
    {
        /// <summary>
        /// How long the bearer access token from login works. There is no refresh: the client logs in again with a new
        /// Steam ticket. Tokens are stateless and cannot be revoked early, so keep this short.
        /// </summary>
        [Range(60_000, 86_400_000)] public int AccessTokenLifetimeMs { get; set; }

        /// <summary>Largest request body the Auth API reads, in bytes (bounded input, as R-2.5-04 asks of decoders).</summary>
        [Range(1_024, 1_048_576)] public int MaxRequestBodyBytes { get; set; }
    }
}
