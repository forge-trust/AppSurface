using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Auth.AspNetCore;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Auth.Testing;

/// <summary>
/// Decorates the host AppSurface policy evaluator with test-only persona diagnostics.
/// </summary>
/// <remarks>
/// The inner evaluator remains responsible for real ASP.NET Core policy evaluation and AppSurface result mapping. This
/// decorator pre-validates request-level persona selection from the private test transport and also converts late
/// unknown-persona markers set by authentication handlers into setup failures after the inner evaluator runs.
/// </remarks>
internal sealed class AppSurfaceTestAspNetCorePolicyEvaluator : IAppSurfaceAspNetCorePolicyEvaluator
{
    private readonly AppSurfaceTestInnerPolicyEvaluator _inner;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppSurfaceTestPersonaRegistry _personaRegistry;

    /// <summary>
    /// Creates a test diagnostic decorator for the current host policy evaluator.
    /// </summary>
    /// <param name="inner">The evaluator registration that was active before Auth.Testing added its decorator.</param>
    /// <param name="httpContextAccessor">Accessor used to inspect per-request test transport markers.</param>
    /// <param name="personaRegistry">Immutable test persona registry used to validate request-level persona selection.</param>
    public AppSurfaceTestAspNetCorePolicyEvaluator(
        AppSurfaceTestInnerPolicyEvaluator inner,
        IHttpContextAccessor httpContextAccessor,
        AppSurfaceTestPersonaRegistry personaRegistry)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        ArgumentNullException.ThrowIfNull(personaRegistry);

        _inner = inner;
        _httpContextAccessor = httpContextAccessor;
        _personaRegistry = personaRegistry;
    }

    /// <inheritdoc />
    public async Task<AppSurfaceAuthResult> AuthorizeAsync(
        string policyName,
        object? resource = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetUnknownPersona(_httpContextAccessor.HttpContext, out var personaName))
        {
            return UnknownPersona(policyName, personaName);
        }

        var result = await _inner.PolicyEvaluator.AuthorizeAsync(policyName, resource, cancellationToken);
        return TryGetUnknownPersona(_httpContextAccessor.HttpContext, out personaName)
            ? UnknownPersona(policyName, personaName)
            : result;
    }

    private bool TryGetUnknownPersona(HttpContext? httpContext, out string personaName)
    {
        if (httpContext is null)
        {
            personaName = string.Empty;
            return false;
        }

        var requestPersona = AppSurfaceTestPersonaRegistry.NormalizePersonaName(
            httpContext.Request.Headers[AppSurfaceTestAuthTransport.PersonaHeaderName].ToString());
        if (requestPersona.Length > 0 && !_personaRegistry.TryGet(requestPersona, out _))
        {
            personaName = requestPersona;
            return true;
        }

        if (httpContext.Items.TryGetValue(AppSurfaceTestAuthTransport.UnknownPersonaItemKey, out var value)
            && value is string text
            && !string.IsNullOrWhiteSpace(text))
        {
            personaName = text;
            return true;
        }

        personaName = string.Empty;
        return false;
    }

    private static AppSurfaceAuthResult UnknownPersona(string policyName, string personaName)
    {
        return AppSurfaceAuthResult.MissingSubject(
            AppSurfaceAuthContext.Anonymous,
            $"AppSurface test auth persona '{personaName}' is not registered.",
            PolicyMetadata(AppSurfaceTestAuthDiagnosticCodes.UnknownPersona, policyName));
    }

    private static Dictionary<string, string> PolicyMetadata(string diagnosticCode, string policyName)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppSurfaceAspNetCoreAuthMetadataKeys.DiagnosticCode] = diagnosticCode,
            [AppSurfaceAspNetCoreAuthMetadataKeys.PolicyName] = policyName,
        };
    }
}
