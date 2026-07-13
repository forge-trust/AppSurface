using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

internal sealed partial class PostgreSqlDurableHostedService : CriticalService
{
    private readonly IDurableRuntimeSchemaManager _schemaManager;
    private readonly IDurableRuntimePump _pump;
    private readonly AppSurfaceDurablePostgreSqlOptions _options;
    private readonly IDurableRuntimeDrainControl _drainControl;
    private readonly ILogger<PostgreSqlDurableHostedService> _logger;

    public PostgreSqlDurableHostedService(
        IDurableRuntimeSchemaManager schemaManager,
        IDurableRuntimePump pump,
        IDurableRuntimeDrainControl drainControl,
        PostgreSqlDurableRuntimeRegistration registration,
        ILogger<PostgreSqlDurableHostedService> logger,
        IHostApplicationLifetime applicationLifetime)
        : base(logger, applicationLifetime)
    {
        _schemaManager = schemaManager ?? throw new ArgumentNullException(nameof(schemaManager));
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        _drainControl = drainControl ?? throw new ArgumentNullException(nameof(drainControl));
        _options = registration?.Options ?? throw new ArgumentNullException(nameof(registration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        await _schemaManager.ValidateAsync(stoppingToken).ConfigureAwait(false);
        await _drainControl.ResumeAsync(stoppingToken).ConfigureAwait(false);
        var request = new DurableRuntimePumpRequest(
            _options.MaximumItemsPerPass,
            _options.TimeBudgetPerPass,
            _options.HostedSurfaces);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                DurableRuntimePumpResult result;
                try
                {
                    result = await _pump.RunOnceAsync(request, stoppingToken).ConfigureAwait(false);
                }
                catch (NpgsqlException exception) when (exception.IsTransient)
                {
                    LogTransientStoreFailure(exception, _options.TransientFailureDelay);
                    await Task.Delay(_options.TransientFailureDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                catch (TimeoutException exception)
                {
                    LogTransientStoreFailure(exception, _options.TransientFailureDelay);
                    await Task.Delay(_options.TransientFailureDelay, stoppingToken).ConfigureAwait(false);
                    continue;
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

                var delay = CalculateIdleDelay(result.NextDueAtUtc, _options.IdlePollingInterval);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await _drainControl.BeginDrainAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
            {
                LogDrainMarkerFailure(exception);
            }
        }
    }

    internal static TimeSpan CalculateIdleDelay(DateTimeOffset? nextDueAtUtc, TimeSpan maximumDelay)
    {
        if (nextDueAtUtc is null)
        {
            return maximumDelay;
        }

        var untilDue = nextDueAtUtc.Value - DateTimeOffset.UtcNow;
        if (untilDue <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        return untilDue < maximumDelay ? untilDue : maximumDelay;
    }

    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Warning,
        Message = "ASDUR103 durable PostgreSQL pass failed transiently; retrying after {Delay}.")]
    private partial void LogTransientStoreFailure(Exception exception, TimeSpan delay);

    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Debug,
        Message = "Durable pass discovered {Discovered}, claimed {Claimed}, processed {Processed}, deferred {Deferred}, failed {Failed}, has-more {HasMore}, elapsed {ElapsedMilliseconds} ms.")]
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
        Message = "ASDUR404 durable worker shutdown could not persist its drain marker; liveness will become stale after the configured bound.")]
    private partial void LogDrainMarkerFailure(Exception exception);
}
