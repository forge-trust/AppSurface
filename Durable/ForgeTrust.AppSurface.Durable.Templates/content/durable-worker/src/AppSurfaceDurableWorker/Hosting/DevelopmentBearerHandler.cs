using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Options;

namespace AppSurfaceDurableWorker.Hosting;

/// <summary>Stores the required, bounded local bearer token captured before the Development listener starts.</summary>
internal sealed class DevelopmentBearerOptions : AuthenticationSchemeOptions
{
    /// <summary>Gets or sets the nonempty Development token.</summary>
    internal string Token { get; set; } = string.Empty;
}

/// <summary>Authenticates only the configured Development bearer token using a fixed-size constant-time comparison.</summary>
internal sealed class DevelopmentBearerHandler(
    IOptionsMonitor<DevelopmentBearerOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<DevelopmentBearerOptions>(options, logger, encoder)
{
    /// <summary>Stable authentication scheme name for the local-only sample.</summary>
    internal const string SchemeName = "AppSurfaceTemplateDevelopmentBearer";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!IsValidBearerHeader(Request.Headers.Authorization, Options.Token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
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

    /// <summary>Validates one bounded bearer header without depending on HTTP parser whitespace normalization.</summary>
    /// <param name="values">Authorization header values.</param>
    /// <param name="configuredToken">Captured Development-only token.</param>
    /// <returns>True only for exactly one syntactically valid, constant-time matching bearer credential.</returns>
    internal static bool IsValidBearerHeader(StringValues values, string configuredToken)
    {
        ArgumentNullException.ThrowIfNull(configuredToken);
        if (values.Count != 1)
        {
            return false;
        }

        var header = values[0];
        if (header is null
            || header.Length is < 8 or > 263
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = header.AsSpan(7);
        if (token.IsEmpty || token.IndexOfAny(" \t\r\n") >= 0)
        {
            return false;
        }

        var supplied = Encoding.UTF8.GetBytes(token.ToString());
        if (supplied.Length is 0 or > 256)
        {
            CryptographicOperations.ZeroMemory(supplied);
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(configuredToken);
        var suppliedDigest = SHA256.HashData(supplied);
        var expectedDigest = SHA256.HashData(expected);
        var valid = CryptographicOperations.FixedTimeEquals(suppliedDigest, expectedDigest);
        CryptographicOperations.ZeroMemory(supplied);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(suppliedDigest);
        CryptographicOperations.ZeroMemory(expectedDigest);
        return valid;
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
