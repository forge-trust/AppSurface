using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Runs startup validation and harvest warmup for every finalized named AppSurface Docs runtime.
/// </summary>
/// <remarks>
/// Named Docs products own isolated options and aggregators, so the legacy singleton preflight services cannot validate
/// or warm them. This service runs after endpoint mapping, when the registry has constructed the immutable runtime
/// snapshots, and preserves the same Markdown-policy validation, diagnostics warning, and harvest startup semantics
/// that <c>AddAppSurfaceDocs()</c> provides for a legacy single-instance host.
/// </remarks>
internal sealed class AppSurfaceDocsNamedInstancePreflightService : IHostedService
{
    private readonly AppSurfaceDocsInstanceRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a named-instance startup preflight service.
    /// </summary>
    public AppSurfaceDocsNamedInstancePreflightService(
        AppSurfaceDocsInstanceRegistry registry,
        IServiceProvider services,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>
    /// Validates and warms every named Docs runtime.
    /// </summary>
    /// <param name="cancellationToken">Token observed by synchronous startup preflights.</param>
    /// <returns>A task that completes once all strict preflights complete or background warmups are scheduled.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in _registry.GetFinalizedRuntimes())
        {
            try
            {
                await new AppSurfaceDocsMarkdownDownloadPolicyValidationService(runtime.Options, _services)
                    .StartAsync(cancellationToken);
                await new AppSurfaceDocsOperatorReadPolicyWarningService(
                        runtime.Options,
                        _environment,
                        _loggerFactory.CreateLogger<AppSurfaceDocsOperatorReadPolicyWarningService>())
                    .StartAsync(cancellationToken);
                await new AppSurfaceDocsHarvestFailurePreflightService(
                        runtime.Options,
                        runtime.Aggregator,
                        _loggerFactory.CreateLogger<AppSurfaceDocsHarvestFailurePreflightService>())
                    .StartAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              and not OutOfMemoryException
                                              and not StackOverflowException
                                              and not AccessViolationException)
            {
                throw new InvalidOperationException(
                    $"AppSurface Docs instance '{runtime.Name}' failed startup preflight. {exception.Message}",
                    exception);
            }
        }
    }

    /// <summary>
    /// Stops the preflight service.
    /// </summary>
    /// <param name="cancellationToken">Unused cancellation token supplied by the host.</param>
    /// <returns>A completed task because per-instance preflights own no shutdown work.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
