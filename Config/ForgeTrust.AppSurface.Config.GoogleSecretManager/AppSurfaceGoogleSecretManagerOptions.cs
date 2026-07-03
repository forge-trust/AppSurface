namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Configures the AppSurface Google Secret Manager provider.
/// </summary>
/// <remarks>
/// Keys are claimed explicitly by <see cref="MapSecret"/> or by an opt-in convention resolver. Claimed keys fail closed
/// by default when Google Secret Manager cannot safely return the value. Environment variables still remain the top
/// AppSurface emergency override because <see cref="DefaultConfigManager"/> checks them before normal providers.
/// </remarks>
public sealed class AppSurfaceGoogleSecretManagerOptions
{
    /// <summary>
    /// The Secret Manager version alias that always points at the latest enabled version.
    /// </summary>
    public const string LatestVersion = "latest";

    private readonly List<AppSurfaceGoogleSecretMapping> _mappings = [];
    private readonly List<AppSurfaceGoogleSecretConvention> _conventions = [];

    /// <summary>
    /// Gets or sets the Google Cloud project id used for short secret ids.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Gets or sets the default secret version used by mappings that do not specify one.
    /// </summary>
    /// <remarks>
    /// Keep this unset for production unless every mapping supplies its own version or alias. Set
    /// <see cref="AllowLatestVersion"/> before using <c>latest</c>; mutable latest is an explicit opt-in, not the silent
    /// production default.
    /// </remarks>
    public string? DefaultVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the mutable <c>latest</c> version alias is allowed.
    /// </summary>
    public bool AllowLatestVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether claimed-key failures should stop lower-priority providers.
    /// </summary>
    public bool FailClosedOnProviderFailure { get; set; } = true;

    /// <summary>
    /// Gets or sets the bounded timeout for one Secret Manager lookup.
    /// </summary>
    public TimeSpan LookupTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the optional in-memory cache duration for successful secret values.
    /// </summary>
    /// <remarks>
    /// Leave unset for strict read-through behavior. When enabled, rotation is visible after this duration or after the app
    /// rebuilds the provider. The provider does not run background refresh.
    /// </remarks>
    public TimeSpan? CacheTtl { get; set; }

    /// <summary>
    /// Gets explicit logical-key to Secret Manager mappings.
    /// </summary>
    public IReadOnlyList<AppSurfaceGoogleSecretMapping> Mappings => _mappings;

    /// <summary>
    /// Gets opt-in convention resolvers.
    /// </summary>
    public IReadOnlyList<AppSurfaceGoogleSecretConvention> Conventions => _conventions;

    /// <summary>
    /// Allows use of the mutable <c>latest</c> Secret Manager version alias.
    /// </summary>
    /// <returns>The same options instance.</returns>
    public AppSurfaceGoogleSecretManagerOptions AllowLatest()
    {
        AllowLatestVersion = true;
        return this;
    }

    /// <summary>
    /// Maps an AppSurface logical configuration key to a Google Secret Manager secret id or full version resource name.
    /// </summary>
    /// <param name="logicalKey">The logical AppSurface configuration key, for example <c>Stripe:ApiKey</c>.</param>
    /// <param name="secretIdOrResourceName">A short secret id or full <c>projects/.../secrets/.../versions/...</c> name.</param>
    /// <param name="version">The version or alias for short secret ids. Overrides <see cref="DefaultVersion"/>.</param>
    /// <returns>The same options instance.</returns>
    public AppSurfaceGoogleSecretManagerOptions MapSecret(
        string logicalKey,
        string secretIdOrResourceName,
        string? version = null)
    {
        _mappings.Add(new AppSurfaceGoogleSecretMapping(logicalKey, secretIdOrResourceName, version));
        return this;
    }

    /// <summary>
    /// Enables a scoped convention resolver for logical keys under <paramref name="logicalKeyPrefix"/>.
    /// </summary>
    /// <param name="logicalKeyPrefix">The required logical-key prefix that the convention may claim.</param>
    /// <param name="secretIdPrefix">An optional prefix prepended to normalized secret ids.</param>
    /// <param name="version">The version or alias used by claimed convention keys.</param>
    /// <returns>The same options instance.</returns>
    public AppSurfaceGoogleSecretManagerOptions EnableConventionResolver(
        string logicalKeyPrefix,
        string secretIdPrefix = "",
        string? version = null)
    {
        _conventions.Add(new AppSurfaceGoogleSecretConvention(logicalKeyPrefix, secretIdPrefix, version));
        return this;
    }
}
