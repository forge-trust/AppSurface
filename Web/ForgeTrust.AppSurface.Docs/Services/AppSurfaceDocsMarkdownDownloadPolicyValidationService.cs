using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Validates that an enabled protected Markdown download route names a policy the host actually registered.
/// </summary>
/// <remarks>
/// Options validation catches missing or blank names. This startup check catches a misspelled name before AppSurface Docs
/// maps an authorization-protected raw-source endpoint. The package deliberately resolves the host policy but never
/// registers authentication schemes, identities, or a policy of its own.
/// </remarks>
internal sealed class AppSurfaceDocsMarkdownDownloadPolicyValidationService : IHostedService
{
    private readonly AppSurfaceDocsOptions _options;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes the policy validation service.
    /// </summary>
    public AppSurfaceDocsMarkdownDownloadPolicyValidationService(
        AppSurfaceDocsOptions options,
        IServiceProvider services)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Resolves the configured named policy when Markdown download is enabled.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var download = _options.MarkdownDownload;
        if (download?.Enabled != true)
        {
            return;
        }

        var policyName = download.AuthorizationPolicy;
        if (string.IsNullOrWhiteSpace(policyName))
        {
            throw new InvalidOperationException(
                "AppSurfaceDocs:MarkdownDownload:AuthorizationPolicy is required when Markdown download is enabled.");
        }

        var policyProvider = _services.GetService<IAuthorizationPolicyProvider>();
        var policy = policyProvider is null
            ? null
            : await policyProvider.GetPolicyAsync(policyName);
        if (policy is null)
        {
            throw new InvalidOperationException(
                $"AppSurface Docs Markdown download requires the host to register authorization policy '{policyName}'.");
        }
    }

    /// <summary>
    /// Stops the validation service.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
