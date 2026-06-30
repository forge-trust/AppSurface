using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Logs a startup warning when non-development diagnostics reads are exposed without the shared read policy.
/// </summary>
/// <remarks>
/// The warning is advisory and does not fail startup. This preserves compatibility for hosts that intentionally enforce
/// diagnostics access through their application pipeline, reverse proxy, or network boundary, while making risky package
/// exposure visible during deployment.
/// </remarks>
internal sealed class AppSurfaceDocsOperatorReadPolicyWarningService : IHostedService
{
    private const int ExposedDiagnosticsWithoutReadPolicyEventId = 57801;
    private const string DocsUrl = "https://forge-trust.com/docs/packages/README.md.html#protect-diagnostics-reads";

    private readonly AppSurfaceDocsOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AppSurfaceDocsOperatorReadPolicyWarningService> _logger;

    /// <summary>
    /// Creates the startup warning service.
    /// </summary>
    /// <param name="options">The normalized AppSurface Docs options.</param>
    /// <param name="environment">The current host environment.</param>
    /// <param name="logger">Logger used for the structured startup warning.</param>
    public AppSurfaceDocsOperatorReadPolicyWarningService(
        AppSurfaceDocsOptions options,
        IHostEnvironment environment,
        ILogger<AppSurfaceDocsOperatorReadPolicyWarningService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_environment.IsDevelopment())
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(_options.Diagnostics.OperatorReadPolicy))
        {
            return Task.CompletedTask;
        }

        var exposedSurfaces = ResolveExposedDiagnosticsSurfaces();
        if (exposedSurfaces.Count == 0)
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning(
            new EventId(ExposedDiagnosticsWithoutReadPolicyEventId, "AppSurfaceDocsDiagnosticsReadPolicyMissing"),
            "AppSurface Docs diagnostics read surfaces are exposed without AppSurfaceDocs:Diagnostics:OperatorReadPolicy. Problem: exposed diagnostics can reveal harvest or route state. Likely cause: {Cause}. Fix: configure Diagnostics:OperatorReadPolicy or verify host, reverse-proxy, or network authorization for {Surfaces}. Docs: {DocsUrl}",
            "non-development diagnostics exposure was enabled without the shared read policy",
            string.Join(", ", exposedSurfaces),
            DocsUrl);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> ResolveExposedDiagnosticsSurfaces()
    {
        var surfaces = new List<string>();
        var health = _options.Harvest.Health;
        if (health.ExposeRoutes == AppSurfaceDocsHarvestHealthExposure.Always)
        {
            surfaces.Add("_harvest");
            surfaces.Add(AppSurfaceDocsStreamAuthorization.HarvestProgressChannel);
            if (string.IsNullOrWhiteSpace(health.AuthorizationPolicy))
            {
                surfaces.Add("_health");
                surfaces.Add("_health.json");
            }
        }

        if (_options.Diagnostics.ExposeRouteInspector == AppSurfaceDocsHarvestHealthExposure.Always)
        {
            surfaces.Add("_routes");
            surfaces.Add("_routes.json");
        }

        return surfaces;
    }
}
