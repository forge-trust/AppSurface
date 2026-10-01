using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Examples.DurableExternalActivation;

/// <summary>Stores the required local-only development token for the sample bearer scheme.</summary>
internal sealed class DevelopmentBearerOptions : AuthenticationSchemeOptions
{
    /// <summary>Gets or sets the nonblank development token captured before the server starts.</summary>
    internal string Token { get; set; } = string.Empty;
}

/// <summary>Authenticates the configured local development token without issuing a challenge body.</summary>
internal sealed class DevelopmentBearerHandler(
    IOptionsMonitor<DevelopmentBearerOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<DevelopmentBearerOptions>(options, logger, encoder)
{
    /// <summary>Gets the stable authentication scheme name used by the local sample.</summary>
    internal const string SchemeName = "DurableActivationDevelopmentBearer";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var supplied = Encoding.UTF8.GetBytes(header[7..]);
        var expected = Encoding.UTF8.GetBytes(Options.Token);
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer credential."));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "development-operator"),
            new Claim("permission", "durable-activation"),
        ],
        SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
