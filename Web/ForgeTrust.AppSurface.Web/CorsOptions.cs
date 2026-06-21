namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Represents configuration options for Cross-Origin Resource Sharing (CORS) policies.
/// </summary>
/// <remarks>
/// Use <see cref="CorsOptions"/> when an AppSurface web application should register and apply the framework-managed
/// CORS policy. <see cref="EnableCors"/> controls whether that policy is active outside development, while
/// <see cref="EnableAllOriginsInDevelopment"/> keeps local browser workflows convenient without opening production
/// defaults. Empty <see cref="AllowedHeaders"/> or <see cref="AllowedMethods"/> collections do not opt into permissive
/// preflight behavior in production; configure the exact browser contract an application supports. Pitfall: enabling
/// CORS in non-development environments still requires at least one allowed origin unless development-only all-origin
/// behavior applies, and the literal wildcard origin <c>*</c> is rejected outside development for AppSurface-managed
/// CORS. Use explicit origins, wildcard subdomains such as <c>https://*.example.com</c>, or host-owned ASP.NET Core
/// CORS policy registration for intentionally public wildcard APIs.
/// </remarks>
public record CorsOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether all origins are allowed when running in the development environment.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool EnableAllOriginsInDevelopment { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether CORS is enabled for the application.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool EnableCors { get; set; } = false;

    /// <summary>
    /// Gets or sets the collection of origins permitted to make cross-origin requests.
    /// Defaults to an empty array.
    /// </summary>
    /// <remarks>
    /// Use exact origins such as <c>https://app.example.com</c>. AppSurface also preserves ASP.NET Core wildcard
    /// subdomain support for origins such as <c>https://*.example.com</c>. The exact literal <c>*</c> is allowed only
    /// for the existing development compatibility path; non-development startup fails before registering the
    /// AppSurface-managed CORS policy when this collection contains <c>*</c>.
    /// </remarks>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Gets or sets the request headers advertised by the CORS policy during browser preflight requests.
    /// Defaults to an empty array, which advertises no custom preflight request headers in production.
    /// </summary>
    /// <remarks>
    /// Configure explicit header names, such as <c>Content-Type</c> or <c>X-Request-Id</c>, when preflighted
    /// cross-origin callers need them. Use <c>*</c> only when the application intentionally accepts any request header
    /// from allowed origins. Non-preflight CORS responses are still governed by origin policy and browser safelisted
    /// request behavior; this collection defines the <c>Access-Control-Allow-Headers</c> preflight response contract.
    /// When <see cref="EnableAllOriginsInDevelopment"/> applies, AppSurface still allows any header for local
    /// development convenience unless this collection contains one or more configured values.
    /// </remarks>
    public string[] AllowedHeaders { get; set; } = [];

    /// <summary>
    /// Gets or sets the HTTP methods advertised by the CORS policy during browser preflight requests.
    /// Defaults to an empty array, which advertises no preflight-only HTTP methods in production.
    /// </summary>
    /// <remarks>
    /// Configure explicit method names, such as <c>GET</c> or <c>POST</c>, when preflighted cross-origin callers need
    /// them. Use <c>*</c> only when the application intentionally accepts any method from allowed origins.
    /// Non-preflight CORS responses are still governed by origin policy and browser method safelisting; this collection
    /// defines the <c>Access-Control-Allow-Methods</c> preflight response contract. When
    /// <see cref="EnableAllOriginsInDevelopment"/> applies, AppSurface still allows any method for local development
    /// convenience unless this collection contains one or more configured values.
    /// </remarks>
    public string[] AllowedMethods { get; set; } = [];

    /// <summary>
    /// Gets or sets the name of the CORS policy to register.
    /// Defaults to <c>"DefaultCorsPolicy"</c>.
    /// </summary>
    public string PolicyName { get; set; } = "DefaultCorsPolicy";

    /// <summary>
    /// Gets a default instance of <see cref="CorsOptions"/> with default configuration settings.
    /// </summary>
    public static CorsOptions Default => new();
}
