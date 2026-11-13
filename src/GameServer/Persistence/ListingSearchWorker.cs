using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GameServer.Persistence;

/// <summary>What the search worker reads (<see cref="ListingRepository"/>; a fake in tests).</summary>
public interface IListingSearchStore
{
    Task<IReadOnlyList<ListingSearchRow>> SearchAsync(ListingQuery query, CancellationToken ct = default);
}

/// <summary>One search the world asked for: who asked (the connection and its request id) and the query.</summary>
public readonly record struct ListingSearchAsk(long CharacterId, int Connection, uint RequestId, ListingQuery Query);

/// <summary>A search's answer: <see cref="Rows"/> null when the read failed.</summary>
public readonly record struct ListingSearchAnswer(ListingSearchAsk Ask, IReadOnlyList<ListingSearchRow>? Rows);

/// <summary>The world's side of the search worker (WP-M4-7): it asks for searches and takes their answers in phase 1. Never blocks.</summary>
public interface IListingSearchJobs
{
    void Request(in ListingSearchAsk ask);

    bool TryTakeAnswer(out ListingSearchAnswer answer);
}

/// <summary>
/// The auction search off the world thread (spec 1.6; WP-M4-7, spec 4.5, M4 decision 6: indexed SQL, no listing cache): the
/// world queues a search, this worker runs it on the database (<see cref="IListingSearchStore"/>) and queues the answer, which
/// the world drains in phase 1 and sends to the asking connection. One search runs at a time. A failure is answered as failed,
/// counted, and logged once when searches start failing and once when they work again.
/// <para>
/// The queue is unbounded in type but bounded in use: the world lets each character have one search in flight
/// (<c>GameWorld.Listings</c>) and rate limits them (<c>ListingSettings</c>), so it never holds more than one per character.
/// </para>
/// </summary>
public sealed class ListingSearchWorker : IListingSearchJobs, IAsyncDisposable
{
    private readonly IListingSearchStore _store;
    private readonly Channel<ListingSearchAsk> _asks =
        Channel.CreateUnbounded<ListingSearchAsk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<ListingSearchAnswer> _answers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Action<string> _log;
    private long _failures;
    private bool _failing;

    /// <param name="log">Receives a line when searches start failing and when they recover; default standard error.</param>
    public ListingSearchWorker(IListingSearchStore store, Action<string>? log = null)
    {
        _store = store;
        _log = log ?? Console.Error.WriteLine;
        _loop = Task.Run(LoopAsync);
    }

    public long Failures => Interlocked.Read(ref _failures);

    public void Request(in ListingSearchAsk ask) => _asks.Writer.TryWrite(ask);

    public bool TryTakeAnswer(out ListingSearchAnswer answer) => _answers.TryDequeue(out answer);

    public async ValueTask DisposeAsync()
    {
        _asks.Writer.TryComplete();
        _stop.Cancel();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task LoopAsync()
    {
        var token = _stop.Token;
        await foreach (var ask in _asks.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            IReadOnlyList<ListingSearchRow>? rows = null;
            if (!token.IsCancellationRequested) // at shutdown the queued searches are answered as failed, not run
            {
                try
                {
                    rows = await _store.SearchAsync(ask.Query, token).ConfigureAwait(false);
                    if (_failing)
                        _log("Auction searches work again.");
                    _failing = false;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _failures);
                    if (!_failing)
                        _log($"Auction search failed (answered as failed until it works again): {e.Message}");
                    _failing = true;
                }
            }
            _answers.Enqueue(new ListingSearchAnswer(ask, rows));
        }
    }
}
