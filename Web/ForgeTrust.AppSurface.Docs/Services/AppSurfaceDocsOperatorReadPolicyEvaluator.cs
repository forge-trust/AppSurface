using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Evaluates host-owned ASP.NET Core policies for AppSurface Docs diagnostics read surfaces.
/// </summary>
/// <remarks>
/// This adapter is intentionally local to the Docs package. It keeps the diagnostics-read policy independent from the
/// internal Auth.AspNetCore evaluator while preserving ASP.NET Core policy authentication, challenge, forbid, and setup
/// failure semantics for the harvest progress stream.
/// </remarks>
internal static class AppSurfaceDocsOperatorReadPolicyEvaluator
{
    private const string DocsUrl = "https://forge-trust.com/docs/packages/README.md.html#protect-diagnostics-reads";

    /// <summary>
    /// Evaluates the configured diagnostics read policy against the current request.
    /// </summary>
    /// <param name="httpContext">Current HTTP request context.</param>
    /// <param name="policyName">Non-blank host-owned ASP.NET Core authorization policy name.</param>
    /// <param name="cancellationToken">Cancellation observed before and during policy lookup.</param>
    /// <returns>A passive AppSurface auth result representing the policy outcome.</returns>
    public static async ValueTask<AppSurfaceAuthResult> AuthorizeAsync(
        HttpContext httpContext,
        string policyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        cancellationToken.ThrowIfCancellationRequested();

        var requestServices = httpContext.RequestServices;
        if (requestServices is null)
        {
            return MissingServices(
                typeof(IServiceProvider),
                policyName,
                "missing_request_services",
                "Problem: AppSurface Docs could not evaluate diagnostics read authorization. Likely cause: the current request has no request service provider. Fix: evaluate the stream inside an ASP.NET Core request. Docs: " + DocsUrl);
        }

        var policyProviderResult = ResolveRequiredService<IAuthorizationPolicyProvider>(
            requestServices,
            policyName,
            "missing_authorization_policy_provider",
            "Problem: AppSurface Docs could not find ASP.NET Core authorization policies. Likely cause: the host did not call services.AddAuthorization(...). Fix: register authorization services and the configured Diagnostics:OperatorReadPolicy. Docs: " + DocsUrl);
        if (policyProviderResult.Failure is not null)
        {
            return policyProviderResult.Failure;
        }

        var policyEvaluatorResult = ResolveRequiredService<IPolicyEvaluator>(
            requestServices,
            policyName,
            "missing_policy_evaluator",
            "Problem: AppSurface Docs could not evaluate ASP.NET Core authorization policies. Likely cause: authorization policy evaluator services are missing. Fix: register normal ASP.NET Core authorization services. Docs: " + DocsUrl);
        if (policyEvaluatorResult.Failure is not null)
        {
            return policyEvaluatorResult.Failure;
        }

        var policy = await policyProviderResult.Service.GetPolicyAsync(policyName).WaitAsync(cancellationToken);
        if (policy is null)
        {
            return AppSurfaceAuthResult.MissingPolicy(
                message:
                $"Problem: AppSurface Docs diagnostics read policy '{policyName}' was not found. Likely cause: Diagnostics:OperatorReadPolicy names an unregistered policy. Fix: register the policy or update the configured name. Docs: {DocsUrl}",
                metadata: Metadata("missing_policy", typeof(AuthorizationPolicy), policyName));
        }

        return await AuthorizePolicyAsync(
            httpContext,
            policyEvaluatorResult.Service,
            policy,
            policyName,
            cancellationToken);
    }

