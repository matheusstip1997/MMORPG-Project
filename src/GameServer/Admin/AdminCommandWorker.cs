using System.Collections.Concurrent;
using GameServer.World;
using Shared.Data.Tables;

namespace GameServer.Admin;

/// <summary>
/// The outcomes of admin commands the world executed, from the world thread to <see cref="AdminCommandWorker"/>.
/// Thread-safe; the world only enqueues.
/// </summary>
public sealed class AdminCommandResults
{
    private readonly ConcurrentQueue<(long ActionId, AdminOutcome Outcome)> _results = new();

    public void Post(long actionId, AdminOutcome outcome) => _results.Enqueue((actionId, outcome));

    internal bool TryTake(out (long ActionId, AdminOutcome Outcome) result) => _results.TryDequeue(out result);
}

/// <summary>
/// Brings admin commands from the realm database to this server's world (spec 7.4, admin panel v0). A background loop,
/// never the world thread (spec 1.6): every <c>AdminSettings.CommandPollMs</c> it closes expired commands, claims the
/// pending ones addressed to this server (the holder of the target's ownership lock), posts each to the world's inbox,
/// and writes back the outcomes the world reported. The world executes a kick in phase 1 through the
/// <c>SessionManager</c>, the only writer of sessions.
/// </summary>
public sealed class AdminCommandWorker : IAsyncDisposable
{
    private readonly AdminActionRepository _actions;
    private readonly WorldInbox _inbox;
    private readonly AdminSettings _settings;
    private readonly string _serverId;
    private readonly Action<string> _log;
    private readonly AdminCommandResults _results = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _failing;

    /// <param name="serverId">This process's id, as written in its ownership lock tokens.</param>
    /// <param name="log">Receives one line per failed poll (the database being down); default standard error.</param>
    public AdminCommandWorker(AdminActionRepository actions, WorldInbox inbox, AdminSettings settings, string serverId,
        Action<string>? log = null)
    {
        _actions = actions;
        _inbox = inbox;
        _settings = settings;
        _serverId = serverId;
        _log = log ?? Console.Error.WriteLine;
    }

    /// <summary>Where the world reports outcomes.</summary>
    public AdminCommandResults Results => _results;

    public void Start()
    {
        if (_loop != null)
            throw new InvalidOperationException("Already started.");
        _loop = Task.Run(RunAsync);
    }

    /// <summary>One poll: expire, claim and post, write back outcomes. Public for tests that drive it step by step.</summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        await WriteOutcomesAsync(ct);
        await _actions.ExpireAsync(_settings.CommandExpiryMs, ct);
        foreach (var command in await _actions.ClaimAsync(_serverId, _settings.MaxClaimPerPoll, ct))
        {
            if (command.Action == AdminActionKind.Kick && command.CharacterId > 0)
                _inbox.Post(WorldCommand.AdminKick(command.ActionId, command.CharacterId, _results));
            else // the table allows commands only for kick; anything else is closed rather than left claimed
                await _actions.CompleteAsync(command.ActionId, _serverId, AdminOutcome.NotOnline, ct);
        }
    }

    /// <summary>Writes the outcomes the world has reported so far.</summary>
    public async Task WriteOutcomesAsync(CancellationToken ct = default)
    {
        while (_results.TryTake(out var result))
            await _actions.CompleteAsync(result.ActionId, _serverId, result.Outcome, ct);
    }

    /// <summary>Stops polling and writes the outcomes already reported (call after the world stopped).</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop != null)
            await _loop;
        try
        {
            await WriteOutcomesAsync();
        }
        catch (Exception e)
        {
            // The rows stay claimed and expire; the kicks themselves happened.
            _log($"Admin commands: could not write the last outcomes: {e.Message}");
        }
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollAsync(token);
                if (_failing)
                    _log("Admin commands: polling works again.");
                _failing = false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // One line when polling starts failing (database down), not one per poll.
                if (!_failing)
                    _log($"Admin commands: poll failed: {e.Message}");
                _failing = true;
            }

            try
            {
                await Task.Delay(_settings.CommandPollMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
