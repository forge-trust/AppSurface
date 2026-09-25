using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Runs bounded heartbeat pruning independently of runtime pass execution.</summary>
internal sealed class PostgreSqlDurableHeartbeatMaintenance : IHostedService, IDisposable, IAsyncDisposable
{
    private static readonly Meter Meter = new("ForgeTrust.AppSurface");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("appsurface.durable.runtime_heartbeat.prune.attempts");
    private static readonly Counter<long> Deleted = Meter.CreateCounter<long>("appsurface.durable.runtime_heartbeat.prune.deleted");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("appsurface.durable.runtime_heartbeat.prune.failures");
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromHours(1);
    private readonly PostgreSqlDurableRuntimeRegistration _registration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostgreSqlDurableHeartbeatMaintenance> _logger;
    private readonly Func<CancellationToken, Task<int>>? _pruneOperation;
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private Task? _runner;
    private int _disposed;

    internal PostgreSqlDurableHeartbeatMaintenance(
        PostgreSqlDurableRuntimeRegistration registration,
        TimeProvider timeProvider,
        ILogger<PostgreSqlDurableHeartbeatMaintenance>? logger = null,
        Func<CancellationToken, Task<int>>? pruneOperation = null)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<PostgreSqlDurableHeartbeatMaintenance>.Instance;
        _pruneOperation = pruneOperation;
    }

    /// <summary>Queues maintenance without waiting for database work.</summary>
    internal void SignalAdmittedPass()
    {
        if (!_registration.Options.EnableHeartbeatMaintenance)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed != 0 || _stop.IsCancellationRequested)
            {
                return;
            }

            _runner ??= Task.Run(() => RunAsync(_stop.Token));
            _signals.Writer.TryWrite(true);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopCoreAsync(cancellationToken);

    /// <summary>Stops scheduled maintenance during host shutdown.</summary>
    internal async ValueTask StopAsync(CancellationToken cancellationToken = default)
        => await StopCoreAsync(cancellationToken).ConfigureAwait(false);

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Task? runner;
        lock (_sync)
        {
            _stop.Cancel();
            runner = _runner;
        }

        if (runner is not null)
        {
            await runner.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? runner;
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            _stop.Cancel();
            runner = _runner;
        }

        try
        {
            if (runner is not null)
            {
                await runner.ConfigureAwait(false);
            }
        }
        finally
        {
            _signals.Writer.TryComplete();
            _stop.Dispose();
        }
    }

    public void Dispose()
    {
        Task? runner;
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            _stop.Cancel();
            runner = _runner;
        }

        try
        {
            runner?.GetAwaiter().GetResult();
        }
        finally
        {
            _signals.Writer.TryComplete();
            _stop.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _signals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var nextDelay = TimeSpan.Zero;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (nextDelay > TimeSpan.Zero)
                {
                    await Task.Delay(nextDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                var startedAt = _timeProvider.GetTimestamp();
                try
                {
                    var deleted = await PruneOnceAsync(cancellationToken).ConfigureAwait(false);
                    Deleted.Add(deleted);
                    LogOutcome(deleted > 0 ? "deleted" : "zero_or_contended");
                    nextDelay = deleted == _registration.Options.HeartbeatPruneBatchSize
                        ? CatchUpDelay
                        : _registration.Options.HeartbeatMaintenanceCadence;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    LogOutcome("cancelled");
                    return;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    LogOutcome("cancelled");
                    return;
                }
                catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
                {
                    Failures.Add(1);
                    LogOutcome("failed");
                    _logger.LogWarning(
                        "Heartbeat maintenance outcome {MaintenanceOutcome}; retrying after {RetryDelay}.",
                        "failed",
                        FailureDelay);
                    nextDelay = FailureDelay;
                }

                // Schedule from the start of the preceding SQL call, including its execution time.
                var elapsed = _timeProvider.GetElapsedTime(startedAt);
                nextDelay = nextDelay > elapsed ? nextDelay - elapsed : TimeSpan.Zero;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogOutcome("cancelled");
        }
    }

    private async Task<int> PruneOnceAsync(CancellationToken cancellationToken)
    {
        Attempts.Add(1);
        if (_pruneOperation is not null)
        {
            return await _pruneOperation(cancellationToken).ConfigureAwait(false);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(budget.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(budget.Token).ConfigureAwait(false);
        await using (var timeout = new NpgsqlCommand("SET LOCAL statement_timeout = '5000ms';", connection, transaction)
        {
            CommandTimeout = 5,
        })
        {
            await timeout.ExecuteNonQueryAsync(budget.Token).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(
            "SELECT appsurface_durable.prune_runtime_heartbeats(@retention, @maximum_rows, @worker_id, @worker_instance_id);",
            connection,
            transaction)
        {
            CommandTimeout = 5,
        };
        command.Parameters.AddWithValue("retention", NpgsqlTypes.NpgsqlDbType.Interval, _registration.Options.HeartbeatRetention);
        command.Parameters.AddWithValue("maximum_rows", _registration.Options.HeartbeatPruneBatchSize);
        command.Parameters.AddWithValue("worker_id", _registration.Options.WorkerId);
        command.Parameters.AddWithValue("worker_instance_id", _registration.InstanceId);
        var result = (int)(await command.ExecuteScalarAsync(budget.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Heartbeat pruning returned no row count."));
        await transaction.CommitAsync(budget.Token).ConfigureAwait(false);
        return result;
    }

    private void LogOutcome(string outcome) => _logger.LogDebug(
        "Heartbeat maintenance outcome {MaintenanceOutcome}.", outcome);
}
