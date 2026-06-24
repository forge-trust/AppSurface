using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

/// <summary>
/// Authenticates the configured DevAuth scheme from the local persona cookie.
/// </summary>
/// <remarks>
/// The handler reads only the protected persona id cookie, resolves that id against seeded personas, and returns
/// <see cref="AuthenticateResult.NoResult()"/> for blank, tampered, stale, or unknown state. It does not issue
/// challenges, sign users in, or validate production identity tokens.
/// </remarks>
internal sealed class AppSurfaceDevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string ProtectorPurpose = "ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Persona.v1";
    private readonly IOptions<AppSurfaceDevAuthOptions> _devAuthOptions;
    private readonly IDataProtector _protector;

    /// <summary>
    /// Creates the DevAuth authentication handler.
    /// </summary>
    /// <param name="options">ASP.NET Core scheme options for the registered DevAuth scheme.</param>
    /// <param name="logger">Logger factory used by the base authentication handler.</param>
    /// <param name="encoder">URL encoder used by the base authentication handler.</param>
    /// <param name="devAuthOptions">Materialized DevAuth options containing seeded personas and cookie settings.</param>
    /// <param name="dataProtectionProvider">Data protection provider used to unprotect the persona id cookie.</param>
    public AppSurfaceDevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AppSurfaceDevAuthOptions> devAuthOptions,
        IDataProtectionProvider dataProtectionProvider)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(devAuthOptions);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _devAuthOptions = devAuthOptions;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    /// <summary>
    /// Attempts to authenticate the selected local persona.
    /// </summary>
    /// <returns>
    /// Success with a claims principal for a known protected persona id, or no result when no trustworthy persona is
    /// selected.
    /// </returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = _devAuthOptions.Value;
        if (!Request.Cookies.TryGetValue(options.CookieName, out var protectedPersonaId) ||
            string.IsNullOrWhiteSpace(protectedPersonaId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string personaId;
        try
        {
            personaId = _protector.Unprotect(protectedPersonaId);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!options.Users.Personas.TryGetValue(personaId, out var persona))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(persona.Claims, options.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, options.SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Protects a persona id with the same data-protection purpose as the endpoint cookie writer.
    /// </summary>
    /// <param name="personaId">Configured persona id to protect for tests.</param>
    /// <returns>A protected cookie payload containing only the persona id.</returns>
    internal string ProtectPersonaId(string personaId)
    {
        return _protector.Protect(personaId);
    }
}
