using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;

internal sealed class AppSurfaceDevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string ProtectorPurpose = "ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Persona.v1";
    private readonly IOptionsMonitor<AppSurfaceDevAuthOptions> _devAuthOptions;
    private readonly IDataProtector _protector;

    public AppSurfaceDevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<AppSurfaceDevAuthOptions> devAuthOptions,
        IDataProtectionProvider dataProtectionProvider)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(devAuthOptions);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _devAuthOptions = devAuthOptions;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = _devAuthOptions.CurrentValue;
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

    internal string ProtectPersonaId(string personaId)
    {
        return _protector.Protect(personaId);
    }
}
