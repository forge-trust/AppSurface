using ForgeTrust.Runnable.Web.RazorDocs.Models;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Performs the optional strict RazorDocs harvest-health startup preflight.
/// </summary>
/// <remarks>
/// The service is always registered by <c>AddRazorDocs()</c>, but it is inert unless
/// <see cref="RazorDocsHarvestOptions.FailOnFailure"/> is enabled. Strict mode reads
/// <see cref="DocAggregator.GetHarvestHealthAsync(CancellationToken)"/> so startup observes the same cached snapshot
/// that docs requests use instead of running a second harvest pipeline.
/// </remarks>
internal sealed class RazorDocsHarvestFailurePreflightService : IHostedService
{
    private readonly RazorDocsOptions _options;
    private readonly DocAggregator _aggregator;
    private readonly ILogger<RazorDocsHarvestFailurePreflightService> _logger;

    /// <summary>
    /// Initializes a new instance of the strict harvest preflight service.
    /// </summary>
    /// <param name="options">The normalized RazorDocs options that contain the strict harvest policy.</param>
    /// <param name="aggregator">The docs aggregator used to read cached harvest health.</param>
    /// <param name="logger">The logger that records strict startup failures for operators.</param>
    public RazorDocsHarvestFailurePreflightService(
        RazorDocsOptions options,
        DocAggregator aggregator,
        ILogger<RazorDocsHarvestFailurePreflightService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Checks harvest health during host startup when strict harvest failure is enabled.
    /// </summary>
    /// <param name="cancellationToken">Token observed while waiting for the cached harvest-health snapshot.</param>
    /// <returns>A completed task when strict mode is disabled or the aggregate status is not failed.</returns>
    /// <exception cref="RazorDocsHarvestFailedException">
    /// Thrown when <see cref="RazorDocsHarvestOptions.FailOnFailure"/> is enabled and the aggregate harvest status is
    /// <see cref="DocHarvestHealthStatus.Failed"/>.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Harvest?.FailOnFailure is not true)
        {
            return;
        }

        var health = await _aggregator.GetHarvestHealthAsync(cancellationToken);
        if (health.Status != DocHarvestHealthStatus.Failed)
        {
            return;
        }

        var exception = new RazorDocsHarvestFailedException(health);
        _logger.LogCritical(
            exception,
            "RazorDocs strict harvest failed during startup. {Message}",
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
}
