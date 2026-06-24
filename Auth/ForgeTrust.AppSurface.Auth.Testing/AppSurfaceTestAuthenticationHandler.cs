using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Auth.Testing;

internal sealed class AppSurfaceTestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AppSurfaceTestAuthOptions _testAuthOptions;
    private readonly AppSurfaceTestPersonaRegistry _personaRegistry;

    public AppSurfaceTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppSurfaceTestAuthOptions testAuthOptions,
        AppSurfaceTestPersonaRegistry personaRegistry)
        : base(options, logger, encoder)
    {
        _testAuthOptions = testAuthOptions;
        _personaRegistry = personaRegistry;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var personaName = Request.Headers[AppSurfaceTestAuthTransport.PersonaHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(personaName))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!_personaRegistry.TryGet(personaName, out var persona))
        {
            Context.Items[AppSurfaceTestAuthTransport.UnknownPersonaItemKey] = personaName;
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            persona.CreateClaims(_testAuthOptions.ResolveSubjectClaimType()),
            Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
