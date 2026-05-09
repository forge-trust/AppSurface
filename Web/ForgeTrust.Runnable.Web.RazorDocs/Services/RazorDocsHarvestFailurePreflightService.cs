using ForgeTrust.Runnable.Web.RazorDocs.Models;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

internal sealed class RazorDocsHarvestFailurePreflightService : IHostedService
{
    private readonly RazorDocsOptions _options;
    private readonly DocAggregator _aggregator;
    private readonly ILogger<RazorDocsHarvestFailurePreflightService> _logger;

    public RazorDocsHarvestFailurePreflightService(
        RazorDocsOptions options,
        DocAggregator aggregator,
        ILogger<RazorDocsHarvestFailurePreflightService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            "RazorDocs strict harvest failed during startup. {Message}",
            exception.Message);

        throw exception;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