    /// <summary>
    /// Evaluates the complete ASP.NET Core authorization metadata applied to a named Docs endpoint group.
    /// </summary>
    /// <remarks>
    /// This preserves default-policy, role, authentication-scheme, and multiple-policy semantics for RazorWire streams,
    /// which are not conventional HTTP endpoints and therefore cannot receive ASP.NET Core endpoint authorization
    /// automatically.
    /// </remarks>
    /// <param name="httpContext">Current HTTP request context.</param>
    /// <param name="authorizationData">Authorization metadata captured from the named Docs endpoint group.</param>
    /// <param name="cancellationToken">Cancellation observed before and during policy lookup.</param>
    /// <returns>A passive AppSurface auth result representing the combined policy outcome.</returns>
    public static async ValueTask<AppSurfaceAuthResult> AuthorizeAsync(
        HttpContext httpContext,
        IReadOnlyList<IAuthorizeData> authorizationData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(authorizationData);
        if (authorizationData.Count == 0)
        {
            throw new ArgumentException("Authorization metadata must contain at least one requirement.", nameof(authorizationData));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var requestServices = httpContext.RequestServices;
        const string policyDescription = "the named Docs endpoint authorization requirements";
        if (requestServices is null)
        {
            return MissingServices(
                typeof(IServiceProvider),
                policyDescription,
                "missing_request_services",
                "Problem: AppSurface Docs could not evaluate endpoint authorization. Likely cause: the current request has no request service provider. Fix: evaluate the stream inside an ASP.NET Core request. Docs: " + DocsUrl);
        }

        var policyProviderResult = ResolveRequiredService<IAuthorizationPolicyProvider>(
            requestServices,
            policyDescription,
            "missing_authorization_policy_provider",
            "Problem: AppSurface Docs could not find ASP.NET Core authorization policies. Likely cause: the host did not call services.AddAuthorization(...). Fix: register authorization services for the named Docs endpoint requirements. Docs: " + DocsUrl);
        if (policyProviderResult.Failure is not null)
        {
            return policyProviderResult.Failure;
        }

        var policyEvaluatorResult = ResolveRequiredService<IPolicyEvaluator>(
            requestServices,
            policyDescription,
            "missing_policy_evaluator",
            "Problem: AppSurface Docs could not evaluate ASP.NET Core authorization policies. Likely cause: authorization policy evaluator services are missing. Fix: register normal ASP.NET Core authorization services. Docs: " + DocsUrl);
        if (policyEvaluatorResult.Failure is not null)
        {
            return policyEvaluatorResult.Failure;
        }

        foreach (var policyName in authorizationData
                     .Select(data => data.Policy)
                     .Where(policyName => !string.IsNullOrWhiteSpace(policyName))
                     .Select(policyName => policyName!.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            if (await policyProviderResult.Service.GetPolicyAsync(policyName).WaitAsync(cancellationToken) is null)
            {
                return AppSurfaceAuthResult.MissingPolicy(
                    message:
                    $"Problem: AppSurface Docs endpoint authorization policy '{policyName}' was not found. Likely cause: a named Docs handle references an unregistered host policy. Fix: register the policy or update RequireAuthorization(...). Docs: {DocsUrl}",
                    metadata: Metadata("missing_policy", typeof(AuthorizationPolicy), policyName));
            }
        }

        var combinedPolicy = await AuthorizationPolicy.CombineAsync(policyProviderResult.Service, authorizationData)
            .WaitAsync(cancellationToken);
        if (combinedPolicy is null)
        {
            return AppSurfaceAuthResult.MissingPolicy(
                message:
                $"Problem: AppSurface Docs could not combine the named Docs endpoint authorization requirements. Likely cause: the host did not configure a default authorization policy. Fix: register a default policy or use a named RequireAuthorization(...) policy. Docs: {DocsUrl}",
                metadata: Metadata("missing_policy", typeof(AuthorizationPolicy), policyDescription));
        }

        return await AuthorizePolicyAsync(
            httpContext,
            policyEvaluatorResult.Service,
            combinedPolicy,
            policyDescription,
            cancellationToken);
    }

    private static async ValueTask<AppSurfaceAuthResult> AuthorizePolicyAsync(
        HttpContext httpContext,
        IPolicyEvaluator policyEvaluator,
        AuthorizationPolicy policy,
        string policyName,
        CancellationToken cancellationToken)
    {
        AuthenticateResult authentication;
        try
        {
            authentication = await policyEvaluator.AuthenticateAsync(policy, httpContext);
        }
        catch (InvalidOperationException exception) when (IsMissingAuthenticationSetupFailure(exception))
        {
            return MissingServices(
                typeof(IAuthenticationService),
                policyName,
                "missing_authentication_service",
                "Problem: AppSurface Docs could not authenticate the diagnostics read request. Likely cause: authentication services, schemes, or handlers are missing. Fix: call services.AddAuthentication(...) and register the schemes required by Diagnostics:OperatorReadPolicy. Docs: " + DocsUrl);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var authorization = await policyEvaluator.AuthorizeAsync(
            policy,
            authentication,
            httpContext,
            httpContext);

        if (authorization.Succeeded)
        {
            return AppSurfaceAuthResult.Allowed();
        }

        return authorization.Challenged
            ? AppSurfaceAuthResult.Challenge(
                message:
                $"Problem: AppSurface Docs diagnostics read authorization challenged the caller. Likely cause: the request is anonymous or missing required credentials for '{policyName}'. Fix: sign in or send valid host credentials. Docs: {DocsUrl}",
                metadata: Metadata("authorization_challenged", typeof(IPolicyEvaluator), policyName))
            : AppSurfaceAuthResult.Forbid(
                message:
                $"Problem: AppSurface Docs diagnostics read authorization forbade the caller. Likely cause: the authenticated user does not satisfy '{policyName}'. Fix: grant the required host claim/role or use a maintainer identity. Docs: {DocsUrl}",
                metadata: Metadata("authorization_forbidden", typeof(IPolicyEvaluator), policyName));
    }

    private static ServiceResolution<TService> ResolveRequiredService<TService>(
        IServiceProvider requestServices,
        string policyName,
        string diagnosticCode,
        string message)
        where TService : notnull
    {
        try
        {
            var service = requestServices.GetService<TService>();
            if (service is not null)
            {
                return new ServiceResolution<TService>(service);
            }
        }
        catch (InvalidOperationException exception) when (IsMissingServiceResolutionFailure(exception))
        {
            // Missing transitive framework services are host setup failures, not auth denials.
        }

        return new ServiceResolution<TService>(
            MissingServices(typeof(TService), policyName, diagnosticCode, message));
    }

    private static AppSurfaceAuthResult MissingServices(
        Type missingService,
        string policyName,
        string diagnosticCode,
        string message)
    {
        return AppSurfaceAuthResult.MissingServices(
            message: message,
            metadata: Metadata(diagnosticCode, missingService, policyName));
    }

    private static IReadOnlyDictionary<string, string> Metadata(string code, Type serviceType, string policyName)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["service"] = serviceType.FullName ?? serviceType.Name,
            ["policy"] = policyName,
            ["docs"] = DocsUrl
        };
    }

    private static bool IsMissingServiceResolutionFailure(InvalidOperationException exception)
    {
        return exception.Message.StartsWith("Unable to resolve service for type ", StringComparison.Ordinal)
            || exception.Message.StartsWith("No service for type ", StringComparison.Ordinal);
    }

    private static bool IsMissingAuthenticationSetupFailure(InvalidOperationException exception)
    {
        return exception.Message.StartsWith("Unable to find the required 'IAuthenticationService' service.", StringComparison.Ordinal)
            || exception.Message.StartsWith("No authentication handlers are registered.", StringComparison.Ordinal)
            || exception.Message.StartsWith("No authentication handler is registered for the scheme ", StringComparison.Ordinal)
            || exception.Message.StartsWith(
                "No authenticationScheme was specified, and there was no DefaultAuthenticateScheme found.",
                StringComparison.Ordinal);
    }

    private sealed class ServiceResolution<TService>
        where TService : notnull
    {
        public ServiceResolution(TService service)
        {
            Service = service;
        }

        public ServiceResolution(AppSurfaceAuthResult failure)
        {
            Failure = failure;
        }

        public TService Service { get; } = default!;

        public AppSurfaceAuthResult? Failure { get; }
    }
}
