using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NamedCanaryLab;

/// <summary>Authenticates a Development-only local operator without logging its credential.</summary>
internal sealed class CanaryLabAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "NamedCanaryLabOperator";

    private readonly CanaryLabSettings _settings;

    public CanaryLabAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        CanaryLabSettings settings)
        : base(options, logger, encoder)
    {
        _settings = settings;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.Ordinal)
            || authorization.Length <= prefix.Length
            || authorization.Length > 16 * 1024)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!_settings.MatchesOperatorToken(authorization[prefix.Length..]))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "named-canary-lab-operator")],
            SchemeName);
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Names the host-owned authorization policy used by the lab routes.</summary>
internal static class CanaryLabPolicies
{
    public const string OperatorsOnly = "NamedCanaryLabOperatorsOnly";
}
