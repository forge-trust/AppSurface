using System.Threading.Channels;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Host lifecycle adapter that schedules the one authoritative bounded PostgreSQL runtime pump.</summary>
/// <remarks>
/// This service is registered only by explicit worker-host composition. It does not apply schema migrations and does
/// not own durable correctness: the store still owns claims, leases, permits, fencing, and durable history.
/// </remarks>
internal sealed partial class PostgreSqlDurableHostedService : BackgroundService
{
    private readonly IDurableRuntimeSchemaManager _schemaManager;
    private readonly IDurableRuntimePump _pump;
    private readonly IDurableRuntimeDrainControl _drainControl;
    private readonly PostgreSqlDurableRuntimeRegistration _registration;
    private readonly DurableRuntimeAdmissionGate _admission;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly HostOptions _hostOptions;
    private readonly ILogger<PostgreSqlDurableHostedService> _logger;
    private readonly Channel<bool> _wakeSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly object _shutdownSync = new();
    private CancellationTokenRegistration _stoppingRegistration;
    private CancellationTokenSource? _activePassCancellation;
    private DateTimeOffset? _shutdownDeadlineUtc;
    private Task? _drainTask;
    private Task? _listenerTask;

    internal PostgreSqlDurableHostedService(
        IDurableRuntimeSchemaManager schemaManager,
        IDurableRuntimePump pump,
        IDurableRuntimeDrainControl drainControl,
        PostgreSqlDurableRuntimeRegistration registration,
        DurableRuntimeAdmissionGate admission,
        IHostApplicationLifetime lifetime,
        IOptions<HostOptions> hostOptions,
        ILogger<PostgreSqlDurableHostedService> logger)
    {
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        _drainControl = drainControl ?? throw new ArgumentNullException(nameof(drainControl));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _hostOptions = hostOptions?.Value ?? throw new ArgumentNullException(nameof(hostOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateHostedShutdownBudget();
        _stoppingRegistration = _lifetime.ApplicationStopping.Register(CloseAdmissionAndStartDrain);
        if (_registration.WorkOptions.WakeNotificationMode == PostgreSqlDurableWakeNotificationMode.Enabled)
        {
            _listenerTask = ListenForWakeHintsAsync(_lifetime.ApplicationStopping);
        }

        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CloseAdmissionAndStartDrain();
        var drain = GetDrainTask();
        if (drain is not null)
        {
            try
            {
                await WaitWithoutReplacingFailureAsync(drain, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host has exhausted its own shutdown wait; base.StopAsync still cancels the background loop.
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                // A failed drain marker is observable, but it must not prevent base.StopAsync from cancelling the
                // background service. PersistDrainAsync already recorded the value-free diagnostic, and the closed
                // local admission gate remains the immediate safety boundary.
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_listenerTask is { } listener)
        {
            await listener.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _stoppingRegistration.Dispose();
        _wakeSignals.Writer.TryComplete();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _schemaManager.ValidateAsync(stoppingToken).ConfigureAwait(false);
        if (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return;
        }

        await _drainControl.ResumeAsync(stoppingToken).ConfigureAwait(false);
        var request = new DurableRuntimePumpRequest(
            _registration.Options.MaximumItemsPerPass,
            _registration.Options.TimeBudgetPerPass,
            _registration.Options.HostedSurfaces);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                DurableRuntimePumpResult result;
                using var passCancellation = CreatePassCancellation();
                try
                {
                    result = await _pump.RunOnceAsync(request, passCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (passCancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (NpgsqlException exception) when (exception.IsTransient && !stoppingToken.IsCancellationRequested)
                {
                    LogTransientStoreFailure(_registration.Options.TransientFailureDelay);
                    await Task.Delay(_registration.Options.TransientFailureDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                catch (TimeoutException) when (!stoppingToken.IsCancellationRequested)
                {
                    LogTransientStoreFailure(_registration.Options.TransientFailureDelay);
                    await Task.Delay(_registration.Options.TransientFailureDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                finally
                {
                    ClearActivePassCancellation(passCancellation);
                }

                LogPassCompleted(
                    result.Discovered,
                    result.Claimed,
                    result.Processed,
                    result.Deferred,
                    result.Failed,
                    result.HasMore,
                    result.Elapsed.TotalMilliseconds);
                if (result.HasMore)
                {
                    await Task.Yield();
                    continue;
                }

                await WaitForWakeOrPollAsync(result.NextDueAtUtc, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CloseAdmissionAndStartDrain();
            var drain = GetDrainTask();
            if (drain is not null)
            {
                try
                {
                    await drain.ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
                {
                    LogDrainMarkerFailure();
                }
            }
        }
    }

    private async Task WaitForWakeOrPollAsync(DateTimeOffset? nextDueAtUtc, CancellationToken cancellationToken)
    {
        var maximumDelay = _registration.Options.IdlePollingInterval;
        var delay = CalculateIdleDelay(nextDueAtUtc, maximumDelay);
        using var wakeWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wakeReady = _wakeSignals.Reader.WaitToReadAsync(wakeWaitCancellation.Token).AsTask();
        var timer = Task.Delay(delay, cancellationToken);
        if (await Task.WhenAny(wakeReady, timer).ConfigureAwait(false) == wakeReady
            && await wakeReady.ConfigureAwait(false))
        {
            _ = _wakeSignals.Reader.TryRead(out _);
            return;
        }

        // A poll timeout must release the channel waiter. Leaving it pending would retain one waiter for every
        // idle-poll interval until a later wake notification or host shutdown.
        await wakeWaitCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await wakeReady.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wakeWaitCancellation.IsCancellationRequested)
        {
            // The timeout won the race; authoritative polling is about to run another bounded pass.
        }
    }

    private async Task ListenForWakeHintsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _registration.RuntimeDataSource.OpenConnectionAsync(stoppingToken)
                    .ConfigureAwait(false);
                connection.Notification += OnNotification;
                try
                {
                    await using var listen = new NpgsqlCommand("LISTEN appsurface_durable_wake;", connection);
                    await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    connection.Notification -= OnNotification;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (NpgsqlException) when (!stoppingToken.IsCancellationRequested)
            {
                LogListenerRetry(_registration.Options.TransientFailureDelay);
                await Task.Delay(_registration.Options.TransientFailureDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!stoppingToken.IsCancellationRequested)
            {
                LogListenerRetry(_registration.Options.TransientFailureDelay);
                await Task.Delay(_registration.Options.TransientFailureDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs args)
    {
        // Payload is a dispatch id and intentionally discarded. PostgreSQL due-state polling remains authoritative.
        _wakeSignals.Writer.TryWrite(true);
    }

    private CancellationTokenSource CreatePassCancellation()
    {
        var source = new CancellationTokenSource();
        lock (_shutdownSync)
        {
            _activePassCancellation = source;
            if (_shutdownDeadlineUtc is { } deadline)
            {
                source.CancelAfter(RemainingUntil(deadline));
            }
        }

        return source;
    }

    private void CloseAdmissionAndStartDrain()
    {
        _admission.Close();
        lock (_shutdownSync)
        {
            _shutdownDeadlineUtc ??= DateTimeOffset.UtcNow + (_hostOptions.ShutdownTimeout - _registration.Options.ShutdownReserve);
            _activePassCancellation?.CancelAfter(RemainingUntil(_shutdownDeadlineUtc.Value));
            _drainTask ??= PersistDrainAsync();
        }
    }

    private void ClearActivePassCancellation(CancellationTokenSource source)
    {
        lock (_shutdownSync)
        {
            if (ReferenceEquals(_activePassCancellation, source))
            {
                _activePassCancellation = null;
            }
        }
    }

    private Task? GetDrainTask()
    {
        lock (_shutdownSync)
        {
            return _drainTask;
        }
    }

    private async Task PersistDrainAsync()
    {
        using var cleanup = new CancellationTokenSource(_registration.Options.ShutdownReserve);
        try
        {
            await _drainControl.BeginDrainAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            LogDrainMarkerFailure();
            throw;
        }
    }

    private void ValidateHostedShutdownBudget()
    {
        var availablePassTime = _hostOptions.ShutdownTimeout - _registration.Options.ShutdownReserve;
        if (availablePassTime <= TimeSpan.Zero
            || _registration.Options.TimeBudgetPerPass > availablePassTime)
        {
            throw new InvalidOperationException(
                "The durable hosted pass budget plus ShutdownReserve must fit inside HostOptions.ShutdownTimeout.");
        }
    }

    internal static TimeSpan CalculateIdleDelay(DateTimeOffset? nextDueAtUtc, TimeSpan maximumDelay)
    {
        if (nextDueAtUtc is null)
        {
            return maximumDelay;
        }

        var untilDue = nextDueAtUtc.Value - DateTimeOffset.UtcNow;
        return untilDue <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : Min(untilDue, maximumDelay);
    }

    private static TimeSpan RemainingUntil(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static async Task WaitWithoutReplacingFailureAsync(Task task, CancellationToken cancellationToken)
    {
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Warning,
        Message = "ASDUR103 durable PostgreSQL runtime encountered a transient failure; retrying after {Delay}.")]
    private partial void LogTransientStoreFailure(TimeSpan delay);

    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Debug,
        Message = "Durable Pass completed: discovered {Discovered}, claimed {Claimed}, processed {Processed}, deferred {Deferred}, failed {Failed}, has-more {HasMore}, elapsed {ElapsedMilliseconds} ms.")]
    private partial void LogPassCompleted(
        int discovered,
        int claimed,
        int processed,
        int deferred,
        int failed,
        bool hasMore,
        double elapsedMilliseconds);

    [LoggerMessage(
        EventId = 4105,
        Level = LogLevel.Warning,
        Message = "ASDUR404 durable worker shutdown could not persist its drain marker; PostgreSQL liveness will become stale after the configured bound.")]
    private partial void LogDrainMarkerFailure();

    [LoggerMessage(
        EventId = 4106,
        Level = LogLevel.Debug,
        Message = "ASDUR103 durable PostgreSQL wake listener disconnected transiently; polling remains authoritative and listener retry begins after {Delay}.")]
    private partial void LogListenerRetry(TimeSpan delay);
}
