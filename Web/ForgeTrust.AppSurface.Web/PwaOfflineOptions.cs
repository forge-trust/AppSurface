namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures AppSurface's explicit starter service-worker strategy.
/// </summary>
/// <remarks>
/// Offline behavior is disabled by default because service workers can create stale or private-content bugs when they
/// cache arbitrary routes. When enabled, the built-in worker caches only <see cref="OfflineFallbackPath"/> and
/// <see cref="StaticAssetPaths"/>. It does not cache app navigations, authenticated pages, POST responses, or API data.
/// </remarks>
public sealed class PwaOfflineOptions
{
    private readonly PwaWorkerPathState _pathState;

    /// <summary>
    /// Initializes a standalone offline options instance with the existing service-worker path default.
    /// </summary>
    public PwaOfflineOptions()
        : this(new PwaWorkerPathState())
    {
    }

    internal PwaOfflineOptions(PwaWorkerPathState pathState)
    {
        _pathState = pathState;
    }

    /// <summary>
    /// Gets or sets a value indicating whether AppSurface should map a starter service-worker endpoint.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the app-root-relative service-worker endpoint path.
    /// </summary>
    /// <remarks>
    /// This compatibility property shares its effective value with <see cref="PwaOptions.Worker"/> on the instance
    /// owned by <see cref="PwaOptions"/>. New code should configure <see cref="PwaWorkerOptions.ServiceWorkerPath"/>.
    /// Conflicting explicit legacy and current values fail startup.
    /// </remarks>
    public string ServiceWorkerPath
    {
        get => _pathState.EffectiveValue;
        set => _pathState.SetLegacyValue(value);
    }

    /// <summary>
    /// Gets or sets the app-root-relative offline fallback page cached by the starter service worker.
    /// </summary>
    public string OfflineFallbackPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets app-root-relative static asset URLs the starter service worker should cache.
    /// </summary>
    public string[] StaticAssetPaths { get; set; } = [];
}
