namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Provides object-graph patching for configuration providers that can supply child values beneath a requested key.
/// </summary>
/// <remarks>
/// This is an internal provider seam, not a consumer-facing configuration API. <see cref="DefaultConfigManager"/>
/// uses it after direct provider resolution so hierarchical override sources can update only the supplied child
/// values instead of replacing an entire options object.
/// </remarks>
internal interface IConfigValuePatcher
{
    /// <summary>
    /// Attempts to apply provider-owned child values beneath <paramref name="key"/> to
    /// <paramref name="currentValue"/>.
    /// </summary>
    /// <typeparam name="T">The requested configuration value type.</typeparam>
    /// <param name="environment">The active environment name.</param>
    /// <param name="key">The configuration key whose child values may be present.</param>
    /// <param name="currentValue">The value supplied by a lower-priority provider, or <see langword="default"/>.</param>
    /// <param name="patchedValue">
    /// When this method returns <see langword="true"/>, contains the patched value. Otherwise contains
    /// <see langword="default"/>.
    /// </param>
    /// <returns><see langword="true"/> when at least one child value was applied.</returns>
    bool TryPatch<T>(string environment, string key, T? currentValue, out T? patchedValue);
}
