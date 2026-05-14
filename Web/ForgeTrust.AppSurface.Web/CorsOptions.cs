namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Represents configuration options for Cross-Origin Resource Sharing (CORS) policies.
/// </summary>
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
    public string[] AllowedOrigins { get; set; } = [];

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
