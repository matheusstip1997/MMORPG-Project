using System.Collections.Concurrent;

namespace GameServer.Dev.Commands;

/// <summary>What the dev console knows of an account: only an account the dev login made may run commands (WP-M3-13).</summary>
public enum DevAccountStatus : byte
{
    /// <summary>The lookup is running (the first command right after a login); send again.</summary>
    Checking,
    Dev,
    /// <summary>A Steam account, or no account row: never served.</summary>
    NotDev,
}

/// <summary>Which accounts the dev login made (spec 11.7 dev console). Read on the world thread: it never blocks.</summary>
public interface IDevAccounts
{
    DevAccountStatus Status(long accountId);

    /// <summary>Starts the lookup early (a session started), so the first command finds the answer.</summary>
    void Prefetch(long accountId);
}

/// <summary>
/// Accounts by their SteamID (spec 7.4 implementation: the dev login's <c>dev:&lt;n&gt;</c> is fake SteamID n, a small
/// positive number; every real individual SteamID64 is at least <see cref="FirstSteamId"/>). The SteamID comes from the
/// global database's account row, looked up off the world thread once per account and kept for the process; a failed
/// lookup is tried again on the next command.
/// </summary>
public sealed class DevAccountDirectory : IDevAccounts
{
    /// <summary>The lowest SteamID64 of an individual Steam account (universe 1, type 1, instance 1): a format fact, not tuning.</summary>
    public const long FirstSteamId = 0x0110000100000000;

    private readonly Func<long, CancellationToken, Task<long?>> _steamIdOf;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<long, DevAccountStatus> _known = new();
    private int _failing;

    /// <param name="steamIdOf">The account's SteamID, null when there is no such account (a database read).</param>
    /// <param name="log">One line when lookups start failing; default standard error.</param>
    public DevAccountDirectory(Func<long, CancellationToken, Task<long?>> steamIdOf, Action<string>? log = null)
    {
        _steamIdOf = steamIdOf;
        _log = log ?? Console.Error.WriteLine;
    }

    /// <summary>A SteamID the dev login made.</summary>
    public static bool IsDevSteamId(long steamId) => steamId > 0 && steamId < FirstSteamId;

    public DevAccountStatus Status(long accountId)
    {
        if (_known.TryGetValue(accountId, out var status))
            return status;
        Prefetch(accountId);
        return _known.TryGetValue(accountId, out status) ? status : DevAccountStatus.Checking;
    }

    public void Prefetch(long accountId)
    {
        if (!_known.TryAdd(accountId, DevAccountStatus.Checking))
            return;
        _ = Task.Run(() => LookUpAsync(accountId));
    }

    private async Task LookUpAsync(long accountId)
    {
        try
        {
            long? steamId = await _steamIdOf(accountId, CancellationToken.None).ConfigureAwait(false);
            _known[accountId] = steamId is { } id && IsDevSteamId(id) ? DevAccountStatus.Dev : DevAccountStatus.NotDev;
            Interlocked.Exchange(ref _failing, 0);
        }
        catch (Exception e)
        {
            _known.TryRemove(accountId, out _); // tried again on the next command
            if (Interlocked.Exchange(ref _failing, 1) == 0)
                _log($"Dev console: account lookup failed: {e.Message}");
        }
    }
}

/// <summary>No global database: every account is refused (a server started without <c>MMORPG_GLOBAL_DB</c>).</summary>
public sealed class NoDevAccounts : IDevAccounts
{
    public static readonly NoDevAccounts Instance = new();

    public DevAccountStatus Status(long accountId) => DevAccountStatus.NotDev;

    public void Prefetch(long accountId)
    {
    }
}
