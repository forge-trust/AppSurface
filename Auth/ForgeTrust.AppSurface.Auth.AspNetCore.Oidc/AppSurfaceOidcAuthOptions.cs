using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

/// <summary>
/// Configures AppSurface conventions for ASP.NET Core cookie and OpenID Connect authentication registration.
/// </summary>
/// <remarks>
/// These options name schemes, configure safe AppSurface defaults, and provide callbacks for host-owned ASP.NET Core
/// handler options. They do not insert middleware, execute challenges, execute redirects, store users, provision app
/// users, replace ASP.NET Identity, or configure provider-specific SDKs.
/// </remarks>
public sealed class AppSurfaceOidcAuthOptions
{
    private readonly List<Action<CookieAuthenticationOptions>> _cookieConfigurators = [];
    private readonly List<Action<OpenIdConnectOptions>> _openIdConnectConfigurators = [];

    /// <summary>
    /// Default cookie scheme registered by the package.
    /// </summary>
    public const string DefaultCookieScheme = "AppSurface.Cookies";

    /// <summary>
    /// Default OpenID Connect scheme registered by the package.
    /// </summary>
    public const string DefaultOidcScheme = "AppSurface.Oidc";

    /// <summary>
    /// Default callback path used by the OpenID Connect handler.
    /// </summary>
    public const string DefaultCallbackPath = "/signin-appsurface-oidc";

    /// <summary>
    /// Default signed-out callback path used by the OpenID Connect handler.
    /// </summary>
    public const string DefaultSignedOutCallbackPath = "/signout-callback-appsurface-oidc";

    /// <summary>
    /// Gets or sets the cookie authentication scheme name.
    /// </summary>
    public string CookieScheme { get; set; } = DefaultCookieScheme;

    /// <summary>
    /// Gets or sets the OpenID Connect authentication scheme name.
    /// </summary>
    public string OidcScheme { get; set; } = DefaultOidcScheme;

    /// <summary>
    /// Gets or sets the stable subject claim that should be mapped into AppSurface auth context.
    /// </summary>
    /// <remarks>
    /// The OIDC package defaults this to <c>sub</c>, then delegates to
    /// <see cref="AppSurfaceAspNetCoreAuthOptions.MapSubjectClaim(string)"/>. This keeps subject mapping in the
    /// existing ASP.NET Core adapter instead of adding a parallel mapper.
    /// </remarks>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>
    /// Gets or sets the callback path assigned to the OpenID Connect handler.
    /// </summary>
    public PathString CallbackPath { get; set; } = new(DefaultCallbackPath);

    /// <summary>
    /// Gets or sets the signed-out callback path assigned to the OpenID Connect handler.
    /// </summary>
    public PathString SignedOutCallbackPath { get; set; } = new(DefaultSignedOutCallbackPath);

