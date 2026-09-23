using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Starts the optional AppSurface Docs harvest warmup and performs the strict harvest-health startup preflight.
/// </summary>
/// <remarks>
/// The service is always registered by <c>AddAppSurfaceDocs()</c>. By default it starts the same memoized harvest used by
/// docs requests through their shared <see cref="AppSurfaceDocsHarvestCoordinator"/>. Blocking and strict startup
/// await that same task, so completed warmup is immediately visible to requests even with a zero request-wait budget.
/// Strict mode fails startup only when the aggregate snapshot is failed.
/// </remarks>
internal sealed class AppSurfaceDocsHarvestFailurePreflightService : IHostedService
{
    private readonly AppSurfaceDocsOptions _options;
    private readonly DocAggregator _aggregator;
    private readonly AppSurfaceDocsHarvestCoordinator? _coordinator;
    private readonly ILogger<AppSurfaceDocsHarvestFailurePreflightService> _logger;

    /// <summary>
    /// Initializes a new instance of the strict harvest preflight service.
    /// </summary>
    /// <param name="options">The normalized AppSurface Docs options that contain the strict harvest policy.</param>
    /// <param name="aggregator">The docs aggregator used when the optional request coordinator is absent.</param>
    /// <param name="logger">The logger that records strict startup failures for operators.</param>
    /// <param name="coordinator">The shared startup and request coordinator, when installed by the host.</param>
    public AppSurfaceDocsHarvestFailurePreflightService(
        AppSurfaceDocsOptions options,
        DocAggregator aggregator,
        ILogger<AppSurfaceDocsHarvestFailurePreflightService> logger,
        AppSurfaceDocsHarvestCoordinator? coordinator = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _coordinator = coordinator;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Checks harvest health during host startup when strict harvest failure is enabled.
    /// </summary>
    /// <param name="cancellationToken">Token observed while waiting for the cached harvest-health snapshot.</param>
    /// <returns>A completed task when strict mode is disabled or the aggregate status is not failed.</returns>
    /// <exception cref="AppSurfaceDocsHarvestFailedException">
    /// Thrown when <see cref="AppSurfaceDocsHarvestOptions.FailOnFailure"/> is enabled and the aggregate harvest status is
    /// <see cref="DocHarvestHealthStatus.Failed"/>.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var harvestOptions = _options.Harvest;
        if (harvestOptions is null)
        {
            return;
        }

        if (harvestOptions.StartupMode == AppSurfaceDocsHarvestStartupMode.Disabled
            && !harvestOptions.FailOnFailure)
        {
            return;
        }

        if (harvestOptions.StartupMode == AppSurfaceDocsHarvestStartupMode.Background
            && !harvestOptions.FailOnFailure)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await EnsureHarvestStarted();
                    }
                    catch (Exception ex) when (!IsFatalException(ex))
                    {
                        _logger.LogWarning(ex, "AppSurface Docs background harvest warmup failed.");
                    }
                },
                CancellationToken.None);
            return;
        }

        var health = await EnsureHarvestStarted().WaitAsync(cancellationToken);
        if (!harvestOptions.FailOnFailure || health.Status != DocHarvestHealthStatus.Failed)
        {
            return;
        }

        var exception = new AppSurfaceDocsHarvestFailedException(health);
        _logger.LogCritical(
            exception,
            "AppSurface Docs strict harvest failed during startup. {Message}",
            exception.Message);

        throw exception;
    }

    /// <summary>
    /// Stops the preflight service.
    /// </summary>
    /// <param name="cancellationToken">Unused cancellation token supplied by the host.</param>
    /// <returns>A completed task because the preflight owns no background work.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Shares request readiness when available, preserving hosts that remove the optional coordinator.</summary>
    private Task<DocHarvestHealthSnapshot> EnsureHarvestStarted() =>
        _coordinator?.EnsureStarted() ?? _aggregator.GetHarvestHealthAsync();

    private static bool IsFatalException(Exception exception)
    {
        return exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
    }
}
