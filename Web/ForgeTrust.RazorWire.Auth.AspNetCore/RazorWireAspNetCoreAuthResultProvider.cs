using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using ForgeTrust.RazorWire.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Auth.AspNetCore;

/// <summary>
/// Adapts host-owned ASP.NET Core policy evaluation to RazorWire auth projection.
/// </summary>
/// <remarks>
/// The provider delegates to <see cref="IAppSurfaceAspNetCorePolicyEvaluator"/>. It does not register policies,
/// choose schemes, call challenge or forbid handlers, redirect browsers, sign callers in or out, or mutate cookies.
/// </remarks>
internal sealed class RazorWireAspNetCoreAuthResultProvider : IRazorWireAuthResultProvider
{
    private readonly IServiceProvider _services;

    /// <summary>
    /// Creates a provider backed by the AppSurface ASP.NET Core auth adapter.
    /// </summary>
    /// <param name="services">Request services used to resolve the host-owned policy evaluator.</param>
    public RazorWireAspNetCoreAuthResultProvider(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public Task<AppSurfaceAuthResult> AuthorizeAsync(
        RazorWireAuthRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var policyEvaluator = _services.GetService<IAppSurfaceAspNetCorePolicyEvaluator>();
        if (policyEvaluator is null)
        {
            return Task.FromResult(AppSurfaceAuthResult.MissingServices(
                AppSurfaceAuthContext.Anonymous,
                "RazorWire ASP.NET Core auth projection requires AddAppSurfaceAspNetCoreAuth(...).",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RazorWireAuthDiagnostics.DiagnosticCodeMetadataKey] = RazorWireAuthDiagnostics.MissingAspNetCorePolicyEvaluator,
                }));
        }

        return policyEvaluator.AuthorizeAsync(request.PolicyName, request.Resource, cancellationToken);
    }
}