    /// <summary>
    /// Gets or sets a value indicating whether tokens should be saved into the authentication ticket.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. Enabling token storage can increase cookie size and persist sensitive
    /// token material in host-owned authentication state.
    /// </remarks>
    public bool SaveTokens { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the OpenID Connect handler must have a client secret configured.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="true"/> for confidential ASP.NET Core web applications. Hosts using a provider
    /// flow that does not require a client secret can disable this validation explicitly.
    /// </remarks>
    public bool RequireClientSecret { get; set; } = true;

    /// <summary>
    /// Gets the return-url policy helper used by passive prompt helpers.
    /// </summary>
    public AppSurfaceOidcReturnUrlOptions ReturnUrls { get; } = new();

    /// <summary>
    /// Adds host-owned cookie handler configuration.
    /// </summary>
    /// <param name="configure">Callback that receives the ASP.NET Core cookie handler options.</param>
    /// <returns>The current options instance for chaining.</returns>
    public AppSurfaceOidcAuthOptions ConfigureCookie(Action<CookieAuthenticationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _cookieConfigurators.Add(configure);
        return this;
    }

    /// <summary>
    /// Adds host-owned OpenID Connect handler configuration.
    /// </summary>
    /// <param name="configure">Callback that receives the ASP.NET Core OpenID Connect handler options.</param>
    /// <returns>The current options instance for chaining.</returns>
    /// <remarks>
    /// Provider values such as authority, client id, and client secret should be applied here. AppSurface diagnostics
    /// never copy those values into metadata.
    /// </remarks>
    public AppSurfaceOidcAuthOptions ConfigureOpenIdConnect(Action<OpenIdConnectOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _openIdConnectConfigurators.Add(configure);
        return this;
    }

    /// <summary>
    /// Creates a passive login prompt for host-owned login UI.
    /// </summary>
    /// <param name="targetPath">Optional safe app-relative target path.</param>
    /// <param name="displayText">Optional display text for the host UI.</param>
    /// <returns>A passive login prompt that does not execute a redirect or challenge.</returns>
    public AppSurfaceLoginPrompt CreateLoginPrompt(string? targetPath = null, string? displayText = null)
    {
        var safeTarget = ReturnUrls.Normalize(targetPath, nameof(targetPath));
        return new AppSurfaceLoginPrompt(safeTarget, displayText, PromptMetadata());
    }

    /// <summary>
    /// Creates a passive logout prompt for host-owned logout UI.
    /// </summary>
    /// <param name="targetPath">Optional safe app-relative target path.</param>
    /// <param name="displayText">Optional display text for the host UI.</param>
    /// <returns>A passive logout prompt that does not execute a redirect or sign-out.</returns>
    public AppSurfaceLogoutPrompt CreateLogoutPrompt(string? targetPath = null, string? displayText = null)
    {
        var safeTarget = ReturnUrls.Normalize(targetPath, nameof(targetPath));
        return new AppSurfaceLogoutPrompt(safeTarget, displayText, PromptMetadata());
    }

    /// <summary>
    /// Applies registered cookie configurators to the AppSurface cookie handler options.
    /// </summary>
    /// <param name="options">The ASP.NET Core cookie handler options registered for <see cref="CookieScheme"/>.</param>
    /// <remarks>
    /// Configurators run in the order they were added through <see cref="ConfigureCookie(Action{CookieAuthenticationOptions})"/>.
    /// Host values assigned by later configurators can therefore replace values assigned by earlier configurators.
    /// </remarks>
    internal void ApplyCookie(CookieAuthenticationOptions options)
    {
        foreach (var configure in _cookieConfigurators)
        {
            configure(options);
        }
    }

    /// <summary>
    /// Applies registered OpenID Connect configurators to the AppSurface OIDC handler options.
    /// </summary>
    /// <param name="options">The ASP.NET Core OpenID Connect handler options registered for <see cref="OidcScheme"/>.</param>
    /// <remarks>
    /// Configurators run in the order they were added through
    /// <see cref="ConfigureOpenIdConnect(Action{OpenIdConnectOptions})"/> after package defaults are assigned, so host
    /// configuration can intentionally override default handler values.
    /// </remarks>
    internal void ApplyOpenIdConnect(OpenIdConnectOptions options)
    {
        foreach (var configure in _openIdConnectConfigurators)
        {
            configure(options);
        }
    }

    /// <summary>
    /// Validates AppSurface OIDC scheme names, subject mapping, and callback paths before registration.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when a scheme or subject claim is blank, a callback path is blank or unsafe, or
    /// <see cref="CookieScheme"/> and <see cref="OidcScheme"/> use the same value.
    /// </exception>
    /// <remarks>
    /// Callback paths must be safe app-relative paths accepted by
    /// <see cref="AppSurfaceOidcReturnUrlOptions.IsSafeAppRelativePath(string)"/>.
    /// </remarks>
    internal void Validate()
    {
        ValidateName(CookieScheme, nameof(CookieScheme));
        ValidateName(OidcScheme, nameof(OidcScheme));
        ValidateName(SubjectClaim, nameof(SubjectClaim));
        ValidatePath(CallbackPath, nameof(CallbackPath));
        ValidatePath(SignedOutCallbackPath, nameof(SignedOutCallbackPath));

        if (string.Equals(CookieScheme, OidcScheme, StringComparison.Ordinal))
        {
            throw new ArgumentException("CookieScheme and OidcScheme must be different scheme names.");
        }
    }

    private IReadOnlyDictionary<string, string> PromptMetadata()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppSurfaceOidcAuthMetadataKeys.CookieScheme] = CookieScheme,
            [AppSurfaceOidcAuthMetadataKeys.OidcScheme] = OidcScheme,
        };
    }

    private static void ValidateName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName);
        }
    }

    private static void ValidatePath(PathString value, string parameterName)
    {
        if (!value.HasValue || string.IsNullOrWhiteSpace(value.Value))
        {
            throw new ArgumentException("Path cannot be null, empty, or whitespace.", parameterName);
        }

        if (!AppSurfaceOidcReturnUrlOptions.IsSafeAppRelativePath(value.Value))
        {
            throw new ArgumentException("Path must be an app-relative path without control characters.", parameterName);
        }
    }
}
