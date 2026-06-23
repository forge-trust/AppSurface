using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

/// <summary>
/// Registers AppSurface-safe ASP.NET Core cookie and OpenID Connect authentication conventions.
/// </summary>
public static class AppSurfaceOidcAuthServiceCollectionExtensions
{
    /// <summary>
    /// Adds named cookie and OpenID Connect authentication schemes with AppSurface-safe defaults.
    /// </summary>
    /// <param name="services">Service collection that receives the registrations.</param>
    /// <param name="configure">Optional AppSurface OIDC convention configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// The method registers named schemes only. It does not set global default schemes, insert middleware, execute
    /// challenges, execute redirects, execute sign-in or sign-out, provision users, or configure provider-specific SDKs.
    /// Hosts must still call <c>UseAuthentication()</c> and <c>UseAuthorization()</c> in the ASP.NET Core pipeline.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceOidcAuth(
        this IServiceCollection services,
        Action<AppSurfaceOidcAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var appSurfaceOptions = new AppSurfaceOidcAuthOptions();
        configure?.Invoke(appSurfaceOptions);
        appSurfaceOptions.Validate();

        services.AddSingleton(Options.Create(appSurfaceOptions));
        services.AddAppSurfaceAspNetCoreAuth(options => options.MapSubjectClaim(appSurfaceOptions.SubjectClaim));

        services.AddAuthentication()
            .AddCookie(
                appSurfaceOptions.CookieScheme,
                options => appSurfaceOptions.ApplyCookie(options))
            .AddOpenIdConnect(
                appSurfaceOptions.OidcScheme,
                options =>
                {
                    options.SignInScheme = appSurfaceOptions.CookieScheme;
                    options.ResponseType = OpenIdConnectResponseType.Code;
                    options.CallbackPath = appSurfaceOptions.CallbackPath;
                    options.SignedOutCallbackPath = appSurfaceOptions.SignedOutCallbackPath;
                    options.SaveTokens = appSurfaceOptions.SaveTokens;
                    appSurfaceOptions.ApplyOpenIdConnect(options);
                    AppSurfaceOidcEventComposer.Compose(options, appSurfaceOptions);
                });
        services.PostConfigure<OpenIdConnectOptions>(
            appSurfaceOptions.OidcScheme,
            options => AppSurfaceOidcOpenIdConnectOptionsValidator.ThrowIfInvalid(
                appSurfaceOptions.OidcScheme,
                options,
                appSurfaceOptions));

        return services;
    }
}
