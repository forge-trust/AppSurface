using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

internal sealed class AppSurfaceAspNetCorePolicyEvaluator : IAppSurfaceAspNetCorePolicyEvaluator
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppSurfaceAspNetCoreAuthContextMapper _mapper;

    public AppSurfaceAspNetCorePolicyEvaluator(
        IHttpContextAccessor httpContextAccessor,
        AppSurfaceAspNetCoreAuthContextMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(mapper);

        _httpContextAccessor = httpContextAccessor;
        _mapper = mapper;
    }

    public async Task<AppSurfaceAuthResult> AuthorizeAsync(
        string policyName,
        object? resource = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        cancellationToken.ThrowIfCancellationRequested();

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return MissingServices(
                typeof(HttpContext),
                "missing_http_context",
                policyName,
                "No current ASP.NET Core HTTP request is available. Resolve AppSurface ASP.NET Core auth services inside a request.");
        }

        var requestServices = httpContext.RequestServices;
        if (requestServices is null)
        {
            return MissingServices(
                typeof(IServiceProvider),
                "missing_request_services",
                policyName,
                "The current HTTP request has no request service provider.");
        }

        var policyProviderResult = ResolveRequiredService<IAuthorizationPolicyProvider>(
            requestServices,
            "missing_authorization_policy_provider",
            policyName,
            "ASP.NET Core authorization policies are unavailable. Call services.AddAuthorization(...) in the host.");
        if (policyProviderResult.Failure is not null)
        {
            return policyProviderResult.Failure;
        }

        var policyEvaluatorResult = ResolveRequiredService<IPolicyEvaluator>(
            requestServices,
            "missing_policy_evaluator",
            policyName,
            "ASP.NET Core policy evaluation services are unavailable. Call services.AddAuthorization(...) in the host.");
        if (policyEvaluatorResult.Failure is not null)
        {
            return policyEvaluatorResult.Failure;
        }

        var policy = await policyProviderResult.Service.GetPolicyAsync(policyName).WaitAsync(cancellationToken);
        if (policy is null)
        {
            return AppSurfaceAuthResult.MissingPolicy(
                AppSurfaceAuthContext.Anonymous,
                $"ASP.NET Core authorization policy '{policyName}' was not found.",
                AppSurfaceAspNetCoreAuthDiagnostics.Policy("missing_policy", policyName));
        }

        cancellationToken.ThrowIfCancellationRequested();

        AuthenticateResult authentication;
        try
        {
            authentication = await policyEvaluatorResult.Service.AuthenticateAsync(policy, httpContext);
        }
        catch (InvalidOperationException exception) when (IsMissingAuthenticationSetupFailure(exception))
        {
            return MissingServices(
                typeof(IAuthenticationService),
                "missing_authentication_service",
                policyName,
                "ASP.NET Core authentication services or handlers are unavailable. Call services.AddAuthentication(...) and register the schemes required by the policy.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var authorizationResource = resource ?? httpContext;
        var authorization = await policyEvaluatorResult.Service.AuthorizeAsync(
            policy,
            authentication,
            httpContext,
            authorizationResource);
        var principal = authentication.Principal ?? httpContext.User;
        var contextSnapshot = _mapper.Map(principal);
        if (contextSnapshot.Failure is not null)
        {
            return contextSnapshot.Failure;
        }

        if (authorization.Succeeded)
        {
            return AppSurfaceAuthResult.Allowed(contextSnapshot.Context);
        }

        return authorization.Challenged
            ? AppSurfaceAuthResult.Challenge(contextSnapshot.Context)
            : AppSurfaceAuthResult.Forbid(contextSnapshot.Context);
    }

    private static AppSurfaceAuthResult MissingServices(
        Type missingService,
        string diagnosticCode,
        string policyName,
        string message)
    {
        return AppSurfaceAuthResult.MissingServices(
            AppSurfaceAuthContext.Anonymous,
            message,
            AppSurfaceAspNetCoreAuthDiagnostics.MissingService(missingService, diagnosticCode, policyName));
    }

    private static ServiceResolution<TService> ResolveRequiredService<TService>(
        IServiceProvider requestServices,
        string diagnosticCode,
        string policyName,
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
            MissingServices(
                typeof(TService),
                diagnosticCode,
                policyName,
                message));
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
