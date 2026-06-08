using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Evaluates host-owned ASP.NET Core authorization policies and returns neutral AppSurface auth results.
/// </summary>
/// <remarks>
/// This evaluator does not register or own schemes, policies, middleware, challenges, forbids, redirects, cookies,
/// OIDC, or Identity. It resolves the host policy services from the current request, delegates authentication and
/// authorization to ASP.NET Core, and maps the resulting principal through <see cref="AppSurfaceAspNetCoreAuthContextMapper" />.
/// Missing request services, missing policies, missing authentication setup, and authenticated principals without a
/// subject are reported as setup failures with safe diagnostics; host handler exceptions still propagate.
/// </remarks>
internal sealed class AppSurfaceAspNetCorePolicyEvaluator : IAppSurfaceAspNetCorePolicyEvaluator
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppSurfaceAspNetCoreAuthContextMapper _mapper;

    /// <summary>
    /// Creates an evaluator for the current ASP.NET Core request scope.
    /// </summary>
    /// <param name="httpContextAccessor">Accessor used to find the current request and request services.</param>
    /// <param name="mapper">Mapper used to convert the evaluated principal into an AppSurface context.</param>
    public AppSurfaceAspNetCorePolicyEvaluator(
        IHttpContextAccessor httpContextAccessor,
        AppSurfaceAspNetCoreAuthContextMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(mapper);

        _httpContextAccessor = httpContextAccessor;
        _mapper = mapper;
    }

    /// <summary>
    /// Evaluates a named ASP.NET Core authorization policy and maps the outcome to an AppSurface result.
    /// </summary>
    /// <param name="policyName">The non-blank host policy name to evaluate.</param>
    /// <param name="resource">
    /// Optional resource passed to ASP.NET Core authorization handlers. When omitted, the current
    /// <see cref="HttpContext" /> is used, matching common ASP.NET Core request-policy behavior.
    /// </param>
    /// <param name="cancellationToken">Cancellation observed before and during policy lookup.</param>
    /// <returns>
    /// <see cref="AppSurfaceAuthOutcome.Allowed" /> for successful authorization,
    /// <see cref="AppSurfaceAuthOutcome.Challenge" /> for unauthenticated policy outcomes,
    /// <see cref="AppSurfaceAuthOutcome.Forbid" /> for authenticated denials, or a setup-failure result for missing
    /// host services, missing policies, or missing stable subject claims.
    /// </returns>
    /// <remarks>
    /// Call this after the host has configured normal authentication and authorization services and middleware. ASP.NET
    /// Core policy evaluation does not accept a cancellation token, so handler execution is not cancellable through this
    /// adapter. Invalid operation exceptions that indicate missing framework authentication setup are converted to
    /// missing-services results; other host exceptions propagate.
    /// </remarks>
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

    /// <summary>
    /// Creates a missing-services setup failure with safe ASP.NET Core auth adapter diagnostics.
    /// </summary>
    /// <param name="missingService">The service type that was expected from host setup.</param>
    /// <param name="diagnosticCode">Stable diagnostic code for the missing setup condition.</param>
    /// <param name="policyName">Policy name being evaluated when the setup failure occurred.</param>
    /// <param name="message">User-facing failure message that avoids request secrets.</param>
    /// <returns>A neutral setup-failure auth result.</returns>
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

    /// <summary>
    /// Resolves a required request service and converts known missing-framework-service failures into setup failures.
    /// </summary>
    /// <typeparam name="TService">The request service contract required for policy evaluation.</typeparam>
    /// <param name="requestServices">Current request service provider.</param>
    /// <param name="diagnosticCode">Stable diagnostic code to use if the service cannot be resolved.</param>
    /// <param name="policyName">Policy name being evaluated.</param>
    /// <param name="message">User-facing failure message that avoids request secrets.</param>
    /// <returns>A service resolution containing either the resolved service or a missing-services failure.</returns>
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

    /// <summary>
    /// Determines whether an <see cref="InvalidOperationException" /> represents missing DI setup.
    /// </summary>
    /// <param name="exception">The exception thrown while resolving a request service.</param>
    /// <returns><see langword="true" /> when the exception is a known missing-service resolution failure.</returns>
    private static bool IsMissingServiceResolutionFailure(InvalidOperationException exception)
    {
        return exception.Message.StartsWith("Unable to resolve service for type ", StringComparison.Ordinal)
            || exception.Message.StartsWith("No service for type ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether an authentication exception represents missing host authentication setup.
    /// </summary>
    /// <param name="exception">The exception thrown by ASP.NET Core policy authentication.</param>
    /// <returns><see langword="true" /> when the exception indicates missing authentication services or handlers.</returns>
    private static bool IsMissingAuthenticationSetupFailure(InvalidOperationException exception)
    {
        return exception.Message.StartsWith("Unable to find the required 'IAuthenticationService' service.", StringComparison.Ordinal)
            || exception.Message.StartsWith("No authentication handlers are registered.", StringComparison.Ordinal)
            || exception.Message.StartsWith("No authentication handler is registered for the scheme ", StringComparison.Ordinal)
            || exception.Message.StartsWith(
                "No authenticationScheme was specified, and there was no DefaultAuthenticateScheme found.",
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Represents either a resolved request service or the setup failure produced when resolution failed.
    /// </summary>
    /// <typeparam name="TService">The request service contract being resolved.</typeparam>
    private sealed class ServiceResolution<TService>
        where TService : notnull
    {
        /// <summary>
        /// Creates a successful service resolution.
        /// </summary>
        /// <param name="service">Resolved request service.</param>
        public ServiceResolution(TService service)
        {
            Service = service;
        }

        /// <summary>
        /// Creates a failed service resolution that should be returned to the caller.
        /// </summary>
        /// <param name="failure">Missing-services setup failure.</param>
        public ServiceResolution(AppSurfaceAuthResult failure)
        {
            Failure = failure;
        }

        /// <summary>
        /// Gets the resolved service when resolution succeeded.
        /// </summary>
        public TService Service { get; } = default!;

        /// <summary>
        /// Gets the setup failure when resolution failed.
        /// </summary>
        public AppSurfaceAuthResult? Failure { get; }
    }
}
