namespace Shared.Util
{
    /// <summary>
    /// The only source of time for game code (architecture rule 6: never <c>DateTime.Now</c>).
    /// Tests use a fake clock; the server uses a UTC system clock.
    /// </summary>
    public interface IClock
    {
        /// <summary>Milliseconds since the Unix epoch, UTC.</summary>
        long UtcNowMs { get; }
    }
}
