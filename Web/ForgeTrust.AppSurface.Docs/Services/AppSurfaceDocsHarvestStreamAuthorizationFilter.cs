using ForgeTrust.AppSurface.Auth;
using ForgeTrust.RazorWire.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Enforces AppSurface Docs' reserved harvest-stream gate before host RazorWire stream authorizers run.
/// </summary>
/// <remarks>
/// This filter protects the docs-owned harvest progress channel even when a host registers an
/// <see cref="IRazorWireStreamAuthorizer"/> after <c>AddAppSurfaceDocs()</c> and thereby replaces the normal Docs wrapper
/// in Microsoft DI. The filter only gates the reserved AppSurface Docs channel; non-docs channels continue to the host
/// authorizer unchanged.
/// </remarks>
internal sealed class AppSurfaceDocsHarvestStreamAuthorizationFilter : IRazorWireStreamAuthorizationFilter
{
    private readonly AppSurfaceDocsOptions _options;
    private readonly IHostEnvironment _environment;

    /// <summary>
    /// Creates the harvest stream gate from normalized Docs options and the current host environment.
    /// </summary>
    /// <param name="options">Docs options that define route visibility and the shared diagnostics read policy.</param>
    /// <param name="environment">The current host environment.</param>
    public AppSurfaceDocsHarvestStreamAuthorizationFilter(AppSurfaceDocsOptions options, IHostEnvironment environment)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>
    /// Applies hidden-route and shared read-policy checks to the AppSurface Docs harvest progress channel.
    /// </summary>
    /// <param name="context">Current stream authorization context.</param>
    /// <returns>
    /// <see langword="null"/> for non-docs channels or when the normal Docs wrapper is already active; otherwise a gate
    /// result that denies hidden routes or read-policy failures and lets host authorizers narrow successful reads.
    /// </returns>
    public async ValueTask<AppSurfaceAuthResult?> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!AppSurfaceDocsStreamAuthorization.IsHarvestProgressChannel(context.Channel)
            || IsNormalDocsWrapperActive(context))
        {
            return null;
        }

        if (!AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(_options, _environment))
        {
            return AppSurfaceAuthResult.Forbidden();
        }

        var readPolicy = _options.Diagnostics.OperatorReadPolicy;
        if (string.IsNullOrWhiteSpace(readPolicy))
        {
            return null;
        }

        var policyResult = await AppSurfaceDocsOperatorReadPolicyEvaluator.AuthorizeAsync(
            context.HttpContext,
            readPolicy,
            context.HttpContext.RequestAborted);

        return policyResult.IsAllowed ? AppSurfaceAuthResult.Allowed() : policyResult;
    }

    private static bool IsNormalDocsWrapperActive(RazorWireStreamAuthorizationContext context)
    {
        return context.HttpContext.RequestServices?.GetService<IRazorWireStreamAuthorizer>()
            is AppSurfaceDocsHarvestStreamAuthorizer;
    }
}
