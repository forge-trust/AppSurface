using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Endpoint filter that maps a host-owned ASP.NET Core policy into AppSurface auth results.
/// </summary>
internal sealed class AppSurfacePolicyEndpointFilter : IEndpointFilter
{
    private readonly string _policyName;

    /// <summary>
    /// Creates a policy endpoint filter for the provided policy name.
    /// </summary>
    /// <param name="policyName">The non-blank policy name evaluated by the filter.</param>
    public AppSurfacePolicyEndpointFilter(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        _policyName = policyName;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var evaluator = context.HttpContext.RequestServices.GetService<IAppSurfaceAspNetCorePolicyEvaluator>();
        if (evaluator is null)
        {
            var missingEvaluator = AppSurfaceAuthResult.MissingServices(
                AppSurfaceAuthContext.Anonymous,
                "AppSurface ASP.NET Core auth services are unavailable. Call services.AddAppSurfaceAspNetCoreAuth(...) in the host.",
                AppSurfaceAspNetCoreAuthDiagnostics.MissingService(
                    typeof(IAppSurfaceAspNetCorePolicyEvaluator),
                    "missing_appsurface_policy_evaluator",
                    _policyName));
            return AppSurfacePolicyProblemDetailsMapper.ToResult(missingEvaluator, _policyName);
        }

        var result = await evaluator.AuthorizeAsync(_policyName, cancellationToken: context.HttpContext.RequestAborted);
        if (result.IsAllowed)
        {
            return await next(context);
        }

        return AppSurfacePolicyProblemDetailsMapper.ToResult(result, _policyName);
    }
}
