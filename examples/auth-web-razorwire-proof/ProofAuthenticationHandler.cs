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
    public const string SchemeName = "Proof";
    public const string HeaderName = "X-Proof-User";
    public const string QueryStateName = "proofUser";

    public ProofAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

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

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = CreatePrincipal(ResolveProofUser(Request));
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

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
    public const string Anonymous = "anonymous";
    public const string Operator = "operator";
    public const string Viewer = "viewer";

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
