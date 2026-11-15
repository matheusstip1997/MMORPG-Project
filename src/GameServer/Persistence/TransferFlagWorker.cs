using GameServer.Telemetry;
using Shared.Data.Tables;
using Shared.Simulation.Economy;
using Shared.Util;

namespace GameServer.Persistence;

/// <summary>
/// Flags large one-way transfers between accounts (WP-M4-15; spec 4.5 R-4.5-02, 7.6; M4 decision 17). A background loop,
/// never the world thread (spec 1.6): every <see cref="TransferFlagSettings.ScanIntervalMs"/> it scans the transfers committed
/// since (<see cref="TransferFlagRepository.ScanAsync"/>; again at once while a scan was full, to catch up), and after each
/// scan committed it records one <c>TransferFlagged</c> telemetry record per threshold a pair newly crossed (the queue never
/// waits, rule 10). No automatic action: nothing is refused, nothing reaches a player, the trade and listing paths do not
/// know this exists; the admin panel lists the flags. A crash between a scan's commit and its telemetry loses the records,
/// never the flag rows (the authoritative list).
/// </summary>
public sealed class TransferFlagWorker : IAsyncDisposable
{
    /// <summary>Scans one wake-up runs at most while catching up, so a large backlog never holds the loop.</summary>
    public const int MaxScansPerWake = 20;

    private readonly TransferFlagRepository _flags;
    private readonly TransferFlagSettings _settings;
    private readonly ItemValue? _values;
    private readonly TelemetryQueue? _telemetry;
    private readonly IClock _clock;
    private readonly int _worldId;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _failing;

    public TransferFlagWorker(TransferFlagRepository flags, TransferFlagSettings settings, ItemValue? values, TelemetryQueue? telemetry,
        IClock clock, int worldId, Action<string>? log = null)
    {
        _flags = flags;
        _settings = settings;
        _values = values;
        _telemetry = telemetry;
        _clock = clock;
        _worldId = worldId;
        _log = log ?? Console.Error.WriteLine;
    }

    public void Start()
    {
        if (_loop != null)
            throw new InvalidOperationException("Already started.");
        _loop = Task.Run(RunAsync);
    }

    /// <summary>One scan and its telemetry. Public for tests that drive the worker step by step.</summary>
    public async Task<TransferFlagScan> ScanOnceAsync(CancellationToken ct = default)
    {
        var scan = await _flags.ScanAsync(_settings, _values, ct);
        foreach (var crossing in scan.Crossings)
            _telemetry?.TryEnqueue(Record(crossing, _clock.UtcNowMs, _worldId));
        return scan;
    }

    /// <summary>The <c>TransferFlagged</c> record of a crossing (kind 128; the fields in <c>TelemetrySchema</c>).</summary>
    public static TelemetryRecord Record(TransferFlagCrossing crossing, long utcMs, int worldId) =>
        new(TelemetryKind.TransferFlagged, utcMs, WorldId: worldId, CharacterId: crossing.GiverCharacter, AccountId: crossing.GiverAccount,
            Subject: crossing.Day, Target: crossing.ReceiverCharacter, V0: crossing.NetGold, V1: crossing.NetValue, V2: crossing.NewlyCrossed,
            V3: crossing.ReceiverAccount);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop != null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
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
                for (int i = 0; i < MaxScansPerWake && !token.IsCancellationRequested; i++)
                {
                    var scan = await ScanOnceAsync(token);
                    if (scan.Counted < _settings.ScanBatchMax)
                        break;
                }
                if (_failing)
                    _log("Transfer flags: scanning works again.");
                _failing = false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // One line when scanning starts failing (database down), not one per scan; the next scan picks up where it stopped.
                if (!_failing)
                    _log($"Transfer flags: scan failed: {e.Message}");
                _failing = true;
            }

            try
            {
                await Task.Delay(_settings.ScanIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
