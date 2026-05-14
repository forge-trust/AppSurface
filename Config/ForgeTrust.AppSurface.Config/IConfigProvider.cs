namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Defines a provider that can retrieve configuration values.
/// </summary>
public interface IConfigProvider
{
    /// <summary>
    /// Higher number means higher priority. When multiple providers provide the same key,
    /// the one with the highest priority wins.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Gets the name of the configuration provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Retrieves a configuration value for a specific environment and key.
    /// </summary>
    /// <typeparam name="T">The type of the configuration value.</typeparam>
    /// <param name="environment">The environment name (e.g., "Production").</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configuration value, or the default value of <typeparamref name="T"/> if not found.</returns>
    T? GetValue<T>(string environment, string key);
}
