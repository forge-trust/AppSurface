using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AuthWebRazorWireProofExample;

/// <summary>
/// Local proof-only authentication handler for the AppSurface Auth Web/RazorWire sample.
/// </summary>
/// <remarks>
/// This handler exists only so the sample can run without cookies, OAuth, OIDC, JWT, ASP.NET Identity, or any external
/// identity provider. Real hosts should keep their normal ASP.NET Core authentication handlers and let the AppSurface
/// adapter observe the populated request principal. The <c>X-Proof-User</c> header takes precedence over the URL-local
/// browser proof state so curl checks stay deterministic.
/// </remarks>
internal sealed class ProofAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// Authentication scheme name used only by the proof app.
    /// </summary>
    public const string SchemeName = "Proof";

    /// <summary>
    /// Header used by curl and tests to select a proof persona. A non-empty header value takes precedence
    /// over the browser query parameter even when the value normalizes to <c>anonymous</c>.
    /// </summary>
    public const string HeaderName = "X-Proof-User";

    /// <summary>
    /// Query parameter used by the browser persona switch to keep proof state in the URL.
    /// </summary>
    public const string QueryStateName = "proofUser";

    public ProofAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// Builds a claims principal for a supported proof persona.
    /// </summary>
    /// <param name="user">
    /// Persona value from the proof header or query string. Supported values are <c>operator</c> and
    /// <c>viewer</c>; unknown values are treated as <c>anonymous</c>.
    /// </param>
    /// <returns>
    /// A principal with the sample subject and role claims for supported authenticated personas, or
    /// <see langword="null"/> for anonymous/unknown input.
    /// </returns>
    /// <remarks>
    /// This method is intentionally proof-only. It models the claims a real host would already have after
    /// its normal authentication handler runs.
    /// </remarks>
    public static ClaimsPrincipal? CreatePrincipal(string? user)
    {
        Claim[]? claims = ProofPersona.Normalize(user) switch
        {
            ProofPersona.Operator =>
            [
                new Claim(AuthProofPolicy.SubjectClaimType, "operator-1"),
                new Claim(AuthProofPolicy.RoleClaimType, AuthProofPolicy.OperatorRole),
            ],
            ProofPersona.Viewer =>
            [
                new Claim(AuthProofPolicy.SubjectClaimType, "viewer-1"),
                new Claim(AuthProofPolicy.RoleClaimType, "viewer"),
            ],
            _ => null,
        };

        return claims is null
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
    }

    /// <summary>
    /// Resolves the proof persona for the current request and returns an authentication result.
    /// </summary>
    /// <returns>
    /// <see cref="AuthenticateResult.Success(AuthenticationTicket)"/> for <c>viewer</c> or
    /// <c>operator</c>, otherwise <see cref="AuthenticateResult.NoResult()"/> so the host policy sees an
    /// anonymous request.
    /// </returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = CreatePrincipal(ResolveProofUser(Request));
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    /// <summary>
    /// Reads the proof persona from the request with curl-friendly header precedence.
    /// </summary>
    /// <param name="request">The current ASP.NET Core request.</param>
    /// <returns>
    /// The raw header value when <c>X-Proof-User</c> is non-empty; otherwise the raw <c>proofUser</c> query
    /// value.
    /// </returns>
    /// <remarks>
    /// Header precedence is based on the raw header being present, not on whether it normalizes to an
    /// authenticated persona. That keeps command-line checks deterministic and prevents URL state from
    /// silently overriding an explicit header.
    /// </remarks>
    private static string ResolveProofUser(HttpRequest request)
    {
        var headerUser = request.Headers[HeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(headerUser))
        {
            return headerUser;
        }

        return request.Query[QueryStateName].ToString();
    }
}

/// <summary>
/// Sample-local proof personas supported by the browser switch, URL state, and curl header.
/// </summary>
internal static class ProofPersona
{
    /// <summary>
    /// Anonymous proof persona. This is the fallback for empty or unknown persona input.
    /// </summary>
    public const string Anonymous = "anonymous";

    /// <summary>
    /// Authenticated proof persona with the operator role required by the <c>OperatorsOnly</c> policy.
    /// </summary>
    public const string Operator = "operator";

    /// <summary>
    /// Authenticated proof persona without the operator role.
    /// </summary>
    public const string Viewer = "viewer";

    /// <summary>
    /// Normalizes raw proof persona input to the supported sample-local persona set.
    /// </summary>
    /// <param name="value">Header, query, or test persona value.</param>
    /// <returns>
    /// <c>operator</c>, <c>viewer</c>, or <c>anonymous</c>. Unknown values deliberately normalize to
    /// <c>anonymous</c> so the sample has one predictable fallback path.
    /// </returns>
    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Operator => Operator,
            Viewer => Viewer,
            _ => Anonymous,
        };
    }
}
