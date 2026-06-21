using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

/// <summary>
/// Composes OpenID Connect event handlers so AppSurface can attach safe diagnostics without replacing host behavior.
/// </summary>
/// <remarks>
/// The composer installs a new <see cref="OpenIdConnectEvents"/> instance on the handler options. Existing handlers are
/// copied or invoked first, then AppSurface adds diagnostics for missing subject claims, remote failures, and optional
/// token persistence.
/// </remarks>
internal static class AppSurfaceOidcEventComposer
{
    /// <summary>
    /// Rebuilds the OIDC event set with AppSurface diagnostic wrappers.
    /// </summary>
    /// <param name="options">The ASP.NET Core OpenID Connect handler options to update.</param>
    /// <param name="appSurfaceOptions">AppSurface OIDC options that provide subject-claim and token settings.</param>
    /// <remarks>
    /// This method overwrites <see cref="OpenIdConnectOptions.Events"/>. Existing event delegates are preserved by
    /// copying pass-through handlers and by invoking host handlers first for <c>OnTokenValidated</c>,
    /// <c>OnRemoteFailure</c>, and, when <see cref="AppSurfaceOidcAuthOptions.SaveTokens"/> is enabled,
    /// <c>OnTicketReceived</c>.
    /// </remarks>
    public static void Compose(OpenIdConnectOptions options, AppSurfaceOidcAuthOptions appSurfaceOptions)
    {
        var existing = options.Events ?? new OpenIdConnectEvents();
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = existing.OnRedirectToIdentityProvider,
            OnRedirectToIdentityProviderForSignOut = existing.OnRedirectToIdentityProviderForSignOut,
            OnAuthorizationCodeReceived = existing.OnAuthorizationCodeReceived,
            OnMessageReceived = existing.OnMessageReceived,
            OnTokenResponseReceived = existing.OnTokenResponseReceived,
            OnTokenValidated = async context =>
            {
                await existing.OnTokenValidated(context);
                if (!HasSubjectClaim(context.Principal, appSurfaceOptions.SubjectClaim))
                {
                    AddDiagnostic(
                        context.Properties,
                        AppSurfaceOidcAuthDiagnosticCodes.MissingSubjectClaim,
                        (AppSurfaceOidcAuthMetadataKeys.OptionName, nameof(appSurfaceOptions.SubjectClaim)));
                }
            },
            OnUserInformationReceived = existing.OnUserInformationReceived,
            OnTicketReceived = existing.OnTicketReceived,
            OnAuthenticationFailed = existing.OnAuthenticationFailed,
            OnRemoteFailure = async context =>
            {
                await existing.OnRemoteFailure(context);
                AddDiagnostic(
                    context.Properties,
                    AppSurfaceOidcAuthDiagnosticCodes.RemoteFailure,
                    (AppSurfaceOidcAuthMetadataKeys.EventName, nameof(OpenIdConnectEvents.RemoteFailure)),
                    (AppSurfaceOidcAuthMetadataKeys.ExceptionType, context.Failure?.GetType().Name));
            },
            OnSignedOutCallbackRedirect = existing.OnSignedOutCallbackRedirect,
            OnRemoteSignOut = existing.OnRemoteSignOut,
            OnAccessDenied = existing.OnAccessDenied,
            OnPushAuthorization = existing.OnPushAuthorization,
        };

        if (appSurfaceOptions.SaveTokens)
        {
            options.Events.OnTicketReceived = async context =>
            {
                await existing.OnTicketReceived(context);
                AddDiagnostic(context.Properties, AppSurfaceOidcAuthDiagnosticCodes.TokenPersistenceEnabled);
            };
        }
    }

    private static void AddDiagnostic(
        AuthenticationProperties? properties,
        string diagnosticCode,
        params (string Key, string? Value)[] metadata)
    {
        if (properties is null)
        {
            return;
        }

        properties.Items[AppSurfaceOidcAuthMetadataKeys.DiagnosticCode] = diagnosticCode;
        foreach (var (key, value) in metadata)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                properties.Items[key] = value;
            }
        }
    }

    private static bool HasSubjectClaim(ClaimsPrincipal? principal, string subjectClaim)
    {
        return principal?.Identities.Any(identity =>
            identity.IsAuthenticated
            && identity.Claims.Any(claim => string.Equals(claim.Type, subjectClaim, StringComparison.Ordinal))) == true;
    }
}
